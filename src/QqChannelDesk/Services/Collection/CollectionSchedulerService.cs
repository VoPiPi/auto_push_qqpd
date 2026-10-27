using System.Collections.Concurrent;
using QqChannelDesk.Services;

namespace QqChannelDesk.Services.Collection;

public sealed record CollectionSchedulerRunStarted(long SourceId, long RunId, string Trigger, DateTimeOffset StartedAt);

public sealed record CollectionSchedulerRunCompleted(
    long SourceId,
    long RunId,
    string Result,
    int NewItemCount,
    int DuplicateCount,
    int FailedCount,
    string ErrorCode,
    string ErrorSummary);

public sealed class CollectionSchedulerService(
    CollectionSourceStore sourceStore,
    CollectionRunStore runStore,
    ContentLibraryStore contentLibrary,
    IReadOnlyDictionary<string, ISourceCollector>? collectors = null,
    Func<DateTimeOffset>? clock = null,
    AppLogger? logger = null,
    int maxConcurrency = 1,
    TimeSpan? checkInterval = null)
{
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(2);
    private readonly CollectionSourceStore _sourceStore = sourceStore;
    private readonly CollectionRunStore _runStore = runStore;
    private readonly ContentLibraryStore _contentLibrary = contentLibrary;
    private readonly IReadOnlyDictionary<string, ISourceCollector> _collectors =
        collectors ?? new Dictionary<string, ISourceCollector>();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly AppLogger _logger = logger ?? AppLogger.Instance;
    private readonly SemaphoreSlim _gate = new(Math.Clamp(maxConcurrency, 1, 2), Math.Clamp(maxConcurrency, 1, 2));
    private readonly ConcurrentDictionary<long, byte> _activeSourceIds = [];
    private readonly TimeSpan _checkInterval = checkInterval ?? DefaultCheckInterval;
    private CancellationTokenSource? _stopSource;
    private Task? _loopTask;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public IReadOnlyCollection<long> ActiveSourceIds
    {
        get { lock (_activeSourceIds) return [.. _activeSourceIds.Keys]; }
    }

    public event Action<CollectionSchedulerRunStarted>? RunStarted;
    public event Action<CollectionSchedulerRunCompleted>? RunCompleted;

    public void Start()
    {
        if (IsRunning) return;
        _stopSource = new CancellationTokenSource();
        var token = _stopSource.Token;
        _loopTask = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_stopSource is null) return;
        _stopSource.Cancel();
        if (_loopTask is { } task)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }

        _stopSource.Dispose();
        _stopSource = null;
        _loopTask = null;
    }

    public async Task RunDueSourcesAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock();
        var sources = await _sourceStore.GetSourcesAsync(cancellationToken);
        var dueSources = sources
            .Where(source => source.Enabled && IsDue(source, now))
            .ToArray();
        await Task.WhenAll(dueSources.Select(source => RunSourceAsync(source.Id, "scheduled", cancellationToken)));
    }

    public async Task RunSourceAsync(
        long sourceId,
        string trigger = "manual",
        CancellationToken cancellationToken = default)
    {
        if (!_activeSourceIds.TryAdd(sourceId, 0)) return;
        var runId = 0L;
        try
        {
            var source = await _sourceStore.GetSourceAsync(sourceId, cancellationToken);
            if (source is null) return;
            runId = await _runStore.CreateRunAsync(new CollectionRunDraft(sourceId, trigger), CancellationToken.None);
            RunStarted?.Invoke(new CollectionSchedulerRunStarted(sourceId, runId, trigger, _clock()));
            var result = await CollectAsync(source, cancellationToken);
            await CompleteAsync(source, runId, result, trigger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CompleteCancelledAsync(runId);
        }
        catch (Exception exception)
        {
            _logger.Error($"内容采集执行失败：{CliDiagnostics.Sanitize(exception.Message)}");
            if (runId > 0)
            {
                await _runStore.CompleteRunAsync(new CollectionRunUpdate(
                    runId, "failed", ErrorCode: "internal-error",
                    ErrorSummary: CliDiagnostics.Sanitize(exception.Message)), CancellationToken.None);
                RunCompleted?.Invoke(new CollectionSchedulerRunCompleted(
                    sourceId, runId, "failed", 0, 0, 0, "internal-error",
                    CliDiagnostics.Sanitize(exception.Message)));
            }
        }
        finally
        {
            _activeSourceIds.TryRemove(sourceId, out _);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunDueSourcesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error($"内容采集调度失败：{CliDiagnostics.Sanitize(exception.Message)}");
            }

            await Task.Delay(_checkInterval, cancellationToken);
        }
    }

    public async Task<CollectionResult> TrialRunAsync(
        long sourceId,
        CancellationToken cancellationToken = default)
    {
        var source = await _sourceStore.GetSourceAsync(sourceId, cancellationToken)
            ?? throw new InvalidOperationException("来源不存在或不属于当前账号。");
        return await CollectAsync(source, cancellationToken);
    }

    private async Task<CollectionResult> CollectAsync(CollectionSource source, CancellationToken cancellationToken)
    {
        if (!source.AuthConfirmed)
            return CollectionResult.Failure("authorization-required", "来源尚未确认内容使用授权。");
        if (!source.Enabled)
            return CollectionResult.Failure("source-disabled", "来源已暂停采集。");
        if (!_collectors.TryGetValue(source.SourceType, out var collector))
            return CollectionResult.Failure("collector-missing", $"暂不支持来源类型：{source.SourceType}");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await collector.CollectAsync(source, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CompleteAsync(CollectionSource source, long runId, CollectionResult result, string trigger)
    {
        var resultStatus = !result.Succeeded
            ? result.ErrorCode == "ai-degraded" ? "ai-degraded" : "failed"
            : result.FailedCount > 0 ? "partial" : "succeeded";
        var newCount = 0;
        var duplicateCount = 0;
        var aiFallbackCount = result.Items.Count(item => item.ParsedBy is "ai-fallback" or "ai-only");
        if (result.Succeeded)
        {
            foreach (var item in result.Items)
            {
                if (await SaveItemAsync(source, item))
                {
                    newCount++;
                }
                else
                {
                    duplicateCount++;
                }
            }
        }

        await _runStore.CompleteRunAsync(new CollectionRunUpdate(
            runId,
            resultStatus,
            FoundCount: result.Items.Count,
            FetchedCount: result.Items.Count,
            DuplicateCount: duplicateCount,
            AiFallbackCount: aiFallbackCount,
            NewItemCount: newCount,
            ErrorCode: result.ErrorCode,
            ErrorSummary: result.ErrorSummary,
            EndedAt: _clock()), CancellationToken.None);
        var completed = new CollectionSchedulerRunCompleted(
            source.Id, runId, resultStatus, newCount, duplicateCount,
            result.FailedCount, result.ErrorCode, result.ErrorSummary);
        RunCompleted?.Invoke(completed);
        await UpdateHealthAsync(source, resultStatus, result.ErrorCode, result.ErrorSummary);
    }

    private async Task<bool> SaveItemAsync(CollectionSource source, CollectionItemDraft item)
    {
        var canonicalUrl = UrlNormalizer.Canonicalize(item.Url);
        var fingerprint = ContentFingerprint.Compute(item.Title, item.Content);
        var duplicate = await _contentLibrary.FindCollectionDuplicateAsync(
            source.Id,
            UrlNormalizer.CanonicalItemId(canonicalUrl, item.SourceItemId),
            canonicalUrl,
            fingerprint);
        if (duplicate is not null) return false;
        if (string.IsNullOrWhiteSpace(item.Title) && string.IsNullOrWhiteSpace(item.Content)) return false;

        await _contentLibrary.SaveItemAsync(new CollectedItemDraft(
            canonicalUrl,
            TryGetHost(canonicalUrl, source.EntryUrl),
            item.Title.Trim(),
            item.Content,
            Status: "待处理",
            MediaList: item.Media,
            SourceId: source.Id,
            OriginalContent: item.OriginalContent.Length > 0 ? item.OriginalContent : item.Content,
            CandidateContent: item.CandidateContent,
            ParsedBy: item.ParsedBy,
            SourceItemId: item.SourceItemId.Trim()));
        return true;
    }

    private async Task UpdateHealthAsync(
        CollectionSource source,
        string resultStatus,
        string errorCode,
        string errorSummary)
    {
        var now = _clock();
        var consecutiveFailures = resultStatus == "failed" ? source.ConsecutiveFailures + 1 : 0;
        var healthStatus = resultStatus switch
        {
            "succeeded" => "healthy",
            "partial" or "ai-degraded" => "degraded",
            _ when consecutiveFailures >= 3 => "failed",
            _ => "degraded"
        };
        var nextRunAt = CalculateNextRunAt(source, now);
        if (healthStatus == "failed") nextRunAt = null;
        var message = resultStatus switch
        {
            "succeeded" => "采集完成。",
            "partial" => "部分详情内容解析失败。",
            "ai-degraded" => "规则解析失败，AI 兜底也未成功。",
            "failed" when consecutiveFailures >= 3 => "连续失败 3 次，已暂停自动调度。",
            _ => "采集失败，可手动重试。"
        };
        await _sourceStore.UpdateHealthAsync(new SourceHealthUpdate(
            source.Id,
            healthStatus,
            now,
            resultStatus is "succeeded" or "partial" ? now : source.LastSuccessAt,
            nextRunAt,
            message,
            resultStatus == "failed" ? errorCode : "",
            resultStatus == "failed" ? errorSummary : "",
            consecutiveFailures));
    }

    private async Task CompleteCancelledAsync(long runId)
    {
        if (runId <= 0) return;
        await _runStore.CompleteRunAsync(new CollectionRunUpdate(runId, "cancelled"), CancellationToken.None);
    }

    private DateTimeOffset? CalculateNextRunAt(CollectionSource source, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(source.CronExpression))
            return now.AddMinutes(source.FrequencyMinutes);
        return TelegramCronSchedule.TryParse(source.CronExpression, out var schedule, out _)
            ? schedule.GetNextOccurrence(now.ToLocalTime().DateTime) is { } local
                ? new DateTimeOffset(local, now.ToLocalTime().Offset)
                : null
            : now.AddMinutes(source.FrequencyMinutes);
    }

    private bool IsDue(CollectionSource source, DateTimeOffset now)
    {
        if (source.HealthStatus == "failed") return false;
        if (string.IsNullOrWhiteSpace(source.CronExpression))
            return source.NextRunAt is null || source.NextRunAt <= now;
        if (!TelegramCronSchedule.TryParse(source.CronExpression, out _, out _)) return false;
        return source.NextRunAt is null || source.NextRunAt <= now;
    }

    private static string TryGetHost(string url, string fallback)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.DnsSafeHost;
        return Uri.TryCreate(fallback, UriKind.Absolute, out var fallbackUri) ? fallbackUri.DnsSafeHost : "";
    }
}
