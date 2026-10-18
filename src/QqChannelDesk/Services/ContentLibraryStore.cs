using Microsoft.Data.Sqlite;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services;

public sealed class ContentLibraryStore
{
    private readonly string _databasePath;
    private readonly AccountContext? _accountContext;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public ContentLibraryStore(string? databasePath = null, AccountContext? accountContext = null)
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
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS CollectedItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL DEFAULT '',
                    SourceHost TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '',
                    Content TEXT NOT NULL DEFAULT '',
                    CollectedAt TEXT NOT NULL,
                    Status TEXT NOT NULL DEFAULT '待处理',
                    Notes TEXT NOT NULL DEFAULT '',
                    TagsJson TEXT NOT NULL DEFAULT '[]',
                    ParseError TEXT NOT NULL DEFAULT '',
                    MediaJson TEXT NOT NULL DEFAULT '[]',
                    AccountKey TEXT NULL
                );
                DROP INDEX IF EXISTS UX_CollectedItems_Url;
                CREATE INDEX IF NOT EXISTS IX_CollectedItems_Url ON CollectedItems (Url) WHERE Url <> '';
                CREATE INDEX IF NOT EXISTS IX_CollectedItems_CollectedAt ON CollectedItems (CollectedAt DESC);
                CREATE TABLE IF NOT EXISTS Materials (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL DEFAULT '',
                    Type TEXT NOT NULL DEFAULT 'text' CHECK (Type IN ('text','image','video')),
                    Content TEXT NOT NULL DEFAULT '',
                    GuildId TEXT NOT NULL DEFAULT '',
                    GuildName TEXT NOT NULL DEFAULT '',
                    ChannelId TEXT NOT NULL DEFAULT '',
                    ChannelName TEXT NOT NULL DEFAULT '',
                    PublishAt TEXT NULL,
                    Status TEXT NOT NULL DEFAULT 'waitsend' CHECK (Status IN ('waitsend','queue','published','delete')),
                    Link TEXT NOT NULL DEFAULT '',
                    MediaJson TEXT NOT NULL DEFAULT '[]',
                    SourceType TEXT NOT NULL DEFAULT '手工添加',
                    SourceUrl TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    AccountKey TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Materials_StatusPublishAt ON Materials (Status, PublishAt);
                CREATE TABLE IF NOT EXISTS MaterialSourceClaims (
                    AccountKey TEXT NOT NULL,
                    SourceType TEXT NOT NULL,
                    SourceUrl TEXT NOT NULL,
                    MaterialId INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (AccountKey, SourceType, SourceUrl)
                );
                CREATE INDEX IF NOT EXISTS IX_MaterialSourceClaims_MaterialId ON MaterialSourceClaims (MaterialId);
                CREATE TABLE IF NOT EXISTS ScheduleExecutions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MaterialId INTEGER NOT NULL,
                    ScheduledAt TEXT NOT NULL,
                    Status TEXT NOT NULL CHECK (Status IN ('Pending','Running','Succeeded','Failed','NeedsVerification','Cancelled')),
                    StartedAt TEXT NULL,
                    CompletedAt TEXT NULL,
                    LastError TEXT NOT NULL DEFAULT '',
                    PublishRecordId INTEGER NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    SchedulingMode TEXT NOT NULL DEFAULT 'Manual',
                    RateLimitAttempts INTEGER NOT NULL DEFAULT 0,
                    IsPaused INTEGER NOT NULL DEFAULT 0,
                    AccountKey TEXT NULL,
                    UNIQUE (MaterialId, ScheduledAt),
                    FOREIGN KEY (MaterialId) REFERENCES Materials(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_ScheduleExecutions_StatusTime ON ScheduleExecutions (Status, ScheduledAt);
                CREATE TABLE IF NOT EXISTS MaterialImports (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountKey TEXT NOT NULL,
                    FileHash TEXT NOT NULL,
                    ImportedAt TEXT NOT NULL,
                    ImportedCount INTEGER NOT NULL,
                    UNIQUE (AccountKey, FileHash)
                );
                CREATE INDEX IF NOT EXISTS IX_MaterialImports_AccountImportedAt ON MaterialImports (AccountKey, ImportedAt DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureAccountKeyColumnAsync(connection, "CollectedItems", cancellationToken);
            await EnsureAccountKeyColumnAsync(connection, "Materials", cancellationToken);
            await EnsureAccountKeyColumnAsync(connection, "ScheduleExecutions", cancellationToken);
            await EnsureColumnAsync(connection, "ScheduleExecutions", "SchedulingMode", "TEXT NOT NULL DEFAULT 'Manual'", cancellationToken);
            await EnsureColumnAsync(connection, "ScheduleExecutions", "RateLimitAttempts", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "ScheduleExecutions", "IsPaused", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await using var pausedIndex = connection.CreateCommand();
            pausedIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_ScheduleExecutions_PausedStatusTime ON ScheduleExecutions (IsPaused, Status, ScheduledAt)";
            await pausedIndex.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(connection, "CollectedItems", "MediaJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
            await EnsureColumnAsync(connection, "Materials", "SourceType", "TEXT NOT NULL DEFAULT '手工添加'", cancellationToken);
            await EnsureColumnAsync(connection, "Materials", "SourceUrl", "TEXT NOT NULL DEFAULT ''", cancellationToken);
            // Older installations do not have the source columns yet. Create this
            // index only after the compatibility migration has added them.
            await using var sourceIndex = connection.CreateCommand();
            sourceIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_Materials_Source ON Materials (SourceType, SourceUrl)";
            await sourceIndex.ExecuteNonQueryAsync(cancellationToken);
            await EnsureMaterialSourceClaimsAsync(connection, cancellationToken);
            await PublishPolicyStore.EnsureSchemaAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<long> SaveItemAsync(CollectedItemDraft item, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CollectedItems (Url, SourceHost, Title, Content, CollectedAt, Status, Notes, TagsJson, ParseError, MediaJson, AccountKey)
            VALUES ($url, $host, $title, $content, $at, $status, $notes, $tags, $error, $media, $accountKey);
            SELECT last_insert_rowid();
            """;
        AddItemParameters(command, item);
        AddAccountParameter(command, accountKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task UpdateItemAsync(CollectedItemDraft item, CancellationToken cancellationToken = default)
    {
        if (item.Id is null) throw new ArgumentException("采集条目 ID 不能为空。", nameof(item));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CollectedItems SET Url=$url, SourceHost=$host, Title=$title, Content=$content,
                Status=$status, Notes=$notes, TagsJson=$tags, ParseError=$error, MediaJson=$media WHERE Id=$id AND ACCOUNT_SCOPE;
            """;
        command.CommandText = command.CommandText.Replace("ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        AddItemParameters(command, item, includeCollectedAt: false);
        command.Parameters.AddWithValue("$id", item.Id.Value);
        AddAccountParameter(command, accountKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("采集条目不存在或不属于当前账号。");
    }

    public async Task<CollectedItem?> FindByUrlAsync(string normalizedUrl, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Url, SourceHost, Title, Content, CollectedAt, Status, Notes, TagsJson, ParseError, MediaJson FROM CollectedItems WHERE Url=$url AND {AccountScope("AccountKey")} ORDER BY Id LIMIT 1";
        command.Parameters.AddWithValue("$url", normalizedUrl);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<IReadOnlyList<CollectedItem>> GetItemsAsync(string? keyword = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Url, SourceHost, Title, Content, CollectedAt, Status, Notes, TagsJson, ParseError, MediaJson
            FROM CollectedItems
            WHERE ACCOUNT_SCOPE
              AND ($status='' OR Status=$status)
              AND ($keyword='' OR Title LIKE $pattern OR Content LIKE $pattern OR SourceHost LIKE $pattern OR TagsJson LIKE $pattern)
            ORDER BY CollectedAt DESC, Id DESC;
            """;
        command.CommandText = command.CommandText.Replace("ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        var query = keyword?.Trim() ?? "";
        command.Parameters.AddWithValue("$status", status ?? "");
        command.Parameters.AddWithValue("$keyword", query);
        command.Parameters.AddWithValue("$pattern", $"%{query}%");
        AddAccountParameter(command, accountKey);
        var items = new List<CollectedItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadItem(reader));
        return items;
    }

    public async Task DeleteItemAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM CollectedItems WHERE Id=$id AND {AccountScope("AccountKey")}";
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetItemStatusAsync(long id, string status, CancellationToken cancellationToken = default)
    {
        if (status is not ("待处理" or "保留" or "忽略" or "已入素材库")) throw new ArgumentOutOfRangeException(nameof(status));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE CollectedItems SET Status=$status WHERE Id=$id AND {AccountScope("AccountKey")}";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialRecord>> GetMaterialsAsync(string? keyword = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, SourceType, SourceUrl, CreatedAt, UpdatedAt
            FROM Materials
            WHERE ACCOUNT_SCOPE AND Status <> 'delete'
              AND ($status='' OR Status=$status OR ($status='active' AND Status IN ('waitsend','queue')))
              AND ($keyword='' OR Title LIKE $pattern OR Content LIKE $pattern OR Link LIKE $pattern OR GuildName LIKE $pattern OR ChannelName LIKE $pattern)
            ORDER BY CASE Status WHEN 'queue' THEN 0 ELSE 1 END, PublishAt, Id DESC;
            """;
        command.CommandText = command.CommandText.Replace("ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        var query = keyword?.Trim() ?? "";
        command.Parameters.AddWithValue("$status", status ?? "");
        command.Parameters.AddWithValue("$keyword", query);
        command.Parameters.AddWithValue("$pattern", $"%{query}%");
        AddAccountParameter(command, accountKey);
        var materials = new List<MaterialRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) materials.Add(ReadMaterial(reader));
        return materials;
    }

    public async Task<IReadOnlyList<MaterialRecord>> GetScheduledMaterialsAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, SourceType, SourceUrl, CreatedAt, UpdatedAt
            FROM Materials
            WHERE ACCOUNT_SCOPE AND Status = 'queue' AND PublishAt IS NOT NULL
            ORDER BY PublishAt ASC, Id ASC;
            """;
        command.CommandText = command.CommandText.Replace("ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        AddAccountParameter(command, accountKey);
        var materials = new List<MaterialRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) materials.Add(ReadMaterial(reader));
        return materials;
    }

    public async Task<long> SaveMaterialAsync(MaterialDraft material, CancellationToken cancellationToken = default)
    {
        ValidateMaterial(material);
        var contentError = MaterialMediaValidator.ValidatePost(material.Type, material.Title, material.Content, material.MediaLinks);
        if (contentError.Length > 0) throw new ArgumentException(contentError, nameof(material));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var now = DateTimeOffset.Now.ToString("O");
        if (material.Id is null)
        {
            command.CommandText = """
                INSERT INTO Materials (Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, SourceType, SourceUrl, CreatedAt, UpdatedAt, AccountKey)
                VALUES ($title,$type,$content,$guildId,$guildName,$channelId,$channelName,$publishAt,$status,$link,$media,$sourceType,$sourceUrl,$now,$now,$accountKey);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Materials SET Title=$title, Type=$type, Content=$content, GuildId=$guildId, GuildName=$guildName,
                    ChannelId=$channelId, ChannelName=$channelName, PublishAt=$publishAt, Status=$status,
                    Link=$link, MediaJson=$media, SourceType=$sourceType, SourceUrl=$sourceUrl, UpdatedAt=$now WHERE Id=$id AND ACCOUNT_SCOPE;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", material.Id.Value);
        }
        AddMaterialParameters(command, material, now);
        AddAccountParameter(command, accountKey);
        long id;
        if (material.Id is null)
        {
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
        else
        {
            command.CommandText = command.CommandText.Replace("ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("素材不存在或不属于当前账号。");
            }
            id = material.Id.Value;
        }
        await CancelPendingExecutionsAsync(connection, transaction, id, now, accountKey, cancellationToken);
        if (material.Status == "queue" && material.PublishAt is { } scheduledAt && scheduledAt > DateTimeOffset.Now)
            await InsertPendingExecutionAsync(connection, transaction, id, scheduledAt, now, accountKey, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<MaterialSourceSaveResult> SaveMaterialWithSourceAsync(
        MaterialDraft material,
        CancellationToken cancellationToken = default)
    {
        if (material.Id is not null)
            throw new ArgumentException("来源认领保存只能创建新素材。", nameof(material));
        ValidateMaterial(material);
        var contentError = MaterialMediaValidator.ValidatePost(material.Type, material.Title, material.Content, material.MediaLinks);
        if (contentError.Length > 0) throw new ArgumentException(contentError, nameof(material));

        var sourceType = material.SourceType.Trim();
        var sourceUrl = material.SourceUrl.Trim();
        if (sourceType.Length == 0 || sourceUrl.Length == 0)
            throw new ArgumentException("来源类型和来源地址不能为空。", nameof(material));

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var claim = connection.CreateCommand())
            {
                claim.Transaction = transaction;
                claim.CommandText = """
                    INSERT OR IGNORE INTO MaterialSourceClaims (AccountKey, SourceType, SourceUrl, MaterialId)
                    VALUES ($accountKey, $sourceType, $sourceUrl, 0);
                    SELECT changes();
                    """;
                claim.Parameters.AddWithValue("$accountKey", accountKey);
                claim.Parameters.AddWithValue("$sourceType", sourceType);
                claim.Parameters.AddWithValue("$sourceUrl", sourceUrl);
                if (Convert.ToInt64(await claim.ExecuteScalarAsync(cancellationToken)) == 1)
                {
                    long id;
                    await using (var insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = """
                            INSERT INTO Materials (Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, SourceType, SourceUrl, CreatedAt, UpdatedAt, AccountKey)
                            VALUES ($title,$type,$content,$guildId,$guildName,$channelId,$channelName,$publishAt,$status,$link,$media,$sourceType,$sourceUrl,$now,$now,$accountKey);
                            SELECT last_insert_rowid();
                            """;
                        AddMaterialParameters(insert, material, now);
                        insert.Parameters["$sourceType"].Value = sourceType;
                        insert.Parameters["$sourceUrl"].Value = sourceUrl;
                        AddAccountParameter(insert, accountKey);
                        id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
                    }

                    await using (var updateClaim = connection.CreateCommand())
                    {
                        updateClaim.Transaction = transaction;
                        updateClaim.CommandText = """
                            UPDATE MaterialSourceClaims
                            SET MaterialId=$materialId
                            WHERE AccountKey=$accountKey AND SourceType=$sourceType AND SourceUrl=$sourceUrl;
                            """;
                        updateClaim.Parameters.AddWithValue("$materialId", id);
                        updateClaim.Parameters.AddWithValue("$accountKey", accountKey);
                        updateClaim.Parameters.AddWithValue("$sourceType", sourceType);
                        updateClaim.Parameters.AddWithValue("$sourceUrl", sourceUrl);
                        await updateClaim.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await CancelPendingExecutionsAsync(connection, transaction, id, now, accountKey, cancellationToken);
                    if (material.Status == "queue" && material.PublishAt is { } scheduledAt && scheduledAt > DateTimeOffset.Now)
                        await InsertPendingExecutionAsync(connection, transaction, id, scheduledAt, now, accountKey, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(id, true, null);
                }
            }

            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT m.Id, m.Title, m.Type, m.Content, m.GuildId, m.GuildName, m.ChannelId, m.ChannelName, m.PublishAt,
                           m.Status, m.Link, m.MediaJson, m.SourceType, m.SourceUrl, m.CreatedAt, m.UpdatedAt
                    FROM MaterialSourceClaims c
                    INNER JOIN Materials m ON m.Id=c.MaterialId
                    WHERE c.AccountKey=$accountKey AND c.SourceType=$sourceType AND c.SourceUrl=$sourceUrl
                      AND m.Status <> 'delete'
                    LIMIT 1;
                    """;
                existing.Parameters.AddWithValue("$accountKey", accountKey);
                existing.Parameters.AddWithValue("$sourceType", sourceType);
                existing.Parameters.AddWithValue("$sourceUrl", sourceUrl);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var record = ReadMaterial(reader);
                    await reader.DisposeAsync();
                    await transaction.CommitAsync(cancellationToken);
                    return new(record.Id, false, record);
                }
            }

            await using (var staleClaim = connection.CreateCommand())
            {
                staleClaim.Transaction = transaction;
                staleClaim.CommandText = """
                    DELETE FROM MaterialSourceClaims
                    WHERE AccountKey=$accountKey AND SourceType=$sourceType AND SourceUrl=$sourceUrl
                      AND NOT EXISTS (
                          SELECT 1 FROM Materials m
                          WHERE m.Id=MaterialSourceClaims.MaterialId AND m.Status <> 'delete'
                      );
                    """;
                staleClaim.Parameters.AddWithValue("$accountKey", accountKey);
                staleClaim.Parameters.AddWithValue("$sourceType", sourceType);
                staleClaim.Parameters.AddWithValue("$sourceUrl", sourceUrl);
                await staleClaim.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        throw new InvalidOperationException("来源认领发生并发冲突，请重试。");
    }

    public Task<long[]> SaveMaterialsAsync(
        IReadOnlyList<MaterialDraft> materials,
        CancellationToken cancellationToken = default) =>
        SaveMaterialsCoreAsync(materials, null, cancellationToken);

    public Task<long[]> SaveMaterialsAsync(
        IReadOnlyList<MaterialDraft> materials,
        string importFileHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importFileHash);
        return SaveMaterialsCoreAsync(materials, importFileHash.Trim(), cancellationToken);
    }

    public async Task<bool> HasImportedFileAsync(
        string fileHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return false;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM MaterialImports WHERE FileHash=$hash AND {AccountScope("AccountKey")} LIMIT 1";
        command.Parameters.AddWithValue("$hash", fileHash.Trim());
        AddAccountParameter(command, accountKey);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<long[]> SaveMaterialsCoreAsync(
        IReadOnlyList<MaterialDraft> materials,
        string? importFileHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if (materials.Count == 0) return [];
        if (materials.Any(material => material.Id is not null))
            throw new ArgumentException("批量导入只能创建新素材。", nameof(materials));

        foreach (var material in materials)
        {
            ValidateMaterial(material);
            var contentError = MaterialMediaValidator.ValidatePost(material.Type, material.Title, material.Content, material.MediaLinks);
            if (contentError.Length > 0)
                throw new ArgumentException(contentError, nameof(materials));
            if (material.Status == "published")
                throw new ArgumentException("批量导入不允许直接写入已发布状态。", nameof(materials));
        }

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        var ids = new List<long>(materials.Count);
        try
        {
            foreach (var material in materials)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO Materials (Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, SourceType, SourceUrl, CreatedAt, UpdatedAt, AccountKey)
                    VALUES ($title,$type,$content,$guildId,$guildName,$channelId,$channelName,$publishAt,$status,$link,$media,$sourceType,$sourceUrl,$now,$now,$accountKey);
                    SELECT last_insert_rowid();
                    """;
                AddMaterialParameters(insert, material, now);
                AddAccountParameter(insert, accountKey);
                var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
                ids.Add(id);

                if (material.Status == "queue" && material.PublishAt is { } scheduledAt && scheduledAt > DateTimeOffset.Now)
                    await InsertPendingExecutionAsync(connection, transaction, id, scheduledAt, now, accountKey, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(importFileHash))
            {
                await using var import = connection.CreateCommand();
                import.Transaction = transaction;
                import.CommandText = """
                    INSERT INTO MaterialImports (AccountKey, FileHash, ImportedAt, ImportedCount)
                    VALUES ($accountKey, $hash, $now, $count);
                    """;
                AddAccountParameter(import, accountKey);
                import.Parameters.AddWithValue("$hash", importFileHash);
                import.Parameters.AddWithValue("$now", now);
                import.Parameters.AddWithValue("$count", materials.Count);
                try
                {
                    await import.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new InvalidOperationException("该导入文件已经处理过，不能重复导入。", ex);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return ids.ToArray();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpdateMaterialStatusAsync(long id, string status, string? link = null, CancellationToken cancellationToken = default)
    {
        if (status is not ("waitsend" or "queue" or "published" or "delete")) throw new ArgumentOutOfRangeException(nameof(status));
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE Materials SET Status=$status, Link=CASE WHEN $status='published' THEN $link ELSE Link END, UpdatedAt=$now WHERE Id=$id AND {AccountScope("AccountKey")}";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$link", link ?? "");
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BatchMaterialDeleteResult> DeleteMaterialsAsync(
        IReadOnlyList<long> materialIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        var ids = materialIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new([], [], [], []);

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        var deletedIds = new List<long>(ids.Length);
        var runningIds = new List<long>();
        var missingIds = new List<long>();
        var publishedIds = new List<long>();

        foreach (var id in ids)
        {
            await using var statusCommand = connection.CreateCommand();
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = $"SELECT Status FROM Materials WHERE Id=$id AND {AccountScope("AccountKey")}";
            statusCommand.Parameters.AddWithValue("$id", id);
            AddAccountParameter(statusCommand, accountKey);
            var status = await statusCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (status is null or "delete")
            {
                missingIds.Add(id);
                continue;
            }
            if (status == "published")
            {
                publishedIds.Add(id);
                continue;
            }

            await using var runningCommand = connection.CreateCommand();
            runningCommand.Transaction = transaction;
            runningCommand.CommandText = $"""
                SELECT 1
                FROM ScheduleExecutions
                WHERE MaterialId=$materialId AND {AccountScope("AccountKey")} AND Status='Running'
                LIMIT 1;
                """;
            runningCommand.Parameters.AddWithValue("$materialId", id);
            AddAccountParameter(runningCommand, accountKey);
            if (await runningCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                runningIds.Add(id);
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE Materials SET Status='delete', UpdatedAt=$now WHERE Id=$id AND {AccountScope("AccountKey")}";
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            AddAccountParameter(update, accountKey);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await CancelPendingExecutionsAsync(connection, transaction, id, now, accountKey, cancellationToken);
            deletedIds.Add(id);
        }

        await transaction.CommitAsync(cancellationToken);
        return new BatchMaterialDeleteResult(deletedIds, runningIds, publishedIds, missingIds);
    }

    public async Task<BatchMaterialTargetResult> UpdateMaterialsTargetAsync(
        IReadOnlyList<long> materialIds,
        string guildId,
        string guildName,
        string channelId,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        if (string.IsNullOrWhiteSpace(guildId)) throw new ArgumentException("目标频道不能为空。", nameof(guildId));
        if (string.IsNullOrWhiteSpace(guildName)) throw new ArgumentException("目标频道名称不能为空。", nameof(guildName));
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("目标版块不能为空。", nameof(channelId));
        if (string.IsNullOrWhiteSpace(channelName)) throw new ArgumentException("目标版块名称不能为空。", nameof(channelName));

        var ids = materialIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new([], [], [], []);

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        var updatedIds = new List<long>(ids.Length);
        var runningIds = new List<long>();
        var publishedIds = new List<long>();
        var missingIds = new List<long>();

        foreach (var id in ids)
        {
            await using var statusCommand = connection.CreateCommand();
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = $"SELECT Status FROM Materials WHERE Id=$id AND {AccountScope("AccountKey")}";
            statusCommand.Parameters.AddWithValue("$id", id);
            AddAccountParameter(statusCommand, accountKey);
            var status = await statusCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (status is null or "delete")
            {
                missingIds.Add(id);
                continue;
            }
            if (status == "published")
            {
                publishedIds.Add(id);
                continue;
            }

            await using var runningCommand = connection.CreateCommand();
            runningCommand.Transaction = transaction;
            runningCommand.CommandText = $"""
                SELECT 1
                FROM ScheduleExecutions
                WHERE MaterialId=$materialId AND {AccountScope("AccountKey")} AND Status='Running'
                LIMIT 1;
                """;
            runningCommand.Parameters.AddWithValue("$materialId", id);
            AddAccountParameter(runningCommand, accountKey);
            if (await runningCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                runningIds.Add(id);
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE Materials
                SET GuildId=$guildId, GuildName=$guildName, ChannelId=$channelId, ChannelName=$channelName, UpdatedAt=$now
                WHERE Id=$id AND {AccountScope("AccountKey")};
                """;
            update.Parameters.AddWithValue("$guildId", guildId);
            update.Parameters.AddWithValue("$guildName", guildName);
            update.Parameters.AddWithValue("$channelId", channelId);
            update.Parameters.AddWithValue("$channelName", channelName);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            AddAccountParameter(update, accountKey);
            await update.ExecuteNonQueryAsync(cancellationToken);
            updatedIds.Add(id);
        }

        await transaction.CommitAsync(cancellationToken);
        return new BatchMaterialTargetResult(updatedIds, runningIds, publishedIds, missingIds);
    }

    public async Task<BatchMaterialPublishAtResult> UpdateMaterialsPublishAtAsync(
        IReadOnlyList<long> materialIds,
        DateTimeOffset publishAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        if (publishAt <= DateTimeOffset.Now)
            throw new ArgumentException("发布时间必须晚于当前时间。", nameof(publishAt));

        var ids = materialIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new([], [], [], []);

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        var updatedIds = new List<long>(ids.Length);
        var runningIds = new List<long>();
        var publishedIds = new List<long>();
        var missingIds = new List<long>();

        foreach (var id in ids)
        {
            await using var statusCommand = connection.CreateCommand();
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = $"SELECT Status FROM Materials WHERE Id=$id AND {AccountScope("AccountKey")}";
            statusCommand.Parameters.AddWithValue("$id", id);
            AddAccountParameter(statusCommand, accountKey);
            var status = await statusCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (status is null or "delete")
            {
                missingIds.Add(id);
                continue;
            }
            if (status == "published")
            {
                publishedIds.Add(id);
                continue;
            }

            await using var runningCommand = connection.CreateCommand();
            runningCommand.Transaction = transaction;
            runningCommand.CommandText = $"""
                SELECT 1
                FROM ScheduleExecutions
                WHERE MaterialId=$materialId AND {AccountScope("AccountKey")} AND Status='Running'
                LIMIT 1;
                """;
            runningCommand.Parameters.AddWithValue("$materialId", id);
            AddAccountParameter(runningCommand, accountKey);
            if (await runningCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                runningIds.Add(id);
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE Materials
                SET PublishAt=$publishAt, Status='queue', UpdatedAt=$now
                WHERE Id=$id AND {AccountScope("AccountKey")};
                """;
            update.Parameters.AddWithValue("$publishAt", publishAt.ToString("O"));
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            AddAccountParameter(update, accountKey);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await CancelPendingExecutionsAsync(connection, transaction, id, now, accountKey, cancellationToken);
            await InsertPendingExecutionAsync(connection, transaction, id, publishAt, now, accountKey, cancellationToken);
            updatedIds.Add(id);
        }

        await transaction.CommitAsync(cancellationToken);
        return new BatchMaterialPublishAtResult(updatedIds, runningIds, publishedIds, missingIds);
    }

    public async Task<IReadOnlyList<long>> QueueMaterialsForPublishAsync(
        IReadOnlyList<long> materialIds,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        var ids = materialIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return [];

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");

        foreach (var id in ids)
        {
            await using var statusCommand = connection.CreateCommand();
            statusCommand.Transaction = transaction;
            statusCommand.CommandText = $"SELECT Status FROM Materials WHERE Id=$id AND {AccountScope("AccountKey")}";
            statusCommand.Parameters.AddWithValue("$id", id);
            AddAccountParameter(statusCommand, accountKey);
            var status = await statusCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (status is not ("waitsend" or "queue"))
                throw new InvalidOperationException($"素材 #{id} 不存在、已删除或已发布,不能加入发布队列。");

            await using var runningCommand = connection.CreateCommand();
            runningCommand.Transaction = transaction;
            runningCommand.CommandText = $"""
                SELECT 1
                FROM ScheduleExecutions
                WHERE MaterialId=$materialId AND {AccountScope("AccountKey")} AND Status='Running'
                LIMIT 1;
                """;
            runningCommand.Parameters.AddWithValue("$materialId", id);
            AddAccountParameter(runningCommand, accountKey);
            if (await runningCommand.ExecuteScalarAsync(cancellationToken) is not null)
                throw new InvalidOperationException($"素材 #{id} 正在发布,不能重复加入发布队列。");
        }

        foreach (var id in ids)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE Materials
                SET Status='queue', PublishAt=$scheduledAt, UpdatedAt=$now
                WHERE Id=$id AND {AccountScope("AccountKey")};
                """;
            update.Parameters.AddWithValue("$scheduledAt", scheduledAt.ToString("O"));
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            AddAccountParameter(update, accountKey);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await CancelPendingExecutionsAsync(connection, transaction, id, now, accountKey, cancellationToken);
            await InsertPendingExecutionAsync(connection, transaction, id, scheduledAt, now, accountKey, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    /// <summary>
    /// Queues one newly collected material through an automatic publishing
    /// policy. The policy, queue count, daily quota, and final slot are checked
    /// again inside the write transaction so multiple TG tasks cannot oversubscribe
    /// the same guild/channel by racing their in-memory checks.
    /// </summary>
    public async Task<PublishAutoScheduleResult> TryQueueAutomaticMaterialAsync(
        long materialId,
        DateTimeOffset notBefore,
        PublishPolicy policy,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (materialId <= 0) throw new ArgumentOutOfRangeException(nameof(materialId));
        if (!policy.Enabled) return PublishAutoScheduleResult.Disabled();
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromMinutes(Math.Max(1, policy.MinIntervalMinutes));

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now;
        var nowText = now.ToString("O");

        await using (var material = connection.CreateCommand())
        {
            material.Transaction = transaction;
            material.CommandText = $"""
                SELECT GuildId, ChannelId, Status, Content, MediaJson
                FROM Materials
                WHERE Id=$id AND {AccountScope("AccountKey")} AND Status <> 'delete'
                LIMIT 1;
                """;
            material.Parameters.AddWithValue("$id", materialId);
            AddAccountParameter(material, accountKey);
            await using var reader = await material.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return PublishAutoScheduleResult.NotScheduled("素材不存在或已删除。");
            }

            var guildId = reader.GetString(0);
            var channelId = reader.GetString(1);
            var status = reader.GetString(2);
            var content = reader.GetString(3);
            var mediaJson = reader.GetString(4);
            if (!string.Equals(guildId, policy.GuildId, StringComparison.Ordinal) ||
                !string.Equals(channelId, policy.ChannelId, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return PublishAutoScheduleResult.NotScheduled("素材目标与发布策略不一致。");
            }
            if (status is not ("waitsend" or "queue"))
            {
                await transaction.RollbackAsync(cancellationToken);
                return PublishAutoScheduleResult.NotScheduled("素材已经发布或正在处理。");
            }

            var media = JsonSerializer.Deserialize<string[]>(mediaJson) ?? [];
            if (string.IsNullOrWhiteSpace(content) && media.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PublishAutoScheduleResult.NotScheduled("素材没有可发布内容。");
            }
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = $"""
                SELECT ScheduledAt
                FROM ScheduleExecutions
                WHERE MaterialId=$materialId AND SchedulingMode='Auto' AND Status='Pending'
                  AND {AccountScope("AccountKey")}
                ORDER BY ScheduledAt ASC
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$materialId", materialId);
            AddAccountParameter(existing, accountKey);
            if (await existing.ExecuteScalarAsync(cancellationToken) is string existingAt)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PublishAutoScheduleResult.AlreadyQueued(DateTimeOffset.Parse(existingAt));
            }
        }

        var queueCount = await CountAutomaticExecutionsAsync(
            connection,
            transaction,
            accountKey,
            policy,
            statuses: "('Pending','Running')",
            day: null,
            cancellationToken);
        if (queueCount >= policy.QueueLimit)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PublishAutoScheduleResult.QueueFull();
        }

        DateTimeOffset? latestScheduledAt = null;
        await using (var latest = connection.CreateCommand())
        {
            latest.Transaction = transaction;
            latest.CommandText = $"""
                SELECT MAX(e.ScheduledAt)
                FROM ScheduleExecutions e
                INNER JOIN Materials m ON m.Id=e.MaterialId
                WHERE e.SchedulingMode='Auto'
                  AND e.Status IN ('Pending','Running','Succeeded','NeedsVerification')
                  AND m.GuildId=$guildId AND m.ChannelId=$channelId
                  AND {AccountScope("e.AccountKey")} AND {AccountScope("m.AccountKey")};
                """;
            latest.Parameters.AddWithValue("$guildId", policy.GuildId);
            latest.Parameters.AddWithValue("$channelId", policy.ChannelId);
            AddAccountParameter(latest, accountKey);
            if (await latest.ExecuteScalarAsync(cancellationToken) is string latestAt)
                latestScheduledAt = DateTimeOffset.Parse(latestAt);
        }

        var candidate = notBefore <= now ? now.AddSeconds(1) : notBefore;
        if (latestScheduledAt is { } latestAtValue && latestAtValue + interval > candidate)
            candidate = latestAtValue + interval;

        var guildBudget = await ReadGuildBudgetAsync(connection, transaction, policy.GuildId, accountKey, cancellationToken);
        for (var dayOffset = 0; dayOffset < 370; dayOffset++)
        {
            candidate = policy.MoveIntoWindow(candidate);
            var day = candidate.ToString("yyyy-MM-dd");

            if (await CountAutomaticExecutionsAsync(
                    connection,
                    transaction,
                    accountKey,
                    policy,
                    statuses: "('Pending','Running','Succeeded','NeedsVerification')",
                    day,
                    cancellationToken) >= policy.DailyLimit)
            {
                candidate = policy.MoveIntoWindow(candidate.Date.AddDays(1).Add(policy.WindowStartTime.ToTimeSpan()));
                continue;
            }

            if (guildBudget is { Enabled: true } budget &&
                await CountAutomaticExecutionsAsync(
                    connection,
                    transaction,
                    accountKey,
                    policy,
                    statuses: "('Pending','Running','Succeeded','NeedsVerification')",
                    day,
                    cancellationToken,
                    guildOnly: true) >= budget.DailyLimit)
            {
                candidate = policy.MoveIntoWindow(candidate.Date.AddDays(1).Add(policy.WindowStartTime.ToTimeSpan()));
                continue;
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE Materials
                    SET Status='queue', PublishAt=$scheduledAt, UpdatedAt=$now
                    WHERE Id=$id AND {AccountScope("AccountKey")} AND Status IN ('waitsend','queue');
                    """;
                update.Parameters.AddWithValue("$scheduledAt", candidate.ToString("O"));
                update.Parameters.AddWithValue("$now", nowText);
                update.Parameters.AddWithValue("$id", materialId);
                AddAccountParameter(update, accountKey);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return PublishAutoScheduleResult.NotScheduled("素材状态已变化，请稍后重试。");
                }
            }

            await CancelPendingExecutionsAsync(connection, transaction, materialId, nowText, accountKey, cancellationToken);
            await InsertPendingExecutionAsync(
                connection,
                transaction,
                materialId,
                candidate,
                nowText,
                accountKey,
                cancellationToken,
                ScheduleExecutionMode.Auto);
            await transaction.CommitAsync(cancellationToken);
            return PublishAutoScheduleResult.Queued(candidate);
        }

        await transaction.RollbackAsync(cancellationToken);
        return PublishAutoScheduleResult.QuotaFull();
    }

    public async Task<bool> CancelMaterialScheduleAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Materials
            SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now
            WHERE Id=$id AND ACCOUNT_SCOPE AND Status='queue' AND PublishAt IS NOT NULL
              AND EXISTS (SELECT 1 FROM ScheduleExecutions WHERE MaterialId=$id AND ACCOUNT_SCOPE_EXECUTION AND Status='Pending');
            """;
        command.CommandText = command.CommandText
            .Replace("ACCOUNT_SCOPE_EXECUTION", AccountScope("AccountKey", "ScheduleExecutions"), StringComparison.Ordinal)
            .Replace("ACCOUNT_SCOPE", AccountScope("AccountKey"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using var cancel = connection.CreateCommand();
        cancel.Transaction = transaction;
        cancel.CommandText = $"UPDATE ScheduleExecutions SET Status='Cancelled', IsPaused=0, UpdatedAt=$now, CompletedAt=$now, LastError='用户取消计划' WHERE MaterialId=$id AND {AccountScope("AccountKey")} AND Status='Pending'";
        cancel.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        cancel.Parameters.AddWithValue("$id", id);
        AddAccountParameter(cancel, accountKey);
        await cancel.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ScheduleExecutionRecord>> GetScheduleExecutionsAsync(CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return [];
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Id, e.MaterialId, e.ScheduledAt, e.Status, e.StartedAt, e.CompletedAt,
                   e.LastError, e.PublishRecordId, e.CreatedAt, e.UpdatedAt,
                   m.Title, m.Type, m.GuildName, m.ChannelName, m.PublishAt, m.Link,
                   m.Content, m.MediaJson, e.SchedulingMode, e.RateLimitAttempts, e.IsPaused
            FROM ScheduleExecutions e
            LEFT JOIN Materials m ON m.Id=e.MaterialId
            WHERE ACCOUNT_SCOPE_EXECUTION AND ACCOUNT_SCOPE_MATERIAL
            ORDER BY e.ScheduledAt ASC, e.MaterialId ASC, e.Id ASC;
            """;
        command.CommandText = command.CommandText
            .Replace("ACCOUNT_SCOPE_EXECUTION", AccountScope("e.AccountKey"), StringComparison.Ordinal)
            .Replace("ACCOUNT_SCOPE_MATERIAL", AccountScope("m.AccountKey"), StringComparison.Ordinal);
        AddAccountParameter(command, accountKey);
        var records = new List<ScheduleExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadScheduleExecution(reader));
        return records;
    }

    /// <summary>
    /// 批量暂停或启动待执行计划。运行中的计划不会被抢占，终态记录也不会被改写。
    /// </summary>
    public async Task<ScheduleExecutionMutationResult> SetScheduleExecutionsPausedAsync(
        IReadOnlyList<long> executionIds,
        bool isPaused,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionIds);
        var ids = executionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new([], [], [], []);

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        var affectedIds = new List<long>(ids.Length);
        var runningIds = new List<long>();
        var finishedIds = new List<long>();
        var missingIds = new List<long>();

        foreach (var id in ids)
        {
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = $"SELECT Status FROM ScheduleExecutions WHERE Id=$id AND {AccountScope("AccountKey")} LIMIT 1";
            read.Parameters.AddWithValue("$id", id);
            AddAccountParameter(read, accountKey);
            var statusText = await read.ExecuteScalarAsync(cancellationToken) as string;
            if (statusText is null)
            {
                missingIds.Add(id);
                continue;
            }

            if (!Enum.TryParse<ScheduleExecutionStatus>(statusText, true, out var status) ||
                status != ScheduleExecutionStatus.Pending)
            {
                if (status == ScheduleExecutionStatus.Running) runningIds.Add(id);
                else finishedIds.Add(id);
                continue;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE ScheduleExecutions
                SET IsPaused=$isPaused, UpdatedAt=$now
                WHERE Id=$id AND Status='Pending' AND {AccountScope("AccountKey")};
                """;
            update.Parameters.AddWithValue("$isPaused", isPaused ? 1 : 0);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$id", id);
            AddAccountParameter(update, accountKey);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                affectedIds.Add(id);
                continue;
            }

            await using var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = $"SELECT Status FROM ScheduleExecutions WHERE Id=$id AND {AccountScope("AccountKey")} LIMIT 1";
            current.Parameters.AddWithValue("$id", id);
            AddAccountParameter(current, accountKey);
            var currentStatusText = await current.ExecuteScalarAsync(cancellationToken) as string;
            if (currentStatusText is null) missingIds.Add(id);
            else if (Enum.TryParse<ScheduleExecutionStatus>(currentStatusText, true, out var currentStatus) &&
                     currentStatus == ScheduleExecutionStatus.Running)
                runningIds.Add(id);
            else
                finishedIds.Add(id);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(affectedIds, runningIds, finishedIds, missingIds);
    }

    /// <summary>
    /// 删除计划执行记录，不删除素材或发布历史。待执行计划被删除后，只有在该素材
    /// 已没有其他待执行/运行中计划时才恢复为待发布。
    /// </summary>
    public async Task<ScheduleExecutionMutationResult> DeleteScheduleExecutionsAsync(
        IReadOnlyList<long> executionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionIds);
        var ids = executionIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new([], [], [], []);

        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        var deletedIds = new List<long>(ids.Length);
        var runningIds = new List<long>();
        var finishedIds = new List<long>();
        var missingIds = new List<long>();

        foreach (var id in ids)
        {
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = $"SELECT MaterialId, Status FROM ScheduleExecutions WHERE Id=$id AND {AccountScope("AccountKey")} LIMIT 1";
            read.Parameters.AddWithValue("$id", id);
            AddAccountParameter(read, accountKey);
            long materialId;
            string statusText;
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    missingIds.Add(id);
                    continue;
                }

                materialId = reader.GetInt64(0);
                statusText = reader.GetString(1);
            }

            if (Enum.TryParse<ScheduleExecutionStatus>(statusText, true, out var status) &&
                status == ScheduleExecutionStatus.Running)
            {
                runningIds.Add(id);
                continue;
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM ScheduleExecutions WHERE Id=$id AND Status<>'Running' AND {AccountScope("AccountKey")}";
            delete.Parameters.AddWithValue("$id", id);
            AddAccountParameter(delete, accountKey);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                runningIds.Add(id);
                continue;
            }

            deletedIds.Add(id);
            if (status != ScheduleExecutionStatus.Pending) continue;

            await using var release = connection.CreateCommand();
            release.Transaction = transaction;
            release.CommandText = $"""
                UPDATE Materials
                SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now
                WHERE Id=$materialId AND {AccountScope("AccountKey")} AND Status='queue'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ScheduleExecutions e
                      WHERE e.MaterialId=$materialId
                        AND e.Status IN ('Pending','Running')
                        AND {AccountScope("e.AccountKey")}
                  );
                """;
            release.Parameters.AddWithValue("$now", now);
            release.Parameters.AddWithValue("$materialId", materialId);
            AddAccountParameter(release, accountKey);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(deletedIds, runningIds, finishedIds, missingIds);
    }

    public async Task<ScheduleExecutionRecord?> TryClaimDueExecutionAsync(
        DateTimeOffset now,
        bool includeOverdue = true,
        DateTimeOffset? notBefore = null,
        CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var executionId = await SelectClaimableExecutionAsync(
            connection,
            transaction,
            now,
            includeOverdue,
            notBefore,
            accountKey,
            cancellationToken);
        if (executionId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var startedAt = DateTimeOffset.Now.ToString("O");
        await using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = $"UPDATE ScheduleExecutions SET Status='Running', StartedAt=$started, UpdatedAt=$started WHERE Id=$id AND Status='Pending' AND IsPaused=0 AND {AccountScope("AccountKey")}";
        claim.Parameters.AddWithValue("$started", startedAt);
        claim.Parameters.AddWithValue("$id", executionId);
        AddAccountParameter(claim, accountKey);
        if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using var fetch = connection.CreateCommand();
        fetch.Transaction = transaction;
        fetch.CommandText = """
            SELECT e.Id, e.MaterialId, e.ScheduledAt, e.Status, e.StartedAt, e.CompletedAt,
                   e.LastError, e.PublishRecordId, e.CreatedAt, e.UpdatedAt,
                   m.Title, m.Type, m.GuildName, m.ChannelName, m.PublishAt, m.Link,
                   m.Content, m.MediaJson, e.SchedulingMode, e.RateLimitAttempts, e.IsPaused
            FROM ScheduleExecutions e
            INNER JOIN Materials m ON m.Id=e.MaterialId
            WHERE e.Id=$id AND ACCOUNT_SCOPE_EXECUTION AND ACCOUNT_SCOPE_MATERIAL;
            """;
        fetch.CommandText = fetch.CommandText
            .Replace("ACCOUNT_SCOPE_EXECUTION", AccountScope("e.AccountKey"), StringComparison.Ordinal)
            .Replace("ACCOUNT_SCOPE_MATERIAL", AccountScope("m.AccountKey"), StringComparison.Ordinal);
        fetch.Parameters.AddWithValue("$id", executionId);
        AddAccountParameter(fetch, accountKey);
        ScheduleExecutionRecord result;
        await using (var reader = await fetch.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            result = ReadScheduleExecution(reader);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// 领取到期计划：手工计划优先且不受自动发布策略限制；自动计划逐条重新校验
    /// 策略开关、发布窗口、版块/频道额度和版块并发，被阻塞时保持 Pending，
    /// 从而不会让队首一个被阻塞的自动任务挡住后面的任务。
    /// </summary>
    private async Task<long?> SelectClaimableExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        bool includeOverdue,
        DateTimeOffset? notBefore,
        string? accountKey,
        CancellationToken cancellationToken)
    {
        var manualCandidates = await SelectDueClaimCandidatesAsync(
            connection,
            transaction,
            now,
            includeOverdue,
            notBefore,
            accountKey,
            ScheduleExecutionMode.Manual,
            1,
            cancellationToken);
        if (manualCandidates.Count > 0) return manualCandidates[0].Id;

        var candidates = await SelectDueClaimCandidatesAsync(
            connection,
            transaction,
            now,
            includeOverdue,
            notBefore,
            accountKey,
            ScheduleExecutionMode.Auto,
            300,
            cancellationToken);
        var decisions = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"{candidate.GuildId}\u001f{candidate.ChannelId}";
            if (!decisions.TryGetValue(key, out var allowed))
            {
                allowed = await CanClaimAutoExecutionAsync(
                    connection,
                    transaction,
                    candidate,
                    now,
                    accountKey,
                    cancellationToken);
                decisions[key] = allowed;
            }
            if (allowed) return candidate.Id;
        }
        return null;
    }

    private async Task<IReadOnlyList<ClaimCandidate>> SelectDueClaimCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        bool includeOverdue,
        DateTimeOffset? notBefore,
        string? accountKey,
        ScheduleExecutionMode schedulingMode,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT e.Id, e.SchedulingMode, m.GuildId, m.ChannelId, e.ScheduledAt
            FROM ScheduleExecutions e
            INNER JOIN Materials m ON m.Id=e.MaterialId
            WHERE {AccountScope("e.AccountKey")} AND {AccountScope("m.AccountKey")}
              AND e.Status='Pending' AND e.IsPaused=0 AND e.ScheduledAt <= $now AND m.Status='queue' AND m.PublishAt IS NOT NULL
              AND e.SchedulingMode=$mode
              AND ($includeOverdue=1 OR e.ScheduledAt >= $notBefore)
            ORDER BY e.ScheduledAt ASC, e.MaterialId ASC, e.Id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$includeOverdue", includeOverdue ? 1 : 0);
        command.Parameters.AddWithValue("$notBefore", (object?)(notBefore?.ToString("O")) ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", schedulingMode.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        AddAccountParameter(command, accountKey);

        var candidates = new List<ClaimCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new ClaimCandidate(
                reader.GetInt64(0),
                Enum.TryParse<ScheduleExecutionMode>(reader.GetString(1), true, out var mode)
                    ? mode
                    : ScheduleExecutionMode.Manual,
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }
        return candidates;
    }

    private async Task<bool> CanClaimAutoExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClaimCandidate candidate,
        DateTimeOffset now,
        string? accountKey,
        CancellationToken cancellationToken)
    {
        var policy = await ReadClaimPolicyAsync(
            connection,
            transaction,
            candidate.GuildId,
            candidate.ChannelId,
            accountKey,
            cancellationToken);
        if (policy is null || !policy.Enabled) return false;

        var localNow = now.ToLocalTime();
        if (!policy.IsWithinWindow(localNow)) return false;

        var day = localNow.ToString("yyyy-MM-dd");
        // 当天已经真正开始执行的自动任务才算占用额度；Pending 在排期阶段已经计入
        // 每日上限，这里重复统计会把队列自身算满并造成永久阻塞。
        var committedToday = await CountAutomaticExecutionsAsync(
            connection,
            transaction,
            accountKey,
            policy,
            "('Running','Succeeded','NeedsVerification')",
            day,
            cancellationToken);
        if (committedToday >= policy.DailyLimit) return false;

        var runningSameChannel = await CountAutomaticExecutionsAsync(
            connection,
            transaction,
            accountKey,
            policy,
            "('Running')",
            null,
            cancellationToken);
        if (runningSameChannel >= Math.Max(1, policy.MaxConcurrency)) return false;

        var budget = await ReadGuildBudgetAsync(
            connection,
            transaction,
            candidate.GuildId,
            accountKey,
            cancellationToken);
        if (budget is { Enabled: true })
        {
            var guildCommittedToday = await CountAutomaticExecutionsAsync(
                connection,
                transaction,
                accountKey,
                policy,
                "('Running','Succeeded','NeedsVerification')",
                day,
                cancellationToken,
                guildOnly: true);
            if (guildCommittedToday >= budget.DailyLimit) return false;
        }

        return true;
    }

    public async Task<ScheduleExecutionRecord?> GetActiveScheduleExecutionAsync(long materialId, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Id, e.MaterialId, e.ScheduledAt, e.Status, e.StartedAt, e.CompletedAt,
                   e.LastError, e.PublishRecordId, e.CreatedAt, e.UpdatedAt,
                   m.Title, m.Type, m.GuildName, m.ChannelName, m.PublishAt, m.Link,
                   m.Content, m.MediaJson, e.SchedulingMode, e.RateLimitAttempts, e.IsPaused
            FROM ScheduleExecutions e
            LEFT JOIN Materials m ON m.Id=e.MaterialId
            WHERE e.MaterialId=$materialId AND ACCOUNT_SCOPE_EXECUTION AND ACCOUNT_SCOPE_MATERIAL
              AND e.Status IN ('Pending','Running')
            ORDER BY CASE e.Status WHEN 'Running' THEN 0 ELSE 1 END, e.ScheduledAt ASC, e.Id DESC
            LIMIT 1;
            """;
        command.CommandText = command.CommandText
            .Replace("ACCOUNT_SCOPE_EXECUTION", AccountScope("e.AccountKey"), StringComparison.Ordinal)
            .Replace("ACCOUNT_SCOPE_MATERIAL", AccountScope("m.AccountKey"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$materialId", materialId);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadScheduleExecution(reader) : null;
    }

    public async Task<bool> CompleteScheduleExecutionAsync(long executionId, long? publishRecordId, PublishResult result, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now;
        var nowText = now.ToString("O");
        var status = result.Succeeded ? ScheduleExecutionStatus.Succeeded :
            result.Category == PublishErrorCategory.Timeout ? ScheduleExecutionStatus.NeedsVerification : ScheduleExecutionStatus.Failed;

        long materialId;
        var materialStatus = "";
        var schedulingMode = ScheduleExecutionMode.Manual;
        var rateLimitAttempts = 0;
        string? guildId = null;
        string? channelId = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"""
                SELECT e.MaterialId, e.SchedulingMode, e.RateLimitAttempts, m.Status, m.GuildId, m.ChannelId
                FROM ScheduleExecutions e
                INNER JOIN Materials m ON m.Id=e.MaterialId
                WHERE e.Id=$id AND {AccountScope("e.AccountKey")} AND {AccountScope("m.AccountKey")}
                LIMIT 1;
                """;
            read.Parameters.AddWithValue("$id", executionId);
            AddAccountParameter(read, accountKey);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            materialId = reader.GetInt64(0);
            schedulingMode = Enum.TryParse<ScheduleExecutionMode>(reader.IsDBNull(1) ? "" : reader.GetString(1), true, out var mode)
                ? mode
                : ScheduleExecutionMode.Manual;
            rateLimitAttempts = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            materialStatus = reader.GetString(3);
            guildId = reader.IsDBNull(4) ? "" : reader.GetString(4);
            channelId = reader.IsDBNull(5) ? "" : reader.GetString(5);
        }

        // 只有自动排期的限流失败才需要退避重排；超时保持待核实，绝不自动重试。
        var requeueAfterRateLimit = !result.Succeeded
            && result.Category == PublishErrorCategory.RateLimit
            && schedulingMode == ScheduleExecutionMode.Auto
            && materialStatus == "queue"
            && !string.IsNullOrWhiteSpace(guildId)
            && !string.IsNullOrWhiteSpace(channelId);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            // Only the task that successfully claimed the execution may complete it.
            // This prevents a late publisher from overwriting a recovery result.
            update.CommandText = $"UPDATE ScheduleExecutions SET Status=$status, CompletedAt=$now, UpdatedAt=$now, LastError=$error, PublishRecordId=$record WHERE Id=$id AND {AccountScope("AccountKey")} AND Status='Running'";
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$now", nowText);
            update.Parameters.AddWithValue("$error", result.Succeeded ? "" : AppLogger.Redact(result.Message));
            update.Parameters.AddWithValue("$record", (object?)publishRecordId ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", executionId);
            AddAccountParameter(update, accountKey);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await using (var material = connection.CreateCommand())
        {
            material.Transaction = transaction;
            material.CommandText = requeueAfterRateLimit
                // 限流重排的素材保持 queue，稍后写入新的发布时间。
                ? $"UPDATE Materials SET Status='queue', UpdatedAt=$now WHERE Id=$materialId AND {AccountScope("AccountKey")}"
                : result.Succeeded
                    ? $"UPDATE Materials SET Status='published', Link=$link, PublishAt=NULL, UpdatedAt=$now WHERE Id=$materialId AND {AccountScope("AccountKey")}"
                    : $"UPDATE Materials SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now WHERE Id=$materialId AND {AccountScope("AccountKey")} AND Status='queue'";
            material.Parameters.AddWithValue("$link", result.Url ?? result.PostId ?? "");
            material.Parameters.AddWithValue("$now", nowText);
            material.Parameters.AddWithValue("$materialId", materialId);
            AddAccountParameter(material, accountKey);
            await material.ExecuteNonQueryAsync(cancellationToken);
        }

        if (requeueAfterRateLimit)
        {
            var policy = await ReadClaimPolicyAsync(
                connection,
                transaction,
                guildId!,
                channelId!,
                accountKey,
                cancellationToken);
            DateTimeOffset? retryAt = null;
            var attempts = Math.Max(1, rateLimitAttempts + 1);
            if (policy is { Enabled: true })
            {
                var baseMinutes = Math.Clamp(policy.RateLimitBaseMinutes, 1, 24 * 60);
                var maxMinutes = Math.Clamp(policy.RateLimitMaxMinutes, baseMinutes, 7 * 24 * 60);
                var factor = Math.Pow(2, Math.Min(attempts - 1, 20));
                var delayMinutes = Math.Min(maxMinutes, baseMinutes * factor);
                if (!double.IsFinite(delayMinutes) || delayMinutes <= 0) delayMinutes = maxMinutes;
                retryAt = policy.MoveIntoWindow(now.AddMinutes(Math.Clamp(delayMinutes, 1, 30 * 24 * 60)));
            }

            if (retryAt is { } scheduledAt)
            {
                await InsertPendingExecutionAsync(
                    connection,
                    transaction,
                    materialId,
                    scheduledAt,
                    nowText,
                    accountKey,
                    cancellationToken,
                    ScheduleExecutionMode.Auto,
                    attempts);
                await using var reschedule = connection.CreateCommand();
                reschedule.Transaction = transaction;
                reschedule.CommandText = $"UPDATE Materials SET Status='queue', PublishAt=$at, UpdatedAt=$now WHERE Id=$materialId AND {AccountScope("AccountKey")}";
                reschedule.Parameters.AddWithValue("$at", scheduledAt.ToString("O"));
                reschedule.Parameters.AddWithValue("$now", nowText);
                reschedule.Parameters.AddWithValue("$materialId", materialId);
                AddAccountParameter(reschedule, accountKey);
                await reschedule.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                // 策略已被删除或停用：把素材退回待发布，交给用户手工处理，避免永久卡在队列。
                await using var release = connection.CreateCommand();
                release.Transaction = transaction;
                release.CommandText = $"UPDATE Materials SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now WHERE Id=$materialId AND {AccountScope("AccountKey")} AND Status='queue'";
                release.Parameters.AddWithValue("$now", nowText);
                release.Parameters.AddWithValue("$materialId", materialId);
                AddAccountParameter(release, accountKey);
                await release.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkRunningExecutionsAsNeedsVerificationAsync(string reason, CancellationToken cancellationToken = default)
    {
        var accountKey = RequireAccountKey();
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"UPDATE ScheduleExecutions SET Status='NeedsVerification', CompletedAt=$now, UpdatedAt=$now, LastError=$error WHERE {AccountScope("AccountKey")} AND Status='Running'";
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$error", AppLogger.Redact(reason));
            AddAccountParameter(update, accountKey);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var material = connection.CreateCommand())
        {
            material.Transaction = transaction;
            material.CommandText = $"UPDATE Materials SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now WHERE {AccountScope("AccountKey")} AND Id IN (SELECT MaterialId FROM ScheduleExecutions WHERE {AccountScope("AccountKey", "ScheduleExecutions")} AND Status='NeedsVerification' AND CompletedAt=$now) AND Status='queue'";
            material.Parameters.AddWithValue("$now", now);
            AddAccountParameter(material, accountKey);
            await material.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MaterialRecord?> GetMaterialAsync(long id, CancellationToken cancellationToken = default)
    {
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, SourceType, SourceUrl, CreatedAt, UpdatedAt FROM Materials WHERE Id=$id AND {AccountScope("AccountKey")} AND Status<>'delete'";
        command.Parameters.AddWithValue("$id", id);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMaterial(reader) : null;
    }

    private static void ValidateMaterial(MaterialDraft material)
    {
        if (material.Type is not ("text" or "image" or "video")) throw new ArgumentOutOfRangeException(nameof(material), "素材类型无效。");
        if (material.Status is not ("waitsend" or "queue" or "published")) throw new ArgumentOutOfRangeException(nameof(material), "素材状态无效。");
    }

    private static void AddMaterialParameters(SqliteCommand command, MaterialDraft material, string now)
    {
        command.Parameters.AddWithValue("$title", material.Title.Trim());
        command.Parameters.AddWithValue("$type", material.Type);
        command.Parameters.AddWithValue("$content", material.Content.Trim());
        command.Parameters.AddWithValue("$guildId", material.GuildId);
        command.Parameters.AddWithValue("$guildName", material.GuildName);
        command.Parameters.AddWithValue("$channelId", material.ChannelId);
        command.Parameters.AddWithValue("$channelName", material.ChannelName);
        command.Parameters.AddWithValue("$publishAt", material.PublishAt?.ToString("O") is { } at ? at : DBNull.Value);
        command.Parameters.AddWithValue("$status", material.Status);
        command.Parameters.AddWithValue("$link", material.Link);
        command.Parameters.AddWithValue("$media", JsonSerializer.Serialize(material.MediaLinks));
        command.Parameters.AddWithValue("$sourceType", string.IsNullOrWhiteSpace(material.SourceType) ? "手工添加" : material.SourceType.Trim());
        command.Parameters.AddWithValue("$sourceUrl", material.SourceUrl.Trim());
        command.Parameters.AddWithValue("$now", now);
    }

    private string? ReadAccountKey() => _accountContext?.CurrentAccountKey;

    private string RequireAccountKey() => _accountContext?.RequireAccountKey() ?? "";

    private string AccountScope(string column, string? table = null) =>
        _accountContext is null ? "1=1" : $"{column} = $accountKey";

    private static void AddAccountParameter(SqliteCommand command, string? accountKey)
    {
        if (accountKey is not null)
            command.Parameters.AddWithValue("$accountKey", accountKey);
    }

    private static async Task EnsureAccountKeyColumnAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, tableName, "AccountKey", "TEXT NULL", cancellationToken);
    }

    private static async Task EnsureMaterialSourceClaimsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO MaterialSourceClaims (AccountKey, SourceType, SourceUrl, MaterialId)
            SELECT COALESCE(AccountKey, ''), SourceType, SourceUrl, MIN(Id)
            FROM Materials
            WHERE Status <> 'delete' AND SourceType <> '' AND SourceUrl <> ''
            GROUP BY COALESCE(AccountKey, ''), SourceType, SourceUrl;

            CREATE TRIGGER IF NOT EXISTS TR_Materials_SourceClaim_AfterDelete
            AFTER DELETE ON Materials
            BEGIN
                UPDATE MaterialSourceClaims
                SET MaterialId = COALESCE((
                    SELECT MIN(m.Id)
                    FROM Materials m
                    WHERE m.SourceType=MaterialSourceClaims.SourceType
                      AND m.SourceUrl=MaterialSourceClaims.SourceUrl
                      AND m.Status <> 'delete'
                      AND COALESCE(m.AccountKey, '')=MaterialSourceClaims.AccountKey
                ), MaterialId)
                WHERE MaterialId=OLD.Id;

                DELETE FROM MaterialSourceClaims
                WHERE MaterialId=OLD.Id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Materials m
                      WHERE m.SourceType=MaterialSourceClaims.SourceType
                        AND m.SourceUrl=MaterialSourceClaims.SourceUrl
                        AND m.Status <> 'delete'
                        AND COALESCE(m.AccountKey, '')=MaterialSourceClaims.AccountKey
                  );
            END;

            CREATE TRIGGER IF NOT EXISTS TR_Materials_SourceClaim_AfterSoftDelete
            AFTER UPDATE OF Status ON Materials
            WHEN NEW.Status='delete' AND OLD.Status <> 'delete'
            BEGIN
                UPDATE MaterialSourceClaims
                SET MaterialId = COALESCE((
                    SELECT MIN(m.Id)
                    FROM Materials m
                    WHERE m.SourceType=MaterialSourceClaims.SourceType
                      AND m.SourceUrl=MaterialSourceClaims.SourceUrl
                      AND m.Status <> 'delete'
                      AND COALESCE(m.AccountKey, '')=MaterialSourceClaims.AccountKey
                ), MaterialId)
                WHERE MaterialId=NEW.Id;

                DELETE FROM MaterialSourceClaims
                WHERE MaterialId=NEW.Id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Materials m
                      WHERE m.SourceType=MaterialSourceClaims.SourceType
                        AND m.SourceUrl=MaterialSourceClaims.SourceUrl
                        AND m.Status <> 'delete'
                        AND COALESCE(m.AccountKey, '')=MaterialSourceClaims.AccountKey
                  );
            END;

            CREATE TRIGGER IF NOT EXISTS TR_Materials_SourceClaim_AfterSourceUpdate
            AFTER UPDATE OF SourceType, SourceUrl ON Materials
            WHEN OLD.SourceType <> NEW.SourceType OR OLD.SourceUrl <> NEW.SourceUrl
            BEGIN
                UPDATE OR IGNORE MaterialSourceClaims
                SET SourceType=NEW.SourceType, SourceUrl=NEW.SourceUrl
                WHERE MaterialId=NEW.Id;

                DELETE FROM MaterialSourceClaims
                WHERE MaterialId=NEW.Id
                  AND (SourceType <> NEW.SourceType OR SourceUrl <> NEW.SourceUrl);
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        var found = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }

        if (found) return;
        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MaterialRecord ReadMaterial(SqliteDataReader reader)
    {
        var material = new MaterialRecord(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9),
            reader.GetString(10), JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? [],
            DateTimeOffset.Parse(reader.GetString(14)), DateTimeOffset.Parse(reader.GetString(15)));
        return material with
        {
            SourceType = reader.IsDBNull(12) ? "手工添加" : reader.GetString(12),
            SourceUrl = reader.IsDBNull(13) ? "" : reader.GetString(13)
        };
    }

    public async Task<bool> HasMaterialSourceAsync(
        string sourceType,
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        return await FindMaterialBySourceAsync(sourceType, sourceUrl, cancellationToken) is not null;
    }

    public async Task<MaterialRecord?> FindMaterialBySourceAsync(
        string sourceType,
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceUrl)) return null;
        var accountKey = ReadAccountKey();
        if (_accountContext is not null && accountKey is null) return null;
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var accountScope = AccountScope("AccountKey");
        command.CommandText = $"""
            SELECT Id, Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt,
                   Status, Link, MediaJson, SourceType, SourceUrl, CreatedAt, UpdatedAt
            FROM Materials
            WHERE SourceType=$type AND SourceUrl=$url AND Status <> 'delete' AND {accountScope}
            ORDER BY Id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", sourceType.Trim());
        command.Parameters.AddWithValue("$url", sourceUrl.Trim());
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMaterial(reader) : null;
    }

    private static ScheduleExecutionRecord ReadScheduleExecution(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2)),
        Enum.Parse<ScheduleExecutionStatus>(reader.GetString(3)),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7), DateTimeOffset.Parse(reader.GetString(8)), DateTimeOffset.Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? "" : reader.GetString(10), reader.IsDBNull(11) ? "" : reader.GetString(11),
        reader.IsDBNull(12) ? "" : reader.GetString(12), reader.IsDBNull(13) ? "" : reader.GetString(13),
        reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
        reader.IsDBNull(15) ? "" : reader.GetString(15),
        reader.IsDBNull(16) ? "" : reader.GetString(16),
        reader.IsDBNull(17) ? [] : (JsonSerializer.Deserialize<string[]>(reader.GetString(17)) ?? []),
        Enum.TryParse<ScheduleExecutionMode>(reader.IsDBNull(18) ? "" : reader.GetString(18), true, out var mode)
            ? mode
            : ScheduleExecutionMode.Manual,
        reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
        !reader.IsDBNull(20) && reader.GetInt32(20) != 0);

    private async Task CancelPendingExecutionsAsync(SqliteConnection connection, SqliteTransaction transaction, long materialId, string now, string accountKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE ScheduleExecutions SET Status='Cancelled', IsPaused=0, CompletedAt=$now, UpdatedAt=$now, LastError='计划已重新安排' WHERE MaterialId=$id AND {AccountScope("AccountKey")} AND Status='Pending'";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", materialId);
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertPendingExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long materialId,
        DateTimeOffset scheduledAt,
        string now,
        string accountKey,
        CancellationToken cancellationToken,
        ScheduleExecutionMode schedulingMode = ScheduleExecutionMode.Manual,
        int rateLimitAttempts = 0)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ScheduleExecutions (
                MaterialId, ScheduledAt, Status, CreatedAt, UpdatedAt,
                SchedulingMode, RateLimitAttempts, IsPaused, AccountKey)
            VALUES ($materialId, $scheduledAt, 'Pending', $now, $now, $schedulingMode, $attempts, 0, $accountKey)
            ON CONFLICT(MaterialId, ScheduledAt) DO UPDATE SET
                Status='Pending',
                UpdatedAt=$now,
                CompletedAt=NULL,
                StartedAt=NULL,
                LastError='',
                SchedulingMode=$schedulingMode,
                RateLimitAttempts=$attempts,
                IsPaused=0
            WHERE ScheduleExecutions.Status <> 'Running';
            """;
        command.Parameters.AddWithValue("$materialId", materialId);
        command.Parameters.AddWithValue("$scheduledAt", scheduledAt.ToString("O"));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$schedulingMode", schedulingMode.ToString());
        command.Parameters.AddWithValue("$attempts", Math.Max(0, rateLimitAttempts));
        AddAccountParameter(command, accountKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<PublishPolicy?> ReadClaimPolicyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string guildId,
        string channelId,
        string? accountKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT GuildId, ChannelId, Enabled, WindowStart, WindowEnd,
                   MinIntervalMinutes, MaxIntervalMinutes, DailyLimit, QueueLimit,
                   MaxConcurrency, RateLimitBaseMinutes, RateLimitMaxMinutes,
                   CreatedAt, UpdatedAt
            FROM PublishPolicies
            WHERE GuildId=$guildId AND ChannelId=$channelId AND {AccountScope("AccountKey")}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$guildId", guildId);
        command.Parameters.AddWithValue("$channelId", channelId);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PublishPolicy(
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
    }

    private async Task<PublishGuildBudget?> ReadGuildBudgetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string guildId,
        string? accountKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT GuildId, Enabled, DailyLimit, CreatedAt, UpdatedAt
            FROM PublishGuildBudgets
            WHERE GuildId=$guildId AND {AccountScope("AccountKey")}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$guildId", guildId);
        AddAccountParameter(command, accountKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PublishGuildBudget(
            reader.GetString(0),
            reader.GetInt32(1) == 1,
            reader.GetInt32(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)));
    }

    /// <summary>
    /// 统计自动排期的执行记录。<paramref name="statuses"/> 只允许内部固定集合，
    /// 避免把外部输入拼接到 SQL；<paramref name="guildOnly"/> 为 true 时按频道总预算统计。
    /// </summary>
    private async Task<int> CountAutomaticExecutionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? accountKey,
        PublishPolicy policy,
        string statuses,
        string? day,
        CancellationToken cancellationToken,
        bool guildOnly = false)
    {
        var statusFilter = statuses switch
        {
            "('Pending','Running')" => statuses,
            "('Running')" => statuses,
            "('Running','Succeeded','NeedsVerification')" => statuses,
            "('Pending','Running','Succeeded','NeedsVerification')" => statuses,
            _ => throw new ArgumentOutOfRangeException(nameof(statuses), "自动排期统计的状态集合无效。")
        };

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(1)
            FROM ScheduleExecutions e
            INNER JOIN Materials m ON m.Id=e.MaterialId
            WHERE e.SchedulingMode='Auto'
              AND e.Status IN {statusFilter}
              AND m.Status<>'delete'
              AND m.GuildId=$guildId
              {(guildOnly ? "" : "AND m.ChannelId=$channelId")}
              {(day is null ? "" : "AND substr(e.ScheduledAt,1,10)=$day")}
              AND {AccountScope("e.AccountKey")}
              AND {AccountScope("m.AccountKey")};
            """;
        command.Parameters.AddWithValue("$guildId", policy.GuildId);
        if (!guildOnly) command.Parameters.AddWithValue("$channelId", policy.ChannelId);
        if (day is not null) command.Parameters.AddWithValue("$day", day);
        AddAccountParameter(command, accountKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value is DBNull ? 0 : Convert.ToInt32(value);
    }

    private static void AddItemParameters(SqliteCommand command, CollectedItemDraft item, bool includeCollectedAt = true)
    {
        command.Parameters.AddWithValue("$url", item.Url.Trim());
        command.Parameters.AddWithValue("$host", item.SourceHost.Trim());
        command.Parameters.AddWithValue("$title", item.Title.Trim());
        command.Parameters.AddWithValue("$content", item.Content.Trim());
        if (includeCollectedAt) command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$status", item.Status);
        command.Parameters.AddWithValue("$notes", item.Notes.Trim());
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(item.Tags));
        command.Parameters.AddWithValue("$error", item.ParseError.Trim());
        command.Parameters.AddWithValue("$media", JsonSerializer.Serialize(item.Media));
    }

    private static CollectedItem ReadItem(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6), reader.GetString(7), JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? [], reader.GetString(9), reader.IsDBNull(10) ? [] : JsonSerializer.Deserialize<string[]>(reader.GetString(10)) ?? []);
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 3
        }.ToString());
        await connection.OpenAsync(token);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL";
        await pragma.ExecuteNonQueryAsync(token);
        return connection;
    }

    private sealed record ClaimCandidate(
        long Id,
        ScheduleExecutionMode Mode,
        string GuildId,
        string ChannelId,
        DateTimeOffset ScheduledAt);
}

public sealed record MaterialSourceSaveResult(long MaterialId, bool Created, MaterialRecord? ExistingMaterial);

public sealed record CollectedItemDraft(string Url, string SourceHost, string Title, string Content, string Status = "待处理", string Notes = "", IReadOnlyList<string>? TagList = null, string ParseError = "", long? Id = null, IReadOnlyList<string>? MediaList = null)
{
    public IReadOnlyList<string> Tags => TagList ?? [];
    public IReadOnlyList<string> Media => MediaList ?? [];
}
public sealed record CollectedItem(long Id, string Url, string SourceHost, string Title, string Content, DateTimeOffset CollectedAt, string Status, string Notes, IReadOnlyList<string> Tags, string ParseError, IReadOnlyList<string> Media)
{
    public string MediaSummary => Media.Count == 0 ? "" : $"媒体 {Media.Count} 个（{string.Join("、", Media.Select(Path.GetFileName))}）";
}
public sealed record MaterialDraft(long? Id, string Title, string Type, string Content, string GuildId, string GuildName, string ChannelId, string ChannelName, DateTimeOffset? PublishAt, string Status, string Link, IReadOnlyList<string> MediaLinks, string SourceType = "手工添加", string SourceUrl = "");
public sealed record MaterialRecord(long Id, string Title, string Type, string Content, string GuildId, string GuildName, string ChannelId, string ChannelName, DateTimeOffset? PublishAt, string Status, string Link, IReadOnlyList<string> MediaLinks, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string SourceType = "手工添加", string SourceUrl = "") : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string TypeLabel => Type switch { "image" => "图片", "video" => "视频", _ => "文本" };
    public bool CanEdit => Status is "waitsend" or "queue";
    public bool CanPublish => Status is "waitsend" or "queue";
    public bool CanDelete => Status is "waitsend" or "queue";
    public string TargetDisplay => string.IsNullOrWhiteSpace(GuildName) || string.IsNullOrWhiteSpace(ChannelName) ? "-" : $"{GuildName}/{ChannelName}";
    public string PublishAtDisplay => PublishAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string StatusLabel => Status switch { "queue" => "发布中", "published" => "已发布", "delete" => "已删除", _ => "待发布" };
    public string LinkDisplay => string.IsNullOrWhiteSpace(Link) ? "-" : Link;
    public string TitleContentDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
            if (!string.IsNullOrWhiteSpace(Content)) return Content.Trim();
            if (Type == "image") return $"媒体 · {MediaLinks.Count} 张";
            if (Type == "video") return $"媒体 · {MediaLinks.Count} 个";
            return "（无内容）";
        }
    }
    public string SourceDisplay => SourceType switch
    {
        "网络采集" when !string.IsNullOrWhiteSpace(SourceUrl) => $"网络采集：{SourceUrl}",
        "批量导入" when !string.IsNullOrWhiteSpace(SourceUrl) => $"批量导入：{SourceUrl}",
        "TG采集" when !string.IsNullOrWhiteSpace(SourceUrl) => $"TG采集：{SourceUrl}",
        _ => string.IsNullOrWhiteSpace(SourceType) ? "手工添加" : SourceType
    };
}

public sealed record BatchMaterialDeleteResult(
    IReadOnlyList<long> DeletedIds,
    IReadOnlyList<long> RunningIds,
    IReadOnlyList<long> PublishedIds,
    IReadOnlyList<long> MissingIds);

public sealed record BatchMaterialTargetResult(
    IReadOnlyList<long> UpdatedIds,
    IReadOnlyList<long> RunningIds,
    IReadOnlyList<long> PublishedIds,
    IReadOnlyList<long> MissingIds);

public sealed record BatchMaterialPublishAtResult(
    IReadOnlyList<long> UpdatedIds,
    IReadOnlyList<long> RunningIds,
    IReadOnlyList<long> PublishedIds,
    IReadOnlyList<long> MissingIds);

public sealed record ScheduleExecutionMutationResult(
    IReadOnlyList<long> AffectedIds,
    IReadOnlyList<long> RunningIds,
    IReadOnlyList<long> FinishedIds,
    IReadOnlyList<long> MissingIds);

/// <summary>
/// 自动排期结果。Scheduled 为 true 表示素材已经落在未来的自动发布队列里
/// （既包括本次排期成功，也包括此前已经排期、无需重复插入的情况）。
/// </summary>
public sealed record PublishAutoScheduleResult(
    bool Scheduled,
    DateTimeOffset? ScheduledAt,
    string Reason)
{
    public static PublishAutoScheduleResult Queued(DateTimeOffset scheduledAt) =>
        new(true, scheduledAt, $"已安排在 {scheduledAt:yyyy-MM-dd HH:mm} 自动发布。");

    public static PublishAutoScheduleResult AlreadyQueued(DateTimeOffset scheduledAt) =>
        new(true, scheduledAt, $"素材已在自动发布队列中（{scheduledAt:yyyy-MM-dd HH:mm}）。");

    public static PublishAutoScheduleResult Disabled() =>
        new(false, null, "该频道版块未启用或已关闭自动发布策略。");

    public static PublishAutoScheduleResult QueueFull() =>
        new(false, null, "该频道版块的自动发布队列已满，已停止继续排期。");

    public static PublishAutoScheduleResult QuotaFull() =>
        new(false, null, "该频道版块或频道的发布额度已用满，暂时无法排期。");

    public static PublishAutoScheduleResult NotScheduled(string reason) =>
        new(false, null, reason);
}

public enum ScheduleExecutionStatus { Pending, Running, Succeeded, Failed, NeedsVerification, Cancelled }
public enum ScheduleExecutionMode { Manual, Auto }

public sealed record ScheduleExecutionRecord(
    long Id, long MaterialId, DateTimeOffset ScheduledAt, ScheduleExecutionStatus Status,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string LastError, long? PublishRecordId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Title, string Type, string GuildName,
    string ChannelName, DateTimeOffset? MaterialPublishAt, string MaterialLink,
    string Content, IReadOnlyList<string> MediaLinks,
    ScheduleExecutionMode SchedulingMode = ScheduleExecutionMode.Manual,
    int RateLimitAttempts = 0,
    bool IsPaused = false)
{
    public string TypeLabel => Type switch { "image" => "图片", "video" => "视频", _ => "文本" };
    public string StatusLabel => Status switch
    {
        ScheduleExecutionStatus.Pending when IsPaused => "已暂停",
        ScheduleExecutionStatus.Pending when ScheduledAt <= DateTimeOffset.Now => "已逾期",
        ScheduleExecutionStatus.Pending => "等待发布",
        ScheduleExecutionStatus.Running => "发布中",
        ScheduleExecutionStatus.Succeeded => "发布成功",
        ScheduleExecutionStatus.Failed => "发布未完成",
        ScheduleExecutionStatus.NeedsVerification => "待核实",
        _ => "已取消"
    };
    public string TargetDisplay => string.IsNullOrWhiteSpace(GuildName) || string.IsNullOrWhiteSpace(ChannelName) ? "-" : $"{GuildName}/{ChannelName}";
    public string TitleContentDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
            if (!string.IsNullOrWhiteSpace(Content)) return Content.Trim();
            if (Type == "image") return $"媒体 · {MediaLinks.Count} 张";
            if (Type == "video") return $"媒体 · {MediaLinks.Count} 个";
            return "（无内容）";
        }
    }
    public string ScheduledAtDisplay => ScheduledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string ErrorDisplay => Status switch
    {
        ScheduleExecutionStatus.Succeeded => string.IsNullOrWhiteSpace(MaterialLink) ? "发布成功" : MaterialLink,
        ScheduleExecutionStatus.Failed => string.IsNullOrWhiteSpace(LastError) ? "发布未完成，请查看失败原因后重试" : LastError,
        ScheduleExecutionStatus.NeedsVerification => string.IsNullOrWhiteSpace(LastError) ? "请核实频道是否已发布" : LastError,
        ScheduleExecutionStatus.Pending when IsPaused => "已暂停，等待手动启动",
        ScheduleExecutionStatus.Pending => ScheduledAt <= DateTimeOffset.Now ? "已逾期，等待执行" : "等待执行",
        ScheduleExecutionStatus.Running => "执行中",
        ScheduleExecutionStatus.Cancelled => "已取消",
        _ => "-"
    };
    public string PublishRecordDisplay => PublishRecordId is { } id ? $"#{id}" : "-";
}
