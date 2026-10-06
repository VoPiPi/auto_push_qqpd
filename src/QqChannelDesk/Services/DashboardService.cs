namespace QqChannelDesk.Services;

/// <summary>
/// Shapes existing application data for the operations center. It does not
/// create or mutate any database records.
/// </summary>
public sealed class DashboardService
{
    private readonly ContentLibraryStore _contentLibrary;
    private readonly PublishHistoryStore _history;
    private readonly SystemSettingsStore _settings;

    public DashboardService(ContentLibraryStore contentLibrary, PublishHistoryStore history, SystemSettingsStore settings)
    {
        _contentLibrary = contentLibrary;
        _history = history;
        _settings = settings;
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Date.AddDays(-6), now.Offset);
        var materialsTask = _contentLibrary.GetMaterialsAsync(cancellationToken: cancellationToken);
        var schedulesTask = _contentLibrary.GetScheduleExecutionsAsync(cancellationToken);
        var historyTask = _history.GetSinceAsync(start, cancellationToken);
        var settingsTask = _settings.GetAsync(cancellationToken);
        await Task.WhenAll(materialsTask, schedulesTask, historyTask, settingsTask);

        var materials = await materialsTask;
        var schedules = await schedulesTask;
        var history = await historyTask;
        var settings = await settingsTask;
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        var pending = materials.Count(item => item.Status == "waitsend" && item.PublishAt is null);
        var scheduled = schedules.Count(item => item.Status == ScheduleExecutionStatus.Pending && item.ScheduledAt > now);
        var overdue = schedules.Count(item => item.Status == ScheduleExecutionStatus.Pending && item.ScheduledAt <= now);
        var running = schedules.Count(item => item.Status == ScheduleExecutionStatus.Running);
        var scheduleSucceeded = schedules.Count(item => item.Status == ScheduleExecutionStatus.Succeeded);
        var scheduleFailed = schedules.Count(item => item.Status == ScheduleExecutionStatus.Failed);
        var scheduleNeedsVerification = schedules.Count(item => item.Status == ScheduleExecutionStatus.NeedsVerification);
        var succeeded = Math.Max(scheduleSucceeded, history.Count(item => item.Status == PublishRecordStatus.Succeeded));
        var failed = Math.Max(scheduleFailed, history.Count(item => item.Status == PublishRecordStatus.Failed));
        var needsVerification = Math.Max(scheduleNeedsVerification, history.Count(item => item.Status == PublishRecordStatus.NeedsVerification));
        var todayRecords = history.Where(item => GetRecordDate(item) == today).ToArray();

        var summary = new DashboardTaskSummary(
            pending, scheduled, overdue, running, succeeded, failed, needsVerification,
            todayRecords.Length,
            todayRecords.Count(item => item.Status == PublishRecordStatus.Succeeded),
            todayRecords.Count(item => item.Status == PublishRecordStatus.Failed),
            pending + scheduled + overdue + running);

        return new DashboardSnapshot(
            now,
            summary,
            BuildDailyMetrics(history, today),
            BuildRunningTasks(schedules, now),
            BuildActivities(history, schedules),
            BuildAlerts(summary, materials),
            settings.AutoExecuteSchedules);
    }

    private static IReadOnlyList<DashboardDailyMetric> BuildDailyMetrics(IReadOnlyList<PublishRecord> records, DateOnly today)
    {
        var counts = Enumerable.Range(0, 7)
            .Select(offset => today.AddDays(-6 + offset))
            .ToDictionary(date => date, _ => new int[3]);

        foreach (var record in records)
        {
            if (!counts.TryGetValue(GetRecordDate(record), out var values)) continue;
            switch (record.Status)
            {
                case PublishRecordStatus.Succeeded: values[0]++; break;
                case PublishRecordStatus.Failed: values[1]++; break;
                case PublishRecordStatus.NeedsVerification: values[2]++; break;
            }
        }

        var maximum = Math.Max(1, counts.Values.SelectMany(values => values).DefaultIfEmpty().Max());
        return counts.Select(pair => new DashboardDailyMetric(pair.Key, pair.Value[0], pair.Value[1], pair.Value[2], maximum)).ToArray();
    }

    private static IReadOnlyList<DashboardActivity> BuildActivities(
        IReadOnlyList<PublishRecord> history,
        IReadOnlyList<ScheduleExecutionRecord> schedules) =>
        history.Select(record => new DashboardActivity(
                (record.CompletedAt ?? record.StartedAt).ToLocalTime().ToString("MM-dd HH:mm"),
                $"{record.TypeName} · {record.Target} · {record.StatusName}",
                record.Status switch
                {
                    PublishRecordStatus.Succeeded => "success",
                    PublishRecordStatus.Failed => "danger",
                    PublishRecordStatus.NeedsVerification => "warning",
                    _ => "info"
                }, record.CompletedAt ?? record.StartedAt))
            .Concat(schedules.Where(item => item.Status == ScheduleExecutionStatus.Running)
                .Select(item => new DashboardActivity(
                    item.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"), $"计划执行中 · {item.Title}", "info", item.UpdatedAt)))
            .OrderByDescending(item => item.SortAt)
            .Take(8)
            .ToArray();

    private static IReadOnlyList<DashboardRunningTask> BuildRunningTasks(
        IReadOnlyList<ScheduleExecutionRecord> schedules,
        DateTimeOffset now) =>
        schedules.Where(item => item.Status == ScheduleExecutionStatus.Running)
            .OrderBy(item => item.StartedAt ?? item.UpdatedAt)
            .Take(6)
            .Select(item =>
            {
                var started = item.StartedAt ?? item.UpdatedAt;
                var elapsed = now - started;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                return new DashboardRunningTask(
                    string.IsNullOrWhiteSpace(item.Title) ? $"素材 #{item.MaterialId}" : item.Title,
                    item.TypeLabel,
                    item.TargetDisplay,
                    started.ToLocalTime().ToString("HH:mm:ss"),
                    elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss"));
            })
            .ToArray();

    private static IReadOnlyList<DashboardAlert> BuildAlerts(DashboardTaskSummary summary, IReadOnlyList<MaterialRecord> materials)
    {
        var alerts = new List<DashboardAlert>();
        if (summary.NeedsVerificationCount > 0)
            alerts.Add(new("有任务待核实", $"当前有 {summary.NeedsVerificationCount} 条任务未能确认是否已发帖，请先检查频道。", "warning"));
        if (summary.FailedCount > 0)
            alerts.Add(new("发布失败待处理", $"当前有 {summary.FailedCount} 条失败结果，建议在素材仓库检查内容和权限。", "danger"));
        if (summary.OverdueCount > 0)
            alerts.Add(new("有计划已逾期", $"当前有 {summary.OverdueCount} 条计划等待执行。", "warning"));
        if (materials.Count == 0)
            alerts.Add(new("还没有素材", "可以从内容采集或素材仓库添加第一条待发布内容。", "info"));
        if (alerts.Count == 0)
            alerts.Add(new("运行状态良好", "当前没有需要立即处理的异常。", "success"));
        return alerts;
    }

    private static DateOnly GetRecordDate(PublishRecord record) =>
        DateOnly.FromDateTime((record.CompletedAt ?? record.StartedAt).LocalDateTime);
}
