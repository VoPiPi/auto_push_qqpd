using Microsoft.Data.Sqlite;
using System.IO;

namespace QqChannelDesk.Services;

/// <summary>Stores persistent multi-channel Telegram collection settings.</summary>
public sealed class TelegramCollectionStore
{
    private readonly string _databasePath;
    private readonly AccountContext? _accountContext;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public TelegramCollectionStore(string? databasePath = null, AccountContext? accountContext = null)
    {
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");
        _accountContext = accountContext;
    }

    public string DatabasePath => _databasePath;

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
                CREATE TABLE IF NOT EXISTS TelegramCollectionSchedules (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ChannelUrl TEXT NOT NULL,
                    ChannelName TEXT NOT NULL DEFAULT '',
                    IntervalHours INTEGER NOT NULL DEFAULT 24,
                    Enabled INTEGER NOT NULL DEFAULT 1,
                    ProxySelection TEXT NOT NULL DEFAULT 'auto',
                    MessageLimit INTEGER NOT NULL DEFAULT 20,
                    Keyword TEXT NOT NULL DEFAULT '',
                    IncludeKeyword TEXT NOT NULL DEFAULT '',
                    LastCollectedAt TEXT NULL,
                    LastSuccessAt TEXT NULL,
                    LastStatus TEXT NOT NULL DEFAULT '从未采集',
                    LastError TEXT NOT NULL DEFAULT '',
                    LastAddedCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    FrequencyType TEXT NOT NULL DEFAULT 'daily',
                    FrequencyValue INTEGER NOT NULL DEFAULT 1,
                    Weekday INTEGER NOT NULL DEFAULT 1,
                    TriggerTime TEXT NOT NULL DEFAULT '00:00',
                    CronExpression TEXT NOT NULL DEFAULT '',
                    AutoScheduleEnabled INTEGER NOT NULL DEFAULT 0,
                    AutoScheduleDelayMinutes INTEGER NOT NULL DEFAULT 30,
                    AutoScheduleMaxPerRun INTEGER NOT NULL DEFAULT 20,
                    LastAttemptAt TEXT NULL,
                    NextRetryAt TEXT NULL,
                    RetryAttempt INTEGER NOT NULL DEFAULT 0,
                    FirstRunMode TEXT NOT NULL DEFAULT 'backfill',
                    LastMessageId INTEGER NOT NULL DEFAULT 0,
                    AccountKey TEXT NULL,
                    UNIQUE (AccountKey, ChannelUrl)
                );
                CREATE TABLE IF NOT EXISTS TelegramCollectionDistributionRules (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ScheduleId INTEGER NOT NULL,
                    MessageType TEXT NOT NULL,
                    Keyword TEXT NOT NULL DEFAULT '',
                    GuildId TEXT NOT NULL DEFAULT '',
                    GuildName TEXT NOT NULL DEFAULT '',
                    ChannelId TEXT NOT NULL DEFAULT '',
                    ChannelName TEXT NOT NULL DEFAULT '',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    AccountKey TEXT NULL,
                    FOREIGN KEY (ScheduleId) REFERENCES TelegramCollectionSchedules(Id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS TelegramCollectionRunHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ScheduleId INTEGER NOT NULL,
                    ChannelUrl TEXT NOT NULL,
                    ChannelName TEXT NOT NULL DEFAULT '',
                    TriggerSource TEXT NOT NULL DEFAULT 'manual',
                    StartedAt TEXT NOT NULL,
                    CompletedAt TEXT NOT NULL,
                    Status TEXT NOT NULL DEFAULT '',
                    AddedCount INTEGER NOT NULL DEFAULT 0,
                    SkippedCount INTEGER NOT NULL DEFAULT 0,
                    MatchedRuleCount INTEGER NOT NULL DEFAULT 0,
                    UnmatchedCount INTEGER NOT NULL DEFAULT 0,
                    FailedMediaCount INTEGER NOT NULL DEFAULT 0,
                    AutoScheduledCount INTEGER NOT NULL DEFAULT 0,
                    AutoScheduleSkippedCount INTEGER NOT NULL DEFAULT 0,
                    AutoScheduleReasons TEXT NOT NULL DEFAULT '',
                    PageCount INTEGER NOT NULL DEFAULT 0,
                    LastMessageId INTEGER NOT NULL DEFAULT 0,
                    Error TEXT NOT NULL DEFAULT '',
                    AccountKey TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_TelegramCollectionDistributionRules_Schedule
                    ON TelegramCollectionDistributionRules (AccountKey, ScheduleId, SortOrder, Id);
                CREATE INDEX IF NOT EXISTS IX_TelegramCollectionSchedules_AccountEnabled
                    ON TelegramCollectionSchedules (AccountKey, Enabled, LastCollectedAt);
                CREATE INDEX IF NOT EXISTS IX_TelegramCollectionRunHistory_AccountStarted
                    ON TelegramCollectionRunHistory (AccountKey, StartedAt DESC, Id DESC);
                CREATE INDEX IF NOT EXISTS IX_TelegramCollectionRunHistory_AccountScheduleStarted
                    ON TelegramCollectionRunHistory (AccountKey, ScheduleId, StartedAt DESC, Id DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureAccountKeyColumnAsync(connection, cancellationToken);
            await EnsureColumnAsync(connection, "FrequencyType", "TEXT NOT NULL DEFAULT 'legacy'", cancellationToken);
            await EnsureColumnAsync(connection, "FrequencyValue", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "Weekday", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "TriggerTime", "TEXT NOT NULL DEFAULT '00:00'", cancellationToken);
            await EnsureColumnAsync(connection, "CronExpression", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            await EnsureColumnAsync(connection, "AutoScheduleEnabled", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "AutoScheduleDelayMinutes", "INTEGER NOT NULL DEFAULT 30", cancellationToken);
            await EnsureColumnAsync(connection, "AutoScheduleMaxPerRun", "INTEGER NOT NULL DEFAULT 20", cancellationToken);
            await EnsureColumnAsync(connection, "LastAttemptAt", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "NextRetryAt", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "RetryAttempt", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "FirstRunMode", "TEXT NOT NULL DEFAULT 'backfill'", cancellationToken);
            await EnsureColumnAsync(connection, "LastMessageId", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "IncludeKeyword", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<IReadOnlyList<TelegramCollectionSchedule>> GetAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, ChannelUrl, ChannelName, IntervalHours, Enabled, ProxySelection, MessageLimit, Keyword,
                   LastCollectedAt, LastSuccessAt, LastStatus, LastError, LastAddedCount, CreatedAt, UpdatedAt,
                   FrequencyType, FrequencyValue, Weekday, TriggerTime, CronExpression,
                   AutoScheduleEnabled, AutoScheduleDelayMinutes, AutoScheduleMaxPerRun,
                   LastAttemptAt, NextRetryAt, RetryAttempt, FirstRunMode, LastMessageId, IncludeKeyword
            FROM TelegramCollectionSchedules
            WHERE {Scope("AccountKey")}
            ORDER BY Enabled DESC, ChannelName COLLATE NOCASE, Id;
            """;
        AddAccountParameter(command, accountKey);
        var result = new List<TelegramCollectionSchedule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<TelegramCollectionSchedule?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var scope = Scope("AccountKey");
        command.CommandText = $"SELECT Id, ChannelUrl, ChannelName, IntervalHours, Enabled, ProxySelection, MessageLimit, Keyword, LastCollectedAt, LastSuccessAt, LastStatus, LastError, LastAddedCount, CreatedAt, UpdatedAt, FrequencyType, FrequencyValue, Weekday, TriggerTime, CronExpression, AutoScheduleEnabled, AutoScheduleDelayMinutes, AutoScheduleMaxPerRun, LastAttemptAt, NextRetryAt, RetryAttempt, FirstRunMode, LastMessageId, IncludeKeyword FROM TelegramCollectionSchedules WHERE Id=$id AND {scope}";
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public Task<long> SaveAsync(
        TelegramCollectionScheduleDraft draft,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(draft, null, cancellationToken);

    /// <summary>
    /// 在同一 SQLite 事务中保存定时配置和分发规则，任一步失败都会整体回滚，
    /// 避免界面出现“计划已保存但规则仍是旧版本”的半成功状态。
    /// </summary>
    public Task<long> SaveWithRulesAsync(
        TelegramCollectionScheduleDraft draft,
        IReadOnlyList<TelegramDistributionRuleDraft> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return SaveCoreAsync(draft, rules, cancellationToken);
    }

    private async Task<long> SaveCoreAsync(
        TelegramCollectionScheduleDraft draft,
        IReadOnlyList<TelegramDistributionRuleDraft>? rules,
        CancellationToken cancellationToken)
    {
        var accountKey = RequireAccountKey();
        if (string.IsNullOrWhiteSpace(draft.ChannelUrl)) throw new ArgumentException("TG 频道地址不能为空。", nameof(draft));
        var frequencyType = NormalizeFrequencyType(draft.FrequencyType);
        var frequencyValue = NormalizeFrequencyValue(frequencyType, draft.FrequencyValue);
        var weekday = NormalizeWeekday(draft.Weekday);
        var triggerTime = NormalizeTriggerTime(draft.TriggerTime);
        var interval = NormalizeIntervalHours(draft.IntervalHours > 0 ? draft.IntervalHours : LegacyIntervalFor(frequencyType, frequencyValue));
        var limit = TelegramCollectorService.NormalizeMessageLimit(draft.MessageLimit);
        var cron = NormalizeCronExpression(draft.CronExpression, frequencyType, frequencyValue, weekday, triggerTime);
        var autoScheduleDelay = NormalizeAutoScheduleDelay(draft.AutoScheduleDelayMinutes);
        var autoScheduleMaxPerRun = NormalizeAutoScheduleMaxPerRun(draft.AutoScheduleMaxPerRun);
        var firstRunMode = NormalizeFirstRunMode(draft.FirstRunMode);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        try
        {
            var id = await SaveCoreAsync(
                connection,
                transaction,
                draft,
                accountKey,
                frequencyType,
                frequencyValue,
                weekday,
                triggerTime,
                interval,
                limit,
                cron,
                autoScheduleDelay,
                autoScheduleMaxPerRun,
                firstRunMode,
                cancellationToken);
            if (rules is not null)
                await ReplaceRulesCoreAsync(connection, transaction, accountKey, id, rules, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("这个 TG 频道已经配置过了。", ex);
        }
    }

    private async Task<long> SaveCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TelegramCollectionScheduleDraft draft,
        string accountKey,
        string frequencyType,
        int frequencyValue,
        int weekday,
        string triggerTime,
        int interval,
        int limit,
        string cron,
        int autoScheduleDelay,
        int autoScheduleMaxPerRun,
        string firstRunMode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var now = DateTimeOffset.Now.ToString("O");
        if (draft.Id is null)
        {
            command.CommandText = """
                INSERT INTO TelegramCollectionSchedules
                    (ChannelUrl, ChannelName, IntervalHours, Enabled, ProxySelection, MessageLimit, Keyword, IncludeKeyword,
                     LastStatus, CreatedAt, UpdatedAt, FrequencyType, FrequencyValue, Weekday, TriggerTime, CronExpression,
                     AutoScheduleEnabled, AutoScheduleDelayMinutes, AutoScheduleMaxPerRun, FirstRunMode, AccountKey)
                VALUES ($url,$name,$interval,$enabled,$proxy,$limit,$keyword,$includeKeyword,'从未采集',$now,$now,$frequencyType,$frequencyValue,$weekday,$triggerTime,$cron,
                        $autoEnabled,$autoDelay,$autoMaxPerRun,$firstRunMode,$accountKey);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = $"""
                UPDATE TelegramCollectionSchedules SET ChannelUrl=$url, ChannelName=$name,
                    IntervalHours=$interval, Enabled=$enabled, ProxySelection=$proxy, MessageLimit=$limit,
                    Keyword=$keyword, IncludeKeyword=$includeKeyword, FrequencyType=$frequencyType, FrequencyValue=$frequencyValue,
                    Weekday=$weekday, TriggerTime=$triggerTime, CronExpression=$cron,
                    AutoScheduleEnabled=$autoEnabled, AutoScheduleDelayMinutes=$autoDelay,
                    AutoScheduleMaxPerRun=$autoMaxPerRun,
                    LastMessageId=CASE WHEN ChannelUrl<>$url OR FirstRunMode<>$firstRunMode THEN 0 ELSE LastMessageId END,
                    FirstRunMode=$firstRunMode, UpdatedAt=$now
                WHERE Id=$id AND {Scope("AccountKey")};
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", draft.Id.Value);
        }
        command.Parameters.AddWithValue("$url", draft.ChannelUrl.Trim());
        command.Parameters.AddWithValue("$name", draft.ChannelName.Trim());
        command.Parameters.AddWithValue("$interval", interval);
        command.Parameters.AddWithValue("$enabled", draft.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$proxy", string.IsNullOrWhiteSpace(draft.ProxySelection) ? WebProxySelection.Automatic : draft.ProxySelection.Trim());
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$keyword", draft.Keyword.Trim());
        command.Parameters.AddWithValue("$includeKeyword", draft.IncludeKeyword.Trim());
        command.Parameters.AddWithValue("$frequencyType", frequencyType);
        command.Parameters.AddWithValue("$frequencyValue", frequencyValue);
        command.Parameters.AddWithValue("$weekday", weekday);
        command.Parameters.AddWithValue("$triggerTime", triggerTime);
        command.Parameters.AddWithValue("$cron", cron);
        command.Parameters.AddWithValue("$autoEnabled", draft.AutoScheduleEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$autoDelay", autoScheduleDelay);
        command.Parameters.AddWithValue("$autoMaxPerRun", autoScheduleMaxPerRun);
        command.Parameters.AddWithValue("$firstRunMode", firstRunMode);
        command.Parameters.AddWithValue("$now", now);
        AddAccountParameter(command, accountKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var scope = Scope("AccountKey");
        await using var transaction = connection.BeginTransaction();
        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = $"DELETE FROM TelegramCollectionRunHistory WHERE ScheduleId=$id AND {Scope("AccountKey")}";
            history.Parameters.AddWithValue("$id", id);
            AddAccountParameter(history, accountKey);
            await history.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var rules = connection.CreateCommand())
        {
            rules.Transaction = transaction;
            rules.CommandText = $"DELETE FROM TelegramCollectionDistributionRules WHERE ScheduleId=$id AND {Scope("AccountKey")}";
            rules.Parameters.AddWithValue("$id", id);
            AddAccountParameter(rules, accountKey);
            await rules.ExecuteNonQueryAsync(cancellationToken);
        }
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM TelegramCollectionSchedules WHERE Id=$id AND {scope}";
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE TelegramCollectionSchedules SET Enabled=$enabled, UpdatedAt=$now WHERE Id=$id AND {Scope("AccountKey")}";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TelegramDistributionRule>> GetRulesAsync(long scheduleId, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, ScheduleId, MessageType, Keyword, GuildId, GuildName, ChannelId, ChannelName,
                   SortOrder, CreatedAt, UpdatedAt
            FROM TelegramCollectionDistributionRules
            WHERE ScheduleId=$scheduleId AND {Scope("AccountKey")}
            ORDER BY SortOrder, Id;
            """;
        command.Parameters.AddWithValue("$scheduleId", scheduleId);
        AddAccountParameter(command, accountKey);
        var rules = new List<TelegramDistributionRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rules.Add(ReadRule(reader));
        return rules;
    }

    /// <summary>
    /// 一次读取多个 Schedule 的分发规则（按 ScheduleId 分组），避免界面按 Schedule 逐个查询造成 N+1。
    /// 未命中的 Schedule 不会出现在结果字典里，调用方按“无规则”处理即可。
    /// </summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<TelegramDistributionRule>>> GetRulesByScheduleIdsAsync(
        IReadOnlyCollection<long> scheduleIds,
        CancellationToken cancellationToken = default)
    {
        var grouped = new Dictionary<long, IReadOnlyList<TelegramDistributionRule>>();
        if (scheduleIds is null || scheduleIds.Count == 0) return grouped;
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return grouped;
        var ids = scheduleIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return grouped;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        // SQLite 绑定参数数量有上限，按固定大小分批查询，避免配置很多频道时 SQL 过长。
        foreach (var batch in ids.Chunk(400))
        {
            await using var command = connection.CreateCommand();
            var names = batch.Select((_, index) => $"$id{index}").ToArray();
            command.CommandText = $"""
                SELECT Id, ScheduleId, MessageType, Keyword, GuildId, GuildName, ChannelId, ChannelName,
                       SortOrder, CreatedAt, UpdatedAt
                FROM TelegramCollectionDistributionRules
                WHERE ScheduleId IN ({string.Join(", ", names)}) AND {Scope("AccountKey")}
                ORDER BY ScheduleId, SortOrder, Id;
                """;
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            AddAccountParameter(command, accountKey);
            var buckets = new Dictionary<long, List<TelegramDistributionRule>>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rule = ReadRule(reader);
                if (!buckets.TryGetValue(rule.ScheduleId, out var bucket))
                {
                    bucket = [];
                    buckets[rule.ScheduleId] = bucket;
                }
                bucket.Add(rule);
            }
            foreach (var pair in buckets) grouped[pair.Key] = pair.Value;
        }
        return grouped;
    }

    public async Task ReplaceRulesAsync(long scheduleId, IReadOnlyList<TelegramDistributionRuleDraft> rules, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await ReplaceRulesCoreAsync(connection, transaction, accountKey, scheduleId, rules, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ReplaceRulesCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountKey,
        long scheduleId,
        IReadOnlyList<TelegramDistributionRuleDraft> rules,
        CancellationToken cancellationToken)
    {
        await using (var scheduleScope = connection.CreateCommand())
        {
            scheduleScope.Transaction = transaction;
            scheduleScope.CommandText = accountKey.Length == 0
                ? "SELECT 1 FROM TelegramCollectionSchedules WHERE Id=$scheduleId LIMIT 1;"
                : "SELECT 1 FROM TelegramCollectionSchedules WHERE Id=$scheduleId AND AccountKey=$accountKey LIMIT 1;";
            scheduleScope.Parameters.AddWithValue("$scheduleId", scheduleId);
            if (accountKey.Length > 0) scheduleScope.Parameters.AddWithValue("$accountKey", accountKey);
            if (await scheduleScope.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("TG 定时采集配置不存在。");
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = accountKey.Length == 0
                ? "DELETE FROM TelegramCollectionDistributionRules WHERE ScheduleId=$scheduleId"
                : "DELETE FROM TelegramCollectionDistributionRules WHERE ScheduleId=$scheduleId AND AccountKey=$accountKey";
            delete.Parameters.AddWithValue("$scheduleId", scheduleId);
            if (accountKey.Length > 0) delete.Parameters.AddWithValue("$accountKey", accountKey);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        var now = DateTimeOffset.Now.ToString("O");
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            if (rule.MessageType is not ("text" or "image" or "video")) throw new ArgumentException("分发规则消息类型无效。", nameof(rules));
            if (string.IsNullOrWhiteSpace(rule.ChannelId) || string.IsNullOrWhiteSpace(rule.GuildId)) throw new ArgumentException("分发规则必须选择频道和版块。", nameof(rules));
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO TelegramCollectionDistributionRules
                    (ScheduleId, MessageType, Keyword, GuildId, GuildName, ChannelId, ChannelName, SortOrder, CreatedAt, UpdatedAt, AccountKey)
                VALUES ($scheduleId,$type,$keyword,$guildId,$guildName,$channelId,$channelName,$sortOrder,$created,$updated,$accountKey)
                """;
            insert.Parameters.AddWithValue("$scheduleId", scheduleId);
            insert.Parameters.AddWithValue("$type", rule.MessageType.Trim().ToLowerInvariant());
            insert.Parameters.AddWithValue("$keyword", rule.Keyword?.Trim() ?? "");
            insert.Parameters.AddWithValue("$guildId", rule.GuildId.Trim());
            insert.Parameters.AddWithValue("$guildName", rule.GuildName?.Trim() ?? "");
            insert.Parameters.AddWithValue("$channelId", rule.ChannelId.Trim());
            insert.Parameters.AddWithValue("$channelName", rule.ChannelName?.Trim() ?? "");
            insert.Parameters.AddWithValue("$sortOrder", index);
            insert.Parameters.AddWithValue("$created", now);
            insert.Parameters.AddWithValue("$updated", now);
            insert.Parameters.AddWithValue("$accountKey", accountKey.Length == 0 ? DBNull.Value : accountKey);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpdateSuccessAsync(
        long id,
        DateTimeOffset attemptedAt,
        int addedCount,
        string status,
        string? lastMessageId = null,
        CancellationToken cancellationToken = default,
        string? detail = null)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var scope = Scope("AccountKey");
        var watermarkUpdate = long.TryParse(lastMessageId, out var parsedMessageId) && parsedMessageId > 0
            ? "LastMessageId=CASE WHEN $lastMessageId>LastMessageId THEN $lastMessageId ELSE LastMessageId END,"
            : "";
        command.CommandText = $"""
            UPDATE TelegramCollectionSchedules
            SET {watermarkUpdate}
                LastCollectedAt=$attempted, LastSuccessAt=$attempted, LastAttemptAt=$attempted,
                NextRetryAt=NULL, RetryAttempt=0, LastStatus=$status, LastError=$error,
                LastAddedCount=$count, UpdatedAt=$now
            WHERE Id=$id AND {scope};
            """;
        command.Parameters.AddWithValue("$attempted", attemptedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$status",
            CliDiagnostics.Sanitize(string.IsNullOrWhiteSpace(status) ? "成功" : status.Trim()));
        command.Parameters.AddWithValue("$error", CliDiagnostics.Sanitize(detail ?? ""));
        command.Parameters.AddWithValue("$count", addedCount);
        if (watermarkUpdate.Length > 0) command.Parameters.AddWithValue("$lastMessageId", parsedMessageId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveRunHistoryAsync(
        TelegramCollectionRunHistory history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TelegramCollectionRunHistory
                (ScheduleId, ChannelUrl, ChannelName, TriggerSource, StartedAt, CompletedAt, Status,
                 AddedCount, SkippedCount, MatchedRuleCount, UnmatchedCount, FailedMediaCount,
                 AutoScheduledCount, AutoScheduleSkippedCount, AutoScheduleReasons,
                 PageCount, LastMessageId, Error, AccountKey)
            VALUES
                ($scheduleId, $channelUrl, $channelName, $triggerSource, $startedAt, $completedAt, $status,
                 $added, $skipped, $matched, $unmatched, $failedMedia,
                 $autoScheduled, $autoSkipped, $autoReasons,
                 $pageCount, $lastMessageId, $error, $accountKey);
            """;
        command.Parameters.AddWithValue("$scheduleId", history.ScheduleId);
        command.Parameters.AddWithValue("$channelUrl", history.ChannelUrl);
        command.Parameters.AddWithValue("$channelName", history.ChannelName);
        command.Parameters.AddWithValue("$triggerSource", string.IsNullOrWhiteSpace(history.TriggerSource) ? "manual" : history.TriggerSource);
        command.Parameters.AddWithValue("$startedAt", history.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", history.CompletedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$status",
            CliDiagnostics.Sanitize(string.IsNullOrWhiteSpace(history.Status) ? "未知" : history.Status.Trim()));
        command.Parameters.AddWithValue("$added", Math.Max(0, history.AddedCount));
        command.Parameters.AddWithValue("$skipped", Math.Max(0, history.SkippedCount));
        command.Parameters.AddWithValue("$matched", Math.Max(0, history.MatchedRuleCount));
        command.Parameters.AddWithValue("$unmatched", Math.Max(0, history.UnmatchedCount));
        command.Parameters.AddWithValue("$failedMedia", Math.Max(0, history.FailedMediaCount));
        command.Parameters.AddWithValue("$autoScheduled", Math.Max(0, history.AutoScheduledCount));
        command.Parameters.AddWithValue("$autoSkipped", Math.Max(0, history.AutoScheduleSkippedCount));
        command.Parameters.AddWithValue("$autoReasons", CliDiagnostics.Sanitize(history.AutoScheduleReasons ?? ""));
        command.Parameters.AddWithValue("$pageCount", Math.Max(0, history.PageCount));
        command.Parameters.AddWithValue("$lastMessageId", Math.Max(0, history.LastMessageId));
        command.Parameters.AddWithValue("$error", CliDiagnostics.Sanitize(history.Error ?? ""));
        command.Parameters.AddWithValue("$accountKey", accountKey.Length == 0 ? DBNull.Value : accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = $"""
            DELETE FROM TelegramCollectionRunHistory
            WHERE Id IN (
                SELECT Id
                FROM TelegramCollectionRunHistory
                WHERE ScheduleId=$scheduleId AND {Scope("AccountKey")}
                ORDER BY StartedAt DESC, Id DESC
                LIMIT -1 OFFSET $keep
            );
            """;
        trim.Parameters.AddWithValue("$scheduleId", history.ScheduleId);
        trim.Parameters.AddWithValue("$keep", RunHistoryRetentionLimit);
        AddAccountParameter(trim, accountKey);
        await trim.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TelegramCollectionRunHistory>> GetRunHistoryAsync(
        int limit = 200,
        string? search = null,
        long? scheduleId = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var scopedSearch = search?.Trim() ?? "";
        var scopedStatus = status?.Trim() ?? "";
        command.CommandText = $"""
            SELECT Id, ScheduleId, ChannelUrl, ChannelName, TriggerSource, StartedAt, CompletedAt, Status,
                   AddedCount, SkippedCount, MatchedRuleCount, UnmatchedCount, FailedMediaCount,
                   AutoScheduledCount, AutoScheduleSkippedCount, AutoScheduleReasons,
                   PageCount, LastMessageId, Error
            FROM TelegramCollectionRunHistory
            WHERE {Scope("AccountKey")}
              AND ($scheduleId<=0 OR ScheduleId=$scheduleId)
              AND ($status='' OR Status=$status)
              AND ($search='' OR ChannelName LIKE $pattern ESCAPE '\'
                   OR ChannelUrl LIKE $pattern ESCAPE '\'
                   OR Status LIKE $pattern ESCAPE '\'
                   OR Error LIKE $pattern ESCAPE '\')
            ORDER BY StartedAt DESC, Id DESC
            LIMIT $limit;
            """;
        AddAccountParameter(command, accountKey);
        command.Parameters.AddWithValue("$search", scopedSearch);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikePattern(scopedSearch)}%");
        command.Parameters.AddWithValue("$scheduleId", scheduleId is > 0 ? scheduleId.Value : 0);
        command.Parameters.AddWithValue("$status", scopedStatus);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit <= 0 ? 200 : limit, 1, 1000));
        var results = new List<TelegramCollectionRunHistory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TelegramCollectionRunHistory(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseDate(reader, 5) ?? DateTimeOffset.MinValue,
                ParseDate(reader, 6) ?? DateTimeOffset.MinValue,
                CliDiagnostics.Sanitize(reader.GetString(7)),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                CliDiagnostics.Sanitize(reader.GetString(15)),
                reader.GetInt32(16),
                reader.GetInt64(17),
                CliDiagnostics.Sanitize(reader.GetString(18))));
        }
        return results;
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task UpdateFailureAsync(
        long id,
        DateTimeOffset attemptedAt,
        int addedCount,
        string error,
        string? status = null,
        bool automaticRetry = true,
        CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var retryAt5 = attemptedAt.AddMinutes(5).ToString("O");
        var retryAt15 = attemptedAt.AddMinutes(15).ToString("O");
        var retryAt30 = attemptedAt.AddMinutes(30).ToString("O");
        var retryAt60 = attemptedAt.AddMinutes(60).ToString("O");
        command.CommandText = $"""
            UPDATE TelegramCollectionSchedules
            SET LastAttemptAt=$attempted,
                NextRetryAt=CASE
                    WHEN $automaticRetry=0 THEN NextRetryAt
                    WHEN RetryAttempt+1 >= $maximumRetries THEN NULL
                    WHEN RetryAttempt=0 THEN $retry5
                    WHEN RetryAttempt=1 THEN $retry15
                    WHEN RetryAttempt=2 THEN $retry30
                    ELSE $retry60
                END,
                RetryAttempt=CASE
                    WHEN $automaticRetry=0 THEN RetryAttempt
                    WHEN RetryAttempt >= $maximumRetries THEN $maximumRetries
                    ELSE RetryAttempt+1
                END,
                LastStatus=CASE
                    WHEN RetryAttempt >= $maximumRetries AND NextRetryAt IS NULL THEN $stoppedStatus
                    WHEN $automaticRetry=1 AND RetryAttempt+1 >= $maximumRetries THEN $stoppedStatus
                    ELSE $status
                END,
                LastError=$error,
                LastAddedCount=$count, UpdatedAt=$now
            WHERE Id=$id AND {Scope("AccountKey")};
            """;
        command.Parameters.AddWithValue("$attempted", attemptedAt.ToString("O"));
        command.Parameters.AddWithValue("$retry5", retryAt5);
        command.Parameters.AddWithValue("$retry15", retryAt15);
        command.Parameters.AddWithValue("$retry30", retryAt30);
        command.Parameters.AddWithValue("$retry60", retryAt60);
        command.Parameters.AddWithValue("$maximumRetries", MaxAutomaticRetryAttempts);
        command.Parameters.AddWithValue("$automaticRetry", automaticRetry ? 1 : 0);
        command.Parameters.AddWithValue("$stoppedStatus", AutomaticRetryStoppedStatus);
        command.Parameters.AddWithValue(
            "$status",
            CliDiagnostics.Sanitize(string.IsNullOrWhiteSpace(status) ? "失败" : status.Trim()));
        command.Parameters.AddWithValue("$error", CliDiagnostics.Sanitize(error ?? ""));
        command.Parameters.AddWithValue("$count", addedCount);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateCancelledAsync(
        long id,
        DateTimeOffset attemptedAt,
        int addedCount,
        string error,
        CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE TelegramCollectionSchedules
            SET LastAttemptAt=$attempted, LastStatus='已取消', LastError=$error,
                NextRetryAt=NULL, RetryAttempt=0,
                LastAddedCount=$count, UpdatedAt=$now
            WHERE Id=$id AND {Scope("AccountKey")};
            """;
        command.Parameters.AddWithValue("$attempted", attemptedAt.ToString("O"));
        command.Parameters.AddWithValue("$error", CliDiagnostics.Sanitize(error ?? ""));
        command.Parameters.AddWithValue("$count", addedCount);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateResultAsync(
        long id,
        DateTimeOffset collectedAt,
        bool succeeded,
        int addedCount,
        string error,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.Now.ToString("O");
        command.CommandText = succeeded
            ? $"""
                UPDATE TelegramCollectionSchedules
                SET LastCollectedAt=$collected, LastSuccessAt=$collected, LastAttemptAt=$collected,
                    NextRetryAt=NULL, RetryAttempt=0, LastStatus=$status, LastError=$error,
                    LastAddedCount=$count, UpdatedAt=$now
                WHERE Id=$id AND {Scope("AccountKey")};
                """
            : $"""
                UPDATE TelegramCollectionSchedules
                SET LastAttemptAt=$collected, LastStatus=$status, LastError=$error,
                    LastAddedCount=$count, UpdatedAt=$now
                WHERE Id=$id AND {Scope("AccountKey")};
                """;
        command.Parameters.AddWithValue("$collected", collectedAt.ToString("O"));
        command.Parameters.AddWithValue("$success", succeeded ? 1 : 0);
        command.Parameters.AddWithValue(
            "$status",
            CliDiagnostics.Sanitize(string.IsNullOrWhiteSpace(status) ? (succeeded ? "成功" : "失败") : status.Trim()));
        command.Parameters.AddWithValue("$error", CliDiagnostics.Sanitize(error ?? ""));
        command.Parameters.AddWithValue("$count", addedCount);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static bool IsDue(DateTimeOffset? lastCollectedAt, DateTimeOffset now, TimeSpan? interval = null) =>
        lastCollectedAt is null || now - lastCollectedAt.Value >= (interval ?? TimeSpan.FromHours(24));

    public static bool IsDue(TelegramCollectionSchedule schedule, DateTimeOffset now)
    {
        if (IsAutomaticRetryStopped(schedule))
            return false;

        if (schedule.NextRetryAt is { } nextRetryAt)
            return now >= nextRetryAt;

        if (string.IsNullOrWhiteSpace(schedule.CronExpression) &&
            string.Equals(schedule.FrequencyType, "legacy", StringComparison.OrdinalIgnoreCase))
            return IsDue(schedule.LastCollectedAt, now, TimeSpan.FromHours(schedule.IntervalHours));

        if (string.IsNullOrWhiteSpace(schedule.CronExpression) &&
            string.Equals(schedule.FrequencyType, "every-days", StringComparison.OrdinalIgnoreCase) &&
            schedule.FrequencyValue > 1)
        {
            if (!TimeSpan.TryParseExact(schedule.TriggerTime, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var everyDaysTrigger))
                everyDaysTrigger = TimeSpan.Zero;
            var everyDaysNow = now.ToLocalTime();
            var everyDaysTriggerToday = everyDaysNow.Date + everyDaysTrigger;
            if (everyDaysNow.DateTime < everyDaysTriggerToday) return false;
            return schedule.LastCollectedAt is null ||
                ((everyDaysNow.Date - schedule.LastCollectedAt.Value.ToLocalTime().Date).Days >= NormalizeFrequencyValue("every-days", schedule.FrequencyValue) &&
                 schedule.LastCollectedAt.Value.ToLocalTime().DateTime < everyDaysTriggerToday);
        }

        // EffectiveCronExpression may be synthesized only for displaying legacy
        // records. Scheduling must use Cron only after the user explicitly saved it.
        if (!string.IsNullOrWhiteSpace(schedule.CronExpression) &&
            TelegramCronSchedule.TryParse(schedule.CronExpression, out var cron, out _))
            return cron.IsDue(schedule.LastCollectedAt, now);

        if (!TimeSpan.TryParseExact(schedule.TriggerTime, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var trigger))
            trigger = TimeSpan.Zero;
        // Legacy schedules were stored with their original offset. Use that value
        // while reading old records so their behavior remains stable across hosts.
        var localNow = now;
        var triggerToday = localNow.Date + trigger;
        if (localNow.DateTime < triggerToday) return false;

        var last = schedule.LastCollectedAt;
        if (string.Equals(schedule.FrequencyType, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            var desiredDay = Math.Clamp(schedule.Weekday, 1, 7);
            var currentDay = (int)localNow.DayOfWeek;
            currentDay = currentDay == 0 ? 7 : currentDay;
            return currentDay == desiredDay && (schedule.LastCollectedAt is null || last < triggerToday);
        }

        if (string.Equals(schedule.FrequencyType, "every-days", StringComparison.OrdinalIgnoreCase))
            return last is null ||
                ((localNow.Date - last.Value.Date).Days >= NormalizeFrequencyValue("every-days", schedule.FrequencyValue)
                 && last < triggerToday);

        return last is null || last < triggerToday;
    }

    public const int MaxAutomaticRetryAttempts = 8;

    public const int RunHistoryRetentionLimit = 500;

    public const string AutomaticRetryStoppedStatus = "失败（自动重试已停止）";

    public static bool IsAutomaticRetryStopped(TelegramCollectionSchedule schedule) =>
        schedule.RetryAttempt >= MaxAutomaticRetryAttempts &&
        schedule.NextRetryAt is null &&
        schedule.LastStatus.StartsWith("失败", StringComparison.Ordinal);

    public static int NormalizeIntervalHours(int hours) => hours <= 0 ? 24 : Math.Clamp(hours, 1, 24 * 30);

    public static string NormalizeFrequencyType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "every-days" => "every-days",
        "weekly" => "weekly",
        "legacy" => "legacy",
        _ => "daily"
    };

    public static int NormalizeFrequencyValue(string frequencyType, int value) =>
        string.Equals(frequencyType, "every-days", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(value <= 0 ? 1 : value, 1, 365)
            : 1;

    public static int NormalizeWeekday(int weekday) => Math.Clamp(weekday <= 0 ? 1 : weekday, 1, 7);

    public static int NormalizeAutoScheduleDelay(int minutes) => Math.Clamp(minutes, 0, 7 * 24 * 60);

    public static int NormalizeAutoScheduleMaxPerRun(int count) => Math.Clamp(count <= 0 ? 20 : count, 1, 200);

    public static string NormalizeFirstRunMode(string? value) =>
        string.Equals(value?.Trim(), "new-only", StringComparison.OrdinalIgnoreCase) ? "new-only" : "backfill";

    public static string NormalizeTriggerTime(string? value) =>
        TimeSpan.TryParseExact(value?.Trim(), "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time)
            ? time.ToString("hh\\:mm", System.Globalization.CultureInfo.InvariantCulture)
            : "00:00";

    public static string NormalizeCronExpression(string? value, string frequencyType = "daily", int frequencyValue = 1, int weekday = 1, string triggerTime = "00:00")
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length > 0)
        {
            if (!TelegramCronSchedule.TryParse(trimmed, out var parsed, out var error)) throw new ArgumentException($"计划表达式无效：{error}", nameof(value));
            return parsed.Expression;
        }
        var time = NormalizeTriggerTime(triggerTime).Split(':');
        var minute = time[1];
        var hour = time[0];
        if (string.Equals(frequencyType, "legacy", StringComparison.OrdinalIgnoreCase))
            return "";
        if (string.Equals(frequencyType, "every-days", StringComparison.OrdinalIgnoreCase) && NormalizeFrequencyValue(frequencyType, frequencyValue) > 1)
            return "";
        return frequencyType switch
        {
            "weekly" => $"{minute} {hour} * * {NormalizeWeekday(weekday) % 7}",
            "every-days" when NormalizeFrequencyValue(frequencyType, frequencyValue) == 1 => $"{minute} {hour} * * *",
            _ => $"{minute} {hour} * * *"
        };
    }

    public static string LegacyCronFor(TelegramCollectionSchedule schedule) =>
        string.Equals(schedule.FrequencyType, "every-days", StringComparison.OrdinalIgnoreCase) && schedule.FrequencyValue > 1
            ? NormalizeCronExpression(null, "daily", 1, schedule.Weekday, schedule.TriggerTime)
            : NormalizeCronExpression(schedule.CronExpression, schedule.FrequencyType, schedule.FrequencyValue, schedule.Weekday, schedule.TriggerTime);

    private static int LegacyIntervalFor(string frequencyType, int frequencyValue) => frequencyType switch
    {
        "every-days" => frequencyValue * 24,
        "weekly" => 7 * 24,
        _ => 24
    };

    private string? ReadAccountKey() => _accountContext?.CurrentAccountKey;
    private string RequireAccountKey() => _accountContext?.RequireAccountKey() ?? "";
    private string Scope(string column) => _accountContext is null ? "1=1" : $"{column} = $accountKey";
    private static void AddAccountParameter(SqliteCommand command, string? key)
    {
        if (key is not null) command.Parameters.AddWithValue("$accountKey", key);
    }

    private async Task EnsureAccountKeyColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(TelegramCollectionSchedules)";
        var found = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                if (string.Equals(reader.GetString(1), "AccountKey", StringComparison.OrdinalIgnoreCase)) found = true;
        }
        if (found) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE TelegramCollectionSchedules ADD COLUMN AccountKey TEXT NULL";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string name, string definition, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(TelegramCollectionSchedules)";
        var found = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase)) found = true;
        }
        if (found) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE TelegramCollectionSchedules ADD COLUMN {name} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TelegramCollectionSchedule Read(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4) == 1,
        reader.GetString(5), TelegramCollectorService.NormalizeMessageLimit(reader.GetInt32(6)),
        reader.GetString(7), ParseDate(reader, 8), ParseDate(reader, 9),
        reader.GetString(10), reader.GetString(11), reader.GetInt32(12), DateTimeOffset.Parse(reader.GetString(13)), DateTimeOffset.Parse(reader.GetString(14)),
        reader.GetString(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetString(18), reader.GetString(19),
        reader.GetInt32(20) == 1, NormalizeAutoScheduleDelay(reader.GetInt32(21)),
        NormalizeAutoScheduleMaxPerRun(reader.GetInt32(22)), ParseDate(reader, 23), ParseDate(reader, 24),
        reader.GetInt32(25), NormalizeFirstRunMode(reader.GetString(26)), reader.GetInt64(27),
        reader.GetString(28));

    private static TelegramDistributionRule ReadRule(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8),
        DateTimeOffset.Parse(reader.GetString(9)), DateTimeOffset.Parse(reader.GetString(10)));

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false, DefaultTimeout = 3 }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

public sealed record TelegramCollectionSchedule(
    long Id,
    string ChannelUrl,
    string ChannelName,
    int IntervalHours,
    bool Enabled,
    string ProxySelection,
    int MessageLimit,
    string Keyword,
    DateTimeOffset? LastCollectedAt,
    DateTimeOffset? LastSuccessAt,
    string LastStatus,
    string LastError,
    int LastAddedCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string FrequencyType = "legacy",
    int FrequencyValue = 1,
    int Weekday = 1,
    string TriggerTime = "00:00",
    string CronExpression = "",
    bool AutoScheduleEnabled = false,
    int AutoScheduleDelayMinutes = 30,
    int AutoScheduleMaxPerRun = 20,
    DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? NextRetryAt = null,
    int RetryAttempt = 0,
    string FirstRunMode = "backfill",
    long LastMessageId = 0,
    string IncludeKeyword = "")
{
    public string EnabledLabel => Enabled ? "已启用" : "已停用";
    public string ToggleLabel => Enabled ? "关闭" : "开启";
    public string EffectiveCronExpression => string.IsNullOrWhiteSpace(CronExpression)
        ? TelegramCollectionStore.LegacyCronFor(this)
        : CronExpression;
    public string FrequencyDisplay => string.IsNullOrWhiteSpace(CronExpression)
        ? FrequencyType switch
        {
            "every-days" => $"每 {FrequencyValue} 天 {TriggerTime}",
            "weekly" => $"每周{WeekdayDisplay} {TriggerTime}",
            "legacy" => IntervalHours == 24 ? "每天一次" : $"每 {IntervalHours} 小时",
            _ => $"每天 {TriggerTime}"
        }
        : CronExpression;
    public string WeekdayDisplay => Weekday switch { 1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五", 6 => "六", 7 => "日", _ => "一" };
    public string LastRunDisplay => LastCollectedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "从未采集";
    public string LastAttemptDisplay => (LastAttemptAt ?? LastCollectedAt)?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "从未尝试";
    public string LastSuccessDisplay => LastSuccessAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "从未成功";
    public string FirstRunModeDisplay => FirstRunMode == "new-only"
        ? "先导入首页，之后仅采新消息"
        : $"导入首页最新 {TelegramCollectorService.TempListLimit} 条";
    public string NextRunDisplay
    {
        get
        {
            if (TelegramCollectionStore.IsAutomaticRetryStopped(this))
                return "自动重试已停止，请手动采集";
            if (NextRetryAt is { } retryAt) return $"重试 {retryAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            if (string.IsNullOrWhiteSpace(CronExpression) && string.Equals(FrequencyType, "every-days", StringComparison.OrdinalIgnoreCase))
            {
                if (LastCollectedAt is null) return "首次检查后执行";
                return LastCollectedAt.Value.ToLocalTime().AddDays(Math.Max(1, FrequencyValue)).ToString("yyyy-MM-dd HH:mm");
            }
            if (!TelegramCronSchedule.TryParse(EffectiveCronExpression, out var cron, out _)) return "-";
            return (cron.GetNextOccurrence(DateTime.Now) is { } next ? next.ToString("yyyy-MM-dd HH:mm") : "-");
        }
    }
    public string CronDisplay => EffectiveCronExpression.Length == 0 ? "兼容旧频率" : EffectiveCronExpression;
    public string MessageLimitDisplay =>
        $"{TelegramCollectorService.NormalizeMessageLimit(MessageLimit)} 条";
    public string DistributionDisplay { get; init; } = "未配置规则";
    public string ResultDisplay => LastStatus == "从未采集"
        ? "-"
        : string.IsNullOrWhiteSpace(LastError)
            ? $"{LastStatus}，新增 {LastAddedCount} 条"
            : $"{LastStatus}，新增 {LastAddedCount} 条：{CliDiagnostics.Sanitize(LastError)}";
    public string AutoScheduleDisplay => AutoScheduleEnabled
        ? $"延迟 {AutoScheduleDelayMinutes} 分钟，单次最多 {AutoScheduleMaxPerRun} 条"
        : "未启用";
}

public sealed record TelegramCollectionRunHistory(
    long Id,
    long ScheduleId,
    string ChannelUrl,
    string ChannelName,
    string TriggerSource,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Status,
    int AddedCount,
    int SkippedCount,
    int MatchedRuleCount,
    int UnmatchedCount,
    int FailedMediaCount,
    int AutoScheduledCount,
    int AutoScheduleSkippedCount,
    string AutoScheduleReasons,
    int PageCount,
    long LastMessageId,
    string Error)
{
    public string StartedDisplay => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string TriggerDisplay => TriggerSource switch
    {
        "scheduled" => "定时执行",
        "retry" => "失败重试",
        _ => "手动执行"
    };
    public string DurationDisplay
    {
        get
        {
            var duration = CompletedAt - StartedAt;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}时{duration.Minutes}分"
                : duration.TotalMinutes >= 1
                    ? $"{(int)duration.TotalMinutes}分{duration.Seconds}秒"
                    : $"{Math.Max(0, duration.Seconds)}秒";
        }
    }
    public string ResultDisplay
    {
        get
        {
            var status = CliDiagnostics.Sanitize(Status);
            return string.IsNullOrWhiteSpace(Error)
                ? status
                : $"{status}：{CliDiagnostics.Sanitize(Error)}";
        }
    }
    public string StatisticsDisplay =>
        $"新增 {AddedCount} / 跳过 {SkippedCount} / 规则命中 {MatchedRuleCount} / 未命中 {UnmatchedCount} / 媒体失败 {FailedMediaCount}";
    public string AutoScheduleDisplay =>
        AutoScheduleSkippedCount == 0 && AutoScheduledCount == 0
            ? "未触发"
            : $"已排期 {AutoScheduledCount}，未排期 {AutoScheduleSkippedCount}"
              + (string.IsNullOrWhiteSpace(AutoScheduleReasons)
                  ? ""
                  : $"：{CliDiagnostics.Sanitize(AutoScheduleReasons)}");
    public string PagingDisplay => LastMessageId > 0
        ? $"读取频道首页，水位 #{LastMessageId}"
        : "读取频道首页，未建立水位";
}

public sealed record TelegramCollectionScheduleDraft(
    long? Id,
    string ChannelUrl,
    string ChannelName,
    int IntervalHours,
    bool Enabled,
    string ProxySelection,
    int MessageLimit,
    string Keyword,
    string FrequencyType = "legacy",
    int FrequencyValue = 1,
    int Weekday = 1,
    string TriggerTime = "00:00",
    string CronExpression = "",
    bool AutoScheduleEnabled = false,
    int AutoScheduleDelayMinutes = 30,
    int AutoScheduleMaxPerRun = 20,
    string FirstRunMode = "backfill",
    string IncludeKeyword = "");

public sealed record TelegramScheduleAdvancedSettings(
    string CronExpression,
    string Keyword,
    IReadOnlyList<TelegramDistributionRuleDraft> DistributionRules,
    bool AutoScheduleEnabled = false,
    int AutoScheduleDelayMinutes = 30,
    int AutoScheduleMaxPerRun = 20,
    string FirstRunMode = "backfill",
    string IncludeKeyword = "")
{
    public static TelegramScheduleAdvancedSettings Default { get; } =
        new("0 0 * * *", "", [], false, 30, 20, "backfill", "");
}
