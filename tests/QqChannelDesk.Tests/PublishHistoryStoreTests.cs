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
    public async Task CreateAndComplete_PersistsOutcomeWithoutContentOrFullMediaPath()
    {
        var id = await _store.CreateAsync(new PublishRecordDraft(
            FeedType.Image, "1", "测试频道", "11", "公告", "标题", ["photo.png"]));

        await _store.CompleteAsync(id, new PublishResult(true, PublishErrorCategory.None, "已提交", "https://example.test/post/1", "post-1"));

        var record = Assert.Single(await _store.GetRecentAsync());
        Assert.Equal(id, record.Id);
        Assert.Equal(PublishRecordStatus.Succeeded, record.Status);
        Assert.Equal("photo.png", Assert.Single(record.MediaNames));
        Assert.Equal("https://example.test/post/1", record.PostUrl);
        Assert.Equal("post-1", record.PostId);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(PublishRecords)";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.DoesNotContain("Content", columns);
        Assert.DoesNotContain("MediaPath", columns);
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
