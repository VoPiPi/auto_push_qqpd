using System.IO;

namespace QqChannelDesk.Services;

/// <summary>Converts a Telegram message into a material after media is downloaded.</summary>
public sealed class TelegramMaterializer
{
    private const int MediaAttempts = 2;

    private readonly TelegramCollectorService _collector;
    private readonly ContentLibraryStore _library;
    private readonly Func<TelegramChannelMessage, string, CancellationToken, Task<TelegramDraftResult>> _draftAsync;
    private readonly ITelegramTransport? _transport;
    private readonly MediaStorageService _mediaStorage;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _cleanupStagingAsync;

    public TelegramMaterializer(
        TelegramCollectorService collector,
        ContentLibraryStore library,
        Func<TelegramChannelMessage, string, CancellationToken, Task<TelegramDraftResult>>? draftAsync = null,
        MediaStorageService? mediaStorage = null,
        Func<IReadOnlyList<string>, CancellationToken, Task>? cleanupStagingAsync = null,
        ITelegramTransport? transport = null)
    {
        _collector = collector;
        _library = library;
        _draftAsync = draftAsync ?? ((message, cookie, cancellationToken) =>
            _collector.DraftMessageAsync(message, cookie, cancellationToken: cancellationToken));
        _transport = transport;
        _mediaStorage = mediaStorage ?? new MediaStorageService();
        _cleanupStagingAsync = cleanupStagingAsync ?? ((paths, cancellationToken) =>
            _mediaStorage.DeleteOrphanStagingAsync(paths, cancellationToken: cancellationToken));
    }

    public async Task<TelegramMaterializationResult> MaterializeAsync(
        TelegramChannelMessage message,
        string cookieHeader,
        TelegramDistributionRule? targetRule = null,
        CancellationToken cancellationToken = default)
        => await MaterializeCoreAsync(
            message,
            targetRule,
            cancellationToken,
            token => _draftAsync(message, cookieHeader, token));

    /// <summary>使用消息采集时的实际传输上下文下载媒体，避免跨批次串用代理或网页代理 Cookie。</summary>
    public async Task<TelegramMaterializationResult> MaterializeAsync(
        TelegramChannelMessage message,
        TelegramTransportContext transportContext,
        TelegramDistributionRule? targetRule = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transportContext);
        var transport = _transport
            ?? throw new InvalidOperationException("当前素材器未配置 TG 传输服务。");
        return await MaterializeCoreAsync(
            message,
            targetRule,
            cancellationToken,
            token => DownloadDraftAsync(transport, message, transportContext, token));
    }

    private async Task<TelegramMaterializationResult> MaterializeCoreAsync(
        TelegramChannelMessage message,
        TelegramDistributionRule? targetRule,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<TelegramDraftResult>> draftFactory)
    {
        var existing = await _library.FindMaterialBySourceAsync("TG采集", message.Url, cancellationToken);
        if (existing is not null)
            return new(existing.Id, "", existing.MediaLinks, 0, false, "这条 TG 消息已在素材库中。", true, true);

        var draft = await draftFactory(cancellationToken);
        if (draft.MediaPaths.Count == 0 && string.IsNullOrWhiteSpace(message.Title) && string.IsNullOrWhiteSpace(message.Text))
        {
            await DiscardStagingAsync(draft, cancellationToken);
            return new(null, draft.Directory, draft.MediaPaths, draft.Failed, true, "媒体全部下载失败，未创建素材。请检查网络或代理后重试。", false, false);
        }

        var type = MaterialMediaValidator.InferType(draft.MediaPaths) ?? (draft.MediaPaths.Count == 0 ? "text" : "");
        if (type.Length == 0)
        {
            await DiscardStagingAsync(draft, cancellationToken);
            return new(null, draft.Directory, draft.MediaPaths, draft.Failed, false, "媒体类型无法识别，请更换文件或链接后重试。", false, false);
        }

        var importedMedia = new List<string>();
        IReadOnlyList<string> storedMedia;
        var recordCreated = false;
        try
        {
            storedMedia = await ImportToLibraryAsync(draft.MediaPaths, type, importedMedia, cancellationToken);
            // Telegram materials intentionally keep the title field empty. The message
            // body remains the publishing content and the field is reserved for later use.
            var saved = await _library.SaveMaterialWithSourceAsync(new MaterialDraft(
                null, "", type, message.Text,
                targetRule?.GuildId ?? "", targetRule?.GuildName ?? "",
                targetRule?.ChannelId ?? "", targetRule?.ChannelName ?? "", null, "waitsend", "",
                storedMedia, "TG采集", message.Url), cancellationToken);
            if (!saved.Created)
            {
                await DeleteImportedCopiesAsync(importedMedia);
                await DiscardStagingAsync(draft, cancellationToken);
                var savedExisting = saved.ExistingMaterial
                    ?? throw new InvalidOperationException("来源已存在，但无法读取既有素材。");
                return new(savedExisting.Id, "", savedExisting.MediaLinks, 0, false, "这条 TG 消息已在素材库中。", true, true);
            }

            recordCreated = true;
            await DiscardStagingAsync(draft, cancellationToken);
            var warning = draft.Failed > 0
                ? $"有 {draft.Failed} 个媒体下载失败；已保留成功下载的媒体或正文，本次不自动排期。"
                : "";
            return new(saved.MaterialId, draft.Directory, storedMedia, draft.Failed, false, warning, true, false);
        }
        catch
        {
            if (!recordCreated) await DeleteImportedCopiesAsync(importedMedia);
            throw;
        }
    }

    private async Task<IReadOnlyList<string>> ImportToLibraryAsync(
        IReadOnlyList<string> mediaPaths,
        string type,
        List<string> importedMedia,
        CancellationToken cancellationToken)
    {
        var storedMedia = new List<string>(mediaPaths.Count);
        foreach (var source in mediaPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MaterialMediaValidator.IsLocalPath(source))
            {
                storedMedia.Add(source);
                continue;
            }

            var stored = await _mediaStorage.ImportToLibraryAsync(source, type, cancellationToken);
            storedMedia.Add(stored);
            if (!string.Equals(
                    Path.GetFullPath(source),
                    Path.GetFullPath(stored),
                    StringComparison.OrdinalIgnoreCase))
            {
                importedMedia.Add(stored);
            }
        }
        return storedMedia;
    }

    private async Task DeleteImportedCopiesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    AppLogger.Instance.Warning($"TG 入库回滚失败，残留文件：{Path.GetFileName(path)}，{CliDiagnostics.Sanitize(ex.Message)}");
                }
            }
        });
    }

    private static async Task<TelegramDraftResult> DownloadDraftAsync(
        ITelegramTransport transport,
        TelegramChannelMessage message,
        TelegramTransportContext context,
        CancellationToken cancellationToken)
    {
        var directory = TelegramCollectorService.BuildDraftDirectory(
            AppContext.BaseDirectory,
            DateTimeOffset.Now,
            TelegramCollectorService.ReadMessageChannel(message.Url));
        var media = TelegramCollectorService.SelectDraftMedia(message.Media);
        if (media.Count > 0) Directory.CreateDirectory(directory);
        Func<TelegramMediaRef, TelegramTransportContext, string?, CancellationToken, Task<string>> downloadMedia =
            context.AllowFallback && transport is ITelegramMediaFallbackTransport fallbackTransport
                ? fallbackTransport.DownloadMediaWithFallbackAsync
                : transport.DownloadMediaAsync;

        var paths = new List<string>();
        var failed = 0;
        for (var index = 0; index < media.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 1; attempt <= MediaAttempts; attempt++)
            {
                try
                {
                    paths.Add(await downloadMedia(
                        media[index],
                        context,
                        directory,
                        cancellationToken));
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var error = CliDiagnostics.Sanitize(ex.Message);
                    if (attempt == MediaAttempts)
                    {
                        failed++;
                        AppLogger.Instance.Warning($"TG 媒体下载失败：{error}");
                    }
                    else
                    {
                        AppLogger.Instance.Warning($"TG 媒体下载第 {attempt} 次尝试失败，重试：{error}");
                    }
                }
            }
        }

        return new TelegramDraftResult(directory, paths, failed);
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
