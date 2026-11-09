using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "QqChannelDeskMigrationTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public MigrationTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "channels.db");
    }

    [Fact]
    public async Task LegacyCollectedItems_GainCollectionColumnsAndOriginalContentBackfill()
    {
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
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
                VALUES ('https://legacy.example', 'legacy.example', 'Legacy', 'Original body', '2026-10-01T00:00:00+08:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var settings = new SystemSettingsStore(_databasePath);
        var channels = new ChannelCacheStore(_databasePath);
        var content = new ContentLibraryStore(_databasePath);
        var sources = new CollectionSourceStore(_databasePath);
        var runs = new CollectionRunStore(_databasePath);
        await new AppDatabaseInitializer(settings, channels, content, new PublishHistoryStore(_databasePath),
            collectionSources: sources, collectionRuns: runs).InitializeAsync();

        var columns = await GetColumnsAsync(_databasePath);
        foreach (var column in new[] { "SourceId", "OriginalContent", "CandidateContent", "DuplicateOfId", "ParsedBy", "SourceItemId" })
            Assert.Contains(column, columns);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Content, OriginalContent FROM CollectedItems WHERE Id=1";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Original body", reader.GetString(0));
            Assert.Equal("Original body", reader.GetString(1));
        }

        await new AppDatabaseInitializer(settings, channels, content, new PublishHistoryStore(_databasePath),
            collectionSources: sources, collectionRuns: runs).InitializeAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT OriginalContent FROM CollectedItems WHERE Id=1";
            Assert.Equal("Original body", Convert.ToString(await command.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task LegacyCollectionSources_GainMissingColumnsAndRemainWritable()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE CollectionSources (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountKey TEXT NULL,
                    Name TEXT NOT NULL,
                    SourceType TEXT NOT NULL,
                    EntryUrl TEXT NOT NULL DEFAULT '',
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    AuthConfirmed INTEGER NOT NULL DEFAULT 0,
                    AuthConfirmedAt TEXT NULL,
                    ParseMode TEXT NOT NULL DEFAULT 'rule-first',
                    FrequencyMinutes INTEGER NOT NULL DEFAULT 60,
                    RuleConfigJson TEXT NOT NULL DEFAULT '{}',
                    AiFallbackEnabled INTEGER NOT NULL DEFAULT 0,
                    MaxItemsPerRun INTEGER NOT NULL DEFAULT 20,
                    IncludeKeywords TEXT NOT NULL DEFAULT '',
                    ExcludeKeywords TEXT NOT NULL DEFAULT '',
                    LastAttemptAt TEXT NULL,
                    LastSuccessAt TEXT NULL,
                    NextRunAt TEXT NULL,
                    HealthStatus TEXT NOT NULL DEFAULT 'healthy',
                    HealthMessage TEXT NOT NULL DEFAULT '',
                    LastErrorCode TEXT NOT NULL DEFAULT '',
                    LastErrorSummary TEXT NOT NULL DEFAULT '',
                    DeletedAt TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE CollectionRuns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourceId INTEGER NOT NULL,
                    StartedAt TEXT NOT NULL,
                    Result TEXT NOT NULL DEFAULT 'running',
                    CreatedAt TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var sources = new CollectionSourceStore(_databasePath, context);
        var runs = new CollectionRunStore(_databasePath, context);
        await sources.InitializeAsync();
        await runs.InitializeAsync();
        var id = await sources.CreateSourceAsync(new CollectionSourceDraft(
            "旧表来源", "rss", "https://example.test/rss", AuthConfirmed: true));
        var source = await sources.GetSourceAsync(id);
        Assert.NotNull(source);
        Assert.Equal("", source!.CronExpression);
        Assert.Equal(0, source.ConsecutiveFailures);
        Assert.Equal("旧表来源", (await sources.GetSourcesAsync()).Single().Name);
    }

    [Fact]
    public async Task OriginalContentBackfill_LimitsRowsPerInitializationAndResumesAcrossRestarts()
    {
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE CollectedItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, Url TEXT NOT NULL DEFAULT '', SourceHost TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '', Content TEXT NOT NULL DEFAULT '', CollectedAt TEXT NOT NULL,
                    Status TEXT NOT NULL DEFAULT '待处理', Notes TEXT NOT NULL DEFAULT '', TagsJson TEXT NOT NULL DEFAULT '[]',
                    ParseError TEXT NOT NULL DEFAULT '');
                INSERT INTO CollectedItems (Url, SourceHost, Title, Content, CollectedAt) VALUES
                    ('https://a.example', 'a.example', 'A', '正文 A', '2026-10-01T00:00:00+08:00'),
                    ('https://b.example', 'b.example', 'B', '正文 B', '2026-10-01T00:01:00+08:00'),
                    ('https://c.example', 'c.example', 'C', '正文 C', '2026-10-01T00:02:00+08:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new ContentLibraryStore(_databasePath, originalContentBackfillLimit: 2);
        await store.InitializeAsync();
        Assert.Equal(2, await CountBackfilledAsync());

        // 模拟重启：新实例继续回填剩余行，避免大表一次性 UPDATE 拖慢启动。
        var restarted = new ContentLibraryStore(_databasePath, originalContentBackfillLimit: 2);
        await restarted.InitializeAsync();
        Assert.Equal(3, await CountBackfilledAsync());
    }

    [Fact]
    public async Task LegacyChineseEnumSourceTable_IsRenamedAndMigratedWithoutDataLoss()
    {
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // 早期实验版本的中文枚举结构：CHECK 约束拒绝当前英文枚举值。
            command.CommandText = """
                CREATE TABLE CollectionSources (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountKey TEXT NULL,
                    Name TEXT NOT NULL,
                    SourceType TEXT NOT NULL CHECK (SourceType IN ('单页','RSS','网页栏目','TG')),
                    EntryUrl TEXT NOT NULL DEFAULT '',
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    AuthConfirmed INTEGER NOT NULL DEFAULT 0,
                    AuthConfirmedAt TEXT NULL,
                    ParseMode TEXT NOT NULL DEFAULT '规则优先',
                    FrequencyMinutes INTEGER NOT NULL DEFAULT 60,
                    RuleConfigJson TEXT NOT NULL DEFAULT '{}',
                    AiFallbackEnabled INTEGER NOT NULL DEFAULT 0,
                    MaxItemsPerRun INTEGER NOT NULL DEFAULT 20,
                    IncludeKeywords TEXT NOT NULL DEFAULT '',
                    ExcludeKeywords TEXT NOT NULL DEFAULT '',
                    LastAttemptAt TEXT NULL,
                    LastSuccessAt TEXT NULL,
                    NextRunAt TEXT NULL,
                    HealthStatus TEXT NOT NULL DEFAULT '正常',
                    HealthMessage TEXT NOT NULL DEFAULT '',
                    LastErrorCode TEXT NOT NULL DEFAULT '',
                    LastErrorSummary TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO CollectionSources (AccountKey, Name, SourceType, EntryUrl, FrequencyMinutes)
                VALUES ('ak-a', '旧栏目', '网页栏目', 'https://legacy.test/column', 90),
                       ('ak-a', '旧单页', '单页', 'https://legacy.test/page', 60);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "account-a"));
        var store = new CollectionSourceStore(_databasePath, context);
        await store.InitializeAsync();

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var verify = connection.CreateCommand();
            verify.CommandText = """
                SELECT (SELECT COUNT(*) FROM CollectionSourcesLegacyCn),
                       (SELECT COUNT(*) FROM CollectionSources WHERE Name='旧栏目' AND SourceType='web-column'),
                       (SELECT COUNT(*) FROM CollectionSources WHERE Name='旧单页' AND SourceType='single-page'),
                       (SELECT COUNT(*) FROM sqlite_master WHERE name='CollectionSourcesLegacyCn');
                """;
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
        }

        // 新结构必须可以继续创建来源（旧 CHECK 不再拦截）。
        var newId = await store.CreateSourceAsync(new CollectionSourceDraft("新来源", "rss", "https://example.test/rss", AuthConfirmed: true));
        Assert.True(newId > 0);

        // 幂等：再次初始化不重复搬迁。
        var restarted = new CollectionSourceStore(_databasePath, context);
        await restarted.InitializeAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM CollectionSources";
            Assert.Equal(3L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
    }

    private async Task<int> CountBackfilledAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM CollectedItems WHERE OriginalContent <> ''";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<IReadOnlyList<string>> GetColumnsAsync(string databasePath)
    {
        var columns = new List<string>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(CollectedItems)";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
