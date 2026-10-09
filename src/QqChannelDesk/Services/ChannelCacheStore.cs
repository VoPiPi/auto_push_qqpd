using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services;

public sealed class ChannelCacheStore
{
    private readonly string _databasePath;
    private readonly string _syncStatePath;
    private readonly AccountContext? _accountContext;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public ChannelCacheStore(string? databasePath = null, AccountContext? accountContext = null)
    {
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");
        _syncStatePath = Path.Combine(Path.GetDirectoryName(_databasePath)!, "channels.sync.json");
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
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();

            var tables = await GetTablesAsync(connection, transaction, cancellationToken);
            var hasGuilds = tables.Contains("Guilds");
            var hasChannels = tables.Contains("Channels");
            var guildColumns = hasGuilds ? await GetColumnsAsync(connection, transaction, "Guilds", cancellationToken) : [];
            var channelColumns = hasChannels ? await GetColumnsAsync(connection, transaction, "Channels", cancellationToken) : [];

            // Rebuild old tables because their primary keys cannot represent two accounts.
            if (hasGuilds)
            {
                await RenameTableAsync(connection, transaction, "Guilds", "Guilds_Legacy", cancellationToken);
            }
            if (hasChannels)
                await RenameTableAsync(connection, transaction, "Channels", "Channels_Legacy", cancellationToken);

            await using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS Guilds (
                        AccountKey TEXT NULL,
                        GuildId TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        Role TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY (AccountKey, GuildId)
                    );
                    CREATE TABLE IF NOT EXISTS Channels (
                        AccountKey TEXT NULL,
                        GuildId TEXT NOT NULL,
                        ChannelId TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        PRIMARY KEY (AccountKey, GuildId, ChannelId),
                        FOREIGN KEY (AccountKey, GuildId) REFERENCES Guilds(AccountKey, GuildId) ON DELETE CASCADE
                    );
                    """;
                await schema.ExecuteNonQueryAsync(cancellationToken);
            }

            if (hasGuilds)
            {
                await using var migrateGuilds = connection.CreateCommand();
                migrateGuilds.Transaction = transaction;
                migrateGuilds.CommandText = guildColumns.Contains("AccountKey")
                    ? "INSERT OR IGNORE INTO Guilds (AccountKey, GuildId, Name, Role) SELECT AccountKey, GuildId, Name, Role FROM Guilds_Legacy"
                    : "INSERT OR IGNORE INTO Guilds (AccountKey, GuildId, Name, Role) SELECT NULL, GuildId, Name, Role FROM Guilds_Legacy";
                await migrateGuilds.ExecuteNonQueryAsync(cancellationToken);
            }

            if (hasChannels)
            {
                // Keep orphaned channel rows readable even if a damaged/partial
                // legacy database has no matching Guilds table or row.
                await using (var ensureGuilds = connection.CreateCommand())
                {
                    ensureGuilds.Transaction = transaction;
                    ensureGuilds.CommandText = channelColumns.Contains("AccountKey")
                        ? "INSERT OR IGNORE INTO Guilds (AccountKey, GuildId, Name, Role) SELECT AccountKey, GuildId, '', '' FROM Channels_Legacy"
                        : "INSERT OR IGNORE INTO Guilds (AccountKey, GuildId, Name, Role) SELECT NULL, GuildId, '', '' FROM Channels_Legacy";
                    await ensureGuilds.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var migrateChannels = connection.CreateCommand();
                migrateChannels.Transaction = transaction;
                migrateChannels.CommandText = channelColumns.Contains("AccountKey")
                    ? "INSERT OR IGNORE INTO Channels (AccountKey, GuildId, ChannelId, Name) SELECT AccountKey, GuildId, ChannelId, Name FROM Channels_Legacy"
                    : "INSERT OR IGNORE INTO Channels (AccountKey, GuildId, ChannelId, Name) SELECT NULL, GuildId, ChannelId, Name FROM Channels_Legacy";
                await migrateChannels.ExecuteNonQueryAsync(cancellationToken);

                await using var dropChannels = connection.CreateCommand();
                dropChannels.Transaction = transaction;
                dropChannels.CommandText = "DROP TABLE Channels_Legacy";
                await dropChannels.ExecuteNonQueryAsync(cancellationToken);
            }
            if (hasGuilds)
            {
                await using var dropGuilds = connection.CreateCommand();
                dropGuilds.Transaction = transaction;
                dropGuilds.CommandText = "DROP TABLE Guilds_Legacy";
                await dropGuilds.ExecuteNonQueryAsync(cancellationToken);
            }
            if (tables.Contains("Accounts"))
            {
                await using var dropAccounts = connection.CreateCommand();
                dropAccounts.Transaction = transaction;
                dropAccounts.CommandText = "DROP TABLE Accounts";
                await dropAccounts.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetGuildsAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT GuildId, Name, Role FROM Guilds WHERE {AccountScope("AccountKey")} ORDER BY Name COLLATE NOCASE";
        AddAccountParameter(command, accountKey);
        var result = new List<ChannelChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ChannelChoice(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetChannelsAsync(string guildId, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ChannelId, Name FROM Channels WHERE GuildId = $guildId AND {AccountScope("AccountKey")} ORDER BY Name COLLATE NOCASE";
        command.Parameters.AddWithValue("$guildId", guildId);
        AddAccountParameter(command, accountKey);
        var result = new List<ChannelChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ChannelChoice(reader.GetString(0), reader.GetString(1), ""));
        return result;
    }

    public async Task<ChannelSyncState> GetSyncStateAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null)
            return new(null, false, null, "尚未确认登录账号，暂无频道同步状态");
        await InitializeAsync(cancellationToken);
        if (!File.Exists(_syncStatePath)) return new(accountKey, false, null, "尚未同步频道数据");
        try
        {
            await using var stream = File.OpenRead(_syncStatePath);
            var state = await JsonSerializer.DeserializeAsync<ChannelSyncState>(stream, cancellationToken: cancellationToken);
            if (state is null || (_accountContext is not null && !string.Equals(state.AccountKey, accountKey, StringComparison.Ordinal)))
                return new(accountKey, false, null, "尚未同步频道数据");
            return state;
        }
        catch (JsonException)
        {
            return new(accountKey, false, null, "频道同步状态文件无效，请重新刷新频道数据");
        }
        catch (IOException)
        {
            return new(accountKey, false, null, "无法读取频道同步状态，请重新刷新频道数据");
        }
    }

    public async Task ReplaceSnapshotAsync(IReadOnlyList<CachedGuild> guilds, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var deleteChannels = connection.CreateCommand())
        {
            deleteChannels.Transaction = transaction;
            deleteChannels.CommandText = $"DELETE FROM Channels WHERE {AccountScope("AccountKey")}";
            AddAccountParameter(deleteChannels, accountKey);
            await deleteChannels.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var deleteGuilds = connection.CreateCommand())
        {
            deleteGuilds.Transaction = transaction;
            deleteGuilds.CommandText = $"DELETE FROM Guilds WHERE {AccountScope("AccountKey")}";
            AddAccountParameter(deleteGuilds, accountKey);
            await deleteGuilds.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var guild in guilds)
        {
            await using var insertGuild = connection.CreateCommand();
            insertGuild.Transaction = transaction;
            insertGuild.CommandText = "INSERT INTO Guilds (AccountKey, GuildId, Name, Role) VALUES ($accountKey, $id, $name, $role)";
            AddAccountParameter(insertGuild, accountKey);
            insertGuild.Parameters.AddWithValue("$id", guild.Id);
            insertGuild.Parameters.AddWithValue("$name", guild.Name);
            insertGuild.Parameters.AddWithValue("$role", guild.Role);
            await insertGuild.ExecuteNonQueryAsync(cancellationToken);

            foreach (var channel in guild.Channels)
            {
                await using var insertChannel = connection.CreateCommand();
                insertChannel.Transaction = transaction;
                insertChannel.CommandText = "INSERT INTO Channels (AccountKey, GuildId, ChannelId, Name) VALUES ($accountKey, $guildId, $id, $name)";
                AddAccountParameter(insertChannel, accountKey);
                insertChannel.Parameters.AddWithValue("$guildId", guild.Id);
                insertChannel.Parameters.AddWithValue("$id", channel.Id);
                insertChannel.Parameters.AddWithValue("$name", channel.Name);
                await insertChannel.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        await WriteSyncStateAsync(new(accountKey, true, DateTimeOffset.Now, null), cancellationToken);
    }

    public async Task MarkSyncFailedAsync(string error, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        var previous = await GetSyncStateAsync(cancellationToken);
        await WriteSyncStateAsync(new(accountKey, false, previous.SyncedAt, error), cancellationToken);
    }

    private async Task WriteSyncStateAsync(ChannelSyncState state, CancellationToken cancellationToken)
    {
        var temporaryPath = _syncStatePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        File.Move(temporaryPath, _syncStatePath, true);
    }

    private string AccountScope(string column) => _accountContext is null ? "1=1" : $"{column} = $accountKey";
    private string? ReadAccountKey() => _accountContext?.CurrentAccountKey;
    private string RequireAccountKey() => _accountContext?.RequireAccountKey() ?? "";

    private static void AddAccountParameter(SqliteCommand command, string? accountKey)
    {
        if (accountKey is not null) command.Parameters.AddWithValue("$accountKey", accountKey);
    }

    private static async Task RenameTableAsync(SqliteConnection connection, SqliteTransaction transaction, string source, string target, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {source} RENAME TO {target}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> GetTablesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName})";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
            DefaultTimeout = 3
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode=WAL";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}

public sealed record CachedGuild(string Id, string Name, string Role, IReadOnlyList<ChannelChoice> Channels);
public sealed record ChannelSyncState(string? AccountKey, bool Succeeded, DateTimeOffset? SyncedAt, string? Error);
