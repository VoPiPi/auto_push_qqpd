namespace QqChannelDesk.Services;

public sealed record DashboardSnapshot(
    DateTimeOffset RefreshedAt,
    DashboardTaskSummary Tasks,
    IReadOnlyList<DashboardDailyMetric> LastSevenDays,
    IReadOnlyList<DashboardRunningTask> RunningTasks,
    IReadOnlyList<DashboardActivity> RecentActivities,
    IReadOnlyList<DashboardAlert> Alerts,
    bool AutoExecutionEnabled);

public sealed record DashboardTaskSummary(
    int PendingCount,
    int ScheduledCount,
    int OverdueCount,
    int RunningCount,
    int SucceededCount,
    int FailedCount,
    int NeedsVerificationCount,
    int TodayTotal,
    int TodaySucceeded,
    int TodayFailed,
    int RemainingCount)
{
    public int TotalCount => PendingCount + ScheduledCount + OverdueCount + RunningCount + SucceededCount + FailedCount + NeedsVerificationCount;
    public int AttentionCount => FailedCount + NeedsVerificationCount;
    public double TodaySuccessRate => TodayTotal == 0 ? 0 : TodaySucceeded * 100d / TodayTotal;
}

public sealed record DashboardRunningTask(
    string Title,
    string TypeLabel,
    string Target,
    string StartedDisplay,
    string ElapsedDisplay);

public sealed record DashboardDailyMetric(
    DateOnly Date,
    int Succeeded,
    int Failed,
    int NeedsVerification,
    int ChartMax)
{
    public string Label => Date.ToString("MM-dd");
    public int Total => Succeeded + Failed + NeedsVerification;
    public double SuccessHeight => HeightFor(Succeeded);
    public double FailureHeight => HeightFor(Failed);
    public double VerificationHeight => HeightFor(NeedsVerification);

    private double HeightFor(int value) => value == 0 ? 0 : Math.Max(8, value * 86d / Math.Max(1, ChartMax));
}

public sealed record DashboardActivity(string TimeDisplay, string Message, string Tone, DateTimeOffset SortAt);

public sealed record DashboardAlert(string Title, string Detail, string Tone);
