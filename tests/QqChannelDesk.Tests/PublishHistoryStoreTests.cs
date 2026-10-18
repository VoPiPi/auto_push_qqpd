using Microsoft.Data.Sqlite;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class PublishHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QqChannelDeskHistoryTests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly PublishHistoryStore _store;

    public PublishHistoryStoreTests()
    {
        _databasePath = Path.Combine(_directory, "channels.db");
        _store = new PublishHistoryStore(_databasePath);
    }

    [Fact]
    public async Task CreateAndComplete_PersistsContentAndOutcomeWithoutFullMediaPath()
    {
        var id = await _store.CreateAsync(new PublishRecordDraft(
            FeedType.Image, "1", "测试频道", "11", "公告", "标题", ["photo.png"], "正文第一行\n正文第二行"));

        await _store.CompleteAsync(id, new PublishResult(true, PublishErrorCategory.None, "已提交", "https://example.test/post/1", "post-1"));

        var record = Assert.Single(await _store.GetRecentAsync());
        Assert.Equal(id, record.Id);
        Assert.Equal(PublishRecordStatus.Succeeded, record.Status);
        Assert.Equal("photo.png", Assert.Single(record.MediaNames));
        Assert.Equal("https://example.test/post/1", record.PostUrl);
        Assert.Equal("post-1", record.PostId);
        Assert.Equal("正文第一行\n正文第二行", record.Content);
        Assert.Equal("标题", record.ContentDisplay);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(PublishRecords)";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("Content", columns);
        Assert.DoesNotContain("MediaPath", columns);
    }

    [Theory]
    [InlineData("标题", "正文", "标题")]
    [InlineData("", "正文第一行\n正文第二行", "正文第一行\n正文第二行")]
    [InlineData(" \t\r\n", "正文", "正文")]
    [InlineData("", "", "（无内容）")]
    public async Task ContentDisplay_PrefersTitleAndFallsBackToBody(string title, string content, string expected)
    {
        await _store.CreateAsync(new PublishRecordDraft(FeedType.Text, "1", "频道", "2", "版块", title, [], content));

        var record = Assert.Single(await _store.GetRecentAsync());
        Assert.Equal(expected, record.ContentDisplay);
        var sinceRecord = Assert.Single(await _store.GetSinceAsync(DateTimeOffset.Now.AddDays(-1)));
        Assert.Equal(content, sinceRecord.Content);
        Assert.Equal(expected, sinceRecord.ContentDisplay);
    }

    [Fact]
    public async Task ContentDisplayFallsBackToMediaSummaryForMediaOnlyRecord()
    {
        await _store.CreateAsync(new PublishRecordDraft(FeedType.Image, "1", "频道", "2", "版块", "", ["a.png", "b.png"]));

        var record = Assert.Single(await _store.GetRecentAsync());

        Assert.Equal("图片 · 2 张", record.ContentDisplay);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_UpgradesLegacyHistoryAndPreservesRecords(bool hasAccountKey)
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE PublishRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartedAt TEXT NOT NULL, CompletedAt TEXT NULL,
                    FeedType TEXT NOT NULL, GuildId TEXT NOT NULL, GuildName TEXT NOT NULL,
                    ChannelId TEXT NOT NULL, ChannelName TEXT NOT NULL,
                    Title TEXT NOT NULL DEFAULT '', MediaNames TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL, ErrorCategory TEXT NOT NULL DEFAULT '',
                    Summary TEXT NOT NULL DEFAULT '', PostUrl TEXT NULL, PostId TEXT NULL
                );
                INSERT INTO PublishRecords
                    (StartedAt, FeedType, GuildId, GuildName, ChannelId, ChannelName, Title, Status)
                VALUES
                    ('2026-10-12T10:00:00+08:00', 'Text', '1', 'guild', '2', 'channel', 'Legacy title', 'Succeeded');
                """;
            await command.ExecuteNonQueryAsync();
            if (hasAccountKey)
            {
                command.CommandText = "ALTER TABLE PublishRecords ADD COLUMN AccountKey TEXT NULL";
                await command.ExecuteNonQueryAsync();
            }
        }

        await _store.InitializeAsync();
        var reopened = new PublishHistoryStore(_databasePath);
        await reopened.InitializeAsync();
        var legacy = Assert.Single(await reopened.GetRecentAsync());
        Assert.Equal("Legacy title", legacy.ContentDisplay);
        Assert.Equal("", legacy.Content);
        Assert.Equal(PublishRecordStatus.Succeeded, legacy.Status);

        await reopened.CreateAsync(new PublishRecordDraft(FeedType.Text, "1", "guild", "2", "channel", "", [], "New body"));
        var records = await reopened.GetRecentAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal("New body", records[0].ContentDisplay);
        Assert.Equal(legacy, records[1]);
    }

    [Fact]
    public async Task Execute_RecordsBodyEvenWhenRequestFailsValidation()
    {
        var executor = new PublishExecutionService(new CliWorkflow(), _store, new MediaStorageService());
        var request = new PublishRequest("", "2", "Body without a title");

        var outcome = await executor.ExecuteAsync(request, "guild", "channel");

        Assert.Equal(PublishErrorCategory.Validation, outcome.Result.Category);
        var record = Assert.Single(await _store.GetRecentAsync());
        Assert.Equal(outcome.PublishRecordId, record.Id);
        Assert.Equal(request.Content, record.Content);
        Assert.Equal(request.Content, record.ContentDisplay);
    }

    [Theory]
    [InlineData(PublishErrorCategory.Timeout, PublishRecordStatus.NeedsVerification)]
    [InlineData(PublishErrorCategory.Permission, PublishRecordStatus.Failed)]
    public async Task Complete_MapsFailuresAndTimeoutToDistinctStatuses(PublishErrorCategory category, PublishRecordStatus expected)
    {
        var id = await _store.CreateAsync(new PublishRecordDraft(FeedType.Text, "1", "频道", "2", "版块", "", []));

        await _store.CompleteAsync(id, new PublishResult(false, category, "结果摘要", null, null));

        Assert.Equal(expected, Assert.Single(await _store.GetRecentAsync()).Status);
    }

    [Fact]
    public async Task Initialize_AddsHistoryTableAlongsideChannelTables()
    {
        var channelStore = new ChannelCacheStore(_databasePath);
        await channelStore.InitializeAsync();
        await _store.InitializeAsync();

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        Assert.Equal(new[] { "Channels", "Guilds", "PublishRecords" }, tables);
    }

    [Fact]
    public async Task MarkInterruptedAsUnverified_RecoversProcessingRecords()
    {
        await _store.CreateAsync(new PublishRecordDraft(FeedType.Video, "1", "频道", "2", "版块", "标题", ["clip.mp4"]));

        await _store.MarkInterruptedAsUnverifiedAsync();

        var record = Assert.Single(await _store.GetRecentAsync());
        Assert.Equal(PublishRecordStatus.NeedsVerification, record.Status);
        Assert.Contains("检查目标频道", record.Summary);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
