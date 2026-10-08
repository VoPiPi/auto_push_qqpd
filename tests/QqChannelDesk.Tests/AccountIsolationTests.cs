using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        var syncPath = Path.Combine(_directory, "channels.sync.json");
        if (File.Exists(syncPath)) File.Delete(syncPath);
    }
}
