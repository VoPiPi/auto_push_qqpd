using Microsoft.Data.Sqlite;
using System.IO;

namespace QqChannelDesk.Services;

public sealed class SystemSettingsStore
{
    private const string NotifyUpgradeKey = "notify_upgrade";
    private const string RunTasksOnStartupKey = "run_tasks_on_startup";
    private const string MaterialStoragePathKey = "material_storage_path";
    private const string FfmpegPathKey = "ffmpeg_path";
    private const string MaterialRetentionPolicyKey = "material_retention_policy";
    private const string AutoExecuteSchedulesKey = "auto_execute_schedules";
    private const string MaxScheduleConcurrencyKey = "max_schedule_concurrency";
    private const string StartWithWindowsKey = "start_with_windows";
    private const string CloseWindowBehaviorKey = "close_window_behavior";
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SystemSettingsStore(string? databasePath = null) =>
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");

    public string DatabasePath => _databasePath;

    /// <summary>
    /// Creates the settings table without changing any existing values.
    /// Application startup uses this before any settings page is opened.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeCoreAsync(cancellationToken);

    /// <summary>
    /// Writes only missing settings so a first run has an explicit database
    /// configuration while existing user preferences remain untouched.
    /// </summary>
    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeCoreAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [NotifyUpgradeKey] = "false",
                [RunTasksOnStartupKey] = "false",
                [MaterialStoragePathKey] = new AppSettings().EffectiveMaterialStoragePath,
                [FfmpegPathKey] = string.Empty,
                [MaterialRetentionPolicyKey] = MaterialRetentionPolicy.DeleteAfterSuccessfulPublish.ToString(),
                [AutoExecuteSchedulesKey] = "true",
                [MaxScheduleConcurrencyKey] = "1",
                [StartWithWindowsKey] = "false",
                [CloseWindowBehaviorKey] = CloseWindowBehavior.ExitApplication.ToString()
            };

            foreach (var pair in defaults)
                await InsertIfMissingAsync(connection, transaction, pair.Key, pair.Value, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultsAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM AppSettings";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            values[reader.GetString(0)] = reader.GetString(1);

        return new AppSettings(
            NotifyUpgrade: ReadBoolean(values, NotifyUpgradeKey),
            RunTasksOnStartup: ReadBoolean(values, RunTasksOnStartupKey),
            MaterialStoragePath: ReadString(values, MaterialStoragePathKey),
            MaterialRetentionPolicy: ReadRetentionPolicy(values, MaterialRetentionPolicyKey),
            AutoExecuteSchedules: ReadBoolean(values, AutoExecuteSchedulesKey),
            MaxScheduleConcurrency: ReadConcurrency(values, MaxScheduleConcurrencyKey),
            StartWithWindows: ReadBoolean(values, StartWithWindowsKey),
            CloseWindowBehavior: ReadCloseWindowBehavior(values, CloseWindowBehaviorKey),
            FfmpegPath: ReadString(values, FfmpegPathKey));
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            await UpsertAsync(connection, transaction, NotifyUpgradeKey, settings.NotifyUpgrade, cancellationToken);
            await UpsertAsync(connection, transaction, RunTasksOnStartupKey, settings.RunTasksOnStartup, cancellationToken);
            await UpsertAsync(connection, transaction, MaterialStoragePathKey,
                NormalizeStoragePath(settings.MaterialStoragePath), cancellationToken);
            await UpsertAsync(connection, transaction, FfmpegPathKey,
                NormalizeFfmpegPath(settings.FfmpegPath), cancellationToken);
            await UpsertAsync(connection, transaction, MaterialRetentionPolicyKey,
                settings.MaterialRetentionPolicy.ToString(), cancellationToken);
            await UpsertAsync(connection, transaction, AutoExecuteSchedulesKey, settings.AutoExecuteSchedules, cancellationToken);
            await UpsertAsync(connection, transaction, MaxScheduleConcurrencyKey,
                NormalizeConcurrency(settings.MaxScheduleConcurrency).ToString(), cancellationToken);
            await UpsertAsync(connection, transaction, StartWithWindowsKey, settings.StartWithWindows, cancellationToken);
            await UpsertAsync(connection, transaction, CloseWindowBehaviorKey,
                NormalizeCloseWindowBehavior(settings.CloseWindowBehavior).ToString(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
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
                CREATE TABLE IF NOT EXISTS AppSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        bool value,
        CancellationToken cancellationToken) =>
        UpsertAsync(connection, transaction, key, value ? "true" : "false", cancellationToken);

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

    private static bool ReadBoolean(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static string? ReadString(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string NormalizeStoragePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path.Trim());

    private static string NormalizeFfmpegPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path.Trim());

    private static MaterialRetentionPolicy ReadRetentionPolicy(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && Enum.TryParse<MaterialRetentionPolicy>(value, out var policy)
            ? policy
            : MaterialRetentionPolicy.DeleteAfterSuccessfulPublish;

    private static int ReadConcurrency(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var concurrency)
            ? NormalizeConcurrency(concurrency)
            : 1;

    private static CloseWindowBehavior ReadCloseWindowBehavior(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && Enum.TryParse<CloseWindowBehavior>(value, true, out var behavior)
            ? NormalizeCloseWindowBehavior(behavior)
            : CloseWindowBehavior.ExitApplication;

    public static int NormalizeConcurrency(int concurrency) => concurrency is 1 or 2 or 3 ? concurrency : 1;

    public static CloseWindowBehavior NormalizeCloseWindowBehavior(CloseWindowBehavior behavior) =>
        Enum.IsDefined(behavior) ? behavior : CloseWindowBehavior.ExitApplication;
}

public sealed record AppSettings(
    bool NotifyUpgrade = false,
    bool RunTasksOnStartup = false,
    string? MaterialStoragePath = null,
    MaterialRetentionPolicy MaterialRetentionPolicy = MaterialRetentionPolicy.DeleteAfterSuccessfulPublish,
    bool AutoExecuteSchedules = true,
    int MaxScheduleConcurrency = 1,
    bool StartWithWindows = false,
    CloseWindowBehavior CloseWindowBehavior = CloseWindowBehavior.ExitApplication,
    string? FfmpegPath = null)
{
    public string EffectiveMaterialStoragePath =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(MaterialStoragePath)
            ? Path.Combine(AppContext.BaseDirectory, "temp")
            : MaterialStoragePath);
}

public enum MaterialRetentionPolicy
{
    DeleteAfterSuccessfulPublish,
    KeepSevenDays,
    KeepForever
}

public enum CloseWindowBehavior
{
    MinimizeToTray,
    ExitApplication
}
