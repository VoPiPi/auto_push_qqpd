using System.Text.Json;
using QqChannelDesk.Services;

namespace QqChannelDesk.Services.Collection;

public sealed record TelegramSourceRunCompleted(
    long ViewId,
    long ScheduleId,
    string ChannelName,
    string TriggerSource,
    string Status,
    bool Succeeded,
    string Message);

public sealed class TelegramSourceAdapter : IDisposable
{
    private readonly TelegramCollectionStore _store;
    private readonly TelegramCollectionScheduler? _scheduler;

    public TelegramSourceAdapter(
        TelegramCollectionStore store,
        TelegramCollectionScheduler? scheduler = null,
        int? maxConcurrency = null)
    {
        _store = store;
        _scheduler = scheduler;
        if (scheduler is not null && maxConcurrency is { } concurrency)
            scheduler.SetMaxConcurrency(concurrency);
        if (scheduler is not null)
            scheduler.RunCompleted += HandleRunCompleted;
    }

    public event Action<TelegramSourceRunCompleted>? RunCompleted;

    public static long ToViewId(long scheduleId)
    {
        if (scheduleId <= 0) throw new ArgumentOutOfRangeException(nameof(scheduleId), "TG 来源 ID 无效。");
        return -scheduleId;
    }

    public static long ToScheduleId(long viewId)
    {
        if (viewId >= 0) throw new ArgumentOutOfRangeException(nameof(viewId), "统一来源 ID 不是 TG 来源。");
        return -viewId;
    }

    public async Task<IReadOnlyList<CollectionSource>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        var schedules = await _store.GetAsync(cancellationToken);
        return schedules.Select(ToSource).ToArray();
    }

    public async Task<CollectionSource?> GetSourceAsync(long viewId, CancellationToken cancellationToken = default)
    {
        var schedule = await GetScheduleAsync(viewId, cancellationToken);
        return schedule is null ? null : ToSource(schedule);
    }

    public async Task<CollectionSource> RunNowAsync(long viewId, CancellationToken cancellationToken = default)
    {
        if (_scheduler is null) throw new InvalidOperationException("TG 采集调度器未初始化。");
        var scheduleId = ToScheduleId(viewId);
        var schedule = await _store.GetAsync(scheduleId, cancellationToken)
            ?? throw new InvalidOperationException("TG 来源不存在或不属于当前账号。");
        var result = await _scheduler.RunNowAsync(schedule.Id, cancellationToken);
        var refreshed = await _store.GetAsync(schedule.Id, cancellationToken) ?? schedule;
        return ToSource(refreshed);
    }

    public async Task SetEnabledAsync(long viewId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _store.SetEnabledAsync(ToScheduleId(viewId), enabled, cancellationToken);
    }

    public async Task<IReadOnlyList<CollectionRunRecord>> GetRunHistoryAsync(
        int limit = 200,
        long? viewId = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        long? scheduleId = viewId is null ? null : ToScheduleId(viewId.Value);
        var histories = await _store.GetRunHistoryAsync(limit, scheduleId: scheduleId, status: status, cancellationToken: cancellationToken);
        return histories.Select(ToRun).ToArray();
    }

    private async Task<TelegramCollectionSchedule?> GetScheduleAsync(
        long viewId,
        CancellationToken cancellationToken)
    {
        return await _store.GetAsync(ToScheduleId(viewId), cancellationToken);
    }

    private static CollectionSource ToSource(TelegramCollectionSchedule schedule)
    {        var healthStatus = !schedule.Enabled
            ? "disabled"
            : TelegramCollectionStore.IsAutomaticRetryStopped(schedule)
                ? "failed"
                : schedule.LastStatus.Contains("失败", StringComparison.Ordinal)
                    ? "degraded"
                    : "healthy";
        return new CollectionSource(
            ToViewId(schedule.Id),
            string.IsNullOrWhiteSpace(schedule.ChannelName) ? schedule.ChannelUrl : schedule.ChannelName,
            "telegram",
            schedule.ChannelUrl,
            schedule.Enabled,
            AuthConfirmed: true,
            AuthConfirmedAt: schedule.CreatedAt,
            ParseMode: "rule-only",
            FrequencyMinutes: Math.Max(30, schedule.IntervalHours * 60),
            CronExpression: schedule.EffectiveCronExpression,
            RuleConfigJson: JsonSerializer.Serialize(new TelegramSourceRuleConfig(
                schedule.ProxySelection,
                schedule.MessageLimit,
                schedule.Keyword,
                schedule.IncludeKeyword,
                schedule.FirstRunMode,
                schedule.LastMessageId,
                schedule.AutoScheduleEnabled,
                schedule.AutoScheduleDelayMinutes,
                schedule.AutoScheduleMaxPerRun)),
            AiFallbackEnabled: false,
            MaxItemsPerRun: TelegramCollectorService.NormalizeMessageLimit(schedule.MessageLimit),
            IncludeKeywords: schedule.IncludeKeyword,
            ExcludeKeywords: schedule.Keyword,
            LastAttemptAt: schedule.LastAttemptAt ?? schedule.LastCollectedAt,
            LastSuccessAt: schedule.LastSuccessAt,
            NextRunAt: schedule.NextRetryAt ?? ComputeNextRunAt(schedule),
            healthStatus,
            HealthMessage: schedule.LastStatus,
            LastErrorCode: healthStatus is "failed" or "degraded" ? "telegram-run-failed" : "",
            LastErrorSummary: CliDiagnostics.Sanitize(schedule.LastError),
            DeletedAt: null,
            CreatedAt: schedule.CreatedAt,
            UpdatedAt: schedule.UpdatedAt,
            ConsecutiveFailures: schedule.RetryAttempt);
    }

    // 正常运行的 TG 计划没有 NextRetryAt，按与 TG 调度器一致的 Cron 规则推算下次运行时间；
    // Cron 无效时退回按 IntervalHours 间隔估算。
    private static DateTimeOffset? ComputeNextRunAt(TelegramCollectionSchedule schedule)
    {
        if (!schedule.Enabled) return null;
        var now = DateTimeOffset.Now;
        if (TelegramCronSchedule.TryParse(schedule.EffectiveCronExpression, out var cron, out _)
            && cron.GetNextOccurrence(now.ToLocalTime().DateTime) is { } local)
        {
            return new DateTimeOffset(local, now.ToLocalTime().Offset);
        }

        return now.AddHours(Math.Max(1, schedule.IntervalHours));
    }

    private static CollectionRunRecord ToRun(TelegramCollectionRunHistory history) => new(
        -history.Id,
        ToViewId(history.ScheduleId),
        history.TriggerSource,
        history.StartedAt,
        history.CompletedAt,
        MapResult(history.Status),
        FoundCount: history.AddedCount + history.SkippedCount + history.UnmatchedCount,
        FetchedCount: history.AddedCount + history.SkippedCount + history.UnmatchedCount,
        DuplicateCount: history.SkippedCount,
        AiFallbackCount: 0,
        AiFailedCount: 0,
        NewItemCount: history.AddedCount,
        ErrorCode: history.Status.Contains("失败", StringComparison.Ordinal) ? "telegram-run-failed" : "",
        ErrorSummary: history.Error,
        CreatedAt: history.StartedAt);

    private static string MapResult(string status) => status switch
    {
        "成功" => "succeeded",
        "部分成功" => "partial",
        "已取消" => "cancelled",
        _ => "failed"
    };

    private void HandleRunCompleted(TelegramCollectionRunCompleted completed)
    {
        RunCompleted?.Invoke(new TelegramSourceRunCompleted(
            ToViewId(completed.ScheduleId),
            completed.ScheduleId,
            completed.ChannelName,
            completed.TriggerSource,
            completed.Status,
            completed.Succeeded,
            completed.Message));
    }

    public void Dispose()
    {
        if (_scheduler is not null) _scheduler.RunCompleted -= HandleRunCompleted;
    }
}

internal sealed record TelegramSourceRuleConfig(
    string ProxySelection,
    int MessageLimit,
    string ExcludeKeyword,
    string IncludeKeyword,
    string FirstRunMode,
    long LastMessageId,
    bool AutoScheduleEnabled,
    int AutoScheduleDelayMinutes,
    int AutoScheduleMaxPerRun);
