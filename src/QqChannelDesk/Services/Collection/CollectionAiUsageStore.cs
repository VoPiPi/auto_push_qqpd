using Microsoft.Data.Sqlite;
using System.IO;
using QqChannelDesk.Services;

namespace QqChannelDesk.Services.Collection;

public sealed class CollectionAiUsageStore
{
    private readonly string _databasePath;
    private readonly AccountContext? _accountContext;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public CollectionAiUsageStore(string? databasePath = null, AccountContext? accountContext = null)
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
                CREATE TABLE IF NOT EXISTS AiUsageCalls (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountKey TEXT NULL,
                    SourceId INTEGER NOT NULL,
                    CalledAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_AiUsageCalls_AccountDate
                    ON AiUsageCalls (AccountKey, CalledAt);
                CREATE INDEX IF NOT EXISTS IX_AiUsageCalls_AccountSourceDate
                    ON AiUsageCalls (AccountKey, SourceId, CalledAt);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<int> CountAsync(long? sourceId, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return 0;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(1)
            FROM AiUsageCalls
            WHERE substr(CalledAt,1,10)=$date
              AND {AccountScope("AccountKey")}
              AND ($sourceId=0 OR SourceId=$sourceId);
            """;
        command.Parameters.AddWithValue("$date", DateTimeOffset.Now.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$sourceId", sourceId ?? 0);
        AddAccountParameter(command, accountKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task RecordAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AiUsageCalls (AccountKey, SourceId, CalledAt)
            VALUES ($accountKey, $sourceId, $calledAt);
            """;
        command.Parameters.AddWithValue("$accountKey", accountKey);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$calledAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
}

public sealed class AiUsageGate
{
    private readonly SystemSettingsStore _settings;
    private readonly CollectionAiUsageStore _usage;

    public AiUsageGate(SystemSettingsStore settings, CollectionAiUsageStore usage)
    {
        _settings = settings;
        _usage = usage;
    }

    public async Task<bool> TryReserveAsync(CollectionSource source, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        if (!settings.AiAssistEnabled) return false;
        if (await _usage.CountAsync(sourceId: null, cancellationToken) >= settings.AiDailyCallLimit) return false;
        if (await _usage.CountAsync(source.Id, cancellationToken) >= source.MaxItemsPerRun) return false;
        await _usage.RecordAsync(source.Id, cancellationToken);
        return true;
    }
}
