namespace QqChannelDesk.Services;

/// <summary>Converts a Telegram message into a material after media is downloaded.</summary>
public sealed class TelegramMaterializer
{
    private readonly TelegramCollectorService _collector;
    private readonly ContentLibraryStore _library;
    private readonly Func<TelegramChannelMessage, string, CancellationToken, Task<TelegramDraftResult>> _draftAsync;
    private readonly MediaStorageService _mediaStorage;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _cleanupStagingAsync;

    public TelegramMaterializer(
        TelegramCollectorService collector,
        ContentLibraryStore library,
        Func<TelegramChannelMessage, string, CancellationToken, Task<TelegramDraftResult>>? draftAsync = null,
        MediaStorageService? mediaStorage = null,
        Func<IReadOnlyList<string>, CancellationToken, Task>? cleanupStagingAsync = null)
    {
        _collector = collector;
        _library = library;
        _draftAsync = draftAsync ?? ((message, cookie, cancellationToken) =>
            _collector.DraftMessageAsync(message, cookie, cancellationToken: cancellationToken));
        _mediaStorage = mediaStorage ?? new MediaStorageService();
        _cleanupStagingAsync = cleanupStagingAsync ?? ((paths, cancellationToken) =>
            _mediaStorage.DeleteOrphanStagingAsync(paths, cancellationToken: cancellationToken));
    }

    public async Task<TelegramMaterializationResult> MaterializeAsync(
        TelegramChannelMessage message,
        string cookieHeader,
        TelegramDistributionRule? targetRule = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _library.FindMaterialBySourceAsync("TG采集", message.Url, cancellationToken);
        if (existing is not null)
            return new(existing.Id, "", existing.MediaLinks, 0, false, "这条 TG 消息已在素材库中。", true, true);

        var draft = await _draftAsync(message, cookieHeader, cancellationToken);
        if (draft.MediaPaths.Count == 0 && string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Text))
            return new(null, draft.Directory, draft.MediaPaths, draft.Failed, true, "媒体全部下载失败，未创建素材。请检查网络或代理后重试。", false, false);

        if (draft.Failed > 0)
        {
            // 未入库就不留暂存文件，否则 temp/telegram 会堆积没有素材引用的孤儿媒体。
            await DiscardStagingAsync(draft, cancellationToken);
            return new(null, draft.Directory, draft.MediaPaths, draft.Failed, false, $"有 {draft.Failed} 个媒体下载失败，未创建素材。请检查网络或代理后重试。", false, false);
        }

        var type = MaterialMediaValidator.InferType(draft.MediaPaths) ?? (draft.MediaPaths.Count == 0 ? "text" : "");
        if (type.Length == 0)
        {
            await DiscardStagingAsync(draft, cancellationToken);
            return new(null, draft.Directory, draft.MediaPaths, draft.Failed, false, "媒体类型无法识别，请更换文件或链接后重试。", false, false);
        }

        // Telegram materials intentionally keep the title field empty. The message
        // body remains the publishing content and the field is reserved for later use.
        var id = await _library.SaveMaterialAsync(new MaterialDraft(
            null, "", type, message.Text,
            targetRule?.GuildId ?? "", targetRule?.GuildName ?? "",
            targetRule?.ChannelId ?? "", targetRule?.ChannelName ?? "", null, "waitsend", "",
            draft.MediaPaths, "TG采集", message.Url), cancellationToken);
        return new(id, draft.Directory, draft.MediaPaths, draft.Failed, false, "", true, false);
    }

    private async Task DiscardStagingAsync(TelegramDraftResult draft, CancellationToken cancellationToken)
    {
        if (draft.MediaPaths.Count == 0) return;
        try
        {
            await _cleanupStagingAsync(draft.MediaPaths, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Instance.Warning($"TG 暂存媒体清理失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }
}

public sealed record TelegramMaterializationResult(
    long? MaterialId,
    string Directory,
    IReadOnlyList<string> MediaPaths,
    int FailedMediaCount,
    bool SkippedEmpty,
    string Error,
    bool Succeeded,
    bool SkippedDuplicate = false);
