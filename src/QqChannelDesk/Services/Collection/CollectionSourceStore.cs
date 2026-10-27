using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services.Collection;

public sealed class CollectionSourceStore
{
    private static readonly IReadOnlySet<string> SourceTypes = new HashSet<string>(
        ["single-page", "rss", "web-column", "telegram"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ParseModes = new HashSet<string>(
        ["rule-first", "rule-only", "ai-only"], StringComparer.OrdinalIgnoreCase);

    private readonly string _databasePath;
    private readonly AccountContext? _accountContext;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public CollectionSourceStore(string? databasePath = null, AccountContext? accountContext = null)
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
            // 早期实验版本用中文枚举建过 CollectionSources（CHECK 约束无法修改）；
            // 先把旧表改名保留，再用当前结构新建并搬迁数据，全程不删除任何表和数据。
            await RenameLegacyChineseSourceTableAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS CollectionSources (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountKey TEXT NULL,
                    Name TEXT NOT NULL,
                    SourceType TEXT NOT NULL CHECK (SourceType IN ('single-page','rss','web-column','telegram')),
                    EntryUrl TEXT NOT NULL DEFAULT '',
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    AuthConfirmed INTEGER NOT NULL DEFAULT 0,
                    AuthConfirmedAt TEXT NULL,
                    ParseMode TEXT NOT NULL DEFAULT 'rule-first' CHECK (ParseMode IN ('rule-first','rule-only','ai-only')),
                    FrequencyMinutes INTEGER NOT NULL DEFAULT 60,
                    CronExpression TEXT NOT NULL DEFAULT '',
                    RuleConfigJson TEXT NOT NULL DEFAULT '{}',
                    AiFallbackEnabled INTEGER NOT NULL DEFAULT 0,
                    MaxItemsPerRun INTEGER NOT NULL DEFAULT 20,
                    IncludeKeywords TEXT NOT NULL DEFAULT '',
                    ExcludeKeywords TEXT NOT NULL DEFAULT '',
                    LastAttemptAt TEXT NULL,
                    LastSuccessAt TEXT NULL,
                    NextRunAt TEXT NULL,
                    HealthStatus TEXT NOT NULL DEFAULT 'healthy' CHECK (HealthStatus IN ('healthy','degraded','failed','disabled')),
                    HealthMessage TEXT NOT NULL DEFAULT '',
                    LastErrorCode TEXT NOT NULL DEFAULT '',
                    LastErrorSummary TEXT NOT NULL DEFAULT '',
                    DeletedAt TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    ConsecutiveFailures INTEGER NOT NULL DEFAULT 0
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await CopyLegacyChineseSourceRowsAsync(connection, cancellationToken);
            await EnsureCollectionSourceColumnsAsync(connection, cancellationToken);
            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_CollectionSources_AccountEnabled
                    ON CollectionSources (AccountKey, Enabled, DeletedAt);
                CREATE INDEX IF NOT EXISTS IX_CollectionSources_AccountType
                    ON CollectionSources (AccountKey, SourceType);
                CREATE INDEX IF NOT EXISTS IX_CollectionSources_NextRunAt
                    ON CollectionSources (NextRunAt) WHERE Enabled = 1 AND DeletedAt IS NULL;
                """;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<long> CreateSourceAsync(CollectionSourceDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft, nameof(draft));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CollectionSources (
                AccountKey, Name, SourceType, EntryUrl, Enabled, AuthConfirmed, AuthConfirmedAt,
                ParseMode, FrequencyMinutes, CronExpression, RuleConfigJson, AiFallbackEnabled,
                MaxItemsPerRun, IncludeKeywords, ExcludeKeywords, NextRunAt, CreatedAt, UpdatedAt
            ) VALUES (
                $accountKey, $name, $sourceType, $entryUrl, $enabled, $authConfirmed, $authConfirmedAt,
                $parseMode, $frequencyMinutes, $cronExpression, $ruleConfigJson, $aiFallbackEnabled,
                $maxItemsPerRun, $includeKeywords, $excludeKeywords, $now, $now, $now
            );
            SELECT last_insert_rowid();
            """;
        AddSourceParameters(command, draft);
        AddAccountParameter(command, accountKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<CollectionSource>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SourceColumns}
            FROM CollectionSources
            WHERE {AccountScope("AccountKey")} AND DeletedAt IS NULL
            ORDER BY Id DESC;
            """;
        AddAccountParameter(command, accountKey);
        return await ReadSourcesAsync(command, cancellationToken);
    }

    public async Task<CollectionSource?> GetSourceAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SourceColumns}
            FROM CollectionSources
            WHERE Id=$id AND {AccountScope("AccountKey")} AND DeletedAt IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSource(reader) : null;
    }

    public async Task UpdateSourceAsync(CollectionSourceDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft.Id is null) throw new ArgumentException("来源 ID 不能为空。", nameof(draft));
        ValidateDraft(draft, nameof(draft));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CollectionSources SET
                Name=$name, SourceType=$sourceType, EntryUrl=$entryUrl, Enabled=$enabled,
                AuthConfirmed=$authConfirmed, AuthConfirmedAt=$authConfirmedAt,
                ParseMode=$parseMode, FrequencyMinutes=$frequencyMinutes,
                CronExpression=$cronExpression, RuleConfigJson=$ruleConfigJson,
                AiFallbackEnabled=$aiFallbackEnabled, MaxItemsPerRun=$maxItemsPerRun,
                IncludeKeywords=$includeKeywords, ExcludeKeywords=$excludeKeywords, UpdatedAt=$now
            WHERE Id=$id AND ACCOUNT_SCOPE;
            """;
        command.CommandText = command.CommandText.Replace(
            "ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        AddSourceParameters(command, draft);
        command.Parameters.AddWithValue("$id", draft.Id.Value);
        AddAccountParameter(command, accountKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("来源不存在或不属于当前账号。");
    }

    public async Task SetSourceEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CollectionSources SET
                Enabled=$enabled,
                HealthStatus=CASE
                    WHEN $enabled=0 THEN 'disabled'
                    WHEN HealthStatus='disabled' THEN 'healthy'
                    ELSE HealthStatus END,
                UpdatedAt=$now
            WHERE Id=$id AND ACCOUNT_SCOPE;
            """;
        command.CommandText = command.CommandText.Replace(
            "ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("来源不存在或不属于当前账号。");
    }

    public async Task<bool> SoftDeleteSourceAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CollectionSources SET
                Enabled=0, DeletedAt=$now, UpdatedAt=$now,
                HealthStatus='disabled'
            WHERE Id=$id AND ACCOUNT_SCOPE AND DeletedAt IS NULL;
            """;
        command.CommandText = command.CommandText.Replace(
            "ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpdateHealthAsync(SourceHealthUpdate update, CancellationToken cancellationToken = default)
    {
        ValidateHealthStatus(update.HealthStatus);
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CollectionSources SET
                LastAttemptAt=$lastAttemptAt, LastSuccessAt=$lastSuccessAt, NextRunAt=$nextRunAt,
                HealthStatus=$healthStatus, HealthMessage=$healthMessage,
                LastErrorCode=$lastErrorCode, LastErrorSummary=$lastErrorSummary,
                ConsecutiveFailures=$consecutiveFailures, UpdatedAt=$now
            WHERE Id=$id AND ACCOUNT_SCOPE;
            """;
        command.CommandText = command.CommandText.Replace(
            "ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$lastAttemptAt", (object?)ToNullableText(update.LastAttemptAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastSuccessAt", (object?)ToNullableText(update.LastSuccessAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextRunAt", (object?)ToNullableText(update.NextRunAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$healthStatus", update.HealthStatus);
        command.Parameters.AddWithValue("$healthMessage", update.HealthMessage.Trim());
        command.Parameters.AddWithValue("$lastErrorCode", update.LastErrorCode.Trim());
        command.Parameters.AddWithValue("$lastErrorSummary", update.LastErrorSummary.Trim());
        command.Parameters.AddWithValue("$consecutiveFailures", update.ConsecutiveFailures);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", update.Id);
        AddAccountParameter(command, accountKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("来源不存在或不属于当前账号。");
    }

    private const string SourceColumns = """
        Id, AccountKey, Name, SourceType, EntryUrl, Enabled, AuthConfirmed, AuthConfirmedAt,
        ParseMode, FrequencyMinutes, CronExpression, RuleConfigJson, AiFallbackEnabled,
        MaxItemsPerRun, IncludeKeywords, ExcludeKeywords, LastAttemptAt, LastSuccessAt, NextRunAt,
        HealthStatus, HealthMessage, LastErrorCode, LastErrorSummary, DeletedAt, CreatedAt, UpdatedAt,
        ConsecutiveFailures
        """;

    private static async Task<IReadOnlyList<CollectionSource>> ReadSourcesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var sources = new List<CollectionSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) sources.Add(ReadSource(reader));
        return sources;
    }

    private static CollectionSource ReadSource(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5) == 1,
        reader.GetInt32(6) == 1,
        ParseDate(reader, 7),
        reader.GetString(8),
        reader.GetInt32(9),
        reader.GetString(10),
        reader.GetString(11),
        reader.GetInt32(12) == 1,
        reader.GetInt32(13),
        reader.GetString(14),
        reader.GetString(15),
        ParseDate(reader, 16),
        ParseDate(reader, 17),
        ParseDate(reader, 18),
        reader.GetString(19),
        reader.GetString(20),
        reader.GetString(21),
        reader.GetString(22),
        ParseDate(reader, 23),
        DateTimeOffset.Parse(reader.GetString(24)),
        DateTimeOffset.Parse(reader.GetString(25)),
        reader.GetInt32(26));

    private static void AddSourceParameters(SqliteCommand command, CollectionSourceDraft draft)
    {
        var now = DateTimeOffset.Now.ToString("O");
        command.Parameters.AddWithValue("$name", draft.Name.Trim());
        command.Parameters.AddWithValue("$sourceType", draft.SourceType.Trim());
        command.Parameters.AddWithValue("$entryUrl", draft.EntryUrl.Trim());
        command.Parameters.AddWithValue("$enabled", draft.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$authConfirmed", draft.AuthConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$authConfirmedAt", (object?)ToNullableText(draft.AuthConfirmedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$parseMode", draft.ParseMode.Trim());
        command.Parameters.AddWithValue("$frequencyMinutes", draft.FrequencyMinutes);
        command.Parameters.AddWithValue("$cronExpression", draft.CronExpression.Trim());
        command.Parameters.AddWithValue("$ruleConfigJson", draft.RuleConfigJson.Trim());
        command.Parameters.AddWithValue("$aiFallbackEnabled", draft.AiFallbackEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$maxItemsPerRun", draft.MaxItemsPerRun);
        command.Parameters.AddWithValue("$includeKeywords", draft.IncludeKeywords.Trim());
        command.Parameters.AddWithValue("$excludeKeywords", draft.ExcludeKeywords.Trim());
        command.Parameters.AddWithValue("$now", now);
    }

    private static void ValidateDraft(CollectionSourceDraft draft, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new ArgumentException("来源名称不能为空。", parameterName);
        if (!SourceTypes.Contains(draft.SourceType))
            throw new ArgumentException("来源类型无效。", parameterName);
        if (!ParseModes.Contains(draft.ParseMode))
            throw new ArgumentException("解析方式无效。", parameterName);
        if (draft.FrequencyMinutes <= 0)
            throw new ArgumentException("采集频率必须大于 0 分钟。", parameterName);
        if (draft.MaxItemsPerRun <= 0)
            throw new ArgumentException("单次采集上限必须大于 0。", parameterName);
        ValidateJson(draft.RuleConfigJson, parameterName);
    }

    private static void ValidateJson(string value, string parameterName)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                throw new JsonException("配置必须是 JSON 对象。");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("来源规则配置必须是有效的 JSON 对象。", parameterName, exception);
        }
    }

    private static void ValidateHealthStatus(string healthStatus)
    {
        if (healthStatus is not ("healthy" or "degraded" or "failed" or "disabled"))
            throw new ArgumentOutOfRangeException(nameof(healthStatus), "来源健康状态无效。");
    }

    private static string? ToNullableText(DateTimeOffset? value) =>
        value?.ToString("O");

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
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    // 早期实验版本以中文枚举创建过 CollectionSources，其 CHECK 约束拒绝当前的英文枚举值；
    // 旧表整体改名保留（不删除、不重建），数据按列映射搬迁到新结构，迁移幂等。
    private const string LegacyChineseSourceTable = "CollectionSourcesLegacyCn";

    private static async Task RenameLegacyChineseSourceTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type='table' AND name='CollectionSources' AND sql LIKE '%单页%')
                AND NOT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type='table' AND name='CollectionSourcesLegacyCn')
            THEN 1 ELSE 0 END;
            """;
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) != 1) return;

        await using var rename = connection.CreateCommand();
        rename.CommandText = $"ALTER TABLE CollectionSources RENAME TO {LegacyChineseSourceTable}";
        await rename.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyLegacyChineseSourceRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var legacyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columnsCommand = connection.CreateCommand())
        {
            columnsCommand.CommandText = $"PRAGMA table_info({LegacyChineseSourceTable})";
            await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) legacyColumns.Add(reader.GetString(1));
        }
        if (legacyColumns.Count == 0) return;

        // 旧表列集合不完全可控，只引用确实存在的列，缺失列用默认值。
        string Select(string column, string fallback) =>
            legacyColumns.Contains(column) ? $"l.{column}" : fallback;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO CollectionSources (
                AccountKey, Name, SourceType, EntryUrl, Enabled, AuthConfirmed, AuthConfirmedAt,
                ParseMode, FrequencyMinutes, CronExpression, RuleConfigJson, AiFallbackEnabled,
                MaxItemsPerRun, IncludeKeywords, ExcludeKeywords,
                LastAttemptAt, LastSuccessAt, NextRunAt, HealthStatus, HealthMessage,
                LastErrorCode, LastErrorSummary, DeletedAt, CreatedAt, UpdatedAt, ConsecutiveFailures)
            SELECT
                l.AccountKey,
                l.Name,
                CASE l.SourceType
                    WHEN '单页' THEN 'single-page'
                    WHEN 'RSS' THEN 'rss'
                    WHEN '网页栏目' THEN 'web-column'
                    WHEN 'TG' THEN 'telegram'
                    ELSE 'single-page' END,
                l.EntryUrl,
                l.Enabled,
                l.AuthConfirmed,
                l.AuthConfirmedAt,
                'rule-first',
                l.FrequencyMinutes,
                '',
                l.RuleConfigJson,
                l.AiFallbackEnabled,
                l.MaxItemsPerRun,
                {Select("IncludeKeywords", "''")},
                {Select("ExcludeKeywords", "''")},
                {Select("LastAttemptAt", "NULL")},
                {Select("LastSuccessAt", "NULL")},
                {Select("NextRunAt", "NULL")},
                CASE l.HealthStatus
                    WHEN '正常' THEN 'healthy'
                    WHEN '降级' THEN 'degraded'
                    WHEN '失败' THEN 'failed'
                    WHEN '已停用' THEN 'disabled'
                    ELSE 'healthy' END,
                {Select("HealthMessage", "''")},
                {Select("LastErrorCode", "''")},
                {Select("LastErrorSummary", "''")},
                {Select("DeletedAt", "NULL")},
                {Select("CreatedAt", "$now")},
                {Select("UpdatedAt", "$now")},
                0
            FROM {LegacyChineseSourceTable} l
            WHERE NOT EXISTS (
                SELECT 1 FROM CollectionSources s
                WHERE s.AccountKey IS l.AccountKey
                  AND s.Name = l.Name
                  AND s.EntryUrl = l.EntryUrl);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureCollectionSourceColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        (string Name, string Definition)[] columns =
        [
            ("AccountKey", "TEXT NULL"),
            ("Name", "TEXT NOT NULL DEFAULT ''"),
            ("SourceType", "TEXT NOT NULL DEFAULT 'single-page'"),
            ("EntryUrl", "TEXT NOT NULL DEFAULT ''"),
            ("Enabled", "INTEGER NOT NULL DEFAULT 1"),
            ("AuthConfirmed", "INTEGER NOT NULL DEFAULT 0"),
            ("AuthConfirmedAt", "TEXT NULL"),
            ("ParseMode", "TEXT NOT NULL DEFAULT 'rule-first'"),
            ("FrequencyMinutes", "INTEGER NOT NULL DEFAULT 60"),
            ("CronExpression", "TEXT NOT NULL DEFAULT ''"),
            ("RuleConfigJson", "TEXT NOT NULL DEFAULT '{}'"),
            ("AiFallbackEnabled", "INTEGER NOT NULL DEFAULT 0"),
            ("MaxItemsPerRun", "INTEGER NOT NULL DEFAULT 20"),
            ("IncludeKeywords", "TEXT NOT NULL DEFAULT ''"),
            ("ExcludeKeywords", "TEXT NOT NULL DEFAULT ''"),
            ("LastAttemptAt", "TEXT NULL"),
            ("LastSuccessAt", "TEXT NULL"),
            ("NextRunAt", "TEXT NULL"),
            ("HealthStatus", "TEXT NOT NULL DEFAULT 'healthy'"),
            ("HealthMessage", "TEXT NOT NULL DEFAULT ''"),
            ("LastErrorCode", "TEXT NOT NULL DEFAULT ''"),
            ("LastErrorSummary", "TEXT NOT NULL DEFAULT ''"),
            ("DeletedAt", "TEXT NULL"),
            ("CreatedAt", "TEXT NOT NULL DEFAULT ''"),
            ("UpdatedAt", "TEXT NOT NULL DEFAULT ''"),
            ("ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0")
        ];

        foreach (var column in columns)
        {
            await EnsureColumnAsync(connection, "CollectionSources", column.Name, column.Definition, cancellationToken);
        }
    }
}

public sealed record CollectionSourceDraft(
    string Name,
    string SourceType,
    string EntryUrl = "",
    bool Enabled = true,
    bool AuthConfirmed = false,
    DateTimeOffset? AuthConfirmedAt = null,
    string ParseMode = "rule-first",
    int FrequencyMinutes = 60,
    string CronExpression = "",
    string RuleConfigJson = "{}",
    bool AiFallbackEnabled = false,
    int MaxItemsPerRun = 20,
    string IncludeKeywords = "",
    string ExcludeKeywords = "",
    long? Id = null);

public sealed record CollectionSource(
    long Id,
    string Name,
    string SourceType,
    string EntryUrl,
    bool Enabled,
    bool AuthConfirmed,
    DateTimeOffset? AuthConfirmedAt,
    string ParseMode,
    int FrequencyMinutes,
    string CronExpression,
    string RuleConfigJson,
    bool AiFallbackEnabled,
    int MaxItemsPerRun,
    string IncludeKeywords,
    string ExcludeKeywords,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextRunAt,
    string HealthStatus,
    string HealthMessage,
    string LastErrorCode,
    string LastErrorSummary,
    DateTimeOffset? DeletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int ConsecutiveFailures = 0);

public sealed record SourceHealthUpdate(
    long Id,
    string HealthStatus,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextRunAt,
    string HealthMessage = "",
    string LastErrorCode = "",
    string LastErrorSummary = "",
    int ConsecutiveFailures = 0);
