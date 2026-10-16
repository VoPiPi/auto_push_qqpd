namespace QqChannelDesk.Services;

/// <summary>
/// Independently schedules Telegram collection. It is deliberately separate
/// from publish scheduling because collection writes materials rather than posts.
/// </summary>
public sealed class TelegramCollectionScheduler
{
    private readonly TelegramCollectionStore _store;
    private readonly ContentLibraryStore _library;
    private readonly TelegramCollectorService _collector;
    private readonly TelegramMaterializer _materializer;
    private readonly WebProxyProviderRegistry _proxyRegistry;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _runCancelLock = new();
    private CancellationTokenSource? _stopSource;
    private CancellationTokenSource? _activeRunSource;
    private Task? _loopTask;

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

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public event Action<string>? StatusChanged;
    public event Action<TelegramCollectionProgress>? ProgressChanged;

    public void Start()
    {
        lock (_stateLock)
        {
            if (IsRunning) return;
            _stopSource?.Dispose();
            _stopSource = new CancellationTokenSource();
            _loopTask = Task.Run(() => LoopAsync(_stopSource.Token));
        }
        StatusChanged?.Invoke("TG 定时采集已启动");
    }

    public async Task<bool> StopAsync(TimeSpan? waitTimeout = null)
    {
        CancellationTokenSource? source;
        Task? task;
        lock (_stateLock)
        {
            CancelCurrentRun();
            source = _stopSource;
            task = _loopTask;
            if (source is null || task is null) return true;
            source.Cancel();
        }

        try
        {
            var completed = await Task.WhenAny(task, Task.Delay(waitTimeout ?? TimeSpan.FromSeconds(20)));
            if (!ReferenceEquals(completed, task)) return false;
            await task;
            return true;
        }
        catch (OperationCanceledException) { return true; }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_stopSource, source))
                {
                    _stopSource = null;
                    _loopTask = null;
                }
            }
            source.Dispose();
            StatusChanged?.Invoke("TG 定时采集已停止");
        }
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try { await RunDueSchedulesAsync(force: false, cancellationToken); }
        finally { _runLock.Release(); }
    }

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

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CheckNowAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 用户取消了某次后台采集，调度循环本身继续运行。
                    StatusChanged?.Invoke("TG 定时采集已取消，调度器继续运行");
                }
                await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Error($"TG 定时采集调度器异常：{CliDiagnostics.Sanitize(ex.Message)}");
            StatusChanged?.Invoke($"TG 定时采集调度器异常：{CliDiagnostics.Sanitize(ex.Message)}");
        }
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
                message => StatusChanged?.Invoke($"{schedule.ChannelName}：{message}"),
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
            await _store.UpdateResultAsync(schedule.Id, started, true, added, detail, cancellationToken);
            _logger.Info($"TG 定时采集 {schedule.ChannelUrl}：{detail}");
            Report(schedule, "完成", preview.Messages.Count, preview.Messages.Count, $"采集完成，{detail}。");
            return new(schedule.Id, true, added, skipped, matched, unmatched, failedMedia, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消或调度器停止时，仍然发一次终态，让界面能复位行状态。
            Report(schedule, "失败", 0, 0, $"采集已取消（已新增 {added} 条，已跳过 {skipped} 条）。");
            throw;
        }
        catch (Exception ex)
        {
            var error = CliDiagnostics.Sanitize(ex.Message);
            var detail = $"{error}（已新增 {added} 条，已跳过 {skipped} 条）";
            try { await _store.UpdateResultAsync(schedule.Id, started, false, added, detail, cancellationToken); }
            catch (Exception updateEx) { _logger.Error($"保存 TG 定时采集失败状态失败：{CliDiagnostics.Sanitize(updateEx.Message)}"); }
            _logger.Warning($"TG 定时采集 {schedule.ChannelUrl} 失败：{detail}");
            Report(schedule, "失败", 0, 0, $"采集未完成，请检查网络、代理或节点后重试：{detail}");
            return new(schedule.Id, false, added, skipped, matched, unmatched, failedMedia, detail);
        }
    }

    private void Report(TelegramCollectionSchedule schedule, string stage, int current, int total, string message)
    {
        StatusChanged?.Invoke($"{schedule.ChannelName}：{message}");
        ProgressChanged?.Invoke(new TelegramCollectionProgress(schedule.Id, schedule.ChannelName, stage, current, total, message));
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
