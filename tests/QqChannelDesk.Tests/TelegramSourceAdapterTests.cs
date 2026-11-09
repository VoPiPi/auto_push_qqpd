using System.Text.Json;
using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramSourceAdapterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "QqChannelDeskTelegramSourceTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly AccountContext _context = new();

    public TelegramSourceAdapterTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "channels.db");
        _context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
    }

    [Fact]
    public async Task MapsTelegramSchedulesAsReadOnlyUnifiedSources()
    {
        var store = new TelegramCollectionStore(_databasePath, _context);
        var sourceStore = new CollectionSourceStore(_databasePath, _context);
        await sourceStore.InitializeAsync();
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/channel", "TG 频道", 6, true,
            "direct", 15, "广告", CronExpression: "*/10 * * * *",
            AutoScheduleEnabled: true, AutoScheduleDelayMinutes: 45,
            AutoScheduleMaxPerRun: 8, FirstRunMode: "new-only", IncludeKeyword: "公告"));
        await SetWatermarkAsync(scheduleId, 123);
        var adapter = new TelegramSourceAdapter(store);

        var sources = await adapter.GetSourcesAsync();
        var source = Assert.Single(sources);

        Assert.Equal(-scheduleId, source.Id);
        Assert.Equal("TG 频道", source.Name);
        Assert.Equal("telegram", source.SourceType);
        Assert.Equal("https://t.me/s/channel", source.EntryUrl);
        Assert.True(source.AuthConfirmed);
        Assert.Equal("*/10 * * * *", source.CronExpression);
        Assert.Equal(360, source.FrequencyMinutes);
        Assert.Equal(15, source.MaxItemsPerRun);
        Assert.Equal("公告", source.IncludeKeywords);
        Assert.Equal("广告", source.ExcludeKeywords);
        Assert.Equal("healthy", source.HealthStatus);
        Assert.Equal(123, JsonSerializer.Deserialize<TelegramSourceRuleConfig>(source.RuleConfigJson)!.LastMessageId);
        Assert.Empty(await sourceStore.GetSourcesAsync());

        var byId = await adapter.GetSourceAsync(-scheduleId);
        Assert.NotNull(byId);
        Assert.Equal(source.Id, byId!.Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => adapter.GetSourceAsync(scheduleId));
    }

    [Fact]
    public async Task MapsHistoryAndTogglesOldScheduleOnly()
    {
        var store = new TelegramCollectionStore(_databasePath, _context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/history", "历史频道", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var adapter = new TelegramSourceAdapter(store);

        await store.SaveRunHistoryAsync(new TelegramCollectionRunHistory(
            0, scheduleId, "https://t.me/s/history", "历史频道", "manual",
            DateTimeOffset.Now.AddMinutes(-2), DateTimeOffset.Now.AddMinutes(-1),
            "部分成功", 3, 2, 4, 1, 1, 2, 1, "", 2, 88, "脱敏错误"));

        var run = Assert.Single(await adapter.GetRunHistoryAsync(viewId: -scheduleId));
        Assert.Equal(-scheduleId, run.SourceId);
        Assert.Equal("partial", run.Result);
        Assert.Equal(6, run.FoundCount);
        Assert.Equal(2, run.DuplicateCount);
        Assert.Equal(3, run.NewItemCount);

        await adapter.SetEnabledAsync(-scheduleId, false);
        var source = await adapter.GetSourceAsync(-scheduleId);
        Assert.NotNull(source);
        Assert.False(source!.Enabled);
        Assert.Equal("disabled", source.HealthStatus);
    }

    [Fact]
    public async Task RunNowDelegatesToOldSchedulerAndPreservesWatermark()
    {
        var store = new TelegramCollectionStore(_databasePath, _context);
        var library = new ContentLibraryStore(_databasePath, _context);
        var scheduleId = await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/run", "运行频道", 24, true,
            "direct", 10, ""));
        await SetWatermarkAsync(scheduleId, 321);
        var scheduler = new TelegramCollectionScheduler(
            store,
            library,
            transport: new EmptyTelegramTransport(),
            logger: new AppLogger(Path.Combine(_directory, "tg-scheduler.log")));
        try
        {
            var adapter = new TelegramSourceAdapter(store, scheduler, maxConcurrency: 1);
            var completed = new TaskCompletionSource<TelegramSourceRunCompleted>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.RunCompleted += eventArgs => completed.TrySetResult(eventArgs);

            var source = await adapter.RunNowAsync(-scheduleId);

            Assert.Equal(-scheduleId, source.Id);
            Assert.Equal("healthy", source.HealthStatus);
            var oldSchedule = await store.GetAsync(scheduleId);
            Assert.NotNull(oldSchedule);
            Assert.Equal("成功", oldSchedule!.LastStatus);
            Assert.Equal(321, oldSchedule.LastMessageId);
            Assert.Equal(321, JsonSerializer.Deserialize<TelegramSourceRuleConfig>(source.RuleConfigJson)!.LastMessageId);
            var finishedEvent = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(-scheduleId, finishedEvent.ViewId);
            Assert.True(finishedEvent.Succeeded);
            var history = Assert.Single(await adapter.GetRunHistoryAsync(viewId: -scheduleId));
            Assert.Equal("succeeded", history.Result);
            Assert.Equal(-scheduleId, history.SourceId);
        }
        finally
        {
            await scheduler.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AdapterIsolatesAccounts()
    {
        var store = new TelegramCollectionStore(_databasePath, _context);
        await store.SaveAsync(new TelegramCollectionScheduleDraft(
            null, "https://t.me/s/private", "私有频道", 24, true,
            WebProxySelection.Automatic, 20, ""));
        var adapter = new TelegramSourceAdapter(store);

        _context.Set(CurrentAccountIdentity.Create("global-b", "account-b"));
        Assert.Empty(await adapter.GetSourcesAsync());
        Assert.Null(await adapter.GetSourceAsync(-1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.RunNowAsync(-1));
    }

    private async Task SetWatermarkAsync(long scheduleId, long messageId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TelegramCollectionSchedules SET LastMessageId=$messageId WHERE Id=$id";
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$id", scheduleId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}

internal sealed class EmptyTelegramTransport : ITelegramTransport
{
    public Task<TelegramTransportPreview> FetchChannelAsync(
        string channelUrl,
        TelegramTransportSelection selection,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TelegramTransportPreview(
            true, [], TelegramTransportContext.Direct(), TelegramTransportSelection.Direct,
            "测试通道", "", []));

    public Task<string> DownloadMediaAsync(
        TelegramMediaRef media,
        TelegramTransportContext context,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("测试不下载媒体。");
}
