namespace QqChannelDesk.Services;

/// <summary>
/// Runs persisted schedule executions while the application is alive. The scheduler
/// claims work in SQLite before starting a publish, so a second scheduler cannot
/// publish the same execution.
/// </summary>
public sealed class PublishScheduler
{
    private readonly ContentLibraryStore _store;
    private readonly IPublishExecutor _publisher;
    private readonly SystemSettingsStore _settingsStore;
    private readonly Func<Task<ScheduleEnvironment>> _environment;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _taskGate = new();
    private readonly object _stateGate = new();
    private readonly List<Task> _runningTasks = [];
    private CancellationTokenSource? _stopSource;
    private Task? _loopTask;
    private long _generation;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _lastHeartbeatAt;
    private DateTimeOffset? _nextCheckAt;
    private bool _enabled = true;
    private long _enabledVersion;
    private bool _runOverdueAtStartup;
    private PublishSchedulerStatus _status = PublishSchedulerStatus.Stopped;

    public PublishScheduler(
        ContentLibraryStore store,
        IPublishExecutor publisher,
        SystemSettingsStore settingsStore,
        Func<Task<ScheduleEnvironment>> environment,
        AppLogger? logger = null)
    {
        _store = store;
        _publisher = publisher;
        _settingsStore = settingsStore;
        _environment = environment;
        _logger = logger ?? AppLogger.Instance;
    }

    public event Action<PublishSchedulerStatus>? StatusChanged;

    public PublishSchedulerStatus Status
    {
        get { lock (_stateGate) return _status; }
    }

    public bool IsEnabled
    {
        get { lock (_stateGate) return _enabled; }
    }

    /// <summary>
    /// Applies the user's auto-execute choice immediately. The scan loop keeps
    /// running and refreshing its heartbeat, but it will not dispatch new plans
    /// while auto execution is disabled.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        PublishSchedulerStatus current;
        lock (_stateGate)
        {
            _enabled = enabled;
            _enabledVersion++;
            current = _status;
        }

        var message = current.Faulted
            ? current.Message
            : enabled
                ? current.Running ? "自动执行已开启，等待下一轮检查" : "计划执行器未启动"
                : current.Running ? "自动执行已关闭，执行器仍在运行" : "计划执行器未启动，自动执行已关闭";
        SetStatus(current with
        {
            Message = message,
            UpdatedAt = DateTimeOffset.Now
        });
    }

    public void Start(bool runOverdueAtStartup)
    {
        CancellationTokenSource source;
        long generation;
        lock (_stateGate)
        {
            // 并发 Start 只能有一次成功创建循环，避免同一窗口启动出两个扫描循环。
            if (_loopTask is { IsCompleted: false }) return;
            RemoveCompletedTasksCore();
            if (RunningCountCore() > 0) return;

            _startedAt = DateTimeOffset.Now;
            _lastHeartbeatAt = _startedAt;
            _nextCheckAt = null;
            _runOverdueAtStartup = runOverdueAtStartup;
            // 上一次 StopAsync 已完成时 _stopSource 必为 null；这里只防御性释放残留对象。
            _stopSource?.Dispose();
            source = new CancellationTokenSource();
            _stopSource = source;
            generation = ++_generation;
        }

        SetStatus(new PublishSchedulerStatus(
            true,
            false,
            "正在启动计划执行器",
            0,
            DateTimeOffset.Now),
            TimeSpan.FromSeconds(2));
        var loop = Task.Run(() => RunLoopAsync(generation, source.Token));
        lock (_stateGate)
        {
            if (_generation == generation) _loopTask = loop;
        }
    }

    public async Task<ScheduleCheckResult> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var environment = await _environment().ConfigureAwait(false);
            if (!environment.Ready)
            {
                SetStatus(new PublishSchedulerStatus(true, true, environment.Reason, RunningCount(), DateTimeOffset.Now)
                {
                    Faulted = true
                });
                return new ScheduleCheckResult(false, 0, 0, environment.Reason);
            }

            var settings = await _settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
            var result = await StartDueTasksAsync(
                includeOverdue: true,
                notBefore: null,
                maxConcurrency: SystemSettingsStore.NormalizeConcurrency(settings.MaxScheduleConcurrency),
                waitForStartedTasks: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            SetStatus(new PublishSchedulerStatus(
                true,
                false,
                result.StartedCount == 0 ? "立即检查完成，没有可执行计划" : $"立即检查完成，处理 {result.StartedCount} 条计划",
                RunningCount(),
                DateTimeOffset.Now));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ScheduleCheckResult(false, 0, 0, "已取消检查发布计划");
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            _logger.Error($"立即检查发布计划失败：{message}");
            SetStatus(new PublishSchedulerStatus(true, true, message, RunningCount(), DateTimeOffset.Now));
            return new ScheduleCheckResult(false, 0, 0, message);
        }
    }

    public async Task<bool> StopAsync(TimeSpan? waitTimeout = null)
    {
        CancellationTokenSource? source;
        Task? loop;
        long generation;
        lock (_stateGate)
        {
            source = _stopSource;
            loop = _loopTask;
            generation = _generation;
        }
        var timeout = waitTimeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + timeout;
        var stopped = true;

        if (source is not null && loop is not null)
            source.Cancel();

        try
        {
            if (loop is not null)
                await WaitWithDeadlineAsync(loop, deadline).ConfigureAwait(false);
            Task[] running;
            lock (_taskGate) running = _runningTasks.ToArray();
            if (running.Length > 0)
                await WaitWithDeadlineAsync(Task.WhenAll(running), deadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stopped = false;
            try
            {
                await _store.MarkRunningExecutionsAsNeedsVerificationAsync(
                    "应用关闭前未能确认计划发布结果，请检查目标频道。", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error($"停止计划执行器时恢复运行中任务失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }
        }
        finally
        {
            if (stopped)
            {
                var ownsState = false;
                lock (_stateGate)
                {
                    // 只清理本次停止对应的那一次运行；期间若有 Start 创建了新循环，不能覆盖新状态。
                    if (_generation == generation && ReferenceEquals(_stopSource, source))
                    {
                        _nextCheckAt = null;
                        _loopTask = null;
                        _stopSource = null;
                        ownsState = true;
                    }
                }
                RemoveCompletedTasksCore();
                if (ownsState)
                    SetStatus(new PublishSchedulerStatus(false, false, "计划执行器已停止", 0, DateTimeOffset.Now));
                // 循环与在途任务都已确认结束，此时释放 CTS 是安全的。
                source?.Dispose();
            }
            else
            {
                // 超时：循环或任务仍在运行，保留 _stopSource/_loopTask 引用，避免旧循环持有已释放的 CTS。
                SetStatus(new PublishSchedulerStatus(true, true, "计划执行器正在等待在途任务结束", RunningCount(), DateTimeOffset.Now));
            }
        }
        return stopped;
    }

    private async Task RunLoopAsync(long generation, CancellationToken cancellationToken)
    {
        var firstScan = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsCurrentGeneration(generation))
            {
                RemoveCompletedTasks();
                long enabledVersion;
                lock (_stateGate) enabledVersion = _enabledVersion;
                var settings = await _settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
                bool enabled;
                lock (_stateGate)
                {
                    // A user toggle can land while settings are being read. In that
                    // case the immediate choice wins until the next scan.
                    if (_enabledVersion == enabledVersion)
                        _enabled = settings.AutoExecuteSchedules;
                    enabled = _enabled;
                }

                // Startup recovery is intentionally independent from the live auto-run
                // switch. It is a separate user consent for overdue plans.
                if (firstScan && _runOverdueAtStartup)
                {
                    var startupEnvironment = await _environment().ConfigureAwait(false);
                    if (startupEnvironment.Ready)
                    {
                        var startupResult = await StartDueTasksAsync(
                            includeOverdue: true,
                            notBefore: null,
                            maxConcurrency: SystemSettingsStore.NormalizeConcurrency(settings.MaxScheduleConcurrency),
                            waitForStartedTasks: false,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        SetStatus(new PublishSchedulerStatus(true, false,
                            startupResult.StartedCount == 0 ? "启动补发检查完成" : $"启动补发已开始 {startupResult.StartedCount} 条计划",
                            RunningCount(), DateTimeOffset.Now),
                            TimeSpan.FromSeconds(2));
                    }
                    else
                    {
                        SetStatus(new PublishSchedulerStatus(true, true, startupEnvironment.Reason, RunningCount(), DateTimeOffset.Now)
                        {
                            Faulted = true
                        }, TimeSpan.FromSeconds(5));
                    }
                    firstScan = false;
                }

                if (!enabled)
                {
                    SetStatus(new PublishSchedulerStatus(true, false, "自动执行已关闭，执行器仍在运行", RunningCount(), DateTimeOffset.Now),
                        TimeSpan.FromSeconds(2));
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var environment = await _environment().ConfigureAwait(false);
                if (!environment.Ready)
                {
                    SetStatus(new PublishSchedulerStatus(true, true, environment.Reason, RunningCount(), DateTimeOffset.Now)
                    {
                        Faulted = true
                    }, TimeSpan.FromSeconds(5));
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // When startup recovery is disabled, this process may only execute
                // plans created for a time at or after the current process start.
                // Keep that boundary for every scan; otherwise the second scan
                // would accidentally pick up old overdue plans.
                var includeOverdue = _runOverdueAtStartup;
                DateTimeOffset? notBefore = _runOverdueAtStartup ? null : _startedAt;
                var result = await StartDueTasksAsync(
                    includeOverdue,
                    notBefore,
                    SystemSettingsStore.NormalizeConcurrency(settings.MaxScheduleConcurrency),
                    waitForStartedTasks: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                firstScan = false;
                SetStatus(new PublishSchedulerStatus(
                    true,
                    false,
                    result.StartedCount > 0 ? $"正在执行 {result.StartedCount} 条到期计划" : "自动执行已开启",
                    RunningCount(),
                    DateTimeOffset.Now),
                    TimeSpan.FromSeconds(2));
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // StopAsync waits for the claimed tasks and marks any unfinished rows.
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            _logger.Error($"计划执行器异常：{message}");
            SetStatus(new PublishSchedulerStatus(true, true, message, RunningCount(), DateTimeOffset.Now)
            {
                Faulted = true
            });
        }
    }

    private async Task<ScheduleCheckResult> StartDueTasksAsync(
        bool includeOverdue,
        DateTimeOffset? notBefore,
        int maxConcurrency,
        bool waitForStartedTasks,
        CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RemoveCompletedTasks();
            var available = Math.Max(0, maxConcurrency - RunningCount());
            if (available == 0) return new ScheduleCheckResult(true, 0, 0, null);

            var started = new List<Task>();
            for (var index = 0; index < available; index++)
            {
                var execution = await _store.TryClaimDueExecutionAsync(
                    DateTimeOffset.Now,
                    includeOverdue,
                    notBefore,
                    cancellationToken).ConfigureAwait(false);
                if (execution is null) break;

                var task = ExecuteAsync(execution, cancellationToken);
                lock (_taskGate) _runningTasks.Add(task);
                started.Add(task);
            }

            if (waitForStartedTasks && started.Count > 0)
                await Task.WhenAll(started).ConfigureAwait(false);
            return new ScheduleCheckResult(true, started.Count, started.Count, null);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task ExecuteAsync(ScheduleExecutionRecord execution, CancellationToken cancellationToken)
    {
        try
        {
            var material = await _store.GetMaterialAsync(execution.MaterialId, cancellationToken).ConfigureAwait(false);
            if (material is null)
            {
                var missing = new PublishResult(false, PublishErrorCategory.Validation, "素材不存在或已删除，未执行发布。", null, null);
                await CompleteExecutionSafelyAsync(execution.Id, null, missing).ConfigureAwait(false);
                return;
            }

            var request = new PublishRequest(
                material.GuildId,
                material.ChannelId,
                material.Content,
                material.Title,
                material.Type switch { "image" => FeedType.Image, "video" => FeedType.Video, _ => FeedType.Text },
                material.MediaLinks);
            var outcome = await _publisher.ExecuteAsync(request, material.GuildName, material.ChannelName, cancellationToken).ConfigureAwait(false);
            // Result writes must survive scheduler shutdown; never use the cancellable token here.
            await CompleteExecutionSafelyAsync(execution.Id, outcome.PublishRecordId, outcome.Result).ConfigureAwait(false);
            _logger.Info(outcome.Result.Succeeded
                ? $"计划发布成功：素材 {execution.MaterialId}"
                : $"计划发布未成功：素材 {execution.MaterialId}，{outcome.Result.Category}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var timeout = new PublishResult(false, PublishErrorCategory.Timeout,
                "计划发布被应用停止中止，请先核实目标频道，不要立即重试。", null, null);
            await CompleteExecutionSafelyAsync(execution.Id, null, timeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            _logger.Error($"计划 {execution.Id} 执行异常：{message}");
            var failure = new PublishResult(false, PublishErrorCategory.Other, $"计划未执行，请稍后重试或查看运行日志：{message}", null, null);
            await CompleteExecutionSafelyAsync(execution.Id, null, failure).ConfigureAwait(false);
        }
    }

    private async Task CompleteExecutionSafelyAsync(long executionId, long? recordId, PublishResult result)
    {
        try
        {
            if (!await _store.CompleteScheduleExecutionAsync(executionId, recordId, result).ConfigureAwait(false))
                _logger.Debug($"计划执行记录 {executionId} 已被其他状态处理，忽略迟到的发布结果。");
        }
        catch (Exception ex) { _logger.Error($"更新计划执行结果失败：{CliDiagnostics.Sanitize(ex.Message)}"); }
    }

    private void RemoveCompletedTasks()
    {
        lock (_taskGate) _runningTasks.RemoveAll(task => task.IsCompleted);
    }

    private void RemoveCompletedTasksCore()
    {
        _runningTasks.RemoveAll(task => task.IsCompleted);
    }

    private int RunningCount()
    {
        lock (_taskGate) return RunningCountCore();
    }

    private int RunningCountCore()
    {
        _runningTasks.RemoveAll(task => task.IsCompleted);
        return _runningTasks.Count;
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_stateGate) return _generation == generation;
    }

    /// <summary>
    /// Publishes the status together with the real heartbeat of the scan loop.
    /// <paramref name="nextCheckIn"/> is only passed by the loop itself, so a
    /// heartbeat always means "the loop really ran", never a UI timer.
    /// </summary>
    private void SetStatus(PublishSchedulerStatus status, TimeSpan? nextCheckIn = null)
    {
        var now = DateTimeOffset.Now;
        PublishSchedulerStatus effective;
        lock (_stateGate)
        {
            if (nextCheckIn is not null)
            {
                _lastHeartbeatAt = now;
                _nextCheckAt = now + nextCheckIn.Value;
            }

            effective = status with
            {
                LastHeartbeatAt = status.LastHeartbeatAt ?? _lastHeartbeatAt ?? status.UpdatedAt,
                NextCheckAt = status.NextCheckAt ?? _nextCheckAt,
                Enabled = _enabled
            };
            _status = effective;
        }

        try { StatusChanged?.Invoke(effective); }
        catch (Exception ex) { _logger.Error($"更新计划执行器状态失败：{CliDiagnostics.Sanitize(ex.Message)}"); }
    }

    private static async Task WaitWithDeadlineAsync(Task task, DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException();
        var completed = await Task.WhenAny(task, Task.Delay(remaining)).ConfigureAwait(false);
        if (completed != task) throw new TimeoutException();
        await task.ConfigureAwait(false);
    }
}

public sealed record ScheduleEnvironment(bool Ready, string Reason)
{
    public static ScheduleEnvironment Available => new(true, "");
}

public sealed record PublishSchedulerStatus(
    bool Running,
    bool Paused,
    string Message,
    int RunningTaskCount,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastHeartbeatAt = null,
    DateTimeOffset? NextCheckAt = null,
    bool Faulted = false,
    bool Enabled = true)
{
    public static PublishSchedulerStatus Stopped => new(false, false, "计划执行器未启动", 0, DateTimeOffset.Now);
}

public sealed record ScheduleCheckResult(bool EnvironmentReady, int StartedCount, int CompletedCount, string? Message);
