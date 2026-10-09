using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class PublishSchedulerTests
{
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