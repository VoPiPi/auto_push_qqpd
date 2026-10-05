using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class ChannelCacheStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QqChannelDeskTests", Guid.NewGuid().ToString("N"));
    private readonly ChannelCacheStore _store;

    public ChannelCacheStoreTests()
    {
        _store = new ChannelCacheStore(Path.Combine(_directory, "channels.db"));
    }

    [Fact]
    public async Task ReplaceSnapshot_PersistsGuildsAndChannelsAndRemovesOldRows()
    {
        await _store.ReplaceSnapshotAsync([
            new CachedGuild("1", "频道一", "管理员", [new ChannelChoice("11", "公告", "")]),
            new CachedGuild("2", "频道二", "成员", [new ChannelChoice("21", "闲聊", "")])
        ]);

        var before = await _store.GetSyncStateAsync();
        Assert.True(before.Succeeded);
        Assert.Equal(2, (await _store.GetGuildsAsync()).Count);
        Assert.Equal("公告", Assert.Single(await _store.GetChannelsAsync("1")).Name);

        await _store.ReplaceSnapshotAsync([
            new CachedGuild("2", "频道二改名", "成员", [new ChannelChoice("22", "新板块", "")])
        ]);

        var guilds = Assert.Single(await _store.GetGuildsAsync());
        Assert.Equal("2", guilds.Id);
        Assert.Empty(await _store.GetChannelsAsync("1"));
        Assert.Equal("新板块", Assert.Single(await _store.GetChannelsAsync("2")).Name);
    }

    [Fact]
    public async Task Initialize_CreatesOnlyGuildAndChannelTables()
    {
        await _store.InitializeAsync();

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _store.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        Assert.Equal(new[] { "Channels", "Guilds" }, tables);
    }

    [Fact]
    public async Task Initialize_MigratesLegacyThreeTableDatabaseAndKeepsCachedRows()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _store.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE Accounts (AccountKey TEXT PRIMARY KEY, LastSyncSucceeded INTEGER NOT NULL, LastSyncAt TEXT NULL, LastSyncError TEXT NULL);
                INSERT INTO Accounts VALUES ('default-cli-profile', 1, '2026-10-01T12:00:00+08:00', NULL);
                CREATE TABLE Guilds (AccountKey TEXT NOT NULL, GuildId TEXT NOT NULL, Name TEXT NOT NULL, Role TEXT NOT NULL, PRIMARY KEY (AccountKey, GuildId), FOREIGN KEY (AccountKey) REFERENCES Accounts(AccountKey));
                INSERT INTO Guilds VALUES ('default-cli-profile', '1', '保留频道', '管理员');
                CREATE TABLE Channels (AccountKey TEXT NOT NULL, GuildId TEXT NOT NULL, ChannelId TEXT NOT NULL, Name TEXT NOT NULL, PRIMARY KEY (AccountKey, GuildId, ChannelId), FOREIGN KEY (AccountKey, GuildId) REFERENCES Guilds(AccountKey, GuildId));
                INSERT INTO Channels VALUES ('default-cli-profile', '1', '11', '保留版块');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await _store.InitializeAsync();

        Assert.Equal("保留频道", Assert.Single(await _store.GetGuildsAsync()).Name);
        Assert.Equal("保留版块", Assert.Single(await _store.GetChannelsAsync("1")).Name);
        Assert.False((await _store.GetSyncStateAsync()).Succeeded);
        await using var migrated = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _store.DatabasePath }.ToString());
        await migrated.OpenAsync();
        await using var tablesCommand = migrated.CreateCommand();
        tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        await using var reader = await tablesCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.Equal(new[] { "Channels", "Guilds" }, tables);
    }

    [Fact]
    public async Task ReplaceSnapshot_RollsBackEntireSnapshotWhenInsertFails()
    {
        await _store.ReplaceSnapshotAsync([
            new CachedGuild("1", "原频道", "管理员", [new ChannelChoice("11", "原版块", "")])
        ]);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _store.ReplaceSnapshotAsync([
            new CachedGuild("2", "新频道", "成员", [new ChannelChoice("21", "新板块", "")]),
            new CachedGuild("2", "重复频道", "成员", [])
        ]));

        var guild = Assert.Single(await _store.GetGuildsAsync());
        Assert.Equal("1", guild.Id);
        Assert.Equal("原版块", Assert.Single(await _store.GetChannelsAsync("1")).Name);
        Assert.True((await _store.GetSyncStateAsync()).Succeeded);
    }

    [Fact]
    public async Task MarkSyncFailed_PreservesCacheButDisablesFreshness()
    {
        await _store.ReplaceSnapshotAsync([
            new CachedGuild("1", "频道", "管理员", [new ChannelChoice("11", "版块", "")])
        ]);

        await _store.MarkSyncFailedAsync("测试失败");

        Assert.False((await _store.GetSyncStateAsync()).Succeeded);
        Assert.Equal("测试失败", (await _store.GetSyncStateAsync()).Error);
        Assert.Equal("频道", Assert.Single(await _store.GetGuildsAsync()).Name);
        Assert.Single(await _store.GetChannelsAsync("1"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_store.DatabasePath)) File.Delete(_store.DatabasePath);
    }
}
