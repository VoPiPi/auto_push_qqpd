using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class AppDatabaseInitializerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "QqChannelDeskDatabaseTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public AppDatabaseInitializerTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "channels.db");
    }

    [Fact]
    public async Task FirstInitializationCreatesDatabaseTablesAndDefaults()
    {
        Assert.False(File.Exists(_databasePath));

        var settings = new SystemSettingsStore(_databasePath);
        var channels = new ChannelCacheStore(_databasePath);
        var content = new ContentLibraryStore(_databasePath);
        var history = new PublishHistoryStore(_databasePath);

        await new AppDatabaseInitializer(settings, channels, content, history).InitializeAsync();

        Assert.True(File.Exists(_databasePath));
        var persisted = await settings.GetAsync();
        Assert.True(persisted.AutoExecuteSchedules);
        Assert.False(persisted.RunTasksOnStartup);
        Assert.False(persisted.StartWithWindows);
        Assert.Equal(CloseWindowBehavior.ExitApplication, persisted.CloseWindowBehavior);
        Assert.Equal(1, persisted.MaxScheduleConcurrency);

        var tables = await ReadTableNamesAsync(_databasePath);

        Assert.Equal(new[]
        {
            "AppSettings",
            "Channels",
            "CollectedItems",
            "Guilds",
            "MaterialImports",
            "MaterialSourceClaims",
            "Materials",
            "PublishGuildBudgets",
            "PublishPolicies",
            "PublishRecords",
            "ScheduleExecutions"
        }, tables);
    }

    [Fact]
    public async Task InitializationDoesNotOverwriteExistingSettings()
    {
        var settings = new SystemSettingsStore(_databasePath);
        await settings.SaveAsync(new AppSettings(
            AutoExecuteSchedules: false,
            RunTasksOnStartup: true,
            StartWithWindows: true,
            CloseWindowBehavior: CloseWindowBehavior.MinimizeToTray,
            MaxScheduleConcurrency: 3));

        await new AppDatabaseInitializer(
            new SystemSettingsStore(_databasePath),
            new ChannelCacheStore(_databasePath),
            new ContentLibraryStore(_databasePath),
            new PublishHistoryStore(_databasePath)).InitializeAsync();

        var persisted = await new SystemSettingsStore(_databasePath).GetAsync();
        Assert.False(persisted.AutoExecuteSchedules);
        Assert.True(persisted.RunTasksOnStartup);
        Assert.True(persisted.StartWithWindows);
        Assert.Equal(CloseWindowBehavior.MinimizeToTray, persisted.CloseWindowBehavior);
        Assert.Equal(3, persisted.MaxScheduleConcurrency);
    }

    [Fact]
    public async Task InitializationMigratesLegacyChannelTablesBeforeCreatingOtherTables()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
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

        var settings = new SystemSettingsStore(_databasePath);
        var channels = new ChannelCacheStore(_databasePath);
        await new AppDatabaseInitializer(
            settings,
            channels,
            new ContentLibraryStore(_databasePath),
            new PublishHistoryStore(_databasePath),
            new ProxyPoolStore(_databasePath),
            new TelegramCollectionStore(_databasePath)).InitializeAsync();

        Assert.Equal("保留频道", Assert.Single(await channels.GetGuildsAsync()).Name);
        Assert.Equal("保留版块", Assert.Single(await channels.GetChannelsAsync("1")).Name);
        Assert.True((await settings.GetAsync()).AutoExecuteSchedules);

        var tables = await ReadTableNamesAsync(_databasePath);
        Assert.DoesNotContain("Accounts", tables);
        Assert.Contains("AppSettings", tables);
        Assert.Contains("ProxySources", tables);
        Assert.Contains("TelegramCollectionSchedules", tables);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(string databasePath)
    {
        var tables = new List<string>();
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
