using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

public partial class OperationsCenterPage : UserControl
{
    private readonly DashboardService _dashboard;
    private readonly RuntimeSessionState _runtime;
    private readonly DispatcherTimer _timer;
    private bool _dashboardRefreshing;
    private PublishSchedulerStatus _schedulerStatus = PublishSchedulerStatus.Stopped;

    public event EventHandler? RefreshRequested;
    public event EventHandler? InstallNodeRequested;
    public event EventHandler? InstallCliRequested;
    public event EventHandler? InstallFfmpegRequested;
    public event EventHandler? LoginRequested;
    public event Action<FeedType>? PublishRequested;

    public OperationsCenterPage(DashboardService dashboard, RuntimeSessionState? runtime = null)
    {
        _dashboard = dashboard;
        _runtime = runtime ?? new RuntimeSessionState();
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        Loaded += (_, _) =>
        {
            _timer.Start();
            _ = RefreshDashboardAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public string LoginState => LoginBadge.Text;
    public string CliState => CliBadge.Text;
    public string NodeState => NodeBadge.Text;
    public bool Installing { get; set; }

    public void SetFooter(string value)
    {
        CenterFooter.Text = value;
        FooterStatus.Text = value;
        ManualResultText.Text = value;
    }

    public void SetRefreshEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        RefreshDashboardButton.IsEnabled = enabled;
    }

    public void SetInstallEnabled(bool enabled)
    {
        InstallNodeButton.IsEnabled = enabled;
        InstallCliButton.IsEnabled = enabled;
        InstallFfmpegButton.IsEnabled = enabled;
        DashboardInstallNodeButton.IsEnabled = enabled;
        DashboardInstallCliButton.IsEnabled = enabled;
        DashboardInstallFfmpegButton.IsEnabled = enabled;
    }

    public void UpdateReport(DiagnosticReport report)
    {
        NodeValue.Text = report.Node.Detail;
        SetState(NodeBadge, report.Node);
        SetDashboardState(DashboardNodeBadge, DashboardNodeValue, report.Node);
        DashboardInstallNodeButton.Visibility = report.Node.State == DiagnosticState.Ready ? Visibility.Collapsed : Visibility.Visible;
        DashboardInstallNodeButton.IsEnabled = !Installing;
        CliValue.Text = report.Cli.Detail;
        SetState(CliBadge, report.Cli);
        SetDashboardState(DashboardCliBadge, DashboardCliValue, report.Cli);
        InstallNodeButton.Visibility = report.Node.State != DiagnosticState.Ready ? Visibility.Visible : Visibility.Collapsed;
        InstallNodeButton.IsEnabled = !Installing;
        InstallCliButton.Visibility = report.Cli.State == DiagnosticState.Warning && report.Node.State == DiagnosticState.Ready ? Visibility.Visible : Visibility.Collapsed;
        InstallCliButton.IsEnabled = !Installing;
        DashboardInstallCliButton.Visibility = report.Cli.State == DiagnosticState.Ready || report.Node.State != DiagnosticState.Ready
            ? Visibility.Collapsed : Visibility.Visible;
        DashboardInstallCliButton.IsEnabled = !Installing;
        LoginValue.Text = report.Login.State == DiagnosticState.Ready ? "CLI 授权有效，服务连通正常" : report.Login.Detail;
        SetState(LoginBadge, report.Login);
        SetDashboardState(DashboardLoginBadge, DashboardLoginValue, report.Login);
        LoginAction.Visibility = report.Cli.State == DiagnosticState.Ready && report.Login.State == DiagnosticState.Warning && report.Login.StateLabel == "未登录"
            ? Visibility.Visible : Visibility.Collapsed;
        DashboardLoginButton.Visibility = LoginAction.Visibility;
        DashboardLoginButton.IsEnabled = !Installing;
        FfmpegValue.Text = report.Ffmpeg.Detail;
        SetState(FfmpegBadge, report.Ffmpeg);
        SetDashboardState(DashboardFfmpegBadge, DashboardFfmpegValue, report.Ffmpeg);
        InstallFfmpegButton.Visibility = report.Ffmpeg.State == DiagnosticState.Ready ? Visibility.Collapsed : Visibility.Visible;
        InstallFfmpegButton.IsEnabled = !Installing;
        DashboardInstallFfmpegButton.Visibility = InstallFfmpegButton.Visibility;
        DashboardInstallFfmpegButton.IsEnabled = !Installing;
        ManualCliText.Text = report.Cli.StateLabel;
    }

    public void SetFeedResult(FeedType type, PublishResult result)
    {
        var status = type switch { FeedType.Text => ManualCliText, FeedType.Image => ManualImageStatus, _ => ManualVideoStatus };
        status.Text = result.Succeeded ? $"已发布 · {DateTime.Now:HH:mm}" : $"验证失败 · {result.Category}";
        status.Foreground = result.Succeeded ? Brushes.SeaGreen : Brushes.Firebrick;
        ManualResultText.Text = result.Succeeded ? $"{status.Text}：{CliDiagnostics.Sanitize(result.Message)}" : $"{status.Text}：{CliDiagnostics.Sanitize(result.Message)}";
        ManualProgress.Value = result.Succeeded ? 1 : 0;
        _ = RefreshDashboardAsync();
    }

    public void SetAccountDisplay(string nickname)
    {
        AccountSummaryText.Text = string.IsNullOrWhiteSpace(nickname) || nickname is "未登录" or "未知账号"
            ? "当前账号：未登录" : $"当前账号：{nickname}";
    }

    public void UpdateSchedulerStatus(PublishSchedulerStatus status)
    {
        _schedulerStatus = status;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpdateSchedulerStatus(status));
            return;
        }
        ScheduleStatusText.Text = status.Paused ? "已暂停" : status.Message;
        ScheduleStatusText.Foreground = status.Paused ? Brushes.DarkGoldenrod : Brushes.Gray;
        RunningTaskText.Text = status.RunningTaskCount.ToString();
    }

    public async Task RefreshDashboardAsync()
    {
        if (_dashboardRefreshing || !IsLoaded) return;
        _dashboardRefreshing = true;
        try
        {
            var snapshot = await _dashboard.GetSnapshotAsync();
            RenderSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            CenterFooter.Text = $"看板读取失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally { _dashboardRefreshing = false; }
    }

    private void RenderSnapshot(DashboardSnapshot snapshot)
    {
        var tasks = snapshot.Tasks;
        TotalTaskText.Text = tasks.TotalCount.ToString();
        RemainingTaskText.Text = tasks.RemainingCount.ToString();
        if (_schedulerStatus.RunningTaskCount == 0) RunningTaskText.Text = tasks.RunningCount.ToString();
        SuccessRateText.Text = tasks.SucceededCount + tasks.FailedCount + tasks.NeedsVerificationCount == 0
            ? "暂无" : $"{tasks.SucceededCount * 100d / Math.Max(1, tasks.SucceededCount + tasks.FailedCount + tasks.NeedsVerificationCount):0}%";
        TodayMetricText.Text = tasks.TodayTotal == 0 ? "今日暂无记录" : $"今日 {tasks.TodaySucceeded} 成功 · {tasks.TodayFailed} 失败";
        AttentionText.Text = tasks.AttentionCount > 0 ? $"{tasks.AttentionCount} 条需处理" : "运行平稳";
        PendingStatusText.Text = $"待排期 {tasks.PendingCount}";
        ScheduledStatusText.Text = $"已排期 {tasks.ScheduledCount}";
        OverdueStatusText.Text = $"已逾期 {tasks.OverdueCount}";
        SucceededStatusText.Text = $"已成功 {tasks.SucceededCount}";
        FailedStatusText.Text = $"失败 {tasks.FailedCount} · 待核实 {tasks.NeedsVerificationCount}";
        TaskProgress.Maximum = Math.Max(1, tasks.TotalCount);
        TaskProgress.Value = Math.Min(tasks.TotalCount, tasks.RemainingCount);
        ScheduleStatusText.Text = _schedulerStatus.Paused ? "已暂停" : snapshot.AutoExecutionEnabled ? "自动执行已开启" : "自动执行已关闭";
        ScheduleStatusText.Foreground = _schedulerStatus.Paused ? Brushes.DarkGoldenrod : Brushes.Gray;
        DailyMetricsItems.ItemsSource = snapshot.LastSevenDays;
        ActivitiesItems.ItemsSource = snapshot.RecentActivities;
        TrendSummaryText.Text = $"成功 {snapshot.LastSevenDays.Sum(item => item.Succeeded)} · 失败 {snapshot.LastSevenDays.Sum(item => item.Failed)} · 待核实 {snapshot.LastSevenDays.Sum(item => item.NeedsVerification)}";
        DashboardRefreshText.Text = $"更新于 {snapshot.RefreshedAt:HH:mm:ss}";
        CenterFooter.Text = "看板已更新";
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        UptimeText.Text = _runtime.ElapsedDisplay;
        if (IsVisible && DateTime.Now.Second % 10 == 0) _ = RefreshDashboardAsync();
    }

    private void CenterTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == CenterTabs && CenterTabs.SelectedIndex == 0) _ = RefreshDashboardAsync();
    }

    private void RefreshDashboardButton_Click(object sender, RoutedEventArgs e) => _ = RefreshDashboardAsync();
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void InstallNodeButton_Click(object sender, RoutedEventArgs e) => InstallNodeRequested?.Invoke(this, EventArgs.Empty);
    private void InstallCliButton_Click(object sender, RoutedEventArgs e) => InstallCliRequested?.Invoke(this, EventArgs.Empty);
    private void InstallFfmpegButton_Click(object sender, RoutedEventArgs e) => InstallFfmpegRequested?.Invoke(this, EventArgs.Empty);
    private void LoginAction_Click(object sender, RoutedEventArgs e) => LoginRequested?.Invoke(this, EventArgs.Empty);
    private void TextFeedButton_Click(object sender, RoutedEventArgs e) => PublishRequested?.Invoke(FeedType.Text);
    private void ImageFeedButton_Click(object sender, RoutedEventArgs e) => PublishRequested?.Invoke(FeedType.Image);
    private void VideoFeedButton_Click(object sender, RoutedEventArgs e) => PublishRequested?.Invoke(FeedType.Video);

    private static void SetState(TextBlock target, DiagnosticItem item)
    {
        target.Text = item.StateLabel;
        target.Foreground = StateBrush(item.State);
    }

    private static void SetDashboardState(TextBlock badge, TextBlock detail, DiagnosticItem item)
    {
        badge.Text = item.StateLabel;
        badge.Foreground = StateBrush(item.State);
        detail.Text = item.Detail;
    }

    private static Brush StateBrush(DiagnosticState state) => state switch
    {
        DiagnosticState.Ready => Brushes.SeaGreen,
        DiagnosticState.Warning => Brushes.DarkGoldenrod,
        DiagnosticState.Error => Brushes.Firebrick,
        _ => Brushes.DimGray
    };
}
