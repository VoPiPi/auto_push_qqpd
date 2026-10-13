using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services;

public sealed record ProxySource(
    long Id,
    string SourceKey,
    string DisplayName,
    string SourceUrl,
    bool IsBuiltIn,
    bool IsEnabled,
    DateTimeOffset? LastFetchedAt,
    string LastFetchStatus,
    string LastFetchError,
    int LastNodeCount);

public sealed record ProxyNode(
    long Id,
    long SourceId,
    string RawValue,
    string Scheme,
    string Host,
    int Port,
    string SourceCountry,
    bool IsAlive,
    string ExitIp,
    string CountryCode,
    string CountryName,
    DateTimeOffset? LastCheckedAt,
    string LastCheckError,
    DateTimeOffset? LastTgCheckedAt,
    bool? TgAccessible,
    int LastTgMessageCount,
    string LastTgCheckError,
    bool IsPresentInLatestFetch,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt)
{
    public string Address => $"{Scheme}://{Host}:{Port}";
    public bool IsSupported => Scheme is "http" or "https";
}

public sealed class ProxyPoolStore
{
    public const string CbSourceKey = "cb-x2-jun";
    public const string XiaoSourceKey = "xiaocongyu66";

    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public ProxyPoolStore(string? databasePath = null) =>
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");

    public string DatabasePath => _databasePath;

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
                CREATE TABLE IF NOT EXISTS ProxySources (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourceKey TEXT NOT NULL UNIQUE,
                    DisplayName TEXT NOT NULL,
                    SourceUrl TEXT NOT NULL,
                    IsBuiltIn INTEGER NOT NULL DEFAULT 0,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    LastFetchedAt TEXT NULL,
                    LastFetchStatus TEXT NOT NULL DEFAULT '',
                    LastFetchError TEXT NOT NULL DEFAULT '',
                    LastNodeCount INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ProxyNodes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourceId INTEGER NOT NULL,
                    RawValue TEXT NOT NULL,
                    Scheme TEXT NOT NULL,
                    Host TEXT NOT NULL,
                    Port INTEGER NOT NULL,
                    SourceCountry TEXT NOT NULL DEFAULT '',
                    IsAlive INTEGER NOT NULL DEFAULT 0,
                    ExitIp TEXT NOT NULL DEFAULT '',
                    CountryCode TEXT NOT NULL DEFAULT '',
                    CountryName TEXT NOT NULL DEFAULT '',
                    LastCheckedAt TEXT NULL,
                    LastCheckError TEXT NOT NULL DEFAULT '',
                    LastTgCheckedAt TEXT NULL,
                    TgAccessible INTEGER NULL,
                    LastTgMessageCount INTEGER NOT NULL DEFAULT 0,
                    LastTgCheckError TEXT NOT NULL DEFAULT '',
                    IsPresentInLatestFetch INTEGER NOT NULL DEFAULT 1,
                    FirstSeenAt TEXT NOT NULL,
                    LastSeenAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    UNIQUE (SourceId, Scheme, Host, Port),
                    FOREIGN KEY (SourceId) REFERENCES ProxySources(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_ProxyNodes_SourceAlive ON ProxyNodes (SourceId, IsAlive);
                CREATE INDEX IF NOT EXISTS IX_ProxyNodes_SourcePresent ON ProxyNodes (SourceId, IsPresentInLatestFetch);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SeedBuiltInSourceAsync(connection, CbSourceKey, "CB-X2-Jun Proxy List", "https://raw.githubusercontent.com/CB-X2-Jun/proxy-lists/main/proxy.txt", cancellationToken);
            await SeedBuiltInSourceAsync(connection, XiaoSourceKey, "xiaocongyu66 Proxy Pool", "https://raw.githubusercontent.com/xiaocongyu66/proxypool/main/output/nodes.txt", cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<IReadOnlyList<ProxySource>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SourceKey, DisplayName, SourceUrl, IsBuiltIn, IsEnabled, LastFetchedAt, LastFetchStatus, LastFetchError, LastNodeCount FROM ProxySources ORDER BY IsBuiltIn DESC, DisplayName COLLATE NOCASE";
        var result = new List<ProxySource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSource(reader));
        return result;
    }

    public async Task<long> CreateCustomSourceAsync(string displayName, string sourceUrl, bool isEnabled = true, CancellationToken cancellationToken = default)
    {
        var normalized = ProxyPoolService.NormalizeSourceUrl(sourceUrl);
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("代理源别名不能为空。", nameof(displayName));
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ProxySources (SourceKey, DisplayName, SourceUrl, IsBuiltIn, IsEnabled, CreatedAt, UpdatedAt) VALUES ($key, $name, $url, 0, $enabled, $now, $now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$key", "custom:" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$name", displayName.Trim());
        command.Parameters.AddWithValue("$url", normalized);
        command.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task UpdateSourceAsync(ProxySource source, string displayName, string sourceUrl, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var normalized = ProxyPoolService.NormalizeSourceUrl(sourceUrl);
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("代理源别名不能为空。", nameof(displayName));
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProxySources SET DisplayName=$name, SourceUrl=$url, IsEnabled=$enabled, UpdatedAt=$now WHERE Id=$id";
        command.Parameters.AddWithValue("$name", displayName.Trim());
        command.Parameters.AddWithValue("$url", normalized);
        command.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", source.Id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("代理源不存在。");
    }

    public async Task DeleteCustomSourceAsync(ProxySource source, CancellationToken cancellationToken = default)
    {
        if (source.IsBuiltIn) throw new InvalidOperationException("系统代理源不能删除。");
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ProxySources WHERE Id=$id AND IsBuiltIn=0";
        command.Parameters.AddWithValue("$id", source.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProxyNode>> GetNodesAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SourceId, RawValue, Scheme, Host, Port, SourceCountry, IsAlive, ExitIp, CountryCode, CountryName, LastCheckedAt, LastCheckError, LastTgCheckedAt, TgAccessible, LastTgMessageCount, LastTgCheckError, IsPresentInLatestFetch, FirstSeenAt, LastSeenAt FROM ProxyNodes WHERE SourceId=$source ORDER BY IsAlive DESC, Host, Port";
        command.Parameters.AddWithValue("$source", sourceId);
        var result = new List<ProxyNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadNode(reader));
        return result;
    }

    public async Task ApplyFetchedNodesAsync(ProxySource source, IReadOnlyList<ProxyNodeCandidate> candidates, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        await using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE ProxyNodes SET IsPresentInLatestFetch=0, UpdatedAt=$now WHERE SourceId=$source";
            mark.Parameters.AddWithValue("$now", now);
            mark.Parameters.AddWithValue("$source", source.Id);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in candidates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ProxyNodes (SourceId, RawValue, Scheme, Host, Port, SourceCountry, IsPresentInLatestFetch, FirstSeenAt, LastSeenAt, UpdatedAt)
                VALUES ($source, $raw, $scheme, $host, $port, $country, 1, $now, $now, $now)
                ON CONFLICT(SourceId, Scheme, Host, Port) DO UPDATE SET
                    RawValue=$raw, SourceCountry=$country, IsPresentInLatestFetch=1, LastSeenAt=$now, UpdatedAt=$now;
                """;
            command.Parameters.AddWithValue("$source", source.Id);
            command.Parameters.AddWithValue("$raw", item.RawValue);
            command.Parameters.AddWithValue("$scheme", item.Scheme);
            command.Parameters.AddWithValue("$host", item.Host);
            command.Parameters.AddWithValue("$port", item.Port);
            command.Parameters.AddWithValue("$country", item.SourceCountry);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE ProxySources SET LastFetchedAt=$at, LastFetchStatus='成功', LastFetchError='', LastNodeCount=$count, UpdatedAt=$at WHERE Id=$id";
            update.Parameters.AddWithValue("$at", now);
            update.Parameters.AddWithValue("$count", candidates.Count);
            update.Parameters.AddWithValue("$id", source.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFetchFailedAsync(long sourceId, string error, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProxySources SET LastFetchedAt=$at, LastFetchStatus='失败', LastFetchError=$error, UpdatedAt=$at WHERE Id=$id";
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateHealthAsync(long nodeId, bool alive, string exitIp, string countryCode, string countryName, string error, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProxyNodes SET IsAlive=$alive, ExitIp=$ip, CountryCode=$code, CountryName=$country, LastCheckedAt=$at, LastCheckError=$error, UpdatedAt=$at WHERE Id=$id";
        command.Parameters.AddWithValue("$alive", alive ? 1 : 0);
        command.Parameters.AddWithValue("$ip", exitIp ?? "");
        command.Parameters.AddWithValue("$code", countryCode ?? "");
        command.Parameters.AddWithValue("$country", countryName ?? "");
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$id", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateTelegramAsync(long nodeId, bool? accessible, int messageCount, string error, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProxyNodes SET TgAccessible=$accessible, LastTgCheckedAt=$at, LastTgMessageCount=$count, LastTgCheckError=$error, UpdatedAt=$at WHERE Id=$id";
        command.Parameters.AddWithValue("$accessible", (object?)accessible is null ? DBNull.Value : accessible.Value ? 1 : 0);
        command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$count", messageCount);
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$id", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedBuiltInSourceAsync(SqliteConnection connection, string key, string name, string url, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO ProxySources (SourceKey, DisplayName, SourceUrl, IsBuiltIn, IsEnabled, CreatedAt, UpdatedAt) VALUES ($key, $name, $url, 1, 1, $now, $now)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static ProxySource ReadSource(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0, reader.GetInt64(5) != 0,
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(7), reader.GetString(8), reader.GetInt32(9));

    private static ProxyNode ReadNode(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6),
        reader.GetInt64(7) != 0, reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
        reader.GetString(12), reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)), reader.IsDBNull(14) ? null : reader.GetInt64(14) != 0,
        reader.GetInt32(15), reader.GetString(16), reader.GetInt64(17) != 0, DateTimeOffset.Parse(reader.GetString(18)), DateTimeOffset.Parse(reader.GetString(19)));
}

public sealed record ProxyNodeCandidate(string RawValue, string Scheme, string Host, int Port, string SourceCountry);
