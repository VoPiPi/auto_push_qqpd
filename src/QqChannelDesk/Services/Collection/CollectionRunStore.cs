using Microsoft.Data.Sqlite;
using System.IO;

namespace QqChannelDesk.Services.Collection;

public sealed class CollectionRunStore
{
    private static readonly IReadOnlySet<string> TriggerTypes = new HashSet<string>(
        ["manual", "scheduled", "retry"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> Results = new HashSet<string>(
        ["running", "succeeded", "partial", "failed", "ai-degraded", "cancelled"], StringComparer.OrdinalIgnoreCase);

    private readonly string _databasePath;
    private readonly AccountContext? _accountContext;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public CollectionRunStore(string? databasePath = null, AccountContext? accountContext = null)
    {
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");
        _accountContext = accountContext;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS CollectionRuns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountKey TEXT NULL,
                    SourceId INTEGER NOT NULL,
                    [Trigger] TEXT NOT NULL DEFAULT 'manual' CHECK ([Trigger] IN ('manual','scheduled','retry')),
                    StartedAt TEXT NOT NULL,
                    EndedAt TEXT NULL,
                    Result TEXT NOT NULL DEFAULT 'running' CHECK (Result IN ('running','succeeded','partial','failed','ai-degraded','cancelled')),
                    FoundCount INTEGER NOT NULL DEFAULT 0,
                    FetchedCount INTEGER NOT NULL DEFAULT 0,
                    DuplicateCount INTEGER NOT NULL DEFAULT 0,
                    AiFallbackCount INTEGER NOT NULL DEFAULT 0,
                    AiFailedCount INTEGER NOT NULL DEFAULT 0,
                    NewItemCount INTEGER NOT NULL DEFAULT 0,
                    ErrorCode TEXT NOT NULL DEFAULT '',
                    ErrorSummary TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    FOREIGN KEY (SourceId) REFERENCES CollectionSources(Id)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureCollectionRunColumnsAsync(connection, cancellationToken);
            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_CollectionRuns_SourceId
                    ON CollectionRuns (SourceId, StartedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_CollectionRuns_AccountStarted
                    ON CollectionRuns (AccountKey, StartedAt DESC);
                """;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<long> CreateRunAsync(CollectionRunDraft draft, CancellationToken cancellationToken = default)
    {
        if (!TriggerTypes.Contains(draft.Trigger)) throw new ArgumentException("采集触发方式无效。", nameof(draft));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"""
            SELECT 1
            FROM CollectionSources
            WHERE Id=$sourceId AND {AccountScope("AccountKey")} AND DeletedAt IS NULL
            LIMIT 1;
            """;
        checkCommand.Parameters.AddWithValue("$sourceId", draft.SourceId);
        AddAccountParameter(checkCommand, accountKey);
        if (await checkCommand.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("来源不存在或不属于当前账号。");

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CollectionRuns (AccountKey, SourceId, [Trigger], StartedAt, CreatedAt)
            VALUES ($accountKey, $sourceId, $trigger, $startedAt, $now);
            SELECT last_insert_rowid();
            """;
        var now = DateTimeOffset.Now.ToString("O");
        command.Parameters.AddWithValue("$accountKey", accountKey);
        command.Parameters.AddWithValue("$sourceId", draft.SourceId);
        command.Parameters.AddWithValue("$trigger", draft.Trigger.Trim());
        command.Parameters.AddWithValue("$startedAt", (draft.StartedAt ?? DateTimeOffset.Now).ToString("O"));
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task CompleteRunAsync(CollectionRunUpdate update, CancellationToken cancellationToken = default)
    {
        if (!Results.Contains(update.Result)) throw new ArgumentException("采集结果状态无效。", nameof(update));
        if (update.FoundCount < 0 || update.FetchedCount < 0 || update.DuplicateCount < 0 ||
            update.AiFallbackCount < 0 || update.AiFailedCount < 0 || update.NewItemCount < 0)
            throw new ArgumentOutOfRangeException(nameof(update), "采集统计数量不能小于 0。");

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CollectionRuns SET
                EndedAt=$endedAt, Result=$result,
                FoundCount=$foundCount, FetchedCount=$fetchedCount, DuplicateCount=$duplicateCount,
                AiFallbackCount=$aiFallbackCount, AiFailedCount=$aiFailedCount,
                NewItemCount=$newItemCount, ErrorCode=$errorCode, ErrorSummary=$errorSummary
            WHERE Id=$id AND ACCOUNT_SCOPE;
            """;
        command.CommandText = command.CommandText.Replace(
            "ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$endedAt", (update.EndedAt ?? DateTimeOffset.Now).ToString("O"));
        command.Parameters.AddWithValue("$result", update.Result);
        command.Parameters.AddWithValue("$foundCount", update.FoundCount);
        command.Parameters.AddWithValue("$fetchedCount", update.FetchedCount);
        command.Parameters.AddWithValue("$duplicateCount", update.DuplicateCount);
        command.Parameters.AddWithValue("$aiFallbackCount", update.AiFallbackCount);
        command.Parameters.AddWithValue("$aiFailedCount", update.AiFailedCount);
        command.Parameters.AddWithValue("$newItemCount", update.NewItemCount);
        command.Parameters.AddWithValue("$errorCode", update.ErrorCode.Trim());
        command.Parameters.AddWithValue("$errorSummary", update.ErrorSummary.Trim());
        command.Parameters.AddWithValue("$id", update.Id);
        AddAccountParameter(command, accountKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("采集批次不存在或不属于当前账号。");
    }

    public async Task<IReadOnlyList<CollectionRunRecord>> GetRunsAsync(
        long? sourceId = null,
        string? result = null,
        CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {RunColumns}
            FROM CollectionRuns
            WHERE {AccountScope("AccountKey")}
              AND ($sourceId=0 OR SourceId=$sourceId)
              AND ($result='' OR Result=$result)
            ORDER BY StartedAt DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId ?? 0);
        command.Parameters.AddWithValue("$result", result?.Trim() ?? "");
        AddAccountParameter(command, accountKey);
        var runs = new List<CollectionRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) runs.Add(ReadRun(reader));
        return runs;
    }

    public async Task<int> GetTodaySucceededCountAsync(DateTimeOffset todayStart, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return 0;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM CollectionRuns
            WHERE Result='succeeded' AND StartedAt >= $since AND {AccountScope("AccountKey")};
            """;
        command.Parameters.AddWithValue("$since", todayStart.ToString("O"));
        AddAccountParameter(command, accountKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<CollectionRunRecord?> GetRunAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {RunColumns}
            FROM CollectionRuns
            WHERE Id=$id AND {AccountScope("AccountKey")}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    private const string RunColumns = """
        Id, SourceId, [Trigger], StartedAt, EndedAt, Result,
        FoundCount, FetchedCount, DuplicateCount, AiFallbackCount, AiFailedCount,
        NewItemCount, ErrorCode, ErrorSummary, CreatedAt
        """;

    private static CollectionRunRecord ReadRun(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3)),
        ParseDate(reader, 4),
        reader.GetString(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetString(12),
        reader.GetString(13),
        DateTimeOffset.Parse(reader.GetString(14)));

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index));

    private string? ReadAccountKey() => _accountContext?.CurrentAccountKey;

    private string RequireAccountKey() => _accountContext?.RequireAccountKey() ?? "";

    private string AccountScope(string column) =>
        _accountContext is null ? "1=1" : $"{column} = $accountKey";

    private static void AddAccountParameter(SqliteCommand command, string? accountKey)
    {
        if (accountKey is not null)
            command.Parameters.AddWithValue("$accountKey", accountKey);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 3
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureCollectionRunColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        (string Name, string Definition)[] columns =
        [
            ("AccountKey", "TEXT NULL"),
            ("SourceId", "INTEGER NOT NULL DEFAULT 0"),
            ("Trigger", "TEXT NOT NULL DEFAULT 'manual'"),
            ("StartedAt", "TEXT NOT NULL DEFAULT ''"),
            ("EndedAt", "TEXT NULL"),
            ("Result", "TEXT NOT NULL DEFAULT 'running'"),
            ("FoundCount", "INTEGER NOT NULL DEFAULT 0"),
            ("FetchedCount", "INTEGER NOT NULL DEFAULT 0"),
            ("DuplicateCount", "INTEGER NOT NULL DEFAULT 0"),
            ("AiFallbackCount", "INTEGER NOT NULL DEFAULT 0"),
            ("AiFailedCount", "INTEGER NOT NULL DEFAULT 0"),
            ("NewItemCount", "INTEGER NOT NULL DEFAULT 0"),
            ("ErrorCode", "TEXT NOT NULL DEFAULT ''"),
            ("ErrorSummary", "TEXT NOT NULL DEFAULT ''"),
            ("CreatedAt", "TEXT NOT NULL DEFAULT ''")
        ];

        foreach (var column in columns)
            await EnsureColumnAsync(connection, "CollectionRuns", column.Name, column.Definition, cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN [{columnName}] {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record CollectionRunDraft(long SourceId, string Trigger = "manual", DateTimeOffset? StartedAt = null);

public sealed record CollectionRunUpdate(
    long Id,
    string Result,
    int FoundCount = 0,
    int FetchedCount = 0,
    int DuplicateCount = 0,
    int AiFallbackCount = 0,
    int AiFailedCount = 0,
    int NewItemCount = 0,
    string ErrorCode = "",
    string ErrorSummary = "",
    DateTimeOffset? EndedAt = null);

public sealed record CollectionRunRecord(
    long Id,
    long SourceId,
    string Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Result,
    int FoundCount,
    int FetchedCount,
    int DuplicateCount,
    int AiFallbackCount,
    int AiFailedCount,
    int NewItemCount,
    string ErrorCode,
    string ErrorSummary,
    DateTimeOffset CreatedAt);
