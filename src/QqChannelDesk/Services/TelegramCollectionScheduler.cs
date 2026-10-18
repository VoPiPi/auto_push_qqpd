namespace QqChannelDesk.Services;

/// <summary>
/// Independently schedules Telegram collection. It is deliberately separate
/// from publish scheduling because collection writes materials rather than posts.
/// 采集按单任务串行执行（共用一把运行锁），不读取发布计划的“发帖最大并发数”设置。
/// </summary>
public sealed class TelegramCollectionScheduler
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly TelegramCollectionStore _store;
    private readonly ContentLibraryStore _library;
    private readonly TelegramCollectorService _collector;
    private readonly TelegramMaterializer _materializer;
    private readonly WebProxyProviderRegistry _proxyRegistry;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _statusLock = new();
    private readonly object _runCancelLock = new();
    private CancellationTokenSource? _stopSource;
    private CancellationTokenSource? _activeRunSource;
    private Task? _loopTask;
    private long _generation;
    private bool _enabled = true;
    private int _runningScheduleCount;
    private DateTimeOffset? _lastHeartbeatAt;
    private DateTimeOffset? _nextCheckAt;
    private TelegramCollectionSchedulerStatus _status = TelegramCollectionSchedulerStatus.Stopped;

    public TelegramCollectionScheduler(
        TelegramCollectionStore store,
        ContentLibraryStore library,
        TelegramCollectorService? collector = null,
        WebProxyProviderRegistry? proxyRegistry = null,
        AppLogger? logger = null)
    {
        _store = store;
        _library = library;
        _collector = collector ?? new TelegramCollectorService(proxyRegistry);
        _materializer = new TelegramMaterializer(_collector, _library);
        _proxyRegistry = proxyRegistry ?? new WebProxyProviderRegistry();
        _logger = logger ?? AppLogger.Instance;
    }

    public bool IsRunning
    {
        get { lock (_stateLock) return _loopTask is { IsCompleted: false }; }
    }

    public bool IsEnabled
    {
        get { lock (_statusLock) return _enabled; }
    }

    public TelegramCollectionSchedulerStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    public event Action<string>? StatusChanged;
    public event Action<TelegramCollectionProgress>? ProgressChanged;
    public event Action<TelegramCollectionSchedulerStatus>? StateChanged;

    /// <summary>
    /// 保存用户开关意图。关闭后执行器循环继续存活并更新心跳，只是不再自动派发新采集；
    /// 正在执行的采集不会被强制中断，手动“立即采集/检查”也不受影响。
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        lock (_statusLock) _enabled = enabled;
        var running = IsRunning;
        PublishStatus(new TelegramCollectionSchedulerStatus(
            running,
            enabled,
            !enabled,
            enabled
                ? running ? "定时采集已开启，等待下次检查" : "TG 定时采集未启动"
                : running ? "定时采集已关闭，执行器保持运行" : "TG 定时采集未启动，自动采集已关闭",
            RunningScheduleCount(),
            DateTimeOffset.Now),
            running ? DateTimeOffset.Now + (enabled ? CheckInterval : HeartbeatInterval) : null);
        RaiseStatus(enabled ? "TG 定时采集已开启" : "TG 定时采集已关闭，执行器保持运行");
    }

    public void Start()
    {
        CancellationTokenSource source;
        long generation;
        lock (_stateLock)
        {
            if (_loopTask is { IsCompleted: false }) return;
            try { _stopSource?.Dispose(); }
            catch (ObjectDisposedException) { }
            source = new CancellationTokenSource();
            _stopSource = source;
            generation = ++_generation;
        }

        var loop = Task.Run(() => LoopAsync(generation, source.Token));
        lock (_stateLock)
        {
            if (_generation == generation) _loopTask = loop;
        }

        PublishStatus(new TelegramCollectionSchedulerStatus(
            true,
            IsEnabled,
            !IsEnabled,
            IsEnabled ? "TG 定时采集已启动" : "定时采集已关闭，执行器保持运行",
            0,
            DateTimeOffset.Now), DateTimeOffset.Now + HeartbeatInterval);
        RaiseStatus("TG 定时采集已启动");
    }

    public async Task<bool> StopAsync(TimeSpan? waitTimeout = null)
    {
        CancellationTokenSource? source;
        Task? task;
        long generation;
        lock (_stateLock)
        {
            CancelCurrentRun();
            source = _stopSource;
            task = _loopTask;
            generation = _generation;
            if (source is null || task is null) return true;
            source.Cancel();
        }

        var stopped = false;
        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(waitTimeout ?? TimeSpan.FromSeconds(20)));
            if (!ReferenceEquals(completed, task)) return false;
            await task;
            stopped = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            return true;
        }
        finally
        {
            // 只有在确认循环已停止时才清理字段并释放 CTS；超时返回 false 时保留状态，
            // 否则旧循环会继续持有已被释放的 CTS，且 IsRunning 会错误地变为 false。
            if (stopped)
            {
                var ownsState = false;
                lock (_stateLock)
                {
                    // 只有本次停止对应的运行仍是最新运行时才清理字段；否则会覆盖期间新建的循环。
                    if (_generation == generation && ReferenceEquals(_stopSource, source))
                    {
                        _stopSource = null;
                        _loopTask = null;
                        ownsState = true;
                    }
                }
                if (ownsState)
                {
                    // 仅当本次运行确实被清理时才释放 CTS，避免影响期间新建的循环。
                    source.Dispose();
                    lock (_statusLock) _nextCheckAt = null;
                    PublishStatus(new TelegramCollectionSchedulerStatus(
                        false,
                        IsEnabled,
                        !IsEnabled,
                        "TG 定时采集已停止",
                        0,
                        DateTimeOffset.Now));
                    RaiseStatus("TG 定时采集已停止");
                }
            }
            else
            {
                RaiseStatus("TG 定时采集仍在停止中");
            }
        }
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
        => await CheckDueAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 中断当前正在执行的采集（无论是页面手动触发还是后台调度触发）。
    /// 返回是否确实存在可取消的采集任务。
    /// </summary>
    public bool CancelCurrentRun()
    {
        lock (_runCancelLock)
        {
            var source = _activeRunSource;
            if (source is null) return false;
            try
            {
                source.Cancel();
                return true;
            }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public async Task<TelegramCollectionRunResult> RunNowAsync(long scheduleId, CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var schedule = await _store.GetAsync(scheduleId, cancellationToken)
                ?? throw new InvalidOperationException("TG 定时采集配置不存在。");
            return await RunScheduleAsync(schedule, cancellationToken);
        }
        finally { _runLock.Release(); }
    }

    private async Task LoopAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsCurrentGeneration(generation))
            {
                if (!IsEnabled)
                {
                    PublishStatus(new TelegramCollectionSchedulerStatus(
                        true, false, true, "定时采集已关闭，执行器保持运行", RunningScheduleCount(), DateTimeOffset.Now),
                        DateTimeOffset.Now + HeartbeatInterval);
                }
                else
                {
                    try
                    {
                        await CheckDueAsync(cancellationToken).ConfigureAwait(false);
                        PublishStatus(new TelegramCollectionSchedulerStatus(
                            true, true, false, "定时采集已开启，等待下次检查", RunningScheduleCount(), DateTimeOffset.Now),
                            DateTimeOffset.Now + CheckInterval);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // 用户取消了某次后台采集，调度循环本身继续运行。
                        PublishStatus(new TelegramCollectionSchedulerStatus(
                            true, true, false, "本次采集已取消，执行器继续运行", RunningScheduleCount(), DateTimeOffset.Now),
                            DateTimeOffset.Now + CheckInterval);
                        RaiseStatus("TG 定时采集已取消，执行器继续运行");
                    }
                    catch (Exception ex)
                    {
                        var message = CliDiagnostics.Sanitize(ex.Message);
                        _logger.Error($"TG 定时采集执行器异常：{message}");
                        PublishStatus(new TelegramCollectionSchedulerStatus(
                            true, true, false, $"定时采集上次检查失败：{message}", RunningScheduleCount(), DateTimeOffset.Now)
                        {
                            Faulted = true
                        }, DateTimeOffset.Now + CheckInterval);
                        RaiseStatus($"TG 定时采集执行器异常：{message}");
                    }
                }

                await DelayWithHeartbeatAsync(CheckInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            _logger.Error($"TG 定时采集执行器异常：{message}");
            PublishStatus(new TelegramCollectionSchedulerStatus(
                true, IsEnabled, !IsEnabled, $"定时采集执行器异常：{message}", RunningScheduleCount(), DateTimeOffset.Now)
            {
                Faulted = true
            });
            RaiseStatus($"TG 定时采集执行器异常：{message}");
        }
    }

    /// <summary>后台循环专用的到期检查；与页面手动触发的检查共用同一把运行锁。</summary>
    private async Task CheckDueAsync(CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RunDueSchedulesAsync(force: false, cancellationToken).ConfigureAwait(false); }
        finally { _runLock.Release(); }
    }

    /// <summary>
    /// 等待期间按固定间隔刷新心跳，让界面能区分“执行器存活但空闲”和“执行器已经卡死/退出”。
    /// </summary>
    private async Task DelayWithHeartbeatAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + duration;
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = deadline - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) break;
            var slice = remaining < HeartbeatInterval ? remaining : HeartbeatInterval;
            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) break;
            RefreshHeartbeat(deadline);
        }
    }

    private void RefreshHeartbeat(DateTimeOffset nextCheckAt)
    {
        TelegramCollectionSchedulerStatus current;
        lock (_statusLock) current = _status;
        PublishStatus(current with { UpdatedAt = DateTimeOffset.Now }, nextCheckAt);
    }

    private void PublishStatus(TelegramCollectionSchedulerStatus status, DateTimeOffset? nextCheckAt = null)
    {
        TelegramCollectionSchedulerStatus effective;
        lock (_statusLock)
        {
            var now = DateTimeOffset.Now;
            if (nextCheckAt is not null)
            {
                _lastHeartbeatAt = now;
                _nextCheckAt = nextCheckAt;
            }

            effective = status with
            {
                Enabled = _enabled,
                RunningScheduleCount = Math.Max(status.RunningScheduleCount, _runningScheduleCount),
                LastHeartbeatAt = status.LastHeartbeatAt ?? _lastHeartbeatAt ?? status.UpdatedAt,
                NextCheckAt = status.NextCheckAt ?? _nextCheckAt
            };
            _status = effective;
        }

        RaiseSafely(StateChanged, effective,
            ex => _logger.Error($"更新 TG 定时采集执行器状态失败：{CliDiagnostics.Sanitize(ex.Message)}"));
    }

    /// <summary>
    /// 逐个调用多播事件的订阅者：某个订阅者抛异常时只记录日志并继续通知其余订阅者，
    /// 避免界面或后续扩展的事件处理器把异常反向抛进采集流程。
    /// </summary>
    internal static void RaiseSafely<T>(Action<T>? handlers, T payload, Action<Exception>? onSubscriberError = null)
    {
        if (handlers is null) return;
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)subscriber)(payload);
            }
            catch (Exception ex)
            {
                onSubscriberError?.Invoke(ex);
            }
        }
    }

    private void RaiseStatus(string message) =>
        RaiseSafely(StatusChanged, message,
            ex => _logger.Error($"TG 定时采集状态订阅者异常：{CliDiagnostics.Sanitize(ex.Message)}"));

    private void RaiseProgress(TelegramCollectionProgress progress) =>
        RaiseSafely(ProgressChanged, progress,
            ex => _logger.Error($"TG 定时采集进度订阅者异常：{CliDiagnostics.Sanitize(ex.Message)}"));

    private int RunningScheduleCount()
    {
        lock (_statusLock) return _runningScheduleCount;
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_stateLock) return _generation == generation;
    }

    private async Task RunDueSchedulesAsync(bool force, CancellationToken cancellationToken)
    {
        var schedules = await _store.GetAsync(cancellationToken);
        foreach (var schedule in schedules.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!force && !TelegramCollectionStore.IsDue(schedule, DateTimeOffset.Now))
                continue;
            await RunScheduleAsync(schedule, cancellationToken);
        }
    }

    private async Task<TelegramCollectionRunResult> RunScheduleAsync(TelegramCollectionSchedule schedule, CancellationToken cancellationToken)
    {
        // 用链接令牌包一层，让页面上的“取消采集”可以中断后台调度触发的采集。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runCancelLock) { _activeRunSource = linked; }
        lock (_statusLock) _runningScheduleCount++;
        try
        {
            return await ExecuteScheduleAsync(schedule, linked.Token);
        }
        finally
        {
            lock (_runCancelLock)
            {
                if (ReferenceEquals(_activeRunSource, linked)) _activeRunSource = null;
            }
            lock (_statusLock) _runningScheduleCount = Math.Max(0, _runningScheduleCount - 1);
        }
    }

    private async Task<TelegramCollectionRunResult> ExecuteScheduleAsync(TelegramCollectionSchedule schedule, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var added = 0;
        var skipped = 0;
        var matched = 0;
        var unmatched = 0;
        var failedMedia = 0;
        try
        {
            Report(schedule, "准备", 0, 0, "正在准备采集…");
            var selection = new WebProxySelection(schedule.ProxySelection);
            Report(schedule, "请求代理", 0, 0, "正在请求网页代理并打开频道…");
            var preview = await _collector.PreviewChannelAsync(
                schedule.ChannelUrl,
                schedule.MessageLimit,
                schedule.Keyword,
                message => RaiseStatus($"{schedule.ChannelName}：{message}"),
                cancellationToken,
                selection);
            if (!preview.Succeeded)
                throw new InvalidOperationException(preview.Error);

            var rules = await _store.GetRulesAsync(schedule.Id, cancellationToken);

            Report(schedule, "解析完成", preview.Messages.Count, preview.Messages.Count, $"频道页面解析完成，共 {preview.Messages.Count} 条消息，使用 {preview.ProviderName}。");

            for (var index = 0; index < preview.Messages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = preview.Messages[index];
                Report(schedule, "写入素材", index, preview.Messages.Count, $"正在处理第 {index + 1}/{preview.Messages.Count} 条，下载媒体并写入素材库…");
                if (await _library.HasMaterialSourceAsync("TG采集", message.Url, cancellationToken))
                {
                    skipped++;
                    continue;
                }
                var target = TelegramDistributionRuleMatcher.Match(message, rules);
                if (target is null) unmatched++; else matched++;
                var materialized = await _materializer.MaterializeAsync(message, preview.CookieHeader, target, cancellationToken);
                failedMedia += materialized.FailedMediaCount;
                if (materialized.SkippedDuplicate || materialized.SkippedEmpty)
                    skipped++;
                else if (materialized.Succeeded && materialized.FailedMediaCount == 0)
                    added++;
                else
                    skipped++;

                var itemResult = materialized.Succeeded && materialized.FailedMediaCount == 0
                    ? $"第 {index + 1}/{preview.Messages.Count} 条已入库。"
                    : $"第 {index + 1}/{preview.Messages.Count} 条已跳过：{materialized.Error}";
                Report(schedule, "写入素材", index + 1, preview.Messages.Count, itemResult);
            }

            var detail = $"新增 {added} 条，跳过 {skipped} 条，命中规则 {matched} 条，未命中 {unmatched} 条，媒体失败 {failedMedia} 个";
            await _store.UpdateResultAsync(schedule.Id, started, true, added, detail, cancellationToken: cancellationToken);
            _logger.Info($"TG 定时采集 {schedule.ChannelUrl}：{detail}");
            Report(schedule, "完成", preview.Messages.Count, preview.Messages.Count, $"采集完成，{detail}。");
            return new(schedule.Id, true, added, skipped, matched, unmatched, failedMedia, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var detail = $"已新增 {added} 条，已跳过 {skipped} 条";
            try
            {
                // 取消令牌已经触发，结果写回必须使用独立令牌，否则状态无法落库。
                await _store.UpdateResultAsync(
                    schedule.Id,
                    started,
                    false,
                    added,
                    detail,
                    "已取消",
                    CancellationToken.None);
            }
            catch (Exception updateEx)
            {
                _logger.Error($"保存 TG 定时采集取消状态失败：{CliDiagnostics.Sanitize(updateEx.Message)}");
            }
            // 用户取消或执行器停止时，仍然发一次终态，让界面能复位行状态。
            Report(schedule, "已取消", 0, 0, $"采集已取消（{detail}）。");
            throw;
        }
        catch (Exception ex)
        {
            var error = CliDiagnostics.Sanitize(ex.Message);
            var detail = $"{error}（已新增 {added} 条，已跳过 {skipped} 条）";
            try { await _store.UpdateResultAsync(schedule.Id, started, false, added, detail, cancellationToken: cancellationToken); }
            catch (Exception updateEx) { _logger.Error($"保存 TG 定时采集失败状态失败：{CliDiagnostics.Sanitize(updateEx.Message)}"); }
            _logger.Warning($"TG 定时采集 {schedule.ChannelUrl} 失败：{detail}");
            Report(schedule, "失败", 0, 0, $"采集未完成，请检查网络、代理或节点后重试：{detail}");
            return new(schedule.Id, false, added, skipped, matched, unmatched, failedMedia, detail);
        }
    }

    private void Report(TelegramCollectionSchedule schedule, string stage, int current, int total, string message)
    {
        var terminal = stage is "完成" or "失败" or "已取消";
        PublishStatus(new TelegramCollectionSchedulerStatus(
            IsRunning,
            IsEnabled,
            !IsEnabled,
            $"{schedule.ChannelName}：{message}",
            RunningScheduleCount(),
            DateTimeOffset.Now)
        {
            CurrentScheduleName = terminal ? null : schedule.ChannelName,
            CurrentStage = terminal ? null : stage,
            Current = terminal ? 0 : current,
            Total = terminal ? 0 : total
        });
        RaiseStatus($"{schedule.ChannelName}：{message}");
        RaiseProgress(new TelegramCollectionProgress(schedule.Id, schedule.ChannelName, stage, current, total, message));
    }
}

public sealed record TelegramCollectionRunResult(
    long ScheduleId,
    bool Succeeded,
    int AddedCount,
    int SkippedCount,
    int MatchedRuleCount,
    int UnmatchedCount,
    int FailedMediaCount,
    string Message);
public sealed record TelegramCollectionProgress(long ScheduleId, string ChannelName, string Stage, int Current, int Total, string Message);

public sealed record TelegramCollectionSchedulerStatus(
    bool Running,
    bool Enabled,
    bool Paused,
    string Message,
    int RunningScheduleCount,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastHeartbeatAt = null,
    DateTimeOffset? NextCheckAt = null,
    bool Faulted = false,
    string? CurrentScheduleName = null,
    string? CurrentStage = null,
    int Current = 0,
    int Total = 0)
{
    public static TelegramCollectionSchedulerStatus Stopped =>
        new(false, true, false, "TG 定时采集未启动", 0, DateTimeOffset.Now);
}
