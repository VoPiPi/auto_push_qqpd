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
                    MessageLimit INTEGER NOT NULL DEFAULT 50,
                    Keyword TEXT NOT NULL DEFAULT '',
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
                CREATE INDEX IF NOT EXISTS IX_TelegramCollectionDistributionRules_Schedule
                    ON TelegramCollectionDistributionRules (AccountKey, ScheduleId, SortOrder, Id);
                CREATE INDEX IF NOT EXISTS IX_TelegramCollectionSchedules_AccountEnabled
                    ON TelegramCollectionSchedules (AccountKey, Enabled, LastCollectedAt);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureAccountKeyColumnAsync(connection, cancellationToken);
            await EnsureColumnAsync(connection, "FrequencyType", "TEXT NOT NULL DEFAULT 'legacy'", cancellationToken);
            await EnsureColumnAsync(connection, "FrequencyValue", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "Weekday", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
            await EnsureColumnAsync(connection, "TriggerTime", "TEXT NOT NULL DEFAULT '00:00'", cancellationToken);
            await EnsureColumnAsync(connection, "CronExpression", "TEXT NOT NULL DEFAULT ''", cancellationToken);
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
                   FrequencyType, FrequencyValue, Weekday, TriggerTime, CronExpression
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
        command.CommandText = $"SELECT Id, ChannelUrl, ChannelName, IntervalHours, Enabled, ProxySelection, MessageLimit, Keyword, LastCollectedAt, LastSuccessAt, LastStatus, LastError, LastAddedCount, CreatedAt, UpdatedAt, FrequencyType, FrequencyValue, Weekday, TriggerTime, CronExpression FROM TelegramCollectionSchedules WHERE Id=$id AND {scope}";
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<long> SaveAsync(
        TelegramCollectionScheduleDraft draft,
        CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        if (string.IsNullOrWhiteSpace(draft.ChannelUrl)) throw new ArgumentException("TG 频道地址不能为空。", nameof(draft));
        var frequencyType = NormalizeFrequencyType(draft.FrequencyType);
        var frequencyValue = NormalizeFrequencyValue(frequencyType, draft.FrequencyValue);
        var weekday = NormalizeWeekday(draft.Weekday);
        var triggerTime = NormalizeTriggerTime(draft.TriggerTime);
        var interval = NormalizeIntervalHours(draft.IntervalHours > 0 ? draft.IntervalHours : LegacyIntervalFor(frequencyType, frequencyValue));
        var limit = Math.Clamp(draft.MessageLimit, 0, 500);
        var cron = NormalizeCronExpression(draft.CronExpression, frequencyType, frequencyValue, weekday, triggerTime);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.Now.ToString("O");
        if (draft.Id is null)
        {
            command.CommandText = """
                INSERT INTO TelegramCollectionSchedules
                    (ChannelUrl, ChannelName, IntervalHours, Enabled, ProxySelection, MessageLimit, Keyword,
                     LastStatus, CreatedAt, UpdatedAt, FrequencyType, FrequencyValue, Weekday, TriggerTime, CronExpression, AccountKey)
                VALUES ($url,$name,$interval,$enabled,$proxy,$limit,$keyword,'从未采集',$now,$now,$frequencyType,$frequencyValue,$weekday,$triggerTime,$cron,$accountKey);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = $"""
                UPDATE TelegramCollectionSchedules SET ChannelUrl=$url, ChannelName=$name,
                    IntervalHours=$interval, Enabled=$enabled, ProxySelection=$proxy, MessageLimit=$limit,
                    Keyword=$keyword, FrequencyType=$frequencyType, FrequencyValue=$frequencyValue,
                    Weekday=$weekday, TriggerTime=$triggerTime, CronExpression=$cron, UpdatedAt=$now
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
        command.Parameters.AddWithValue("$frequencyType", frequencyType);
        command.Parameters.AddWithValue("$frequencyValue", frequencyValue);
        command.Parameters.AddWithValue("$weekday", weekday);
        command.Parameters.AddWithValue("$triggerTime", triggerTime);
        command.Parameters.AddWithValue("$cron", cron);
        command.Parameters.AddWithValue("$now", now);
        AddAccountParameter(command, accountKey);
        try { return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("这个 TG 频道已经配置过了。", ex);
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var scope = Scope("AccountKey");
        await using var transaction = connection.BeginTransaction();
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
        while (await reader.ReadAsync(cancellationToken))
            rules.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8), DateTimeOffset.Parse(reader.GetString(9)), DateTimeOffset.Parse(reader.GetString(10))));
        return rules;
    }

    public async Task ReplaceRulesAsync(long scheduleId, IReadOnlyList<TelegramDistributionRuleDraft> rules, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM TelegramCollectionDistributionRules WHERE ScheduleId=$scheduleId AND {Scope("AccountKey")}";
            delete.Parameters.AddWithValue("$scheduleId", scheduleId);
            AddAccountParameter(delete, accountKey);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        var now = DateTimeOffset.Now.ToString("O");
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            if (rule.MessageType is not ("text" or "image" or "video")) throw new ArgumentException("分发规则消息类型无效。", nameof(rules));
            if (string.IsNullOrWhiteSpace(rule.ChannelId) || string.IsNullOrWhiteSpace(rule.GuildId)) throw new ArgumentException("分发规则必须选择频道和板块。", nameof(rules));
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
            AddAccountParameter(insert, accountKey);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateResultAsync(
        long id,
        DateTimeOffset collectedAt,
        bool succeeded,
        int addedCount,
        string error,
        CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.Now.ToString("O");
        command.CommandText = $"""
            UPDATE TelegramCollectionSchedules
            SET LastCollectedAt=$collected, LastSuccessAt=CASE WHEN $success=1 THEN $collected ELSE LastSuccessAt END,
                LastStatus=$status, LastError=$error, LastAddedCount=$count, UpdatedAt=$now
            WHERE Id=$id AND {Scope("AccountKey")};
            """;
        command.Parameters.AddWithValue("$collected", collectedAt.ToString("O"));
        command.Parameters.AddWithValue("$success", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$status", succeeded ? "成功" : "失败");
        command.Parameters.AddWithValue("$error", error ?? "");
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

    public static string NormalizeTriggerTime(string? value) =>
        TimeSpan.TryParseExact(value?.Trim(), "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time)
            ? time.ToString("hh\\:mm", System.Globalization.CultureInfo.InvariantCulture)
            : "00:00";

    public static string NormalizeCronExpression(string? value, string frequencyType = "daily", int frequencyValue = 1, int weekday = 1, string triggerTime = "00:00")
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length > 0)
        {
            if (!TelegramCronSchedule.TryParse(trimmed, out var parsed, out var error)) throw new ArgumentException($"Cron 表达式无效：{error}", nameof(value));
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
        reader.GetString(5), reader.GetInt32(6), reader.GetString(7), ParseDate(reader, 8), ParseDate(reader, 9),
        reader.GetString(10), reader.GetString(11), reader.GetInt32(12), DateTimeOffset.Parse(reader.GetString(13)), DateTimeOffset.Parse(reader.GetString(14)),
        reader.GetString(15), reader.GetInt32(16), reader.GetInt32(17), reader.GetString(18), reader.GetString(19));

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
    string CronExpression = "")
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
    public string NextRunDisplay
    {
        get
        {
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
    public string MessageLimitDisplay => MessageLimit <= 0 ? "不限" : $"{MessageLimit} 条";
    public string DistributionDisplay { get; init; } = "未配置规则";
    public string ResultDisplay => LastStatus == "从未采集" ? "-" : $"{LastStatus}，新增 {LastAddedCount} 条";
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
    string CronExpression = "");

public sealed record TelegramScheduleAdvancedSettings(
    string CronExpression,
    string Keyword,
    IReadOnlyList<TelegramDistributionRuleDraft> DistributionRules)
{
    public static TelegramScheduleAdvancedSettings Default { get; } =
        new("0 0 * * *", "", []);
}
