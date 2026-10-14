namespace QqChannelDesk.Services;

/// <summary>Converts a Telegram message into a material after media is downloaded.</summary>
public sealed class TelegramMaterializer
{
    private readonly TelegramCollectorService _collector;
    private readonly ContentLibraryStore _library;
    private readonly Func<TelegramChannelMessage, string, CancellationToken, Task<TelegramDraftResult>> _draftAsync;

    public TelegramMaterializer(
        TelegramCollectorService collector,
        ContentLibraryStore library,
        Func<TelegramChannelMessage, string, CancellationToken, Task<TelegramDraftResult>>? draftAsync = null)
    {
        _collector = collector;
        _library = library;
        _draftAsync = draftAsync ?? ((message, cookie, cancellationToken) =>
            _collector.DraftMessageAsync(message, cookie, cancellationToken: cancellationToken));
    }

    public async Task<TelegramMaterializationResult> MaterializeAsync(
        TelegramChannelMessage message,
        string cookieHeader,
        TelegramDistributionRule? targetRule = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _library.FindMaterialBySourceAsync("TG采集", message.Url, cancellationToken);
        if (existing is not null)
            return new(existing.Id, "", existing.MediaLinks, 0, false, "这条 TG 消息已经在素材仓库中。", true, true);

        var draft = await _draftAsync(message, cookieHeader, cancellationToken);
        if (draft.MediaPaths.Count == 0 && string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Text))
            return new(null, draft.Directory, draft.MediaPaths, draft.Failed, true, "媒体全部下载失败，未创建空白素材。", false, false);

        var type = MaterialMediaValidator.InferType(draft.MediaPaths) ?? (draft.MediaPaths.Count == 0 ? "text" : "");
        if (type.Length == 0)
            return new(null, draft.Directory, draft.MediaPaths, draft.Failed, false, "媒体类型无法识别。", false, false);

        // Telegram materials intentionally keep the title field empty. The message
        // body remains the publishing content and the field is reserved for later use.
        var id = await _library.SaveMaterialAsync(new MaterialDraft(
            null, "", type, message.Text,
            targetRule?.GuildId ?? "", targetRule?.GuildName ?? "",
            targetRule?.ChannelId ?? "", targetRule?.ChannelName ?? "", null, "waitsend", "",
            draft.MediaPaths, "TG采集", message.Url), cancellationToken);
        return new(id, draft.Directory, draft.MediaPaths, draft.Failed, false, "", true, false);
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
