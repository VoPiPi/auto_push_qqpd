using System.IO;

namespace QqChannelDesk.Services;

/// <summary>
/// Contains the non-visual part of a publish operation so manual and scheduled
/// publishing use the same media, history, CLI and cleanup behavior.
/// </summary>
public interface IPublishExecutor
{
    Task<PublishExecutionOutcome> ExecuteAsync(
        PublishRequest request,
        string guildName,
        string channelName,
        CancellationToken cancellationToken = default);
}

public sealed class PublishExecutionService : IPublishExecutor
{
    private readonly CliWorkflow _workflow;
    private readonly PublishHistoryStore _historyStore;
    private readonly MediaStorageService _mediaStorage;
    private readonly AppLogger _logger;

    public PublishExecutionService(
        CliWorkflow workflow,
        PublishHistoryStore historyStore,
        MediaStorageService mediaStorage,
        AppLogger? logger = null)
    {
        _workflow = workflow;
        _historyStore = historyStore;
        _mediaStorage = mediaStorage;
        _logger = logger ?? AppLogger.Instance;
    }

    public async Task<PublishExecutionOutcome> ExecuteAsync(
        PublishRequest sourceRequest,
        string guildName,
        string channelName,
        CancellationToken cancellationToken = default)
    {
        var sourceValidation = CliWorkflow.ValidatePublishRequestSources(sourceRequest, allowUnknownSources: true);
        var recordId = await _historyStore.CreateAsync(new PublishRecordDraft(
            sourceRequest.Type,
            sourceRequest.GuildId,
            guildName,
            sourceRequest.ChannelId,
            channelName,
            sourceRequest.Title.Trim(),
            sourceRequest.MediaPaths.Select(GetMediaName).ToArray()), cancellationToken);

        if (sourceValidation.Length > 0)
        {
            var invalid = new PublishResult(false, PublishErrorCategory.Validation, sourceValidation, null, null);
            await _historyStore.CompleteAsync(recordId, invalid, cancellationToken);
            return new PublishExecutionOutcome(invalid, recordId);
        }

        var jobPaths = new List<string>();
        try
        {
            var publishPaths = new List<string>(sourceRequest.MediaPaths.Count);
            foreach (var source in sourceRequest.MediaPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MaterialMediaValidator.IsLocalPath(source) && await _mediaStorage.IsManagedLibraryPathAsync(source, cancellationToken))
                {
                    if (!File.Exists(source))
                        throw new FileNotFoundException($"素材仓库原件已不存在：{Path.GetFileName(source)}。", source);
                    publishPaths.Add(source);
                }
                else if (MaterialMediaValidator.IsLocalPath(source))
                {
                    var copied = await _mediaStorage.CreateJobCopyAsync(
                        source,
                        sourceRequest.Type == FeedType.Image ? "image" : "video",
                        cancellationToken,
                        allowUnknownSources: true);
                    jobPaths.Add(copied);
                    publishPaths.Add(copied);
                }
                else
                {
                    var downloaded = await _mediaStorage.DownloadToJobAsync(
                        source,
                        sourceRequest.Type == FeedType.Image ? "image" : "video",
                        new PublicArticleFetcher(),
                        cancellationToken);
                    jobPaths.Add(downloaded);
                    publishPaths.Add(downloaded);
                }
            }

            var request = sourceRequest with { Files = publishPaths };
            var validation = CliWorkflow.ValidatePublishRequest(request, allowUnknownSources: true);
            var result = validation.Length > 0
                ? new PublishResult(false, PublishErrorCategory.Validation, validation, null, null)
                : await _workflow.PublishAsync(request, cancellationToken);

            await _historyStore.CompleteAsync(recordId, result, cancellationToken);
            if (result.Succeeded)
                await _mediaStorage.CompleteSuccessfulJobAsync(jobPaths, cancellationToken);

            return new PublishExecutionOutcome(result, recordId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var timeout = new PublishResult(false, PublishErrorCategory.Timeout,
                "发布操作被中止，未自动重试。请先检查频道是否已出现帖子，再决定后续操作。", null, null);
            await CompleteSafelyAsync(recordId, timeout);
            return new PublishExecutionOutcome(timeout, recordId);
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            var failure = new PublishResult(false, PublishErrorCategory.Other, $"发布准备失败：{message}", null, null);
            await CompleteSafelyAsync(recordId, failure);
            _logger.Error($"发布执行异常：{message}");
            return new PublishExecutionOutcome(failure, recordId);
        }
    }

    private async Task CompleteSafelyAsync(long recordId, PublishResult result)
    {
        try
        {
            await _historyStore.CompleteAsync(recordId, result);
        }
        catch (Exception ex)
        {
            _logger.Error($"更新发布记录失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private static string GetMediaName(string source)
    {
        if (MaterialMediaValidator.IsLocalPath(source))
            return Path.GetFileName(source) ?? "本地媒体";
        try
        {
            return Uri.TryCreate(source, UriKind.Absolute, out var uri)
                ? Path.GetFileName(uri.AbsolutePath) is { Length: > 0 } name ? name : uri.Host
                : source;
        }
        catch (UriFormatException) { return "远程媒体"; }
    }
}

public sealed record PublishExecutionOutcome(PublishResult Result, long PublishRecordId);
