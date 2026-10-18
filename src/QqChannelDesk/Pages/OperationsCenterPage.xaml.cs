using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using Microsoft.Win32;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

public partial class OperationsCenterPage : UserControl
{
    private readonly DashboardService _dashboard;
    private readonly RuntimeSessionState _runtime;
    private readonly ChannelSyncService _channelSync;
    private readonly PublishExecutionService _publisher;
    private readonly DispatcherTimer _timer;
    private int _dashboardRefreshVersion;
    private bool _manualLoadingGuilds;
    private int _manualGuildsLoadVersion;
    private int _manualChannelLoadVersion;
    private bool _manualPublishing;
    private readonly List<string> _manualMediaPaths = [];
    private PublishSchedulerStatus _schedulerStatus = PublishSchedulerStatus.Stopped;
    private DiagnosticReport? _latestDiagnosticReport;
    private string? _latestFooter;
    private string? _latestAccountDisplay;
    private bool _refreshEnabled = true;
    private bool _installEnabled = true;
    private CancellationTokenSource? _pageLifetimeCts;
    private TelegramCollectionSchedulerStatus _telegramSchedulerStatus = TelegramCollectionSchedulerStatus.Stopped;
    private int _dashboardRunningCount;
    private bool _publishSchedulerSwitchReady;
    private bool _telegramSchedulerSwitchReady;

    public event Func<Task>? RefreshRequested;
    public event EventHandler? InstallNodeRequested;
    public event EventHandler? InstallCliRequested;
    public event EventHandler? InstallFfmpegRequested;
    public event EventHandler? LoginRequested;
    public event EventHandler<bool>? PublishSchedulerEnabledChanged;
    public event EventHandler<bool>? TelegramSchedulerEnabledChanged;
    public event EventHandler? TelegramCancelRunRequested;

    public OperationsCenterPage(
        DashboardService dashboard,
        RuntimeSessionState? runtime = null,
        ChannelSyncService? channelSync = null,
        PublishExecutionService? publisher = null)
    {
        _dashboard = dashboard;
        _runtime = runtime ?? new RuntimeSessionState();
        _channelSync = channelSync ?? throw new ArgumentNullException(nameof(channelSync));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        Loaded += (_, _) => StartPageLifetime();
        Unloaded += (_, _) => EndPageLifetime();
    }

    private CancellationToken CurrentPageToken => _pageLifetimeCts?.Token ?? new CancellationToken(canceled: true);

    private bool IsPageActiveForUpdates =>
        IsLoaded
        && _pageLifetimeCts is { IsCancellationRequested: false };

    private bool IsPageActive(CancellationToken token) =>
        IsPageActiveForUpdates
        && !token.IsCancellationRequested
        && _pageLifetimeCts is { IsCancellationRequested: false }
        && _pageLifetimeCts.Token == token;

    private void StartPageLifetime()
    {
        var previous = _pageLifetimeCts;
        var lifetime = new CancellationTokenSource();
        _pageLifetimeCts = lifetime;
        previous?.Cancel();

        _timer.Start();
        var token = lifetime.Token;
        ApplySchedulerStatus(_schedulerStatus);
        ApplyTelegramSchedulerStatus(_telegramSchedulerStatus);
        if (_latestDiagnosticReport is { } report) ApplyReport(report);
        ApplyFooter(_latestFooter);
        ApplyAccountDisplay(_latestAccountDisplay);
        ApplyRefreshEnabled(_refreshEnabled);
        ApplyInstallEnabled(_installEnabled);
        UiAsync.Observe("运行总览刷新", RefreshDashboardAsync(token));
        UiAsync.Observe("手动频道刷新", RefreshManualChannelsAsync(token));
    }

    private void EndPageLifetime()
    {
        _timer.Stop();
        _manualLoadingGuilds = false;
        _manualChannelLoadVersion++;
        var lifetime = _pageLifetimeCts;
        _pageLifetimeCts = null;
        lifetime?.Cancel();
    }

    public string LoginState => LoginBadge.Text;
    public string CliState => CliBadge.Text;
    public string NodeState => NodeBadge.Text;
    public bool Installing { get; set; }

    public void SetFooter(string value)
    {
        _latestFooter = value;
        ApplyFooter(value);
    }

    public void SetRefreshEnabled(bool enabled)
    {
        _refreshEnabled = enabled;
        ApplyRefreshEnabled(enabled);
    }

    public void SetInstallEnabled(bool enabled)
    {
        _installEnabled = enabled;
        ApplyInstallEnabled(enabled);
    }

    public void SetSchedulerSwitches(bool publishEnabled, bool telegramEnabled)
    {
        _publishSchedulerSwitchReady = false;
        _telegramSchedulerSwitchReady = false;
        PublishSchedulerSwitch.IsChecked = publishEnabled;
        TelegramSchedulerSwitch.IsChecked = telegramEnabled;
        _publishSchedulerSwitchReady = true;
        _telegramSchedulerSwitchReady = true;
    }

    public void ShowBackgroundTasks()
    {
        CenterTabs.SelectedIndex = 1;
        ApplySchedulerStatus(_schedulerStatus);
        ApplyTelegramSchedulerStatus(_telegramSchedulerStatus);
    }

    public void SetBackgroundTaskFooter(string value)
    {
        if (!Dispatcher.CheckAccess())
        {
            UiAsync.Post(Dispatcher, () => SetBackgroundTaskFooter(value));
            return;
        }
        BackgroundTaskFooterText.Text = value;
    }

    public void UpdateReport(DiagnosticReport report)
    {
        _latestDiagnosticReport = report;
        ApplyReport(report);
    }

    private void ApplyReport(DiagnosticReport report)
    {
        if (!IsPageActiveForUpdates) return;
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
        LoginValue.Text = report.Login.State == DiagnosticState.Ready ? "登录有效，服务正常" : report.Login.Detail;
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
        if (!_manualPublishing && string.IsNullOrWhiteSpace(ManualResultText.Text))
            ManualResultText.Text = report.Cli.StateLabel == "已就绪" ? "频道工具已就绪，可以发布。" : $"频道工具：{report.Cli.StateLabel}";
    }

    public void SetAccountDisplay(string nickname)
    {
        _latestAccountDisplay = nickname;
        ApplyAccountDisplay(nickname);
    }

    private void ApplyFooter(string? value)
    {
        if (!IsPageActiveForUpdates || value is null) return;
        CenterFooter.Text = value;
        FooterStatus.Text = value;
    }

    private void ApplyRefreshEnabled(bool enabled)
    {
        if (!IsPageActiveForUpdates) return;
        RefreshButton.IsEnabled = enabled;
        RefreshDashboardButton.IsEnabled = enabled;
    }

    private void ApplyInstallEnabled(bool enabled)
    {
        if (!IsPageActiveForUpdates) return;
        InstallNodeButton.IsEnabled = enabled;
        InstallCliButton.IsEnabled = enabled;
        InstallFfmpegButton.IsEnabled = enabled;
        DashboardInstallNodeButton.IsEnabled = enabled;
        DashboardInstallCliButton.IsEnabled = enabled;
        DashboardInstallFfmpegButton.IsEnabled = enabled;
    }

    private void ApplyAccountDisplay(string? nickname)
    {
        if (!IsPageActiveForUpdates || nickname is null) return;
        AccountSummaryText.Text = string.IsNullOrWhiteSpace(nickname) || nickname is "未登录" or "未知账号"
            ? "当前账号：未登录" : $"当前账号：{nickname}";
    }

    public void UpdateSchedulerStatus(PublishSchedulerStatus status)
    {
        _schedulerStatus = status;
        if (!Dispatcher.CheckAccess())
        {
            UiAsync.Post(Dispatcher, () => ApplySchedulerStatus(status));
            return;
        }
        ApplySchedulerStatus(status);
    }

    private void ApplySchedulerStatus(PublishSchedulerStatus status)
    {
        if (!IsPageActiveForUpdates || !Equals(_schedulerStatus, status)) return;
        ScheduleStatusText.Text = status.Message;
        ScheduleStatusText.Foreground = status.Faulted
            ? UiStatusBrushes.Danger
            : status.Paused ? UiStatusBrushes.Warning : UiStatusBrushes.Neutral;

        var now = DateTimeOffset.Now;
        var health = DescribeBackgroundHealth(
            status.Running,
            status.Enabled,
            status.Paused,
            status.Faulted,
            status.LastHeartbeatAt,
            now,
            TimeSpan.FromSeconds(10),
            "自动执行已关闭");
        PublishSchedulerStateText.Text = health.Label;
        PublishSchedulerStateText.Foreground = health.Brush;
        PublishSchedulerStatePill.Background = health.SoftBrush;
        PublishSchedulerHeartbeatText.Text = FormatHeartbeat(status.LastHeartbeatAt, now);
        PublishSchedulerNextCheckText.Text = FormatNextCheck(status.NextCheckAt, now);
        PublishSchedulerRunningText.Text = $"{status.RunningTaskCount} 条";
        PublishSchedulerLoopText.Text = status.Running ? "运行中" : "未运行";
        PublishSchedulerMessageText.Text = status.Message;
        PublishSchedulerWarningText.Text = status.Faulted ? status.Message : string.Empty;
        PublishSchedulerWarningText.Visibility = status.Faulted ? Visibility.Visible : Visibility.Collapsed;
        ApplyBackgroundTaskSummary();
    }

    public void UpdateTelegramSchedulerStatus(TelegramCollectionSchedulerStatus status)
    {
        _telegramSchedulerStatus = status;
        if (!Dispatcher.CheckAccess())
        {
            UiAsync.Post(Dispatcher, () => ApplyTelegramSchedulerStatus(status));
            return;
        }
        ApplyTelegramSchedulerStatus(status);
    }

    private void ApplyTelegramSchedulerStatus(TelegramCollectionSchedulerStatus status)
    {
        if (!IsPageActiveForUpdates || !Equals(_telegramSchedulerStatus, status)) return;
        var now = DateTimeOffset.Now;
        var health = DescribeBackgroundHealth(
            status.Running,
            status.Enabled,
            status.Paused,
            status.Faulted,
            status.LastHeartbeatAt,
            now,
            TimeSpan.FromSeconds(60),
            "定时采集已关闭");
        TelegramSchedulerStateText.Text = health.Label;
        TelegramSchedulerStateText.Foreground = health.Brush;
        TelegramSchedulerStatePill.Background = health.SoftBrush;
        TelegramSchedulerHeartbeatText.Text = FormatHeartbeat(status.LastHeartbeatAt, now);
        TelegramSchedulerNextCheckText.Text = FormatNextCheck(status.NextCheckAt, now);
        TelegramSchedulerRunningText.Text = $"{status.RunningScheduleCount} 个";
        TelegramSchedulerLoopText.Text = status.Running ? "运行中" : "未运行";
        TelegramSchedulerMessageText.Text = status.Message;
        TelegramSchedulerWarningText.Text = status.Faulted ? status.Message : string.Empty;
        TelegramSchedulerWarningText.Visibility = status.Faulted ? Visibility.Visible : Visibility.Collapsed;

        if (status.Total > 0 && status.CurrentStage is not null)
        {
            TelegramSchedulerProgressBar.Visibility = Visibility.Visible;
            TelegramSchedulerProgressBar.Maximum = Math.Max(1, status.Total);
            TelegramSchedulerProgressBar.Value = Math.Min(status.Total, status.Current);
        }
        else
        {
            TelegramSchedulerProgressBar.Visibility = Visibility.Collapsed;
            TelegramSchedulerProgressBar.Value = 0;
        }

        TelegramCancelRunButton.Visibility = status.RunningScheduleCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyBackgroundTaskSummary();
    }

    private void ApplyBackgroundTaskSummary()
    {
        if (!IsPageActiveForUpdates) return;
        var publishActive = Math.Max(_schedulerStatus.RunningTaskCount, _dashboardRunningCount);
        var telegramActive = _telegramSchedulerStatus.RunningScheduleCount;
        RunningTaskText.Text = (publishActive + telegramActive).ToString();
        RunningTaskCaptionText.Text = publishActive + telegramActive == 0
            ? "后台任务空闲"
            : $"发布 {publishActive} · 采集 {telegramActive}";
    }

    private static (string Label, Brush Brush, Brush SoftBrush) DescribeBackgroundHealth(
        bool running,
        bool enabled,
        bool paused,
        bool faulted,
        DateTimeOffset? lastHeartbeatAt,
        DateTimeOffset now,
        TimeSpan staleAfter,
        string disabledLabel)
    {
        if (!running) return ("已停止", UiStatusBrushes.Neutral, UiStatusBrushes.NeutralSoft);
        if (faulted) return ("异常", UiStatusBrushes.Danger, UiStatusBrushes.DangerSoft);
        if (!enabled) return (disabledLabel, UiStatusBrushes.Neutral, UiStatusBrushes.NeutralSoft);
        if (paused) return ("已暂停", UiStatusBrushes.Warning, UiStatusBrushes.WarningSoft);
        if (lastHeartbeatAt is null)
            return ("等待心跳", UiStatusBrushes.Warning, UiStatusBrushes.WarningSoft);
        var age = now - lastHeartbeatAt.Value;
        if (age > staleAfter)
            return ($"心跳延迟 {Math.Max(0, (int)age.TotalSeconds)} 秒", UiStatusBrushes.Warning, UiStatusBrushes.WarningSoft);
        return ("运行中", UiStatusBrushes.Success, UiStatusBrushes.SuccessSoft);
    }

    private static string FormatHeartbeat(DateTimeOffset? lastHeartbeatAt, DateTimeOffset now)
    {
        if (lastHeartbeatAt is null) return "暂无";
        var age = now - lastHeartbeatAt.Value;
        var seconds = Math.Max(0, (int)age.TotalSeconds);
        return $"{seconds} 秒前 · {lastHeartbeatAt.Value:HH:mm:ss}";
    }

    private static string FormatNextCheck(DateTimeOffset? nextCheckAt, DateTimeOffset now)
    {
        if (nextCheckAt is null) return "未安排";
        var remaining = nextCheckAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "即将检查";
        return $"{nextCheckAt.Value:HH:mm:ss}（约 {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} 秒后）";
    }

    private void PublishSchedulerSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_publishSchedulerSwitchReady) return;
        PublishSchedulerEnabledChanged?.Invoke(this, PublishSchedulerSwitch.IsChecked == true);
    }

    private void TelegramSchedulerSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_telegramSchedulerSwitchReady) return;
        TelegramSchedulerEnabledChanged?.Invoke(this, TelegramSchedulerSwitch.IsChecked == true);
    }

    private void TelegramCancelRunButton_Click(object sender, RoutedEventArgs e)
    {
        TelegramCancelRunRequested?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshDashboardAsync() => RefreshDashboardAsync(CurrentPageToken);

    private async Task RefreshDashboardAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_dashboardRefreshVersion;
        try
        {
            var snapshot = await _dashboard.GetSnapshotAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _dashboardRefreshVersion) return;
            RenderSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken) || version != _dashboardRefreshVersion) return;
            CenterFooter.Text = $"看板读取失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private void RenderSnapshot(DashboardSnapshot snapshot)
    {
        var tasks = snapshot.Tasks;
        _dashboardRunningCount = tasks.RunningCount;
        TotalTaskText.Text = tasks.TotalCount.ToString();
        RemainingTaskText.Text = tasks.RemainingCount.ToString();
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
        if (!_publishSchedulerSwitchReady)
            PublishSchedulerSwitch.IsChecked = snapshot.AutoExecutionEnabled;
        DailyMetricsItems.ItemsSource = snapshot.LastSevenDays;
        ActivitiesItems.ItemsSource = snapshot.RecentActivities;
        TrendSummaryText.Text = $"成功 {snapshot.LastSevenDays.Sum(item => item.Succeeded)} · 失败 {snapshot.LastSevenDays.Sum(item => item.Failed)} · 待核实 {snapshot.LastSevenDays.Sum(item => item.NeedsVerification)}";
        DashboardRefreshText.Text = $"更新于 {snapshot.RefreshedAt:HH:mm:ss}";
        CenterFooter.Text = "看板已更新";
        ApplyBackgroundTaskSummary();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!IsPageActiveForUpdates) return;
        UptimeText.Text = _runtime.ElapsedDisplay;
        if (CenterTabs.SelectedIndex == 1)
        {
            ApplySchedulerStatus(_schedulerStatus);
            ApplyTelegramSchedulerStatus(_telegramSchedulerStatus);
        }
        if (IsVisible && DateTime.Now.Second % 10 == 0)
            UiAsync.Observe("运行总览定时刷新", RefreshDashboardAsync());
    }

    private void CenterTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == CenterTabs && CenterTabs.SelectedIndex == 0)
            UiAsync.Observe("运行总览切换刷新", RefreshDashboardAsync());
    }

    private Task RefreshManualChannelsAsync() => RefreshManualChannelsAsync(CurrentPageToken);

    private async Task RefreshManualChannelsAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_manualGuildsLoadVersion;
        _manualLoadingGuilds = true;
        InvalidateManualChannelSelection();
        ManualGuildCombo.IsEnabled = false;
        ManualChannelCombo.IsEnabled = false;
        ManualRefreshChannelsButton.IsEnabled = false;
        ManualPublishButton.IsEnabled = false;
        ManualResultText.Text = "正在读取频道数据…";
        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _manualGuildsLoadVersion) return;
            ManualGuildCombo.ItemsSource = guilds;
            ManualGuildCombo.SelectedItem = guilds.FirstOrDefault();
            if (ManualGuildCombo.SelectedItem is ChannelChoice guild)
            {
                await LoadManualChannelsAsync(guild, cancellationToken);
                if (!IsPageActive(cancellationToken) || version != _manualGuildsLoadVersion) return;
                ManualResultText.Text = guilds.Count == 0
                    ? "暂无频道，请先登录并刷新频道。"
                    : ManualChannelCombo.Items.Count == 0 ? "当前频道没有可发布版块。" : "频道已加载，可以编辑内容。";
            }
            else
            {
                ManualChannelCombo.ItemsSource = null;
                ManualResultText.Text = "暂无频道，请先登录并刷新频道。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken) || version != _manualGuildsLoadVersion) return;
            ManualResultText.Text = $"频道数据读取失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            if (version == _manualGuildsLoadVersion) _manualLoadingGuilds = false;
            if (IsPageActive(cancellationToken) && version == _manualGuildsLoadVersion)
            {
                ManualGuildCombo.IsEnabled = ManualGuildCombo.Items.Count > 0;
                ManualRefreshChannelsButton.IsEnabled = true;
                UpdateManualPublishEnabled();
            }
        }
    }

    private Task LoadManualChannelsAsync(ChannelChoice guild) =>
        LoadManualChannelsAsync(guild, CurrentPageToken);

    private async Task LoadManualChannelsAsync(ChannelChoice guild, CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_manualChannelLoadVersion;
        ManualChannelCombo.ItemsSource = null;
        ManualChannelCombo.SelectedItem = null;
        ManualChannelCombo.IsEnabled = false;
        try
        {
            var channels = await _channelSync.GetCachedChannelsAsync(guild.Id, cancellationToken);
            if (!IsPageActive(cancellationToken)
                || version != _manualChannelLoadVersion
                || (ManualGuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id)
                return;
            ManualChannelCombo.ItemsSource = channels;
            ManualChannelCombo.SelectedItem = channels.FirstOrDefault();
            ManualChannelCombo.IsEnabled = channels.Count > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken)
                || version != _manualChannelLoadVersion
                || (ManualGuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id)
                return;
            ManualChannelCombo.ItemsSource = null;
            ManualResultText.Text = $"版块读取失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            if (IsPageActive(cancellationToken) && version == _manualChannelLoadVersion)
                UpdateManualPublishEnabled();
        }
    }

    private void InvalidateManualChannelSelection()
    {
        _manualChannelLoadVersion++;
        ManualChannelCombo.ItemsSource = null;
        ManualChannelCombo.SelectedItem = null;
        ManualChannelCombo.IsEnabled = false;
        UpdateManualPublishEnabled();
    }

    private void ManualGuildCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("手动发布频道切换", async () =>
        {
        if (_manualLoadingGuilds || ManualGuildCombo.SelectedItem is not ChannelChoice guild) return;
        await LoadManualChannelsAsync(guild);
        UpdateManualPublishEnabled();
        });

    private void ManualRefreshChannelsButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("手动发布刷新频道", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        ManualRefreshChannelsButton.IsEnabled = false;
        ManualResultText.Text = "正在刷新频道和版块…";
        try
        {
            var sync = await _channelSync.SynchronizeAsync(
                new Progress<string>(message =>
                {
                    if (IsPageActive(pageToken)) ManualResultText.Text = message;
                }),
                pageToken);
            if (!IsPageActive(pageToken)) return;
            if (!sync.Succeeded)
            {
                ManualResultText.Text = "刷新未完成，继续使用原频道数据。";
                return;
            }
            await RefreshManualChannelsAsync(pageToken);
            if (!IsPageActive(pageToken)) return;
            ManualResultText.Text = $"已刷新：{sync.GuildCount} 个频道、{sync.ChannelCount} 个版块。";
        }
        catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                ManualResultText.Text = $"频道刷新失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            if (IsPageActive(pageToken)) ManualRefreshChannelsButton.IsEnabled = true;
        }
        });

    private void ManualMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = GetManualMode();
        if (mode == FeedType.Text)
        {
            _manualMediaPaths.Clear();
        }
        else if (_manualMediaPaths.Count > 0 && _manualMediaPaths.Any(path => !IsMediaForMode(path, mode)))
        {
            _manualMediaPaths.Clear();
        }
        ManualSelectMediaButton.IsEnabled = mode != FeedType.Text;
        UpdateManualRuleText();
        UpdateManualMediaText();
    }

    private void ManualSelectMediaButton_Click(object sender, RoutedEventArgs e)
    {
        var mode = GetManualMode();
        if (mode == FeedType.Text) return;
        var dialog = new OpenFileDialog
        {
            Multiselect = mode == FeedType.Image,
            Filter = mode == FeedType.Image
                ? "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|所有文件|*.*"
                : "视频文件|*.mp4;*.mov;*.m4v;*.webm;*.avi;*.mkv|所有文件|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _manualMediaPaths.Clear();
        _manualMediaPaths.AddRange(dialog.FileNames);
        UpdateManualMediaText();
        UpdateManualRuleText();
    }

    private void ManualClearMediaButton_Click(object sender, RoutedEventArgs e)
    {
        _manualMediaPaths.Clear();
        UpdateManualMediaText();
        UpdateManualRuleText();
    }

    private void ManualContentChanged(object sender, TextChangedEventArgs e)
    {
        UpdateManualRuleText();
    }

    private void ManualPublishButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("手动发布", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (_manualPublishing) return;
        if (LoginState != "已登录")
        {
            ManualResultText.Text = "请先在“环境检查”中扫码登录，再发布。";
            return;
        }

        var guild = ManualGuildCombo.SelectedItem as ChannelChoice;
        var channel = ManualChannelCombo.SelectedItem as ChannelChoice;
        if (guild is null
            || channel is null
            || !await IsManualChannelSelectionValidAsync(guild, channel, pageToken))
        {
            if (!IsPageActive(pageToken)) return;
            ManualResultText.Text = "频道或版块已变化，请重新选择后再发布。";
            ManualProgress.Value = 0;
            return;
        }
        if (!IsPageActive(pageToken)) return;
        var request = BuildManualRequest(guild, channel);
        var validation = CliWorkflow.ValidatePublishRequestSources(request, allowUnknownSources: false);
        if (validation.Length > 0)
        {
            ManualResultText.Text = validation;
            ManualProgress.Value = 0;
            return;
        }

        _manualPublishing = true;
        ManualPublishButton.IsEnabled = false;
        ManualRefreshChannelsButton.IsEnabled = false;
        ManualProgress.IsIndeterminate = true;
        ManualResultText.Text = "正在发布，请稍候…";
        try
        {
            var outcome = await _publisher.ExecuteAsync(request, guild!.Name, channel!.Name);
            if (!IsPageActive(pageToken)) return;
            var result = outcome.Result;
            ManualProgress.IsIndeterminate = false;
            ManualProgress.Value = result.Succeeded ? 1 : 0;
            var details = result.Message;
            if (!string.IsNullOrWhiteSpace(result.Url)) details += $" · 帖子链接：{result.Url}";
            if (!string.IsNullOrWhiteSpace(result.PostId)) details += $" · 帖子 ID：{result.PostId}";
            ManualResultText.Text = result.Succeeded
                ? $"发布成功：{CliDiagnostics.Sanitize(details)}"
                : $"发布未完成（{result.Category}）：{CliDiagnostics.Sanitize(details)}";
            if (result.Succeeded)
            {
                _manualMediaPaths.Clear();
                ManualTitleBox.Clear();
                ManualContentBox.Clear();
                UpdateManualMediaText();
                UpdateManualRuleText();
                await RefreshDashboardAsync(pageToken);
            }
        }
        catch (Exception ex)
        {
            if (!IsPageActive(pageToken)) return;
            ManualProgress.IsIndeterminate = false;
            ManualProgress.Value = 0;
            ManualResultText.Text = $"发布结果待确认，请先核实频道。\n{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            _manualPublishing = false;
            if (IsPageActive(pageToken))
            {
                ManualRefreshChannelsButton.IsEnabled = true;
                UpdateManualPublishEnabled();
            }
        }
        });

    private async Task<bool> IsManualChannelSelectionValidAsync(
        ChannelChoice guild,
        ChannelChoice channel,
        CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return false;
        if ((ManualGuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id
            || (ManualChannelCombo.SelectedItem as ChannelChoice)?.Id != channel.Id)
            return false;

        var channels = await _channelSync.GetCachedChannelsAsync(guild.Id, cancellationToken);
        return IsPageActive(cancellationToken)
            && (ManualGuildCombo.SelectedItem as ChannelChoice)?.Id == guild.Id
            && (ManualChannelCombo.SelectedItem as ChannelChoice)?.Id == channel.Id
            && channels.Any(item => string.Equals(item.Id, channel.Id, StringComparison.Ordinal));
    }

    private PublishRequest BuildManualRequest(ChannelChoice? guild, ChannelChoice? channel)
    {
        var mediaType = _manualMediaPaths.Count == 0 ? FeedType.Text : GetManualMode();
        return new PublishRequest(
            guild?.Id ?? "",
            channel?.Id ?? "",
            ManualContentBox.Text,
            ManualTitleBox.Text,
            mediaType,
            _manualMediaPaths.ToArray());
    }

    private FeedType GetManualMode() => ManualVideoMode.IsChecked == true
        ? FeedType.Video
        : ManualImageMode.IsChecked == true ? FeedType.Image : FeedType.Text;

    private void UpdateManualRuleText()
    {
        var hasTitle = !string.IsNullOrWhiteSpace(ManualTitleBox.Text);
        var mode = GetManualMode();
        var mediaCount = _manualMediaPaths.Count;
        var effectiveType = mediaCount == 0 ? FeedType.Text : mode;
        var contentLimit = hasTitle ? 10000 : 1000;
        var rule = effectiveType switch
        {
            FeedType.Image => $"当前为{(hasTitle ? "图片长贴" : "图片短贴")}：图片最多 {(hasTitle ? 50 : 18)} 张，正文最多 {contentLimit} 字。",
            FeedType.Video => $"当前为{(hasTitle ? "视频长贴" : "视频短贴")}：视频只能上传 1 个，正文最多 {contentLimit} 字。",
            _ => $"当前为文本{(hasTitle ? "长贴" : "短贴")}：标题和正文至少填写一项，正文最多 {contentLimit} 字。"
        };
        ManualRuleText.Text = rule;
        ManualCountText.Text = $"正文 {ManualContentBox.Text.Length}/{contentLimit} 字 · 媒体 {mediaCount} 个{(mediaCount == 0 ? "（无媒体时正文必填）" : "")}";
        UpdateManualPublishEnabled();
    }

    private void UpdateManualMediaText()
    {
        ManualMediaFilesText.Text = _manualMediaPaths.Count == 0
            ? "未选择媒体"
            : string.Join("、", _manualMediaPaths.Select(Path.GetFileName));
        ManualMediaFilesText.ToolTip = string.Join(Environment.NewLine, _manualMediaPaths);
        ManualClearMediaButton.IsEnabled = _manualMediaPaths.Count > 0;
    }

    private void UpdateManualPublishEnabled()
    {
        ManualPublishButton.IsEnabled = !_manualPublishing
            && ManualGuildCombo.SelectedItem is ChannelChoice
            && ManualChannelCombo.SelectedItem is ChannelChoice;
    }

    private static bool IsMediaForMode(string path, FeedType mode) => mode switch
    {
        FeedType.Image => MaterialMediaValidator.IsValidSource(path, "image", allowUnknownExtension: true),
        FeedType.Video => MaterialMediaValidator.IsValidSource(path, "video", allowUnknownExtension: true),
        _ => false
    };

    private void RefreshDashboardButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("刷新运行总览", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        RefreshDashboardButton.IsEnabled = false;
        CenterFooter.Text = "正在刷新看板和运行环境…";
        try
        {
            await Task.WhenAll(RefreshDashboardAsync(pageToken), RequestDiagnosticsRefreshAsync());
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                CenterFooter.Text = $"刷新失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            if (IsPageActive(pageToken))
                RefreshDashboardButton.IsEnabled = !Installing;
        }
        });

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("检查运行环境", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        try { await RequestDiagnosticsRefreshAsync(); }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                FooterStatus.Text = $"检查失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        });

    private async Task RequestDiagnosticsRefreshAsync()
    {
        var handlers = RefreshRequested;
        if (handlers is null) return;
        foreach (Func<Task> handler in handlers.GetInvocationList())
            await handler();
    }
    private void InstallNodeButton_Click(object sender, RoutedEventArgs e) => InstallNodeRequested?.Invoke(this, EventArgs.Empty);
    private void InstallCliButton_Click(object sender, RoutedEventArgs e) => InstallCliRequested?.Invoke(this, EventArgs.Empty);
    private void InstallFfmpegButton_Click(object sender, RoutedEventArgs e) => InstallFfmpegRequested?.Invoke(this, EventArgs.Empty);
    private void LoginAction_Click(object sender, RoutedEventArgs e) => LoginRequested?.Invoke(this, EventArgs.Empty);
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
        DiagnosticState.Ready => UiStatusBrushes.Success,
        DiagnosticState.Warning => UiStatusBrushes.Warning,
        DiagnosticState.Error => UiStatusBrushes.Danger,
        _ => UiStatusBrushes.Neutral
    };
}
