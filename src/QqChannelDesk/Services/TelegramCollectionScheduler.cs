namespace QqChannelDesk.Services;

/// <summary>
/// Independently schedules Telegram collection. It is deliberately separate
/// from publish scheduling because collection writes materials rather than posts.
/// 采集并发默认是 1，可由用户选择 2；不读取发布计划的“发帖最大并发数”设置。
/// </summary>
public sealed class TelegramCollectionScheduler
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly TelegramCollectionStore _store;
    private readonly ContentLibraryStore _library;
    private readonly TelegramCollectorService _collector;
    private readonly TelegramMaterializer _materializer;
    private readonly PublishAutoScheduleService? _autoSchedule;
    private readonly WebProxyProviderRegistry _proxyRegistry;
    private readonly ITelegramTransport? _transport;
    private readonly AppLogger _logger;
    private readonly object _stateLock = new();
    private readonly object _statusLock = new();
    private readonly object _queueLock = new();
    private readonly object _queueWorkerLock = new();
    private readonly object _terminalTaskLock = new();
    private readonly Queue<QueuedTelegramCollectionRun> _queuedRuns = new();
    private readonly HashSet<long> _queuedScheduleIds = [];
    private readonly Dictionary<long, QueuedTelegramCollectionRun> _activeRuns = [];
    private readonly List<Task> _pendingTerminalTasks = [];
    private readonly SemaphoreSlim _queueSignal = new(0);
    private CancellationTokenSource? _stopSource;
    private CancellationTokenSource? _queueWorkerSource;
    private Task? _queueWorkerTask;
    private Task? _loopTask;
    private long _generation;
    private bool _enabled = true;
    private int _maxConcurrency = 1;
    private int _runningScheduleCount;
    private readonly HashSet<long> _activeScheduleIds = [];
    private DateTimeOffset? _lastHeartbeatAt;
    private DateTimeOffset? _nextCheckAt;
    private TelegramCollectionSchedulerStatus _status = TelegramCollectionSchedulerStatus.Stopped;

    public TelegramCollectionScheduler(
        TelegramCollectionStore store,
        ContentLibraryStore library,
        TelegramCollectorService? collector = null,
        WebProxyProviderRegistry? proxyRegistry = null,
        AppLogger? logger = null,
        PublishAutoScheduleService? autoSchedule = null,
        ITelegramTransport? transport = null)
    {
        _store = store;
        _library = library;
        _collector = collector ?? new TelegramCollectorService(proxyRegistry);
        _transport = transport;
        _materializer = new TelegramMaterializer(_collector, _library, transport: transport);
        _autoSchedule = autoSchedule;
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

    /// <summary>
    /// 当前正在执行的采集配置 ID 快照。界面用它校正行的运行状态：
    /// 页面切换期间会退订事件，可能错过终态通知，只有对比执行器的真实运行集合才能把“取消采集”复位。
    /// </summary>
    public IReadOnlyCollection<long> ActiveScheduleIds
    {
        get { lock (_statusLock) return _activeScheduleIds.ToArray(); }
    }

    /// <summary>当前执行项和等待队列的稳定快照。Position=0 表示执行中，排队项从 1 开始。</summary>
    public IReadOnlyList<TelegramCollectionQueueItem> QueueSnapshot
    {
        get
        {
            lock (_queueLock)
            {
                var items = _activeRuns.Values
                    .OrderBy(run => run.EnqueuedAt)
                    .Select(run => run.ToItem(position: 0, isExecuting: true))
                    .ToList();
                var position = 1;
                foreach (var run in _queuedRuns)
                    items.Add(run.ToItem(position++, isExecuting: false));
                return items;
            }
        }
    }

    public event Action<string>? StatusChanged;
    public event Action<TelegramCollectionProgress>? ProgressChanged;
    public event Action<TelegramCollectionSchedulerStatus>? StateChanged;
    public event Action<IReadOnlyList<TelegramCollectionQueueItem>>? QueueChanged;
    public event Action<TelegramCollectionRunCompleted>? RunCompleted;

    /// <summary>
    /// 修改只影响后续派发；已经运行的任务不会被中断。
    /// </summary>
    public void SetMaxConcurrency(int concurrency)
    {
        var normalized = concurrency is 1 or 2 ? concurrency : 1;
        lock (_queueLock) _maxConcurrency = normalized;
        ReleaseQueueSignal();
        EnsureQueueWorker();
    }

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
        CancelAllRuns();
        CancellationTokenSource? source;
        CancellationTokenSource? workerSource;
        Task? loopTask;
        Task? workerTask;
        long generation;
        lock (_stateLock)
        {
            source = _stopSource;
            loopTask = _loopTask;
            generation = _generation;
            try { source?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        lock (_queueWorkerLock)
        {
            workerSource = _queueWorkerSource;
            workerTask = _queueWorkerTask;
            try { workerSource?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        try { _queueSignal.Release(); }
        catch (SemaphoreFullException) { }

        var stopped = false;
        try
        {
            Task[] pendingTerminalTasks;
            lock (_terminalTaskLock)
            {
                _pendingTerminalTasks.RemoveAll(task => task.IsCompleted);
                pendingTerminalTasks = _pendingTerminalTasks.ToArray();
            }

            var tasks = new[] { loopTask, workerTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .Concat(pendingTerminalTasks)
                .ToArray();
            if (tasks.Length > 0)
            {
                var allTasks = Task.WhenAll(tasks);
                var completed = await Task.WhenAny(
                    allTasks,
                    Task.Delay(waitTimeout ?? TimeSpan.FromSeconds(20)));
                if (!ReferenceEquals(completed, allTasks)) return false;
                try { await allTasks; }
                catch (OperationCanceledException) { }
            }
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
                    source?.Dispose();
                }

                var ownsWorker = false;
                lock (_queueWorkerLock)
                {
                    if (ReferenceEquals(_queueWorkerSource, workerSource) &&
                        (_queueWorkerTask is null || _queueWorkerTask.IsCompleted))
                    {
                        _queueWorkerSource = null;
                        _queueWorkerTask = null;
                        ownsWorker = true;
                    }
                }
                if (ownsWorker)
                {
                    try { workerSource?.Dispose(); }
                    catch (ObjectDisposedException) { }
                }

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
            else
            {
                RaiseStatus("TG 定时采集仍在停止中");
            }
        }
    }

    /// <summary>
    /// 中断当前所有执行中的采集（无论是页面手动触发还是后台调度触发）。
    /// 返回是否确实存在可取消的采集任务。
    /// </summary>
    public bool CancelCurrentRun()
    {
        lock (_queueLock)
        {
            if (_activeRuns.Count == 0) return false;
            foreach (var run in _activeRuns.Values)
            {
                try { run.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            return true;
        }
    }

    /// <summary>取消指定频道的执行中任务或排队项。返回是否找到并发出取消请求。</summary>
    public bool CancelRun(long scheduleId)
    {
        var cancelled = false;
        QueuedTelegramCollectionRun? queuedCancelled = null;
        lock (_queueLock)
        {
            if (_activeRuns.TryGetValue(scheduleId, out var active))
            {
                try
                {
                    active.Cancellation.Cancel();
                    cancelled = true;
                }
                catch (ObjectDisposedException) { }
            }

            var pending = _queuedRuns.FirstOrDefault(run => run.ScheduleId == scheduleId);
            if (pending is not null)
            {
                var retained = _queuedRuns.Where(run => !ReferenceEquals(run, pending)).ToArray();
                _queuedRuns.Clear();
                foreach (var run in retained) _queuedRuns.Enqueue(run);
                _queuedScheduleIds.Remove(pending.ScheduleId);
                try
                {
                    pending.Cancellation.Cancel();
                    pending.Completion.TrySetCanceled(pending.Cancellation.Token);
                    queuedCancelled = pending;
                    cancelled = true;
                }
                catch (ObjectDisposedException) { }
            }
        }
        if (queuedCancelled is not null)
            TrackTerminalTask(CompleteQueuedCancellationAsync(queuedCancelled));
        if (cancelled)
        {
            RaiseQueueChanged();
            RefreshExecutionStatus();
        }
        return cancelled;
    }

    /// <summary>取消当前任务以及所有排队项。页面离开不会调用此方法。</summary>
    public void CancelAllRuns()
    {
        var cancelledQueued = new List<QueuedTelegramCollectionRun>();
        lock (_queueLock)
        {
            foreach (var active in _activeRuns.Values)
            {
                try { active.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            while (_queuedRuns.Count > 0)
            {
                var pending = _queuedRuns.Dequeue();
                _queuedScheduleIds.Remove(pending.ScheduleId);
                try
                {
                    pending.Cancellation.Cancel();
                    pending.Completion.TrySetCanceled(pending.Cancellation.Token);
                    cancelledQueued.Add(pending);
                }
                catch (ObjectDisposedException) { }
            }
        }

        foreach (var pending in cancelledQueued)
            TrackTerminalTask(CompleteQueuedCancellationAsync(pending));
        RaiseQueueChanged();
        RefreshExecutionStatus();
    }

    public async Task<TelegramCollectionRunResult> RunNowAsync(
        long scheduleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schedule = await _store.GetAsync(scheduleId, cancellationToken)
            ?? throw new InvalidOperationException("TG 定时采集配置不存在。");
        EnsureQueueWorker();
        return await EnqueueAsync(schedule, "manual", cancellationToken).ConfigureAwait(false);
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

    /// <summary>后台循环专用的到期检查；只负责入队，不等待采集完成。</summary>
    private async Task CheckDueAsync(CancellationToken cancellationToken)
    {
        await RunDueSchedulesAsync(force: false, cancellationToken).ConfigureAwait(false);
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

    private void PublishRunCompleted(
        QueuedTelegramCollectionRun run,
        string status,
        bool succeeded,
        string message)
    {
        var completed = new TelegramCollectionRunCompleted(
            run.ScheduleId,
            CliDiagnostics.Sanitize(run.Schedule.ChannelName),
            NormalizeTriggerSource(run.TriggerSource),
            CliDiagnostics.Sanitize(status),
            succeeded,
            CliDiagnostics.Sanitize(message));
        RaiseSafely(RunCompleted, completed,
            ex => _logger.Error($"TG 定时采集终态订阅者异常：{CliDiagnostics.Sanitize(ex.Message)}"));
    }

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
            try
            {
                EnsureQueueWorker();
                _ = Queue(
                    schedule,
                    schedule.NextRetryAt is not null ? "retry" : "scheduled",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"TG 定时采集 {schedule.ChannelUrl} 入队异常：{CliDiagnostics.Sanitize(ex.Message)}");
            }
        }
    }

    private async Task<TelegramCollectionRunResult> EnqueueAsync(
        TelegramCollectionSchedule schedule,
        string triggerSource,
        CancellationToken cancellationToken)
    {
        var run = Queue(schedule, triggerSource, cancellationToken);
        // 页面切换只取消调用方等待，不取消已经进入后台队列的采集。
        return await run.Completion.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private QueuedTelegramCollectionRun Queue(
        TelegramCollectionSchedule schedule,
        string triggerSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueuedTelegramCollectionRun? run;
        var duplicate = false;
        lock (_queueLock)
        {
            _activeRuns.TryGetValue(schedule.Id, out var active);
            run = active ?? _queuedRuns.FirstOrDefault(item => item.ScheduleId == schedule.Id);
            if (run is not null)
            {
                duplicate = true;
            }
            else
            {
                run = new QueuedTelegramCollectionRun(
                    schedule,
                    NormalizeTriggerSource(triggerSource),
                    new CancellationTokenSource());
                _queuedRuns.Enqueue(run);
                _queuedScheduleIds.Add(schedule.Id);
            }
        }

        if (!duplicate)
        {
            RaiseQueueChanged();
            ReleaseQueueSignal();
            EnsureQueueWorker();
            RefreshExecutionStatus();
        }

        return run;
    }

    private static string NormalizeTriggerSource(string? triggerSource) =>
        triggerSource switch
        {
            "scheduled" => "scheduled",
            "retry" => "retry",
            _ => "manual"
        };

    private void ReleaseQueueSignal()
    {
        try { _queueSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void RaiseQueueChanged()
    {
        var snapshot = QueueSnapshot;
        RaiseSafely(QueueChanged, snapshot,
            ex => _logger.Error($"TG 定时采集队列订阅者异常：{CliDiagnostics.Sanitize(ex.Message)}"));
    }

    private void RefreshExecutionStatus()
    {
        long[] activeIds;
        lock (_queueLock) activeIds = _activeRuns.Keys.ToArray();
        lock (_statusLock)
        {
            _runningScheduleCount = activeIds.Length;
            _activeScheduleIds.Clear();
            foreach (var scheduleId in activeIds) _activeScheduleIds.Add(scheduleId);
        }
    }

    private void EnsureQueueWorker()
    {
        lock (_queueWorkerLock)
        {
            if (_queueWorkerTask is { IsCompleted: false }) return;
            try { _queueWorkerSource?.Dispose(); }
            catch (ObjectDisposedException) { }
            _queueWorkerSource = new CancellationTokenSource();
            var workerSource = _queueWorkerSource;
            var workerTask = Task.Run(() => ProcessQueueAsync(workerSource.Token));
            _queueWorkerTask = workerTask;
            _ = workerTask.ContinueWith(
                _ => ResumeQueueWorkerIfNeeded(workerTask),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 队列执行器异常退出时自动续跑，避免已经入队的任务永久停留在“排队中”。
    /// 正常停止会先清空排队项，因此不会在这里重新拉起执行器。
    /// </summary>
    private void ResumeQueueWorkerIfNeeded(Task workerTask)
    {
        if (workerTask.IsCanceled) return;

        lock (_queueLock)
        {
            if (_activeRuns.Count == 0 && _queuedRuns.Count == 0) return;
        }

        _logger.Warning("TG 定时采集队列执行器意外结束，正在恢复排队任务。");
        ReleaseQueueSignal();
        EnsureQueueWorker();
    }

    private async Task ProcessQueueAsync(CancellationToken workerToken)
    {
        var activeWorkers = new List<Task>();
        try
        {
            while (!workerToken.IsCancellationRequested)
            {
                await _queueSignal.WaitAsync(workerToken).ConfigureAwait(false);

                while (!workerToken.IsCancellationRequested)
                {
                    QueuedTelegramCollectionRun? run;
                    lock (_queueLock)
                    {
                        if (_activeRuns.Count >= _maxConcurrency || _queuedRuns.Count == 0)
                        {
                            run = null;
                        }
                        else
                        {
                            run = _queuedRuns.Dequeue();
                            _queuedScheduleIds.Remove(run.ScheduleId);
                            _activeRuns[run.ScheduleId] = run;
                        }
                    }

                    if (run is null) break;

                    RefreshExecutionStatus();
                    RaiseQueueChanged();
                    activeWorkers.RemoveAll(task => task.IsCompleted);
                    activeWorkers.Add(ExecuteQueuedRunAsync(run));
                }
            }
        }
        catch (OperationCanceledException) when (workerToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Error($"TG 定时采集队列执行器异常：{CliDiagnostics.Sanitize(ex.Message)}");
        }
        finally
        {
            var remainingWorkers = activeWorkers.Where(task => !task.IsCompleted).ToArray();
            if (remainingWorkers.Length > 0)
            {
                try { await Task.WhenAll(remainingWorkers).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.Error($"TG 定时采集活动任务收尾异常：{CliDiagnostics.Sanitize(ex.Message)}");
                }
            }
        }
    }

    private async Task ExecuteQueuedRunAsync(QueuedTelegramCollectionRun run)
    {
        TelegramCollectionRunResult? result = null;
        string status;
        bool succeeded;
        string message;
        try
        {
            result = await ExecuteScheduleAsync(
                run.Schedule,
                run.TriggerSource,
                run.Cancellation.Token).ConfigureAwait(false);
            run.Completion.TrySetResult(result);
            status = string.IsNullOrWhiteSpace(result.Status)
                ? result.Succeeded ? "成功" : "失败"
                : result.Status;
            succeeded = result.Succeeded;
            message = result.Message;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Completion.TrySetCanceled(run.Cancellation.Token);
            status = "已取消";
            succeeded = false;
            message = "采集已取消。";
        }
        catch (Exception ex)
        {
            message = CliDiagnostics.Sanitize(ex.Message);
            _logger.Error($"TG 定时采集队列任务异常：{message}");
            result = new TelegramCollectionRunResult(
                run.ScheduleId,
                false,
                0,
                0,
                0,
                0,
                0,
                message,
                Status: "失败");
            run.Completion.TrySetResult(result);
            await SaveQueuedFailureAsync(run, message).ConfigureAwait(false);
            status = "失败";
            succeeded = false;
        }
        finally
        {
            lock (_queueLock)
            {
                if (_activeRuns.TryGetValue(run.ScheduleId, out var active) && ReferenceEquals(active, run))
                    _activeRuns.Remove(run.ScheduleId);
            }
            run.Cancellation.Dispose();
            RefreshExecutionStatus();
            RaiseQueueChanged();
            ReleaseQueueSignal();
        }

        PublishRunCompleted(run, status, succeeded, message);
    }

    private async Task<TelegramCollectionRunResult> ExecuteScheduleAsync(
        TelegramCollectionSchedule schedule,
        string triggerSource,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var normalizedTrigger = NormalizeTriggerSource(triggerSource);
        var added = 0;
        var skipped = 0;
        var matched = 0;
        var unmatched = 0;
        var failedMedia = 0;
        var autoScheduled = 0;
        var autoScheduleSkipped = 0;
        var pageCount = 0;
        long? storedWatermark = null;
        try
        {
            Report(schedule, "准备", 0, 0, "正在准备采集…");
            // 生产链路统一走 TelegramTransportService；没有注入时才退回旧的网页代理采集（测试替身路径）。
            TelegramTransportContext? transportContext = null;
            string cookieHeader;
            IReadOnlyList<TelegramChannelMessage> rawMessages;
            var providerName = "";
            var contextsByUrl = new Dictionary<string, TelegramTransportContext?>(StringComparer.OrdinalIgnoreCase);
            if (_transport is null)
            {
                var selection = new WebProxySelection(schedule.ProxySelection);
                Report(schedule, "请求代理", 0, 0, "正在请求网页代理并打开频道…");
                var legacyPreview = await _collector.PreviewChannelAsync(
                    schedule.ChannelUrl,
                    schedule.MessageLimit,
                    "",
                    message => RaiseStatus($"{schedule.ChannelName}：{message}"),
                    cancellationToken,
                    selection);
                if (!legacyPreview.Succeeded)
                    throw new InvalidOperationException(legacyPreview.Error);
                cookieHeader = legacyPreview.CookieHeader;
                rawMessages = legacyPreview.Messages;
                providerName = legacyPreview.ProviderName;
                pageCount = 1;
            }
            else
            {
                var selectionValue = TelegramTransportSelector.Parse(schedule.ProxySelection)
                    ?? throw new InvalidOperationException($"定时采集通道“{schedule.ProxySelection}”无法识别，请在内容采集页重新保存该配置。");
                var fetched = await FetchTransportHomePageAsync(
                    schedule,
                    new TelegramTransportSelection(selectionValue),
                    cancellationToken);
                transportContext = fetched.Context
                    ?? throw new InvalidOperationException("采集通道上下文缺失，请重试。");
                cookieHeader = "";
                rawMessages = fetched.Messages;
                providerName = fetched.ProviderName;
                pageCount = fetched.PageCount;
                contextsByUrl = new Dictionary<string, TelegramTransportContext?>(
                    fetched.ContextsByUrl,
                    StringComparer.OrdinalIgnoreCase);
                if (rawMessages.Count > 0)
                    _logger.Info($"TG 定时采集 {schedule.ChannelUrl}：本次通过 {providerName} 读取频道首页，采集到 {rawMessages.Count} 条。");
            }

            var rules = await _store.GetRulesAsync(schedule.Id, cancellationToken);
            // 首次设置“仅采后续新消息”时也先走默认首页采集，再建立水位。
            // 否则首次执行只记录最高消息号，会直接漏掉当前首页里的历史消息。
            var isInitialNewOnlyRun =
                string.Equals(schedule.FirstRunMode, "new-only", StringComparison.OrdinalIgnoreCase) &&
                schedule.LastMessageId == 0;
            // backfill 模式不参考历史水位：每次都重新处理首页最新 20 条，由素材库去重跳过已入库的消息。
            // 这样首页里之前媒体下载失败的消息也会被重新抓取并补齐，不会因水位越过而永久漏采。
            var effectiveLastMessageId = string.Equals(schedule.FirstRunMode, "backfill", StringComparison.OrdinalIgnoreCase)
                ? 0
                : schedule.LastMessageId;

            var messages = PrepareScheduledMessages(
                rawMessages,
                effectiveLastMessageId,
                schedule.MessageLimit,
                schedule.IncludeKeyword,
                schedule.Keyword,
                out var watermarkCandidate);

            Report(schedule, "解析完成", messages.Count, messages.Count, $"频道页面解析完成，本次待处理 {messages.Count} 条消息，使用 {providerName}。");

            var hasProcessingFailure = false;
            var failedMessageNumbers = new HashSet<long>();
            var autoScheduleReasons = new List<string>();
            void MarkMessageFailed(string messageUrl)
            {
                hasProcessingFailure = true;
                if (TelegramCollectorService.TryReadMessageNumber(messageUrl, out var number) &&
                    number > effectiveLastMessageId)
                {
                    failedMessageNumbers.Add(number);
                }
            }

            for (var index = 0; index < messages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = messages[index];
                Report(schedule, "写入素材", index, messages.Count, $"正在处理第 {index + 1}/{messages.Count} 条，下载媒体并写入素材库…");
                var existingMaterial = await _library.FindMaterialBySourceAsync("TG采集", message.Url, cancellationToken);
                if (existingMaterial is not null && existingMaterial.FailedMedia is { Count: 0 })
                {
                    skipped++;
                    continue;
                }
                var target = TelegramDistributionRuleMatcher.Match(message, rules);
                if (target is null) unmatched++; else matched++;
                TelegramMaterializationResult materialized;
                try
                {
                    var messageContext = contextsByUrl.TryGetValue(message.Url, out var pageContext)
                        ? pageContext
                        : transportContext;
                    materialized = transportContext is null
                        ? await _materializer.MaterializeAsync(message, cookieHeader, target, cancellationToken)
                        : await _materializer.MaterializeAsync(message, messageContext ?? transportContext, target, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    MarkMessageFailed(message.Url);
                    skipped++;
                    var error = CliDiagnostics.Sanitize(ex.Message);
                    _logger.Warning($"TG 定时采集单条消息失败：{error}");
                    Report(schedule, "写入素材", index + 1, messages.Count, $"第 {index + 1}/{messages.Count} 条处理失败，已继续处理后续消息：{error}");
                    continue;
                }

                failedMedia += materialized.FailedMediaCount;
                if (materialized.SkippedDuplicate || materialized.SkippedEmpty)
                {
                    skipped++;
                    if (!materialized.SkippedDuplicate)
                        MarkMessageFailed(message.Url);
                }
                else if (materialized.Succeeded)
                {
                    added++;
                    if (materialized.FailedMediaCount > 0)
                    {
                        MarkMessageFailed(message.Url);
                        autoScheduleSkipped++;
                        autoScheduleReasons.Add("媒体部分失败");
                    }
                    else if (schedule.AutoScheduleEnabled &&
                        autoScheduled < schedule.AutoScheduleMaxPerRun &&
                        _autoSchedule is not null &&
                        target is not null &&
                        materialized.MaterialId is long materialId)
                    {
                        try
                        {
                            var scheduleResult = await _autoSchedule.TryQueueAsync(
                                materialId,
                                target.GuildId,
                                target.ChannelId,
                                schedule.AutoScheduleDelayMinutes,
                                cancellationToken);
                            if (scheduleResult.Scheduled)
                            {
                                autoScheduled++;
                            }
                            else
                            {
                                autoScheduleSkipped++;
                                autoScheduleReasons.Add("自动排期未接受");
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            autoScheduleSkipped++;
                            autoScheduleReasons.Add("自动排期异常");
                            _logger.Warning(
                                $"TG 素材自动排期失败：{CliDiagnostics.Sanitize(ex.Message)}");
                        }
                    }
                    else if (schedule.AutoScheduleEnabled)
                    {
                        autoScheduleSkipped++;
                        autoScheduleReasons.Add(
                            autoScheduled >= schedule.AutoScheduleMaxPerRun ? "达到单次排期上限" : "未命中分发规则");
                    }
                }
                else
                {
                    skipped++;
                    MarkMessageFailed(message.Url);
                }

                var itemResult = materialized.Succeeded
                    ? materialized.FailedMediaCount > 0
                        ? $"第 {index + 1}/{messages.Count} 条已部分入库，{materialized.FailedMediaCount} 个媒体失败，未自动排期。"
                        : $"第 {index + 1}/{messages.Count} 条已入库。"
                    : $"第 {index + 1}/{messages.Count} 条已跳过：{materialized.Error}";
                Report(schedule, "写入素材", index + 1, messages.Count, itemResult);
            }

            var autoScheduleDetail = schedule.AutoScheduleEnabled
                ? $"，自动排期 {autoScheduled} 条，未排期 {autoScheduleSkipped} 条"
                : "";
            var firstRunPrefix = isInitialNewOnlyRun
                ? "首次启用仅采新消息，已先按默认逻辑处理当前首页并建立水位；"
                : "";
            var detail = $"{firstRunPrefix}新增 {added} 条，跳过 {skipped} 条，命中规则 {matched} 条，未命中 {unmatched} 条，媒体失败 {failedMedia} 个{autoScheduleDetail}";
            var partial = hasProcessingFailure || failedMedia > 0;
            storedWatermark = partial
                ? ResolveSafeWatermark(
                    rawMessages,
                    effectiveLastMessageId,
                    failedMessageNumbers)
                : watermarkCandidate;
            // 成功路径也必须把本次新水位带到历史与结果中，而不是继续显示旧水位。
            await _store.UpdateSuccessAsync(
                schedule.Id,
                started,
                added,
                partial ? "部分成功" : "成功",
                storedWatermark?.ToString(),
                cancellationToken);
            if (partial)
                _logger.Warning($"TG 定时采集 {schedule.ChannelUrl} 部分成功：{detail}");
            else
                _logger.Info($"TG 定时采集 {schedule.ChannelUrl}：{detail}");
            Report(schedule, "完成", messages.Count, messages.Count, $"{(partial ? "采集部分成功" : "采集完成")}，{detail}。");
            await TrySaveRunHistoryAsync(
                schedule,
                normalizedTrigger,
                started,
                partial ? "部分成功" : "成功",
                added,
                skipped,
                matched,
                unmatched,
                failedMedia,
                autoScheduled,
                autoScheduleSkipped,
                pageCount,
                storedWatermark ?? schedule.LastMessageId,
                detail,
                string.Join("；", autoScheduleReasons.Distinct(StringComparer.Ordinal)));
            return new(
                schedule.Id,
                true,
                added,
                skipped,
                matched,
                unmatched,
                failedMedia,
                detail,
                autoScheduled,
                autoScheduleSkipped,
                partial ? "部分成功" : "成功");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var detail = $"已新增 {added} 条，已跳过 {skipped} 条";
            try
            {
                // 取消令牌已经触发，结果写回必须使用独立令牌，否则状态无法落库。
                await _store.UpdateCancelledAsync(
                    schedule.Id,
                    started,
                    added,
                    detail,
                    CancellationToken.None);
            }
            catch (Exception updateEx)
            {
                _logger.Error($"保存 TG 定时采集取消状态失败：{CliDiagnostics.Sanitize(updateEx.Message)}");
            }
            await TrySaveRunHistoryAsync(
                schedule,
                normalizedTrigger,
                started,
                "已取消",
                added,
                skipped,
                matched,
                unmatched,
                failedMedia,
                autoScheduled,
                autoScheduleSkipped,
                pageCount,
                storedWatermark ?? schedule.LastMessageId,
                detail);
            // 用户取消或执行器停止时，仍然发一次终态，让界面能复位行状态。
            Report(schedule, "已取消", 0, 0, $"采集已取消（{detail}）。");
            throw;
        }
        catch (Exception ex)
        {
            var error = CliDiagnostics.Sanitize(ex.Message);
            var detail = $"{error}（已新增 {added} 条，已跳过 {skipped} 条）";
            var automaticRetry = normalizedTrigger is "scheduled" or "retry";
            try
            {
                await _store.UpdateFailureAsync(
                    schedule.Id,
                    started,
                    added,
                    detail,
                    automaticRetry: automaticRetry,
                    cancellationToken: CancellationToken.None);
            }
            catch (Exception updateEx) { _logger.Error($"保存 TG 定时采集失败状态失败：{CliDiagnostics.Sanitize(updateEx.Message)}"); }
            await TrySaveRunHistoryAsync(
                schedule,
                normalizedTrigger,
                started,
                "失败",
                added,
                skipped,
                matched,
                unmatched,
                failedMedia,
                autoScheduled,
                autoScheduleSkipped,
                pageCount,
                storedWatermark ?? schedule.LastMessageId,
                detail);
            _logger.Warning($"TG 定时采集 {schedule.ChannelUrl} 失败：{detail}");
            Report(schedule, "失败", 0, 0, $"采集未完成，请检查网络、代理或节点后重试：{detail}");
            return new(
                schedule.Id,
                false,
                added,
                skipped,
                matched,
                unmatched,
                failedMedia,
                detail,
                Status: "失败");
        }
    }

    private async Task<TelegramHomePageFetchResult> FetchTransportHomePageAsync(
        TelegramCollectionSchedule schedule,
        TelegramTransportSelection selection,
        CancellationToken cancellationToken)
    {
        if (_transport is null)
            throw new InvalidOperationException("TG 传输通道未初始化。");

        var limit = TelegramCollectorService.NormalizeMessageLimit(schedule.MessageLimit);
        Report(schedule, "读取频道", 0, limit, "正在读取频道首页…");
        var page = await _transport.FetchChannelPageAsync(
            schedule.ChannelUrl,
            selection,
            beforeMessageId: null,
            message => RaiseStatus($"{schedule.ChannelName}：{message}"),
            cancellationToken).ConfigureAwait(false);
        if (!page.Succeeded)
            throw new InvalidOperationException(page.Error);

        var messages = new List<TelegramChannelMessage>();
        var contextByUrl = new Dictionary<string, TelegramTransportContext?>(StringComparer.OrdinalIgnoreCase);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in TelegramCollectorService.OrderByLatest(page.Messages, item => item))
        {
            if (messages.Count >= limit) break;
            if (message.Url.Length > 0 && !seenUrls.Add(message.Url)) continue;
            messages.Add(message);
            if (message.Url.Length > 0) contextByUrl[message.Url] = page.Context;
        }

        var context = page.Context
            ?? (messages.Count > 0 ? contextByUrl.Values.FirstOrDefault(value => value is not null) : null);
        return new TelegramHomePageFetchResult(
            messages,
            context,
            contextByUrl,
            page.ProviderName,
            PageCount: 1);
    }

    private async Task TrySaveRunHistoryAsync(
        TelegramCollectionSchedule schedule,
        string triggerSource,
        DateTimeOffset started,
        string status,
        int added,
        int skipped,
        int matched,
        int unmatched,
        int failedMedia,
        int autoScheduled,
        int autoScheduleSkipped,
        int pageCount,
        long lastMessageId,
        string detail,
        string autoScheduleReasons = "")
    {
        try
        {
            await _store.SaveRunHistoryAsync(new TelegramCollectionRunHistory(
                0,
                schedule.Id,
                schedule.ChannelUrl,
                schedule.ChannelName,
                NormalizeTriggerSource(triggerSource),
                started,
                DateTimeOffset.Now,
                status,
                added,
                skipped,
                matched,
                unmatched,
                failedMedia,
                autoScheduled,
                autoScheduleSkipped,
                autoScheduleReasons,
                pageCount,
                Math.Max(0, lastMessageId),
                detail), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 历史只是可观测性数据，不能反噬已经完成的采集结果。
            _logger.Error($"保存 TG 定时采集历史失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    internal static long? ResolveSafeWatermark(
        IReadOnlyList<TelegramChannelMessage> messages,
        long lastMessageId,
        IReadOnlySet<long> failedMessageNumbers)
    {
        var scannedNumbers = TelegramCollectorService
            .OrderByLatest(messages, message => message)
            .Select(message =>
                TelegramCollectorService.TryReadMessageNumber(message.Url, out var number) ? number : 0)
            .Where(number => number > lastMessageId)
            .ToArray();
        if (scannedNumbers.Length == 0) return null;

        var highestScanned = scannedNumbers.Max();
        var earliestFailure = failedMessageNumbers
            .Where(number => number > lastMessageId && number <= highestScanned)
            .DefaultIfEmpty(0)
            .Min();
        // 失败消息及其之前尚未确认的消息必须留给下次采集。
        return earliestFailure > 0
            ? Math.Max(lastMessageId, earliestFailure - 1)
            : highestScanned;
    }

    internal static IReadOnlyList<TelegramChannelMessage> PrepareScheduledMessages(
        IReadOnlyList<TelegramChannelMessage> messages,
        long lastMessageId,
        int limit,
        string? inclusionKeywords,
        string? exclusionKeywords,
        out long? watermarkCandidate)
    {
        var included = TelegramCollectorService.SplitKeywords(inclusionKeywords);
        var excluded = TelegramCollectorService.SplitKeywords(exclusionKeywords);
        var take = TelegramCollectorService.NormalizeMessageLimit(limit);
        var ordered = TelegramCollectorService
            .OrderByLatest(messages, message => message)
            .Take(take)
            .Select(message => new
            {
                Message = message,
                Number = TelegramCollectorService.TryReadMessageNumber(message.Url, out var number) ? number : 0
            })
            .ToArray();
        var result = new List<TelegramChannelMessage>();
        var highestNumber = ordered
            .Select(item => item.Number)
            .Where(number => number > lastMessageId)
            .DefaultIfEmpty(0)
            .Max();
        watermarkCandidate = highestNumber > 0 ? highestNumber : null;
        foreach (var item in ordered)
        {
            if (item.Number > 0 && item.Number <= lastMessageId) continue;
            var includedMatch = included.Count == 0 || included.Any(keyword =>
                item.Message.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.Message.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (!includedMatch) continue;
            var excludedMatch = excluded.Any(keyword =>
                item.Message.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.Message.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (!excludedMatch)
                result.Add(item.Message);
        }
        return result;
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

    private async Task CompleteQueuedCancellationAsync(QueuedTelegramCollectionRun run)
    {
        try
        {
            const string message = "排队任务已取消，未开始采集。";
            try
            {
                await _store.UpdateCancelledAsync(
                    run.ScheduleId,
                    run.EnqueuedAt,
                    0,
                    message,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error($"保存 TG 排队取消失败状态失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }

            try
            {
                await _store.SaveRunHistoryAsync(new TelegramCollectionRunHistory(
                    0,
                    run.ScheduleId,
                    run.Schedule.ChannelUrl,
                    run.Schedule.ChannelName,
                    run.TriggerSource,
                    run.EnqueuedAt,
                    DateTimeOffset.Now,
                    "已取消",
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    "",
                    0,
                    run.Schedule.LastMessageId,
                    message), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error($"保存 TG 排队取消历史失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }

            PublishRunCompleted(run, "已取消", succeeded: false, message);
        }
        finally
        {
            try { run.Cancellation.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task SaveQueuedFailureAsync(QueuedTelegramCollectionRun run, string error)
    {
        try
        {
            try
            {
                await _store.UpdateFailureAsync(
                    run.ScheduleId,
                    run.EnqueuedAt,
                    0,
                    error,
                    automaticRetry: NormalizeTriggerSource(run.TriggerSource) is "scheduled" or "retry",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error($"保存 TG 队列失败状态失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }

            await _store.SaveRunHistoryAsync(new TelegramCollectionRunHistory(
                0,
                run.ScheduleId,
                run.Schedule.ChannelUrl,
                run.Schedule.ChannelName,
                run.TriggerSource,
                run.EnqueuedAt,
                DateTimeOffset.Now,
                "失败",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "",
                0,
                run.Schedule.LastMessageId,
                error), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"保存 TG 队列失败历史失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private void TrackTerminalTask(Task task)
    {
        lock (_terminalTaskLock) _pendingTerminalTasks.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_terminalTaskLock) _pendingTerminalTasks.Remove(completed);
            if (completed.IsFaulted)
            {
                _logger.Error(
                    $"TG 排队终态保存异常：{CliDiagnostics.Sanitize(completed.Exception?.GetBaseException().Message ?? "未知错误")}");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
    string Message,
    int AutoScheduledCount = 0,
    int AutoScheduleSkippedCount = 0,
    string? Status = null);
public sealed record TelegramCollectionRunCompleted(
    long ScheduleId,
    string ChannelName,
    string TriggerSource,
    string Status,
    bool Succeeded,
    string Message);
public sealed record TelegramCollectionProgress(long ScheduleId, string ChannelName, string Stage, int Current, int Total, string Message);

internal sealed record TelegramHomePageFetchResult(
    IReadOnlyList<TelegramChannelMessage> Messages,
    TelegramTransportContext? Context,
    IReadOnlyDictionary<string, TelegramTransportContext?> ContextsByUrl,
    string ProviderName,
    int PageCount);

/// <summary>页面只读的队列快照。Position=0 表示执行中，排队项从 1 开始。</summary>
public sealed record TelegramCollectionQueueItem(
    long ScheduleId,
    string ChannelName,
    string TriggerSource,
    DateTimeOffset EnqueuedAt,
    int Position,
    bool IsExecuting);

internal sealed class QueuedTelegramCollectionRun
{
    public QueuedTelegramCollectionRun(
        TelegramCollectionSchedule schedule,
        string triggerSource,
        CancellationTokenSource cancellation)
    {
        Schedule = schedule;
        TriggerSource = triggerSource;
        Cancellation = cancellation;
        EnqueuedAt = DateTimeOffset.Now;
        Completion = new TaskCompletionSource<TelegramCollectionRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public TelegramCollectionSchedule Schedule { get; }
    public long ScheduleId => Schedule.Id;
    public string TriggerSource { get; }
    public CancellationTokenSource Cancellation { get; }
    public TaskCompletionSource<TelegramCollectionRunResult> Completion { get; }
    public DateTimeOffset EnqueuedAt { get; }

    public TelegramCollectionQueueItem ToItem(int position, bool isExecuting) =>
        new(ScheduleId, Schedule.ChannelName, TriggerSource, EnqueuedAt, position, isExecuting);
}

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
