using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services;

public sealed class ChannelCacheStore
{
    private readonly string _databasePath;
    private readonly string _syncStatePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public ChannelCacheStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");
        _syncStatePath = Path.Combine(Path.GetDirectoryName(_databasePath)!, "channels.sync.json");
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
            var hasAccounts = tables.Contains("Accounts");
            var hasGuilds = tables.Contains("Guilds");
            var hasChannels = tables.Contains("Channels");

            if (hasGuilds && hasAccounts)
            {
                await using var dropGuilds = connection.CreateCommand();
                dropGuilds.Transaction = transaction;
                dropGuilds.CommandText = "ALTER TABLE Guilds RENAME TO Guilds_Legacy";
                await dropGuilds.ExecuteNonQueryAsync(cancellationToken);
                if (hasChannels)
                {
                    await using var dropChannels = connection.CreateCommand();
                    dropChannels.Transaction = transaction;
                    dropChannels.CommandText = "ALTER TABLE Channels RENAME TO Channels_Legacy";
                    await dropChannels.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS Guilds (
                        GuildId TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Role TEXT NOT NULL DEFAULT ''
                    );
                    CREATE TABLE IF NOT EXISTS Channels (
                        GuildId TEXT NOT NULL,
                        ChannelId TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        PRIMARY KEY (GuildId, ChannelId),
                        FOREIGN KEY (GuildId) REFERENCES Guilds(GuildId) ON DELETE CASCADE
                    );
                    """;
                await schema.ExecuteNonQueryAsync(cancellationToken);
            }

            if (hasGuilds && hasAccounts)
            {
                await using var migrateGuilds = connection.CreateCommand();
                migrateGuilds.Transaction = transaction;
                migrateGuilds.CommandText = "INSERT OR IGNORE INTO Guilds (GuildId, Name, Role) SELECT GuildId, Name, Role FROM Guilds_Legacy";
                await migrateGuilds.ExecuteNonQueryAsync(cancellationToken);
                if (hasChannels)
                {
                    await using var migrateChannels = connection.CreateCommand();
                    migrateChannels.Transaction = transaction;
                    migrateChannels.CommandText = "INSERT OR IGNORE INTO Channels (GuildId, ChannelId, Name) SELECT GuildId, ChannelId, Name FROM Channels_Legacy";
                    await migrateChannels.ExecuteNonQueryAsync(cancellationToken);
                    await using var dropLegacyChannels = connection.CreateCommand();
                    dropLegacyChannels.Transaction = transaction;
                    dropLegacyChannels.CommandText = "DROP TABLE Channels_Legacy";
                    await dropLegacyChannels.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var dropLegacyGuilds = connection.CreateCommand();
                dropLegacyGuilds.Transaction = transaction;
                dropLegacyGuilds.CommandText = "DROP TABLE Guilds_Legacy";
                await dropLegacyGuilds.ExecuteNonQueryAsync(cancellationToken);
            }
            if (hasAccounts)
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
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GuildId, Name, Role FROM Guilds ORDER BY Name COLLATE NOCASE";
        var result = new List<ChannelChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ChannelChoice(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<ChannelChoice>> GetChannelsAsync(string guildId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ChannelId, Name FROM Channels WHERE GuildId = $guildId ORDER BY Name COLLATE NOCASE";
        command.Parameters.AddWithValue("$guildId", guildId);
        var result = new List<ChannelChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ChannelChoice(reader.GetString(0), reader.GetString(1), ""));
        return result;
    }

    public async Task<ChannelSyncState> GetSyncStateAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (!File.Exists(_syncStatePath)) return new(false, null, "尚未同步频道数据");
        try
        {
            await using var stream = File.OpenRead(_syncStatePath);
            var state = await JsonSerializer.DeserializeAsync<ChannelSyncState>(stream, cancellationToken: cancellationToken);
            return state ?? new(false, null, "尚未同步频道数据");
        }
        catch (JsonException)
        {
            return new(false, null, "频道同步状态文件无效，请重新刷新频道数据");
        }
        catch (IOException)
        {
            return new(false, null, "无法读取频道同步状态，请重新刷新频道数据");
        }
    }

    public async Task ReplaceSnapshotAsync(IReadOnlyList<CachedGuild> guilds, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Guilds";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var guild in guilds)
        {
            await using var insertGuild = connection.CreateCommand();
            insertGuild.Transaction = transaction;
            insertGuild.CommandText = "INSERT INTO Guilds (GuildId, Name, Role) VALUES ($id, $name, $role)";
            insertGuild.Parameters.AddWithValue("$id", guild.Id);
            insertGuild.Parameters.AddWithValue("$name", guild.Name);
            insertGuild.Parameters.AddWithValue("$role", guild.Role);
            await insertGuild.ExecuteNonQueryAsync(cancellationToken);

            foreach (var channel in guild.Channels)
            {
                await using var insertChannel = connection.CreateCommand();
                insertChannel.Transaction = transaction;
                insertChannel.CommandText = "INSERT INTO Channels (GuildId, ChannelId, Name) VALUES ($guildId, $id, $name)";
                insertChannel.Parameters.AddWithValue("$guildId", guild.Id);
                insertChannel.Parameters.AddWithValue("$id", channel.Id);
                insertChannel.Parameters.AddWithValue("$name", channel.Name);
                await insertChannel.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        await WriteSyncStateAsync(new(true, DateTimeOffset.Now, null), cancellationToken);
    }

    public async Task MarkSyncFailedAsync(string error, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var previous = await GetSyncStateAsync(cancellationToken);
        await WriteSyncStateAsync(new(false, previous.SyncedAt, error), cancellationToken);
    }

    private async Task WriteSyncStateAsync(ChannelSyncState state, CancellationToken cancellationToken)
    {
        var temporaryPath = _syncStatePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        File.Move(temporaryPath, _syncStatePath, true);
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

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}

public sealed record CachedGuild(string Id, string Name, string Role, IReadOnlyList<ChannelChoice> Channels);
public sealed record ChannelSyncState(bool Succeeded, DateTimeOffset? SyncedAt, string? Error);
