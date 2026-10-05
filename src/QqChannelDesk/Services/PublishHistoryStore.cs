using Microsoft.Data.Sqlite;
using System.IO;

namespace QqChannelDesk.Services;

public sealed class PublishHistoryStore
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public PublishHistoryStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS PublishRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartedAt TEXT NOT NULL,
                    CompletedAt TEXT NULL,
                    FeedType TEXT NOT NULL,
                    GuildId TEXT NOT NULL,
                    GuildName TEXT NOT NULL,
                    ChannelId TEXT NOT NULL,
                    ChannelName TEXT NOT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    MediaNames TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL,
                    ErrorCategory TEXT NOT NULL DEFAULT '',
                    Summary TEXT NOT NULL DEFAULT '',
                    PostUrl TEXT NULL,
                    PostId TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_PublishRecords_StartedAt ON PublishRecords (StartedAt DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<long> CreateAsync(PublishRecordDraft draft, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PublishRecords
                (StartedAt, FeedType, GuildId, GuildName, ChannelId, ChannelName, Title, MediaNames, Status)
            VALUES
                ($startedAt, $feedType, $guildId, $guildName, $channelId, $channelName, $title, $mediaNames, $status);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$startedAt", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$feedType", draft.FeedType.ToString());
        command.Parameters.AddWithValue("$guildId", draft.GuildId);
        command.Parameters.AddWithValue("$guildName", draft.GuildName);
        command.Parameters.AddWithValue("$channelId", draft.ChannelId);
        command.Parameters.AddWithValue("$channelName", draft.ChannelName);
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$mediaNames", string.Join("\n", draft.MediaNames));
        command.Parameters.AddWithValue("$status", PublishRecordStatus.Processing.ToString());
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task CompleteAsync(long id, PublishResult result, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PublishRecords
            SET CompletedAt = $completedAt, Status = $status, ErrorCategory = $category,
                Summary = $summary, PostUrl = $url, PostId = $postId
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$status", result.Succeeded ? PublishRecordStatus.Succeeded.ToString() :
            result.Category == PublishErrorCategory.Timeout ? PublishRecordStatus.NeedsVerification.ToString() : PublishRecordStatus.Failed.ToString());
        command.Parameters.AddWithValue("$category", result.Category.ToString());
        command.Parameters.AddWithValue("$summary", Truncate(AppLogger.Redact(result.Message), 1000));
        command.Parameters.AddWithValue("$url", (object?)result.Url ?? DBNull.Value);
        command.Parameters.AddWithValue("$postId", (object?)result.PostId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkInterruptedAsUnverifiedAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PublishRecords
            SET Status = $status, CompletedAt = $completedAt,
                Summary = '应用上次未能确认发布结果，请检查目标频道后再判断。'
            WHERE Status = $processing;
            """;
        command.Parameters.AddWithValue("$status", PublishRecordStatus.NeedsVerification.ToString());
        command.Parameters.AddWithValue("$processing", PublishRecordStatus.Processing.ToString());
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PublishRecord>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, StartedAt, CompletedAt, FeedType, GuildId, GuildName, ChannelId, ChannelName,
                   Title, MediaNames, Status, ErrorCategory, Summary, PostUrl, PostId
            FROM PublishRecords ORDER BY Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var records = new List<PublishRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new PublishRecord(
                reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                Enum.Parse<FeedType>(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetString(9).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                Enum.Parse<PublishRecordStatus>(reader.GetString(10)), reader.GetString(11), reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return records;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…";
}

public enum PublishRecordStatus { Processing, Succeeded, Failed, NeedsVerification }

public sealed record PublishRecordDraft(
    FeedType FeedType, string GuildId, string GuildName, string ChannelId, string ChannelName,
    string Title, IReadOnlyList<string> MediaNames);

public sealed record PublishRecord(
    long Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, FeedType FeedType,
    string GuildId, string GuildName, string ChannelId, string ChannelName,
    string Title, IReadOnlyList<string> MediaNames, PublishRecordStatus Status,
    string ErrorCategory, string Summary, string? PostUrl, string? PostId)
{
    public string TypeName => FeedType switch { FeedType.Image => "图片", FeedType.Video => "视频", _ => "文本" };
    public string StatusName => Status switch
    {
        PublishRecordStatus.Processing => "处理中",
        PublishRecordStatus.Succeeded => "成功",
        PublishRecordStatus.NeedsVerification => "待核实",
        _ => "失败"
    };
    public string Target => $"{GuildName}/{ChannelName}";
}
