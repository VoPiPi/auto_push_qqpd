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
    public async Task ActiveScheduleIdsTracksRunUntilCancelled()
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

        Assert.Empty(scheduler.ActiveScheduleIds);

        var run = scheduler.RunNowAsync(scheduleId);
        await provider.Started.WaitAsync(TimeSpan.FromSeconds(5));
        // 采集进行中必须能被页面查到，否则重新进入页面时无法恢复“取消采集”。
        Assert.Contains(scheduleId, scheduler.ActiveScheduleIds);

        Assert.True(scheduler.CancelCurrentRun());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // 终态后必须清空，否则页面会把已经结束的采集当成仍在运行。
        Assert.Empty(scheduler.ActiveScheduleIds);
    }

    [Fact]
    public async Task FailedRunClearsActiveScheduleIds()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var scheduler = new TelegramCollectionScheduler(
            store,
            library,
            new TelegramCollectorService(new WebProxyProviderRegistry([new FailingProxyProvider()])),
            logger: new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-log-{Guid.NewGuid():N}")));
        TelegramCollectionProgress? terminal = null;
        scheduler.ProgressChanged += progress =>
        {
            if (progress.ScheduleId == scheduleId) terminal = progress;
        };

        var result = await scheduler.RunNowAsync(scheduleId);

        Assert.False(result.Succeeded);
        Assert.Equal("失败", terminal?.Stage);
        Assert.Empty(scheduler.ActiveScheduleIds);
    }

    [Fact]
    public async Task NewOnlyFirstRunImportsHomePageBeforeAdvancingWatermark()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "new-only"));
        var messages = new[]
        {
            Message("https://t.me/channel/100", "", "历史 100"),
            Message("https://t.me/channel/101", "", "历史 101")
        };
        var scheduler = CreateScheduler(databasePath, store, library, new StubTelegramTransport(messages));

        var result = await scheduler.RunNowAsync(scheduleId);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, (await library.GetMaterialsAsync()).Count);
        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal(101, saved.LastMessageId);
        Assert.Equal("成功", saved.LastStatus);
        Assert.Contains("已先按默认逻辑处理当前首页并建立水位", result.Message);
    }

    [Fact]
    public async Task NewOnlySubsequentRunsCollectOnlyMessagesAboveWatermark()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "new-only"));
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var firstHomePage = new[]
        {
            Message("https://t.me/channel/100", "", "历史 100", firstPublishedAt),
            Message("https://t.me/channel/101", "", "历史 101", firstPublishedAt.AddMinutes(1))
        };
        var secondHomePage = new[]
        {
            Message("https://t.me/channel/101", "", "历史 101", firstPublishedAt.AddMinutes(1)),
            Message("https://t.me/channel/102", "", "新消息 102", firstPublishedAt.AddMinutes(2))
        };
        var scheduler = CreateScheduler(
            databasePath,
            store,
            library,
            new SequencedTelegramTransport(firstHomePage, secondHomePage));

        var firstResult = await scheduler.RunNowAsync(scheduleId);
        var secondResult = await scheduler.RunNowAsync(scheduleId);

        Assert.True(firstResult.Succeeded);
        Assert.Equal(2, firstResult.AddedCount);
        Assert.True(secondResult.Succeeded);
        Assert.Equal(1, secondResult.AddedCount);
        var materials = await library.GetMaterialsAsync();
        Assert.Equal(3, materials.Count);
        Assert.Contains(materials, material => material.Content == "新消息 102");
        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal(102, saved.LastMessageId);
    }

    [Fact]
    public async Task BackfillModeAlwaysProcessesAllHomePageMessages()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var firstHomePage = new[]
        {
            Message("https://t.me/channel/100", "", "历史 100", firstPublishedAt),
            Message("https://t.me/channel/101", "", "历史 101", firstPublishedAt.AddMinutes(1))
        };
        var secondHomePage = new[]
        {
            Message("https://t.me/channel/101", "", "历史 101", firstPublishedAt.AddMinutes(1)),
            Message("https://t.me/channel/102", "", "新消息 102", firstPublishedAt.AddMinutes(2))
        };
        var scheduler = CreateScheduler(
            databasePath,
            store,
            library,
            new SequencedTelegramTransport(firstHomePage, secondHomePage));

        var firstResult = await scheduler.RunNowAsync(scheduleId);
        var secondResult = await scheduler.RunNowAsync(scheduleId);

        Assert.True(firstResult.Succeeded);
        Assert.Equal(2, firstResult.AddedCount);
        Assert.True(secondResult.Succeeded);
        Assert.Equal(1, secondResult.AddedCount);
        Assert.Equal(1, secondResult.SkippedCount);
        var materials = await library.GetMaterialsAsync();
        Assert.Equal(3, materials.Count);
        Assert.Contains(materials, material => material.Content == "新消息 102");
        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal(102, saved.LastMessageId);
    }

    [Fact]
    public async Task BackfillFirstRunImportsHistoryAndAdvancesWatermark()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var messages = new[]
        {
            Message("https://t.me/channel/10", "", "正文 10"),
            Message("https://t.me/channel/11", "", "正文 11")
        };
        var scheduler = CreateScheduler(databasePath, store, library, new StubTelegramTransport(messages));

        var result = await scheduler.RunNowAsync(scheduleId);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.AddedCount);
        Assert.Equal(2, (await library.GetMaterialsAsync()).Count);
        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal(11, saved.LastMessageId);
        Assert.Equal(0, saved.RetryAttempt);
        Assert.Null(saved.NextRetryAt);
    }

    [Fact]
    public async Task FailedMessageDoesNotAbortBatchOrTriggerFastRetry()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "channel", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var messages = new[]
        {
            Message(
                "https://t.me/channel/30",
                "",
                "",
                new TelegramMediaRef("https://example.com/missing.jpg", "https://example.com/missing.jpg", "image")),
            Message("https://t.me/channel/31", "", "后续消息仍然成功")
        };
        var scheduler = CreateScheduler(databasePath, store, library, new StubTelegramTransport(messages));

        var result = await scheduler.RunNowAsync(scheduleId);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, result.FailedMediaCount);
        var material = Assert.Single(await library.GetMaterialsAsync());
        Assert.Equal("后续消息仍然成功", material.Content);
        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal("部分成功", saved.LastStatus);
        // 失败消息仍是下一次采集的起点，水位不能越过它，否则该消息会被永久跳过。
        Assert.Equal(29, saved.LastMessageId);
        Assert.Equal(0, saved.RetryAttempt);
        Assert.Null(saved.NextRetryAt);
    }

    [Fact]
    public async Task DifferentSchedulesExecuteThroughOneSerialQueue()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var firstScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel-one", "channel-one", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var secondScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel-two", "channel-two", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var transport = new BlockingTelegramTransport();
        var scheduler = CreateScheduler(databasePath, store, library, transport);

        var firstRun = scheduler.RunNowAsync(firstScheduleId);
        try
        {
            await transport.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var secondRun = scheduler.RunNowAsync(secondScheduleId);
            await WaitUntilAsync(() => scheduler.QueueSnapshot.Count == 2, TimeSpan.FromSeconds(5));

            var snapshot = scheduler.QueueSnapshot;
            Assert.Single(snapshot, item => item.IsExecuting);
            Assert.Single(snapshot, item => !item.IsExecuting);
            Assert.Equal(1, transport.CallCount);

            transport.ReleaseFirst();
            var results = await Task.WhenAll(firstRun, secondRun);

            Assert.All(results, result => Assert.True(result.Succeeded));
            Assert.Equal(2, transport.CallCount);
            Assert.Equal(1, transport.MaximumConcurrentCalls);
        }
        finally
        {
            transport.ReleaseFirst();
        }
    }

    [Fact]
    public async Task ConfiguredConcurrencyAllowsTwoSchedulesToRunInParallel()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var firstScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/parallel-one", "parallel-one", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var secondScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/parallel-two", "parallel-two", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var transport = new BlockingTelegramTransport();
        var scheduler = CreateScheduler(databasePath, store, library, transport);
        scheduler.SetMaxConcurrency(2);

        var firstRun = scheduler.RunNowAsync(firstScheduleId);
        try
        {
            await transport.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var secondRun = scheduler.RunNowAsync(secondScheduleId);
            await WaitUntilAsync(() => transport.CallCount >= 2, TimeSpan.FromSeconds(5));

            Assert.Equal(2, transport.MaximumConcurrentCalls);

            transport.ReleaseFirst();
            var results = await Task.WhenAll(firstRun, secondRun);
            Assert.All(results, result => Assert.True(result.Succeeded));
        }
        finally
        {
            transport.ReleaseFirst();
        }
    }

    [Fact]
    public async Task SuccessfulRunRaisesRunCompletedExactlyOnce()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/completed-once", "completed-once", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var scheduler = CreateScheduler(
            databasePath,
            store,
            library,
            new StubTelegramTransport([Message("https://t.me/completed-once/1", "", "完成一次")]));
        var completedCount = 0;
        scheduler.RunCompleted += completed =>
        {
            if (completed.ScheduleId == scheduleId) Interlocked.Increment(ref completedCount);
        };

        var result = await scheduler.RunNowAsync(scheduleId);
        await WaitUntilAsync(() => Volatile.Read(ref completedCount) == 1, TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded);
        Assert.Equal(1, Volatile.Read(ref completedCount));
    }

    [Fact]
    public async Task DuplicateRunForSameScheduleJoinsTheExistingQueueItem()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/duplicate", "duplicate", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var transport = new BlockingTelegramTransport();
        var scheduler = CreateScheduler(databasePath, store, library, transport);

        var firstRun = scheduler.RunNowAsync(scheduleId);
        try
        {
            await transport.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicateRun = scheduler.RunNowAsync(scheduleId);
            Assert.Single(scheduler.QueueSnapshot);

            transport.ReleaseFirst();
            var results = await Task.WhenAll(firstRun, duplicateRun);

            Assert.All(results, result => Assert.True(result.Succeeded));
            Assert.Equal(1, transport.CallCount);
        }
        finally
        {
            transport.ReleaseFirst();
        }
    }

    [Fact]
    public async Task CancellingQueuedRunPersistsCancelledHistoryWithoutStartingIt()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var firstScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/cancel-running", "cancel-running", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var queuedScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/cancel-queued", "cancel-queued", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var transport = new BlockingTelegramTransport();
        var scheduler = CreateScheduler(databasePath, store, library, transport);
        var queuedCompletedCount = 0;
        scheduler.RunCompleted += completed =>
        {
            if (completed.ScheduleId == queuedScheduleId) Interlocked.Increment(ref queuedCompletedCount);
        };

        var firstRun = scheduler.RunNowAsync(firstScheduleId);
        try
        {
            await transport.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedRun = scheduler.RunNowAsync(queuedScheduleId);
            await WaitUntilAsync(() => scheduler.QueueSnapshot.Count == 2, TimeSpan.FromSeconds(5));

            Assert.True(scheduler.CancelRun(queuedScheduleId));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedRun);
            await WaitUntilAsync(
                async () => (await store.GetRunHistoryAsync(scheduleId: queuedScheduleId))
                    .Any(history => history.Status == "已取消"),
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => Volatile.Read(ref queuedCompletedCount) == 1,
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, transport.CallCount);
            Assert.Equal(1, Volatile.Read(ref queuedCompletedCount));
            transport.ReleaseFirst();
            Assert.True((await firstRun).Succeeded);
        }
        finally
        {
            transport.ReleaseFirst();
        }
    }

    [Fact]
    public async Task HomePageCollectionStopsAtTwentyMessagesWithoutPaging()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/home-page", "home-page", 24, true,
            WebProxySelection.Automatic, 100, "", FirstRunMode: "backfill"));
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var messages = Enumerable.Range(1, 25)
            .Select(index => Message(
                $"https://t.me/home-page/{index}",
                "",
                $"消息 {index}",
                firstPublishedAt.AddMinutes(index)))
            .ToArray();
        var transport = new HomePageTelegramTransport(messages);
        var scheduler = CreateScheduler(databasePath, store, library, transport);

        var result = await scheduler.RunNowAsync(scheduleId);

        Assert.True(result.Succeeded);
        Assert.Equal(20, result.AddedCount);
        Assert.Equal(1, transport.PageCallCount);
        Assert.Equal(new long?[] { null }, transport.BeforeMessageIds);
        Assert.Equal(20, (await library.GetMaterialsAsync()).Count);
        var history = Assert.Single(await store.GetRunHistoryAsync(scheduleId: scheduleId));
        Assert.Equal(1, history.PageCount);
        Assert.Equal(25, history.LastMessageId);
    }

    [Fact]
    public async Task HomePagePartialFailureKeepsWatermarkBeforeFailedMessage()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global", "nickname"));
        var store = new TelegramCollectionStore(databasePath, context);
        var library = new ContentLibraryStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/home-page-failure", "home-page-failure", 24, true,
            WebProxySelection.Automatic, 20, "", FirstRunMode: "backfill"));
        var firstPublishedAt = new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        var transport = new HomePageTelegramTransport(
        [
            Message(
                "https://t.me/home-page-failure/29",
                "",
                "",
                firstPublishedAt.AddMinutes(29),
                new TelegramMediaRef(
                    "https://example.com/missing-home-page.jpg",
                    "https://example.com/missing-home-page.jpg",
                    "image")),
            Message(
                "https://t.me/home-page-failure/30",
                "",
                "首页后续消息仍然成功",
                firstPublishedAt.AddMinutes(30))
        ]);
        var scheduler = CreateScheduler(databasePath, store, library, transport);

        var result = await scheduler.RunNowAsync(scheduleId);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, result.FailedMediaCount);
        Assert.Equal(1, transport.PageCallCount);
        Assert.Equal(new long?[] { null }, transport.BeforeMessageIds);
        var saved = Assert.Single(await store.GetAsync());
        Assert.Equal("部分成功", saved.LastStatus);
        Assert.Equal(28, saved.LastMessageId);
        var history = Assert.Single(await store.GetRunHistoryAsync(scheduleId: scheduleId));
        Assert.Equal(1, history.PageCount);
        Assert.Equal(28, history.LastMessageId);
    }

    [Fact]
    public async Task RunHistoryFiltersByScheduleStatusAndKeywordAndStaysAccountScoped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(databasePath, context);
        var firstScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/history-a", "history-a", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var secondScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/history-b", "history-b", 24, true,
            WebProxySelection.Automatic, 20, ""));
        await store.SaveRunHistoryAsync(History(firstScheduleId, "history-a", "成功", ""));
        await store.SaveRunHistoryAsync(History(firstScheduleId, "history-a", "失败", "代理连接超时"));
        await store.SaveRunHistoryAsync(History(secondScheduleId, "history-b", "已取消", "用户取消"));

        Assert.Equal(2, (await store.GetRunHistoryAsync(scheduleId: firstScheduleId)).Count);
        var failed = Assert.Single(await store.GetRunHistoryAsync(status: "失败"));
        Assert.Equal(firstScheduleId, failed.ScheduleId);
        var searched = Assert.Single(await store.GetRunHistoryAsync(search: "超时"));
        Assert.Contains("代理连接超时", searched.Error, StringComparison.Ordinal);

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        Assert.Empty(await store.GetRunHistoryAsync());
        context.Clear();
        Assert.Empty(await store.GetRunHistoryAsync());
    }

    [Fact]
    public async Task RunHistoryRetentionKeepsNewestRecordsPerSchedule()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(databasePath, context);
        var firstScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/retention-a", "retention-a", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var secondScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/retention-b", "retention-b", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var startedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        for (var index = 0; index <= TelegramCollectionStore.RunHistoryRetentionLimit; index++)
        {
            await store.SaveRunHistoryAsync(History(
                firstScheduleId,
                "retention-a",
                "成功",
                $"记录 {index}",
                startedAt.AddSeconds(index)));
        }
        await store.SaveRunHistoryAsync(History(
            secondScheduleId,
            "retention-b",
            "成功",
            "另一个计划",
            startedAt.AddDays(1)));

        var retained = await store.GetRunHistoryAsync(
            limit: TelegramCollectionStore.RunHistoryRetentionLimit,
            scheduleId: firstScheduleId);
        Assert.Equal(TelegramCollectionStore.RunHistoryRetentionLimit, retained.Count);
        Assert.Equal("记录 500", retained[0].Error);
        Assert.DoesNotContain(retained, item => item.Error == "记录 0");
        Assert.Single(await store.GetRunHistoryAsync(scheduleId: secondScheduleId));
    }

    [Fact]
    public async Task RunHistoryRetentionStaysAccountScoped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/retention-account", "retention-account", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var startedAt = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < TelegramCollectionStore.RunHistoryRetentionLimit; index++)
        {
            await store.SaveRunHistoryAsync(History(
                scheduleId,
                "retention-account",
                "成功",
                $"账号 A {index}",
                startedAt.AddSeconds(index)));
        }

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        await store.SaveRunHistoryAsync(History(
            scheduleId,
            "retention-account",
            "成功",
            "账号 B 保留记录",
            startedAt.AddDays(1)));

        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        await store.SaveRunHistoryAsync(History(
            scheduleId,
            "retention-account",
            "成功",
            "账号 A 最新记录",
            startedAt.AddDays(2)));
        Assert.Equal(
            TelegramCollectionStore.RunHistoryRetentionLimit,
            (await store.GetRunHistoryAsync(
                limit: TelegramCollectionStore.RunHistoryRetentionLimit,
                scheduleId: scheduleId)).Count);

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        var accountBHistory = Assert.Single(await store.GetRunHistoryAsync(scheduleId: scheduleId));
        Assert.Equal("账号 B 保留记录", accountBHistory.Error);
    }

    [Fact]
    public async Task DeleteScheduleRemovesOnlyItsRunHistory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(databasePath, context);
        var deletedScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/delete-a", "delete-a", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var retainedScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/delete-b", "delete-b", 24, true,
            WebProxySelection.Automatic, 20, ""));
        await store.SaveRunHistoryAsync(History(deletedScheduleId, "delete-a", "成功", "待删除"));
        await store.SaveRunHistoryAsync(History(retainedScheduleId, "delete-b", "成功", "同账号保留"));

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        var otherAccountScheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/delete-c", "delete-c", 24, true,
            WebProxySelection.Automatic, 20, ""));
        await store.SaveRunHistoryAsync(History(otherAccountScheduleId, "delete-c", "成功", "其他账号保留"));

        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        await store.DeleteAsync(deletedScheduleId);

        Assert.Empty(await store.GetRunHistoryAsync(scheduleId: deletedScheduleId));
        Assert.Single(await store.GetRunHistoryAsync(scheduleId: retainedScheduleId));

        context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        Assert.Single(await store.GetRunHistoryAsync(scheduleId: otherAccountScheduleId));
    }

    [Fact]
    public async Task RunHistoryAndScheduleStatusAreSanitizedBeforePersistence()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-{Guid.NewGuid():N}.db");
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new TelegramCollectionStore(databasePath, context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/sanitize", "sanitize", 24, true,
            WebProxySelection.Automatic, 20, ""));

        await store.UpdateSuccessAsync(
            scheduleId,
            DateTimeOffset.Now,
            0,
            "成功 access_token=status-secret",
            "10",
            detail: "authorization=Bearer detail-secret");
        await store.SaveRunHistoryAsync(History(
            scheduleId,
            "sanitize",
            "失败 authorization=history-status-secret",
            "QQ_AI_CONNECT_TOKEN=history-error-secret",
            autoScheduleReasons: "cookie=history-reason-secret"));

        var schedule = Assert.Single(await store.GetAsync());
        Assert.DoesNotContain("status-secret", schedule.LastStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("detail-secret", schedule.LastError, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", schedule.LastStatus, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", schedule.LastError, StringComparison.Ordinal);

        var history = Assert.Single(await store.GetRunHistoryAsync(scheduleId: scheduleId));
        Assert.DoesNotContain("history-status-secret", history.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("history-error-secret", history.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("history-reason-secret", history.AutoScheduleReasons, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", history.Status, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", history.Error, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", history.AutoScheduleReasons, StringComparison.Ordinal);
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
    public void ThrowingRunCompletedSubscriberDoesNotBlockRemainingSubscribers()
    {
        var received = new List<TelegramCollectionRunCompleted>();
        var failures = new List<Exception>();
        Action<TelegramCollectionRunCompleted>? handlers = null;
        handlers += _ => throw new InvalidOperationException("订阅者异常");
        handlers += completed => received.Add(completed);

        TelegramCollectionScheduler.RaiseSafely(
            handlers,
            new TelegramCollectionRunCompleted(7, "频道", "scheduled", "失败", false, "脱敏错误"),
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
        _ = await scheduler.StopAsync(TimeSpan.Zero);

        // 零超时通常返回“未确认”，但取消请求发出后循环可能恰好在返回后的下一次调度中退出，
        // 因此 IsRunning 的瞬时值不能作为断言；等待取消生效，重点验证下面二次停止与重启没有被破坏。
        await WaitUntilAsync(() => !scheduler.IsRunning, TimeSpan.FromSeconds(5));

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

    private static TelegramCollectionScheduler CreateScheduler(
        string databasePath,
        TelegramCollectionStore store,
        ContentLibraryStore library,
        ITelegramTransport transport) =>
        new(
            store,
            library,
            transport: transport,
            logger: new AppLogger(Path.Combine(Path.GetTempPath(), $"qq-channel-tg-scheduler-log-{Guid.NewGuid():N}")));

    private static TelegramChannelMessage Message(
        string url,
        string title,
        string text,
        params TelegramMediaRef[] media) =>
        new(url, "t.me", DateTimeOffset.Now, title, text, media, "");

    private static TelegramChannelMessage Message(
        string url,
        string title,
        string text,
        DateTimeOffset publishedAt,
        params TelegramMediaRef[] media) =>
        new(url, "t.me", publishedAt, title, text, media, "");

    private static TelegramCollectionRunHistory History(
        long scheduleId,
        string channelName,
        string status,
        string error,
        DateTimeOffset? startedAt = null,
        string autoScheduleReasons = "")
    {
        var started = startedAt ?? DateTimeOffset.Now.AddMinutes(-1);
        return new TelegramCollectionRunHistory(
            0,
            scheduleId,
            $"https://t.me/s/{channelName}",
            channelName,
            "manual",
            started,
            started.AddSeconds(12),
            status,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            autoScheduleReasons,
            1,
            30,
            error);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待调度器状态超时。");
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("等待异步条件超时。");
            await Task.Delay(25);
        }
    }

    private sealed class StubTelegramTransport(IReadOnlyList<TelegramChannelMessage> messages) : ITelegramTransport
    {
        public Task<TelegramTransportPreview> FetchChannelAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TelegramTransportPreview(
                true,
                messages,
                TelegramTransportContext.Direct(),
                TelegramTransportSelection.Direct,
                "测试通道",
                "",
                []));

        public Task<string> DownloadMediaAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("模拟媒体下载失败");
    }

    private sealed class SequencedTelegramTransport(
        params IReadOnlyList<TelegramChannelMessage>[] batches) : ITelegramTransport
    {
        private int _callCount;

        public Task<TelegramTransportPreview> FetchChannelAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var index = Math.Min(Interlocked.Increment(ref _callCount) - 1, batches.Length - 1);
            return Task.FromResult(new TelegramTransportPreview(
                true,
                batches[index],
                TelegramTransportContext.Direct(),
                TelegramTransportSelection.Direct,
                "测试通道",
                "",
                []));
        }

        public Task<string> DownloadMediaAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("模拟媒体下载失败");
    }

    private sealed class BlockingTelegramTransport : ITelegramTransport
    {
        private readonly TaskCompletionSource _firstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _countLock = new();
        private int _callCount;
        private int _activeCalls;
        private int _maximumConcurrentCalls;

        public Task FirstCallStarted => _firstCallStarted.Task;
        public int CallCount { get { lock (_countLock) return _callCount; } }
        public int MaximumConcurrentCalls { get { lock (_countLock) return _maximumConcurrentCalls; } }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public Task<TelegramTransportPreview> FetchChannelAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TelegramTransportPreview>(
                new NotSupportedException("串行队列测试只使用分页接口。"));

        public async Task<TelegramTransportPageResult> FetchChannelPageAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            long? beforeMessageId,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            int callNumber;
            lock (_countLock)
            {
                callNumber = ++_callCount;
                _activeCalls++;
                _maximumConcurrentCalls = Math.Max(_maximumConcurrentCalls, _activeCalls);
            }

            try
            {
                if (callNumber == 1)
                {
                    _firstCallStarted.TrySetResult();
                    await _releaseFirst.Task.WaitAsync(cancellationToken);
                }

                var message = Message(
                    $"{channelUrl.TrimEnd('/')}/{100 + callNumber}",
                    "",
                    $"消息 {callNumber}");
                return new TelegramTransportPageResult(
                    true,
                    [message],
                    TelegramTransportContext.Direct(),
                    TelegramTransportSelection.Direct,
                    "串行测试通道",
                    "",
                    [],
                    false,
                    false);
            }
            finally
            {
                lock (_countLock) _activeCalls--;
            }
        }

        public Task<string> DownloadMediaAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("串行队列测试不下载媒体。");
    }

    private sealed class HomePageTelegramTransport(IReadOnlyList<TelegramChannelMessage> messages) : ITelegramTransport
    {
        private readonly object _countLock = new();
        private int _pageCallCount;
        private readonly List<long?> _beforeMessageIds = [];

        public int PageCallCount { get { lock (_countLock) return _pageCallCount; } }
        public IReadOnlyList<long?> BeforeMessageIds
        {
            get { lock (_countLock) return _beforeMessageIds.ToArray(); }
        }

        public Task<TelegramTransportPreview> FetchChannelAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TelegramTransportPreview>(
                new NotSupportedException("首页测试只使用频道首页接口。"));

        public Task<TelegramTransportPageResult> FetchChannelPageAsync(
            string channelUrl,
            TelegramTransportSelection selection,
            long? beforeMessageId,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            lock (_countLock)
            {
                _pageCallCount++;
                _beforeMessageIds.Add(beforeMessageId);
            }

            return Task.FromResult(new TelegramTransportPageResult(
                true,
                messages,
                TelegramTransportContext.Direct(),
                TelegramTransportSelection.Direct,
                "首页测试通道",
                "",
                [],
                true,
                true));
        }

        public Task<string> DownloadMediaAsync(
            TelegramMediaRef media,
            TelegramTransportContext context,
            string? targetDirectory = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("模拟首页媒体下载失败");
    }

    private sealed class FailingProxyProvider : IWebProxyProvider
    {
        public string Id => "failing";
        public string DisplayName => "失败代理";
        public string HomepageUrl => "https://example.com/";
        public bool SupportsSimulation => true;

        public Task<ProxyPageResult> FetchAsync(
            string targetUrl,
            string? waitSelector = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("模拟代理失败");
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
