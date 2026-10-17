using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class PublishSchedulerTests
{
    [Fact]
    public async Task SetEnabledUpdatesStatusWithoutStoppingLoop()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-{Guid.NewGuid():N}.db");
        var accountContext = new AccountContext();
        accountContext.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new ContentLibraryStore(databasePath, accountContext);
        var settings = new SystemSettingsStore(databasePath);
        var scheduler = new PublishScheduler(
            store,
            new ControlledPublisher(),
            settings,
            () => Task.FromResult(ScheduleEnvironment.Available),
            new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-log-{Guid.NewGuid():N}")));

        scheduler.Start(runOverdueAtStartup: false);
        try
        {
            scheduler.SetEnabled(false);

            var status = scheduler.Status;
            Assert.False(scheduler.IsEnabled);
            Assert.False(status.Enabled);
            Assert.True(status.Running);
            Assert.Contains("自动执行已关闭", status.Message, StringComparison.Ordinal);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task DisabledAutoExecuteStillPublishesHeartbeat()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-{Guid.NewGuid():N}.db");
        var accountContext = new AccountContext();
        accountContext.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new ContentLibraryStore(databasePath, accountContext);
        var settings = new SystemSettingsStore(databasePath);
        await settings.SaveAsync(new AppSettings(AutoExecuteSchedules: false));
        var scheduler = new PublishScheduler(
            store,
            new ControlledPublisher(),
            settings,
            () => Task.FromResult(ScheduleEnvironment.Available),
            new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-log-{Guid.NewGuid():N}")));

        var disabledStatus = new TaskCompletionSource<PublishSchedulerStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.StatusChanged += status =>
        {
            if (status.Running && !status.Enabled) disabledStatus.TrySetResult(status);
        };

        scheduler.Start(runOverdueAtStartup: true);
        try
        {
            var status = await disabledStatus.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(status.Enabled);
            Assert.True(status.Running);
            Assert.NotNull(status.LastHeartbeatAt);
            Assert.NotNull(status.NextCheckAt);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task SuccessfulPublishCompletedDuringStopIsNotMislabeled()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-{Guid.NewGuid():N}.db");
        var accountContext = new AccountContext();
        accountContext.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new ContentLibraryStore(databasePath, accountContext);
        var settings = new SystemSettingsStore(databasePath);
        var publisher = new ControlledPublisher();
        var scheduler = new PublishScheduler(
            store,
            publisher,
            settings,
            () => Task.FromResult(ScheduleEnvironment.Available),
            new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-log-{Guid.NewGuid():N}")));

        var materialId = await store.SaveMaterialAsync(new MaterialDraft(
            null, "标题", "text", "正文", "g1", "频道", "c1", "版块",
            DateTimeOffset.Now.AddSeconds(2), "queue", "", []));

        scheduler.Start(runOverdueAtStartup: false);
        try
        {
            await publisher.Started.WaitAsync(TimeSpan.FromSeconds(20));

            // Stop cancels the scheduler token while the publish is still in flight.
            // The publisher ignores cancellation and still reports success afterwards.
            var stopTask = scheduler.StopAsync();
            await Task.Delay(200);
            publisher.Release(new PublishExecutionOutcome(
                new PublishResult(true, PublishErrorCategory.None, "ok", "https://example.com/post", "post-1"),
                42));
            Assert.True(await stopTask);
        }
        finally
        {
            await scheduler.StopAsync();
        }

        var execution = Assert.Single(await store.GetScheduleExecutionsAsync());
        Assert.Equal(materialId, execution.MaterialId);
        Assert.Equal(ScheduleExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(42, execution.PublishRecordId);
    }

    [Fact]
    public async Task ConcurrentStartOnlyCreatesOneLoopAndStopIsIdempotent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-{Guid.NewGuid():N}.db");
        var accountContext = new AccountContext();
        accountContext.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new ContentLibraryStore(databasePath, accountContext);
        var settings = new SystemSettingsStore(databasePath);
        await settings.SaveAsync(new AppSettings(AutoExecuteSchedules: false));
        var scheduler = new PublishScheduler(
            store,
            new ControlledPublisher(),
            settings,
            () => Task.FromResult(ScheduleEnvironment.Available),
            new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-scheduler-log-{Guid.NewGuid():N}")));

        // Start 同步完成状态切换，随后并发启动只应有一次生效。
        scheduler.Start(runOverdueAtStartup: false);
        Assert.True(scheduler.Status.Running);
        Parallel.For(0, 8, _ => scheduler.Start(runOverdueAtStartup: false));

        Assert.True(scheduler.Status.Running);

        var stopResults = await Task.WhenAll(
                scheduler.StopAsync(),
                scheduler.StopAsync())
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(stopResults, Assert.True);
        Assert.False(scheduler.Status.Running);
        Assert.False(scheduler.Status.Paused);
    }

    private sealed class ControlledPublisher : IPublishExecutor
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PublishExecutionOutcome> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release(PublishExecutionOutcome outcome) => _release.TrySetResult(outcome);

        public async Task<PublishExecutionOutcome> ExecuteAsync(
            PublishRequest request,
            string guildName,
            string channelName,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            // Deliberately ignores the cancellation token to simulate a CLI publish
            // that finishes after the scheduler has been asked to stop.
            return await _release.Task;
        }
    }
}
