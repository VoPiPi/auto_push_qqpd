using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace QqChannelDesk.Services;

/// <summary>
/// Persists automatic publishing budgets. Policies are scoped to a guild and
/// channel, while guild budgets cap all automatic publishing in the guild.
/// </summary>
public sealed class PublishPolicyStore
{
    private readonly string _databasePath;
    private readonly AccountContext? _accountContext;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public PublishPolicyStore(string? databasePath = null, AccountContext? accountContext = null)
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
            await EnsureSchemaAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PublishPolicies (
                AccountKey TEXT NOT NULL,
                GuildId TEXT NOT NULL,
                ChannelId TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                WindowStart TEXT NOT NULL DEFAULT '09:00',
                WindowEnd TEXT NOT NULL DEFAULT '22:00',
                MinIntervalMinutes INTEGER NOT NULL DEFAULT 60,
                MaxIntervalMinutes INTEGER NOT NULL DEFAULT 120,
                DailyLimit INTEGER NOT NULL DEFAULT 5,
                QueueLimit INTEGER NOT NULL DEFAULT 100,
                MaxConcurrency INTEGER NOT NULL DEFAULT 1,
                RateLimitBaseMinutes INTEGER NOT NULL DEFAULT 30,
                RateLimitMaxMinutes INTEGER NOT NULL DEFAULT 240,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (AccountKey, GuildId, ChannelId)
            );
            CREATE INDEX IF NOT EXISTS IX_PublishPolicies_Account_Enabled
                ON PublishPolicies (AccountKey, Enabled, GuildId, ChannelId);
            CREATE TABLE IF NOT EXISTS PublishGuildBudgets (
                AccountKey TEXT NOT NULL,
                GuildId TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 0,
                DailyLimit INTEGER NOT NULL DEFAULT 20,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (AccountKey, GuildId)
            );
            CREATE INDEX IF NOT EXISTS IX_PublishGuildBudgets_Account_Enabled
                ON PublishGuildBudgets (AccountKey, Enabled, GuildId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PublishPolicy>> GetPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT GuildId, ChannelId, Enabled, WindowStart, WindowEnd,
                   MinIntervalMinutes, MaxIntervalMinutes, DailyLimit, QueueLimit,
                   MaxConcurrency, RateLimitBaseMinutes, RateLimitMaxMinutes,
                   CreatedAt, UpdatedAt
            FROM PublishPolicies
            WHERE {Scope("AccountKey")}
            ORDER BY GuildId, ChannelId;
            """;
        AddAccountParameter(command, accountKey);
        var policies = new List<PublishPolicy>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) policies.Add(ReadPolicy(reader));
        return policies;
    }

    public async Task<PublishPolicy?> GetPolicyAsync(
        string guildId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var normalizedGuildId = NormalizeTargetId(guildId, nameof(guildId));
        var normalizedChannelId = NormalizeTargetId(channelId, nameof(channelId));
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT GuildId, ChannelId, Enabled, WindowStart, WindowEnd,
                   MinIntervalMinutes, MaxIntervalMinutes, DailyLimit, QueueLimit,
                   MaxConcurrency, RateLimitBaseMinutes, RateLimitMaxMinutes,
                   CreatedAt, UpdatedAt
            FROM PublishPolicies
            WHERE GuildId=$guildId AND ChannelId=$channelId AND {Scope("AccountKey")}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$guildId", normalizedGuildId);
        command.Parameters.AddWithValue("$channelId", normalizedChannelId);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPolicy(reader) : null;
    }

    public async Task SavePolicyAsync(PublishPolicyDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalized = NormalizePolicy(draft);
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.Now.ToString("O");
        command.CommandText = """
            INSERT INTO PublishPolicies (
                AccountKey, GuildId, ChannelId, Enabled, WindowStart, WindowEnd,
                MinIntervalMinutes, MaxIntervalMinutes, DailyLimit, QueueLimit,
                MaxConcurrency, RateLimitBaseMinutes, RateLimitMaxMinutes,
                CreatedAt, UpdatedAt)
            VALUES (
                $accountKey, $guildId, $channelId, $enabled, $windowStart, $windowEnd,
                $minInterval, $maxInterval, $dailyLimit, $queueLimit,
                $maxConcurrency, $rateLimitBase, $rateLimitMax,
                $now, $now)
            ON CONFLICT(AccountKey, GuildId, ChannelId) DO UPDATE SET
                Enabled=excluded.Enabled,
                WindowStart=excluded.WindowStart,
                WindowEnd=excluded.WindowEnd,
                MinIntervalMinutes=excluded.MinIntervalMinutes,
                MaxIntervalMinutes=excluded.MaxIntervalMinutes,
                DailyLimit=excluded.DailyLimit,
                QueueLimit=excluded.QueueLimit,
                MaxConcurrency=excluded.MaxConcurrency,
                RateLimitBaseMinutes=excluded.RateLimitBaseMinutes,
                RateLimitMaxMinutes=excluded.RateLimitMaxMinutes,
                UpdatedAt=excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$accountKey", accountKey);
        command.Parameters.AddWithValue("$guildId", normalized.GuildId);
        command.Parameters.AddWithValue("$channelId", normalized.ChannelId);
        command.Parameters.AddWithValue("$enabled", normalized.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$windowStart", normalized.WindowStart);
        command.Parameters.AddWithValue("$windowEnd", normalized.WindowEnd);
        command.Parameters.AddWithValue("$minInterval", normalized.MinIntervalMinutes);
        command.Parameters.AddWithValue("$maxInterval", normalized.MaxIntervalMinutes);
        command.Parameters.AddWithValue("$dailyLimit", normalized.DailyLimit);
        command.Parameters.AddWithValue("$queueLimit", normalized.QueueLimit);
        command.Parameters.AddWithValue("$maxConcurrency", normalized.MaxConcurrency);
        command.Parameters.AddWithValue("$rateLimitBase", normalized.RateLimitBaseMinutes);
        command.Parameters.AddWithValue("$rateLimitMax", normalized.RateLimitMaxMinutes);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeletePolicyAsync(
        string guildId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var normalizedGuildId = NormalizeTargetId(guildId, nameof(guildId));
        var normalizedChannelId = NormalizeTargetId(channelId, nameof(channelId));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM PublishPolicies WHERE GuildId=$guildId AND ChannelId=$channelId AND {Scope("AccountKey")}";
        command.Parameters.AddWithValue("$guildId", normalizedGuildId);
        command.Parameters.AddWithValue("$channelId", normalizedChannelId);
        AddAccountParameter(command, accountKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<PublishGuildBudget>> GetGuildBudgetsAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT GuildId, Enabled, DailyLimit, CreatedAt, UpdatedAt
            FROM PublishGuildBudgets
            WHERE {Scope("AccountKey")}
            ORDER BY GuildId;
            """;
        AddAccountParameter(command, accountKey);
        var budgets = new List<PublishGuildBudget>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) budgets.Add(ReadGuildBudget(reader));
        return budgets;
    }

    public async Task<PublishGuildBudget?> GetGuildBudgetAsync(
        string guildId,
        CancellationToken cancellationToken = default)
    {
        var normalizedGuildId = NormalizeTargetId(guildId, nameof(guildId));
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT GuildId, Enabled, DailyLimit, CreatedAt, UpdatedAt
            FROM PublishGuildBudgets
            WHERE GuildId=$guildId AND {Scope("AccountKey")}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$guildId", normalizedGuildId);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGuildBudget(reader) : null;
    }

    public async Task SaveGuildBudgetAsync(
        PublishGuildBudgetDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var normalized = NormalizeGuildBudget(draft);
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var now = DateTimeOffset.Now.ToString("O");
        command.CommandText = """
            INSERT INTO PublishGuildBudgets (
                AccountKey, GuildId, Enabled, DailyLimit, CreatedAt, UpdatedAt)
            VALUES ($accountKey, $guildId, $enabled, $dailyLimit, $now, $now)
            ON CONFLICT(AccountKey, GuildId) DO UPDATE SET
                Enabled=excluded.Enabled,
                DailyLimit=excluded.DailyLimit,
                UpdatedAt=excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$accountKey", accountKey);
        command.Parameters.AddWithValue("$guildId", normalized.GuildId);
        command.Parameters.AddWithValue("$enabled", normalized.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$dailyLimit", normalized.DailyLimit);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteGuildBudgetAsync(string guildId, CancellationToken cancellationToken = default)
    {
        var normalizedGuildId = NormalizeTargetId(guildId, nameof(guildId));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM PublishGuildBudgets WHERE GuildId=$guildId AND {Scope("AccountKey")}";
        command.Parameters.AddWithValue("$guildId", normalizedGuildId);
        AddAccountParameter(command, accountKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static PublishPolicy ReadPolicy(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2) == 1,
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.GetInt32(11),
        DateTimeOffset.Parse(reader.GetString(12)),
        DateTimeOffset.Parse(reader.GetString(13)));

    private static PublishGuildBudget ReadGuildBudget(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1) == 1,
        reader.GetInt32(2),
        DateTimeOffset.Parse(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4)));

    private static PublishPolicyDraft NormalizePolicy(PublishPolicyDraft draft)
    {
        var minInterval = Math.Clamp(draft.MinIntervalMinutes, 1, 7 * 24 * 60);
        var maxInterval = Math.Clamp(draft.MaxIntervalMinutes, minInterval, 7 * 24 * 60);
        var baseMinutes = Math.Clamp(draft.RateLimitBaseMinutes, 1, 24 * 60);
        var maxMinutes = Math.Clamp(draft.RateLimitMaxMinutes, baseMinutes, 7 * 24 * 60);
        var start = NormalizeTime(draft.WindowStart, "09:00", nameof(draft.WindowStart));
        var end = NormalizeTime(draft.WindowEnd, "22:00", nameof(draft.WindowEnd));
        if (string.Equals(start, end, StringComparison.Ordinal))
            throw new ArgumentException("发布窗口开始和结束时间不能相同。", nameof(draft));
        return draft with
        {
            GuildId = NormalizeTargetId(draft.GuildId, nameof(draft.GuildId)),
            ChannelId = NormalizeTargetId(draft.ChannelId, nameof(draft.ChannelId)),
            WindowStart = start,
            WindowEnd = end,
            MinIntervalMinutes = minInterval,
            MaxIntervalMinutes = maxInterval,
            DailyLimit = Math.Clamp(draft.DailyLimit, 1, 100_000),
            QueueLimit = Math.Clamp(draft.QueueLimit, 1, 100_000),
            MaxConcurrency = Math.Clamp(draft.MaxConcurrency, 1, 32),
            RateLimitBaseMinutes = baseMinutes,
            RateLimitMaxMinutes = maxMinutes
        };
    }

    private static PublishGuildBudgetDraft NormalizeGuildBudget(PublishGuildBudgetDraft draft) =>
        draft with
        {
            GuildId = NormalizeTargetId(draft.GuildId, nameof(draft.GuildId)),
            DailyLimit = Math.Clamp(draft.DailyLimit, 1, 100_000)
        };

    private static string NormalizeTargetId(string value, string parameterName)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length == 0 || !normalized.All(char.IsDigit))
            throw new ArgumentException("频道和版块 ID 必须是数字。", parameterName);
        return normalized;
    }

    private static string NormalizeTime(string? value, string fallback, string parameterName)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!TimeOnly.TryParseExact(normalized, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException("时间格式必须是 HH:mm。", parameterName);
        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private string? ReadAccountKey() => _accountContext?.CurrentAccountKey;
    private string RequireAccountKey() => _accountContext?.RequireAccountKey() ?? "";
    private string Scope(string column) => _accountContext is null ? "1=1" : $"{column} = $accountKey";

    private static void AddAccountParameter(SqliteCommand command, string? accountKey)
    {
        if (accountKey is not null) command.Parameters.AddWithValue("$accountKey", accountKey);
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
}

public sealed record PublishPolicyDraft(
    string GuildId,
    string ChannelId,
    bool Enabled,
    string WindowStart,
    string WindowEnd,
    int MinIntervalMinutes,
    int MaxIntervalMinutes,
    int DailyLimit,
    int QueueLimit,
    int MaxConcurrency,
    int RateLimitBaseMinutes,
    int RateLimitMaxMinutes);

public sealed record PublishPolicy(
    string GuildId,
    string ChannelId,
    bool Enabled,
    string WindowStart,
    string WindowEnd,
    int MinIntervalMinutes,
    int MaxIntervalMinutes,
    int DailyLimit,
    int QueueLimit,
    int MaxConcurrency,
    int RateLimitBaseMinutes,
    int RateLimitMaxMinutes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string WindowDisplay => $"{WindowStart}-{WindowEnd}";
    public string IntervalDisplay => $"{MinIntervalMinutes}-{MaxIntervalMinutes} 分钟";
    public string EnabledLabel => Enabled ? "已启用" : "已停用";

    public TimeOnly WindowStartTime =>
        TimeOnly.ParseExact(WindowStart, "HH:mm", CultureInfo.InvariantCulture);

    public TimeOnly WindowEndTime =>
        TimeOnly.ParseExact(WindowEnd, "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 发布窗口允许跨午夜（例如 22:00-06:00）。窗口按本地时间判断，
    /// 调用方应传入与计划时间同一时区的 DateTimeOffset。
    /// </summary>
    public bool IsWithinWindow(DateTimeOffset moment)
    {
        var start = WindowStartTime.ToTimeSpan();
        var end = WindowEndTime.ToTimeSpan();
        var time = moment.TimeOfDay;
        return start < end
            ? time >= start && time < end
            : time >= start || time < end;
    }

    /// <summary>把任意时间移动到策略窗口内，跨午夜窗口按“当天开始时间”处理。</summary>
    public DateTimeOffset MoveIntoWindow(DateTimeOffset moment)
    {
        var start = WindowStartTime.ToTimeSpan();
        var end = WindowEndTime.ToTimeSpan();
        var time = moment.TimeOfDay;
        if (start < end)
        {
            if (time < start) return moment.Date.Add(start);
            if (time >= end) return moment.Date.AddDays(1).Add(start);
            return moment;
        }

        // 跨午夜窗口：[start, 24:00) 与 [00:00, end) 都算窗口内。
        if (time >= end && time < start) return moment.Date.Add(start);
        return moment;
    }

    public PublishPolicyDraft ToDraft() => new(
        GuildId,
        ChannelId,
        Enabled,
        WindowStart,
        WindowEnd,
        MinIntervalMinutes,
        MaxIntervalMinutes,
        DailyLimit,
        QueueLimit,
        MaxConcurrency,
        RateLimitBaseMinutes,
        RateLimitMaxMinutes);
}

public sealed record PublishGuildBudgetDraft(string GuildId, bool Enabled, int DailyLimit);

public sealed record PublishGuildBudget(
    string GuildId,
    bool Enabled,
    int DailyLimit,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string EnabledLabel => Enabled ? "已启用" : "未启用";

    public PublishGuildBudgetDraft ToDraft() => new(GuildId, Enabled, DailyLimit);
}
