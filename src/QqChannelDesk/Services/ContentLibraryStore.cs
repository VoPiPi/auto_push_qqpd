using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services;

public sealed class ContentLibraryStore
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public ContentLibraryStore(string? databasePath = null) =>
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, "channels.db");

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
                    ParseError TEXT NOT NULL DEFAULT ''
                );
                DROP INDEX IF EXISTS UX_CollectedItems_Url;
                CREATE INDEX IF NOT EXISTS IX_CollectedItems_Url ON CollectedItems (Url) WHERE Url <> '';
                CREATE INDEX IF NOT EXISTS IX_CollectedItems_CollectedAt ON CollectedItems (CollectedAt DESC);
                CREATE TABLE IF NOT EXISTS ContentDrafts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CollectedItemId INTEGER NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Content TEXT NOT NULL DEFAULT '',
                    SourceUrl TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PublishCount INTEGER NOT NULL DEFAULT 0,
                    LastPublishedAt TEXT NULL,
                    FOREIGN KEY (CollectedItemId) REFERENCES CollectedItems(Id) ON DELETE SET NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ContentDrafts_UpdatedAt ON ContentDrafts (UpdatedAt DESC);
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
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Materials_StatusPublishAt ON Materials (Status, PublishAt);
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
                    UNIQUE (MaterialId, ScheduledAt),
                    FOREIGN KEY (MaterialId) REFERENCES Materials(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_ScheduleExecutions_StatusTime ON ScheduleExecutions (Status, ScheduledAt);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<long> SaveItemAsync(CollectedItemDraft item, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CollectedItems (Url, SourceHost, Title, Content, CollectedAt, Status, Notes, TagsJson, ParseError)
            VALUES ($url, $host, $title, $content, $at, $status, $notes, $tags, $error);
            SELECT last_insert_rowid();
            """;
        AddItemParameters(command, item);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task UpdateItemAsync(CollectedItemDraft item, CancellationToken cancellationToken = default)
    {
        if (item.Id is null) throw new ArgumentException("采集条目 ID 不能为空。", nameof(item));
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CollectedItems SET Url=$url, SourceHost=$host, Title=$title, Content=$content,
                Status=$status, Notes=$notes, TagsJson=$tags, ParseError=$error WHERE Id=$id;
            """;
        AddItemParameters(command, item, includeCollectedAt: false);
        command.Parameters.AddWithValue("$id", item.Id.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CollectedItem?> FindByUrlAsync(string normalizedUrl, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Url, SourceHost, Title, Content, CollectedAt, Status, Notes, TagsJson, ParseError FROM CollectedItems WHERE Url=$url ORDER BY Id LIMIT 1";
        command.Parameters.AddWithValue("$url", normalizedUrl);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    public async Task<IReadOnlyList<CollectedItem>> GetItemsAsync(string? keyword = null, string? status = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Url, SourceHost, Title, Content, CollectedAt, Status, Notes, TagsJson, ParseError
            FROM CollectedItems
            WHERE ($status='' OR Status=$status)
              AND ($keyword='' OR Title LIKE $pattern OR Content LIKE $pattern OR SourceHost LIKE $pattern OR TagsJson LIKE $pattern)
            ORDER BY CollectedAt DESC, Id DESC;
            """;
        var query = keyword?.Trim() ?? "";
        command.Parameters.AddWithValue("$status", status ?? "");
        command.Parameters.AddWithValue("$keyword", query);
        command.Parameters.AddWithValue("$pattern", $"%{query}%");
        var items = new List<CollectedItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadItem(reader));
        return items;
    }

    public async Task DeleteItemAsync(long id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CollectedItems WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> CreateDraftAsync(long? itemId, string title, string content, string sourceUrl, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ContentDrafts (CollectedItemId, Title, Content, SourceUrl, CreatedAt, UpdatedAt)
            VALUES ($itemId, $title, $content, $url, $now, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$itemId", (object?)itemId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$content", content.Trim());
        command.Parameters.AddWithValue("$url", sourceUrl.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        if (itemId is not null) await SetItemStatusAsync(itemId.Value, "已转草稿", cancellationToken);
        return id;
    }

    public async Task UpdateDraftAsync(ContentDraft draft, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ContentDrafts SET Title=$title, Content=$content, SourceUrl=$url, UpdatedAt=$now WHERE Id=$id";
        command.Parameters.AddWithValue("$title", draft.Title.Trim());
        command.Parameters.AddWithValue("$content", draft.Content.Trim());
        command.Parameters.AddWithValue("$url", draft.SourceUrl.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", draft.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContentDraft>> GetDraftsAsync(string? keyword = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, CollectedItemId, Title, Content, SourceUrl, CreatedAt, UpdatedAt, PublishCount, LastPublishedAt FROM ContentDrafts WHERE ($q='' OR Title LIKE $p OR Content LIKE $p OR SourceUrl LIKE $p) ORDER BY UpdatedAt DESC, Id DESC";
        var query = keyword?.Trim() ?? "";
        command.Parameters.AddWithValue("$q", query);
        command.Parameters.AddWithValue("$p", $"%{query}%");
        var drafts = new List<ContentDraft>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            drafts.Add(new ContentDraft(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6)), reader.GetInt32(7), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8))));
        return drafts;
    }

    public async Task DeleteDraftAsync(long id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ContentDrafts WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkDraftPublishedAsync(long id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ContentDrafts SET PublishCount=PublishCount+1, LastPublishedAt=$now, UpdatedAt=$now WHERE Id=$id";
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetItemStatusAsync(long id, string status, CancellationToken cancellationToken = default)
    {
        if (status is not ("待处理" or "保留" or "忽略" or "已转草稿")) throw new ArgumentOutOfRangeException(nameof(status));
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE CollectedItems SET Status=$status WHERE Id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialRecord>> GetMaterialsAsync(string? keyword = null, string? status = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, CreatedAt, UpdatedAt
            FROM Materials
            WHERE Status <> 'delete' AND ($status='' OR Status=$status)
              AND ($keyword='' OR Title LIKE $pattern OR Content LIKE $pattern OR Link LIKE $pattern OR GuildName LIKE $pattern OR ChannelName LIKE $pattern)
            ORDER BY CASE Status WHEN 'queue' THEN 0 ELSE 1 END, PublishAt, Id DESC;
            """;
        var query = keyword?.Trim() ?? "";
        command.Parameters.AddWithValue("$status", status ?? "");
        command.Parameters.AddWithValue("$keyword", query);
        command.Parameters.AddWithValue("$pattern", $"%{query}%");
        var materials = new List<MaterialRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) materials.Add(ReadMaterial(reader));
        return materials;
    }

    public async Task<IReadOnlyList<MaterialRecord>> GetScheduledMaterialsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, CreatedAt, UpdatedAt
            FROM Materials
            WHERE Status = 'queue' AND PublishAt IS NOT NULL
            ORDER BY PublishAt ASC, Id ASC;
            """;
        var materials = new List<MaterialRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) materials.Add(ReadMaterial(reader));
        return materials;
    }

    public async Task<long> SaveMaterialAsync(MaterialDraft material, CancellationToken cancellationToken = default)
    {
        ValidateMaterial(material);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var now = DateTimeOffset.Now.ToString("O");
        if (material.Id is null)
        {
            command.CommandText = """
                INSERT INTO Materials (Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, CreatedAt, UpdatedAt)
                VALUES ($title,$type,$content,$guildId,$guildName,$channelId,$channelName,$publishAt,$status,$link,$media,$now,$now);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Materials SET Title=$title, Type=$type, Content=$content, GuildId=$guildId, GuildName=$guildName,
                    ChannelId=$channelId, ChannelName=$channelName, PublishAt=$publishAt, Status=$status,
                    Link=$link, MediaJson=$media, UpdatedAt=$now WHERE Id=$id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", material.Id.Value);
        }
        AddMaterialParameters(command, material, now);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        await CancelPendingExecutionsAsync(connection, transaction, id, now, cancellationToken);
        if (material.Status == "queue" && material.PublishAt is { } scheduledAt && scheduledAt > DateTimeOffset.Now)
            await InsertPendingExecutionAsync(connection, transaction, id, scheduledAt, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task UpdateMaterialStatusAsync(long id, string status, string? link = null, CancellationToken cancellationToken = default)
    {
        if (status is not ("waitsend" or "queue" or "published" or "delete")) throw new ArgumentOutOfRangeException(nameof(status));
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Materials SET Status=$status, Link=CASE WHEN $status='published' THEN $link ELSE Link END, UpdatedAt=$now WHERE Id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$link", link ?? "");
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CancelMaterialScheduleAsync(long id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Materials
            SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now
            WHERE Id=$id AND Status='queue' AND PublishAt IS NOT NULL
              AND EXISTS (SELECT 1 FROM ScheduleExecutions WHERE MaterialId=$id AND Status='Pending');
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using var cancel = connection.CreateCommand();
        cancel.Transaction = transaction;
        cancel.CommandText = "UPDATE ScheduleExecutions SET Status='Cancelled', UpdatedAt=$now, CompletedAt=$now, LastError='用户取消计划' WHERE MaterialId=$id AND Status='Pending'";
        cancel.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        cancel.Parameters.AddWithValue("$id", id);
        await cancel.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ScheduleExecutionRecord>> GetScheduleExecutionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Id, e.MaterialId, e.ScheduledAt, e.Status, e.StartedAt, e.CompletedAt,
                   e.LastError, e.PublishRecordId, e.CreatedAt, e.UpdatedAt,
                   m.Title, m.Type, m.GuildName, m.ChannelName, m.PublishAt, m.Link
            FROM ScheduleExecutions e
            LEFT JOIN Materials m ON m.Id=e.MaterialId
            ORDER BY e.ScheduledAt ASC, e.MaterialId ASC, e.Id ASC;
            """;
        var records = new List<ScheduleExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadScheduleExecution(reader));
        return records;
    }

    public async Task<ScheduleExecutionRecord?> TryClaimDueExecutionAsync(
        DateTimeOffset now,
        bool includeOverdue = true,
        DateTimeOffset? notBefore = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT e.Id
            FROM ScheduleExecutions e
            INNER JOIN Materials m ON m.Id=e.MaterialId
            WHERE e.Status='Pending' AND e.ScheduledAt <= $now AND m.Status='queue' AND m.PublishAt IS NOT NULL
              AND ($includeOverdue=1 OR e.ScheduledAt >= $notBefore)
            ORDER BY e.ScheduledAt ASC, e.MaterialId ASC, e.Id ASC
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        select.Parameters.AddWithValue("$includeOverdue", includeOverdue ? 1 : 0);
        select.Parameters.AddWithValue("$notBefore", (object?)(notBefore?.ToString("O")) ?? DBNull.Value);
        var idValue = await select.ExecuteScalarAsync(cancellationToken);
        if (idValue is null || idValue is DBNull)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var executionId = Convert.ToInt64(idValue);
        var startedAt = DateTimeOffset.Now.ToString("O");
        await using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = "UPDATE ScheduleExecutions SET Status='Running', StartedAt=$started, UpdatedAt=$started WHERE Id=$id AND Status='Pending'";
        claim.Parameters.AddWithValue("$started", startedAt);
        claim.Parameters.AddWithValue("$id", executionId);
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
                   m.Title, m.Type, m.GuildName, m.ChannelName, m.PublishAt, m.Link
            FROM ScheduleExecutions e
            INNER JOIN Materials m ON m.Id=e.MaterialId WHERE e.Id=$id;
            """;
        fetch.Parameters.AddWithValue("$id", executionId);
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

    public async Task<ScheduleExecutionRecord?> GetActiveScheduleExecutionAsync(long materialId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Id, e.MaterialId, e.ScheduledAt, e.Status, e.StartedAt, e.CompletedAt,
                   e.LastError, e.PublishRecordId, e.CreatedAt, e.UpdatedAt,
                   m.Title, m.Type, m.GuildName, m.ChannelName, m.PublishAt, m.Link
            FROM ScheduleExecutions e
            LEFT JOIN Materials m ON m.Id=e.MaterialId
            WHERE e.MaterialId=$materialId AND e.Status IN ('Pending','Running')
            ORDER BY CASE e.Status WHEN 'Running' THEN 0 ELSE 1 END, e.ScheduledAt ASC, e.Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$materialId", materialId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadScheduleExecution(reader) : null;
    }

    public async Task<bool> CompleteScheduleExecutionAsync(long executionId, long? publishRecordId, PublishResult result, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        var status = result.Succeeded ? ScheduleExecutionStatus.Succeeded :
            result.Category == PublishErrorCategory.Timeout ? ScheduleExecutionStatus.NeedsVerification : ScheduleExecutionStatus.Failed;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            // Only the task that successfully claimed the execution may complete it.
            // This prevents a late publisher from overwriting a recovery result.
            update.CommandText = "UPDATE ScheduleExecutions SET Status=$status, CompletedAt=$now, UpdatedAt=$now, LastError=$error, PublishRecordId=$record WHERE Id=$id AND Status='Running'";
            update.Parameters.AddWithValue("$status", status.ToString());
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$error", result.Succeeded ? "" : AppLogger.Redact(result.Message));
            update.Parameters.AddWithValue("$record", (object?)publishRecordId ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", executionId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await using (var material = connection.CreateCommand())
        {
            material.Transaction = transaction;
            material.CommandText = result.Succeeded
                ? "UPDATE Materials SET Status='published', Link=$link, PublishAt=NULL, UpdatedAt=$now WHERE Id=(SELECT MaterialId FROM ScheduleExecutions WHERE Id=$id)"
                : "UPDATE Materials SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now WHERE Id=(SELECT MaterialId FROM ScheduleExecutions WHERE Id=$id) AND Status='queue'";
            material.Parameters.AddWithValue("$link", result.Url ?? result.PostId ?? "");
            material.Parameters.AddWithValue("$now", now);
            material.Parameters.AddWithValue("$id", executionId);
            await material.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkRunningExecutionsAsNeedsVerificationAsync(string reason, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.Now.ToString("O");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE ScheduleExecutions SET Status='NeedsVerification', CompletedAt=$now, UpdatedAt=$now, LastError=$error WHERE Status='Running'";
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$error", AppLogger.Redact(reason));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var material = connection.CreateCommand())
        {
            material.Transaction = transaction;
            material.CommandText = "UPDATE Materials SET Status='waitsend', PublishAt=NULL, UpdatedAt=$now WHERE Id IN (SELECT MaterialId FROM ScheduleExecutions WHERE Status='NeedsVerification' AND CompletedAt=$now) AND Status='queue'";
            material.Parameters.AddWithValue("$now", now);
            await material.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MaterialRecord?> GetMaterialAsync(long id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Title, Type, Content, GuildId, GuildName, ChannelId, ChannelName, PublishAt, Status, Link, MediaJson, CreatedAt, UpdatedAt FROM Materials WHERE Id=$id AND Status<>'delete'";
        command.Parameters.AddWithValue("$id", id);
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
        command.Parameters.AddWithValue("$now", now);
    }

    private static MaterialRecord ReadMaterial(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9),
        reader.GetString(10), JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? [], DateTimeOffset.Parse(reader.GetString(12)), DateTimeOffset.Parse(reader.GetString(13)));

    private static ScheduleExecutionRecord ReadScheduleExecution(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2)),
        Enum.Parse<ScheduleExecutionStatus>(reader.GetString(3)),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7), DateTimeOffset.Parse(reader.GetString(8)), DateTimeOffset.Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? "" : reader.GetString(10), reader.IsDBNull(11) ? "" : reader.GetString(11),
        reader.IsDBNull(12) ? "" : reader.GetString(12), reader.IsDBNull(13) ? "" : reader.GetString(13),
        reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
        reader.IsDBNull(15) ? "" : reader.GetString(15));

    private static async Task CancelPendingExecutionsAsync(SqliteConnection connection, SqliteTransaction transaction, long materialId, string now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ScheduleExecutions SET Status='Cancelled', CompletedAt=$now, UpdatedAt=$now, LastError='计划已重新安排' WHERE MaterialId=$id AND Status='Pending'";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", materialId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPendingExecutionAsync(SqliteConnection connection, SqliteTransaction transaction, long materialId, DateTimeOffset scheduledAt, string now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ScheduleExecutions (MaterialId, ScheduledAt, Status, CreatedAt, UpdatedAt)
            VALUES ($materialId, $scheduledAt, 'Pending', $now, $now);
            """;
        command.Parameters.AddWithValue("$materialId", materialId);
        command.Parameters.AddWithValue("$scheduledAt", scheduledAt.ToString("O"));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
    }

    private static CollectedItem ReadItem(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), reader.GetString(6), reader.GetString(7), JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? [], reader.GetString(9));
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(token);
        return connection;
    }
}

public sealed record CollectedItemDraft(string Url, string SourceHost, string Title, string Content, string Status = "待处理", string Notes = "", IReadOnlyList<string>? TagList = null, string ParseError = "", long? Id = null)
{
    public IReadOnlyList<string> Tags => TagList ?? [];
}
public sealed record CollectedItem(long Id, string Url, string SourceHost, string Title, string Content, DateTimeOffset CollectedAt, string Status, string Notes, IReadOnlyList<string> Tags, string ParseError);
public sealed record ContentDraft(long Id, long? CollectedItemId, string Title, string Content, string SourceUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int PublishCount, DateTimeOffset? LastPublishedAt)
{
    public string PublishStatus => PublishCount > 0 ? $"已发布 {PublishCount} 次" : "未发布";
}
public sealed record MaterialDraft(long? Id, string Title, string Type, string Content, string GuildId, string GuildName, string ChannelId, string ChannelName, DateTimeOffset? PublishAt, string Status, string Link, IReadOnlyList<string> MediaLinks);
public sealed record MaterialRecord(long Id, string Title, string Type, string Content, string GuildId, string GuildName, string ChannelId, string ChannelName, DateTimeOffset? PublishAt, string Status, string Link, IReadOnlyList<string> MediaLinks, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string TypeLabel => Type switch { "image" => "图片", "video" => "视频", _ => "文本" };
    public string TargetDisplay => string.IsNullOrWhiteSpace(GuildName) || string.IsNullOrWhiteSpace(ChannelName) ? "-" : $"{GuildName}/{ChannelName}";
    public string PublishAtDisplay => PublishAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string StatusLabel => Status switch { "queue" => "发布中", "published" => "已发布", "delete" => "已删除", _ => "待发布" };
    public string LinkDisplay => string.IsNullOrWhiteSpace(Link) ? "-" : Link;
}

public enum ScheduleExecutionStatus { Pending, Running, Succeeded, Failed, NeedsVerification, Cancelled }

public sealed record ScheduleExecutionRecord(
    long Id, long MaterialId, DateTimeOffset ScheduledAt, ScheduleExecutionStatus Status,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string LastError, long? PublishRecordId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Title, string Type, string GuildName,
    string ChannelName, DateTimeOffset? MaterialPublishAt, string MaterialLink)
{
    public string TypeLabel => Type switch { "image" => "图片", "video" => "视频", _ => "文本" };
    public string StatusLabel => Status switch
    {
        ScheduleExecutionStatus.Pending when ScheduledAt <= DateTimeOffset.Now => "已逾期",
        ScheduleExecutionStatus.Pending => "等待执行",
        ScheduleExecutionStatus.Running => "执行中",
        ScheduleExecutionStatus.Succeeded => "成功",
        ScheduleExecutionStatus.Failed => "失败",
        ScheduleExecutionStatus.NeedsVerification => "待核实",
        _ => "已取消"
    };
    public string TargetDisplay => string.IsNullOrWhiteSpace(GuildName) || string.IsNullOrWhiteSpace(ChannelName) ? "-" : $"{GuildName}/{ChannelName}";
    public string ScheduledAtDisplay => ScheduledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string ErrorDisplay => Status switch
    {
        ScheduleExecutionStatus.Succeeded => string.IsNullOrWhiteSpace(MaterialLink) ? "发布成功" : MaterialLink,
        ScheduleExecutionStatus.Failed => string.IsNullOrWhiteSpace(LastError) ? "发布失败" : LastError,
        ScheduleExecutionStatus.NeedsVerification => string.IsNullOrWhiteSpace(LastError) ? "请核实频道是否已发布" : LastError,
        ScheduleExecutionStatus.Pending => ScheduledAt <= DateTimeOffset.Now ? "已逾期，等待执行" : "等待执行",
        ScheduleExecutionStatus.Running => "执行中",
        ScheduleExecutionStatus.Cancelled => "已取消",
        _ => "-"
    };
    public string PublishRecordDisplay => PublishRecordId is { } id ? $"#{id}" : "-";
}
