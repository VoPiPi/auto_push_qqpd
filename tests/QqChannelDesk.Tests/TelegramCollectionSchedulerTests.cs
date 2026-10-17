using QqChannelDesk.Services;
using System.Diagnostics;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramCollectionSchedulerTests
{
    [Fact]
    public void SetEnabledPublishesPausedStateWithoutStoppingLoop()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var scheduler = CreateScheduler(databasePath);

        TelegramCollectionSchedulerStatus? observed = null;
        scheduler.StateChanged += status => observed = status;

        scheduler.SetEnabled(false);

        Assert.False(scheduler.IsEnabled);
        Assert.NotNull(observed);
        Assert.True(observed!.Paused);
        Assert.False(observed.Enabled);
        Assert.False(observed.Running);

        scheduler.SetEnabled(true);
        Assert.True(scheduler.IsEnabled);
        Assert.False(scheduler.Status.Paused);
    }

    [Fact]
    public async Task DisabledSchedulerStillRefreshesHeartbeat()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var scheduler = CreateScheduler(databasePath);
        scheduler.SetEnabled(false);

        var heartbeat = new TaskCompletionSource<TelegramCollectionSchedulerStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.StateChanged += status =>
        {
            if (status.Running && !status.Enabled && status.LastHeartbeatAt is not null)
                heartbeat.TrySetResult(status);
        };

        scheduler.Start();
        try
        {
            var status = await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(status.Paused);
            Assert.NotNull(status.NextCheckAt);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task CancelCurrentRunPersistsCancelledTerminalState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var provider = new BlockingProxyProvider();
        var scheduler = new TelegramCollectionScheduler(
            store,
            library,
            new TelegramCollectorService(new WebProxyProviderRegistry([provider])),
            logger: new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-log-{Guid.NewGuid():N}")));
        TelegramCollectionProgress? terminal = null;
        scheduler.ProgressChanged += progress =>
        {
            if (progress.ScheduleId == scheduleId) terminal = progress;
        };

        var run = scheduler.RunNowAsync(scheduleId);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduler.CancelCurrentRun());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal("已取消", saved.LastStatus);
        Assert.Equal("已取消", terminal?.Stage);
        Assert.Contains("已新增 0 条", saved.LastError);
    }

    [Fact]
    public void ThrowingStatusSubscriberDoesNotBlockRemainingSubscribers()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var scheduler = CreateScheduler(databasePath);
        var received = new List<string>();
        scheduler.StatusChanged += _ => throw new InvalidOperationException("订阅者异常");
        scheduler.StatusChanged += message => received.Add(message);

        scheduler.SetEnabled(false);

        Assert.Contains(received, message => message.Contains("已关闭", StringComparison.Ordinal));
    }

    [Fact]
    public void ThrowingStateSubscriberDoesNotBlockRemainingSubscribers()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var scheduler = CreateScheduler(databasePath);
        TelegramCollectionSchedulerStatus? observed = null;
        scheduler.StateChanged += _ => throw new InvalidOperationException("订阅者异常");
        scheduler.StateChanged += status => observed = status;

        scheduler.SetEnabled(false);

        Assert.NotNull(observed);
        Assert.False(observed!.Enabled);
    }

    [Fact]
    public void ThrowingProgressSubscriberDoesNotBlockRemainingSubscribers()
    {
        var received = new List<TelegramCollectionProgress>();
        var failures = new List<Exception>();
        Action<TelegramCollectionProgress>? handlers = null;
        handlers += _ => throw new InvalidOperationException("订阅者异常");
        handlers += progress => received.Add(progress);

        TelegramCollectionScheduler.RaiseSafely(
            handlers,
            new TelegramCollectionProgress(7, "频道", "准备", 0, 0, "正在准备采集…"),
            failures.Add);

        Assert.Equal(7, Assert.Single(received).ScheduleId);
        Assert.Single(failures);
    }

    [Fact]
    public async Task ConcurrentStartOnlyKeepsOneLoop()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var scheduler = CreateScheduler(databasePath);

        Parallel.For(0, 8, _ => scheduler.Start());

        Assert.True(scheduler.IsRunning);
        Assert.True(await scheduler.StopAsync(TimeSpan.FromSeconds(20)));
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public async Task StopTimeoutDoesNotClearLoopState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var scheduler = CreateScheduler(databasePath);

        // 先启动循环，再用一个零超时 Stop 走“停止未确认/已停止”两条分支；
        // 无论走哪条，都不能破坏后续停止与重新启动所需的状态。
        scheduler.Start();
        var stopped = await scheduler.StopAsync(TimeSpan.Zero);

        // 超时返回 false 时必须保留循环状态，不能把仍在运行的循环标记成已停止。
        if (!stopped) Assert.True(scheduler.IsRunning);

        // 状态必须仍然可用，第二次停止应能正常清理，不能抛 ObjectDisposedException。
        Assert.True(await scheduler.StopAsync(TimeSpan.FromSeconds(20)));
        Assert.False(scheduler.IsRunning);

        // 停止后必须还能重新启动，验证 CTS 与代际状态没有被破坏。
        scheduler.Start();
        Assert.True(scheduler.IsRunning);
        Assert.True(await scheduler.StopAsync(TimeSpan.FromSeconds(20)));
        Assert.False(scheduler.IsRunning);
    }

    private static TelegramCollectionScheduler CreateScheduler(string databasePath)
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        return new TelegramCollectionScheduler(
            store,
            library,
            logger: new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-log-{Guid.NewGuid():N}")));
    }

    private sealed class BlockingProxyProvider : IWebProxyProvider
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "blocking";
        public string DisplayName => "阻塞代理";
        public string HomepageUrl => "https://example.com/";
        public bool SupportsSimulation => true;
        public Task Started => _started.Task;

        public async Task<ProxyPageResult> FetchAsync(
            string targetUrl,
            string? waitSelector = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}
