using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class AccountIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QqChannelDeskAccountTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public AccountIsolationTests()
    {
        _databasePath = Path.Combine(_directory, "channels.db");
    }

    [Fact]
    public async Task ContentLibrary_IsolatesRowsAndRejectsCrossAccountMutations()
    {
        var context = new AccountContext();
        var store = new ContentLibraryStore(_databasePath, context);
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        var itemId = await store.SaveItemAsync(new CollectedItemDraft("https://example.test/a", "example.test", "A", "A content"));
        var materialId = await store.SaveMaterialAsync(new MaterialDraft(null, "A material", "text", "A", "guild", "Guild", "channel", "Channel", null, "waitsend", "", []));

        context.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        Assert.Empty(await store.GetItemsAsync());
        Assert.Empty(await store.GetMaterialsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateItemAsync(
            new CollectedItemDraft("https://example.test/b", "example.test", "B", "B", Id: itemId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveMaterialAsync(
            new MaterialDraft(materialId, "Hijack", "text", "B", "guild", "Guild", "channel", "Channel", null, "waitsend", "", [])));

        context.Clear();
        Assert.Empty(await store.GetItemsAsync());
        Assert.Empty(await store.GetMaterialsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveItemAsync(
            new CollectedItemDraft("https://example.test/no-account", "example.test", "No account", "No account")));
    }

    [Fact]
    public async Task LegacyNullAccountRows_AreHiddenFromEveryAuthenticatedAccount()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE CollectedItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, Url TEXT NOT NULL DEFAULT '', SourceHost TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '', Content TEXT NOT NULL DEFAULT '', CollectedAt TEXT NOT NULL,
                    Status TEXT NOT NULL DEFAULT '待处理', Notes TEXT NOT NULL DEFAULT '', TagsJson TEXT NOT NULL DEFAULT '[]',
                    ParseError TEXT NOT NULL DEFAULT '');
                INSERT INTO CollectedItems (Url, SourceHost, Title, Content, CollectedAt)
                VALUES ('https://legacy.example', 'legacy.example', 'Legacy', 'Hidden', '2026-10-01T00:00:00+08:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var context = new AccountContext();
        var store = new ContentLibraryStore(_databasePath, context);
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        Assert.Empty(await store.GetItemsAsync());
        context.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        Assert.Empty(await store.GetItemsAsync());
    }

    [Fact]
    public async Task ChannelCache_AndHistory_IsolateAccountsAndSyncState()
    {
        var context = new AccountContext();
        var channels = new ChannelCacheStore(_databasePath, context);
        var history = new PublishHistoryStore(_databasePath, context);

        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        await channels.ReplaceSnapshotAsync([new CachedGuild("guild", "Guild A", "member", [new ChannelChoice("channel-a", "Channel A", "")])]);
        var historyId = await history.CreateAsync(new PublishRecordDraft(FeedType.Text, "guild", "Guild A", "channel-a", "Channel A", "A", []));

        context.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        Assert.Empty(await channels.GetGuildsAsync());
        Assert.Empty(await history.GetRecentAsync());
        Assert.False((await channels.GetSyncStateAsync()).Succeeded);
        await channels.ReplaceSnapshotAsync([new CachedGuild("guild", "Guild B", "member", [new ChannelChoice("channel-b", "Channel B", "")])]);
        Assert.Equal("Guild B", Assert.Single(await channels.GetGuildsAsync()).Name);

        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        Assert.Equal("Guild A", Assert.Single(await channels.GetGuildsAsync()).Name);
        Assert.Equal(historyId, Assert.Single(await history.GetRecentAsync()).Id);
        Assert.Equal("Channel A", Assert.Single(await channels.GetChannelsAsync("guild")).Name);
    }

    [Fact]
    public async Task PublishScheduleMutations_AreIsolatedByAccount()
    {
        var context = new AccountContext();
        var store = new ContentLibraryStore(_databasePath, context);
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        await store.SaveMaterialAsync(new MaterialDraft(
            null,
            "A 计划",
            "text",
            "正文",
            "guild",
            "频道",
            "channel",
            "版块",
            DateTimeOffset.Now.AddHours(2),
            "queue",
            "",
            []));
        var execution = Assert.Single(await store.GetScheduleExecutionsAsync());

        context.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        var pause = await store.SetScheduleExecutionsPausedAsync([execution.Id], true);
        var delete = await store.DeleteScheduleExecutionsAsync([execution.Id]);

        Assert.Empty(pause.AffectedIds);
        Assert.Equal([execution.Id], pause.MissingIds);
        Assert.Empty(delete.AffectedIds);
        Assert.Equal([execution.Id], delete.MissingIds);

        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        var unchanged = Assert.Single(await store.GetScheduleExecutionsAsync());
        Assert.Equal(execution.Id, unchanged.Id);
        Assert.Equal(ScheduleExecutionStatus.Pending, unchanged.Status);
        Assert.False(unchanged.IsPaused);
    }

    [Fact]
    public async Task CollectionSources_AreIsolatedByAccount()
    {
        var context = new AccountContext();
        var store = new CollectionSourceStore(_databasePath, context);
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        var sourceId = await store.CreateSourceAsync(new CollectionSourceDraft("A 来源", "rss", "https://example.test/a"));

        context.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        Assert.Empty(await store.GetSourcesAsync());
        Assert.Null(await store.GetSourceAsync(sourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateSourceAsync(
            new CollectionSourceDraft("越权", "rss", "https://example.test/b", Id: sourceId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateHealthAsync(
            new SourceHealthUpdate(sourceId, "failed", DateTimeOffset.Now, null, null)));
        Assert.False(await store.SoftDeleteSourceAsync(sourceId));

        context.Clear();
        Assert.Empty(await store.GetSourcesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateSourceAsync(
            new CollectionSourceDraft("无账号", "rss", "https://example.test/no-account")));

        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        Assert.Equal(sourceId, (await store.GetSourcesAsync()).Single().Id);
    }

    [Fact]
    public async Task CollectionRuns_AreIsolatedByAccount()
    {
        var context = new AccountContext();
        var sourceStore = new CollectionSourceStore(_databasePath, context);
        var runStore = new CollectionRunStore(_databasePath, context);
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        var sourceId = await sourceStore.CreateSourceAsync(new CollectionSourceDraft("A 来源", "rss", "https://example.test/a"));
        var runId = await runStore.CreateRunAsync(new CollectionRunDraft(sourceId, "manual"));

        context.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        Assert.Empty(await runStore.GetRunsAsync());
        Assert.Null(await runStore.GetRunAsync(runId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runStore.CreateRunAsync(new CollectionRunDraft(sourceId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runStore.CompleteRunAsync(new CollectionRunUpdate(runId, "failed")));

        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        Assert.Equal(runId, (await runStore.GetRunsAsync()).Single().Id);
    }

    [Fact]
    public async Task AiUsageCalls_AreIsolatedByAccount()
    {
        var context = new AccountContext();
        var settings = new SystemSettingsStore(_databasePath);
        await settings.SaveAsync(new AppSettings(AiAssistEnabled: true, AiDailyCallLimit: 10));
        var usage = new CollectionAiUsageStore(_databasePath, context);
        var gate = new AiUsageGate(settings, usage);
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        var source = new CollectionSource(1, "A 来源", "single-page", "https://example.test/a", true, true, null,
            "rule-first", 60, "", "{}", true, 10, "", "", null, null, null, "healthy", "", "", "",
            null, DateTimeOffset.Now, DateTimeOffset.Now);
        Assert.True(await gate.TryReserveAsync(source));

        context.Set(CurrentAccountIdentity.Create("global-b", "nickname-b"));
        Assert.Equal(0, await usage.CountAsync(source.Id));
        Assert.True(await gate.TryReserveAsync(source));
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        Assert.Equal(1, await usage.CountAsync(source.Id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        var syncPath = Path.Combine(_directory, "channels.sync.json");
        if (File.Exists(syncPath)) File.Delete(syncPath);
    }
}
