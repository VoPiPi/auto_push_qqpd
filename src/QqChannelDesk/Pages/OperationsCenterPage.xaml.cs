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
    private bool _dashboardRefreshing;
    private bool _manualLoadingGuilds;
    private bool _manualLoadingChannels;
    private bool _manualPublishing;
    private readonly List<string> _manualMediaPaths = [];
    private PublishSchedulerStatus _schedulerStatus = PublishSchedulerStatus.Stopped;

    public event Func<Task>? RefreshRequested;
    public event EventHandler? InstallNodeRequested;
    public event EventHandler? InstallCliRequested;
    public event EventHandler? InstallFfmpegRequested;
    public event EventHandler? LoginRequested;

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
        Loaded += (_, _) =>
        {
            _timer.Start();
            _ = RefreshDashboardAsync();
            _ = RefreshManualChannelsAsync();
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
        if (!_manualPublishing && string.IsNullOrWhiteSpace(ManualResultText.Text))
            ManualResultText.Text = report.Cli.StateLabel == "已就绪" ? "频道 CLI 已就绪，可以发布。" : $"频道 CLI：{report.Cli.StateLabel}";
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

    private async Task RefreshManualChannelsAsync()
    {
        if (_manualLoadingGuilds) return;
        _manualLoadingGuilds = true;
        ManualGuildCombo.IsEnabled = false;
        ManualChannelCombo.IsEnabled = false;
        ManualRefreshChannelsButton.IsEnabled = false;
        ManualPublishButton.IsEnabled = false;
        ManualResultText.Text = "正在读取频道缓存…";
        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync();
            ManualGuildCombo.ItemsSource = guilds;
            ManualGuildCombo.SelectedItem = guilds.FirstOrDefault();
            if (ManualGuildCombo.SelectedItem is ChannelChoice guild)
            {
                await LoadManualChannelsAsync(guild);
                ManualResultText.Text = guilds.Count == 0
                    ? "暂无可用频道，请先完成登录并刷新频道数据。"
                    : ManualChannelCombo.Items.Count == 0 ? "当前频道没有可发布版块。" : "频道缓存已载入，可以编辑内容。";
            }
            else
            {
                ManualChannelCombo.ItemsSource = null;
                ManualResultText.Text = "暂无可用频道，请先完成登录并刷新频道数据。";
            }
        }
        catch (Exception ex)
        {
            ManualResultText.Text = $"读取频道缓存失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            _manualLoadingGuilds = false;
            ManualGuildCombo.IsEnabled = ManualGuildCombo.Items.Count > 0;
            ManualRefreshChannelsButton.IsEnabled = true;
            UpdateManualPublishEnabled();
        }
    }

    private async Task LoadManualChannelsAsync(ChannelChoice guild)
    {
        if (_manualLoadingChannels) return;
        _manualLoadingChannels = true;
        ManualChannelCombo.IsEnabled = false;
        try
        {
            var channels = await _channelSync.GetCachedChannelsAsync(guild.Id);
            if ((ManualGuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id) return;
            ManualChannelCombo.ItemsSource = channels;
            ManualChannelCombo.SelectedItem = channels.FirstOrDefault();
            ManualChannelCombo.IsEnabled = channels.Count > 0;
        }
        catch (Exception ex)
        {
            ManualChannelCombo.ItemsSource = null;
            ManualResultText.Text = $"读取版块缓存失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally { _manualLoadingChannels = false; }
    }

    private async void ManualGuildCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_manualLoadingGuilds || ManualGuildCombo.SelectedItem is not ChannelChoice guild) return;
        await LoadManualChannelsAsync(guild);
        UpdateManualPublishEnabled();
    }

    private async void ManualRefreshChannelsButton_Click(object sender, RoutedEventArgs e)
    {
        ManualRefreshChannelsButton.IsEnabled = false;
        ManualResultText.Text = "正在从 CLI 刷新频道和版块…";
        try
        {
            var sync = await _channelSync.SynchronizeAsync(new Progress<string>(message => ManualResultText.Text = message));
            if (!sync.Succeeded)
            {
                ManualResultText.Text = "刷新失败，旧缓存已保留。";
                return;
            }
            await RefreshManualChannelsAsync();
            ManualResultText.Text = $"刷新成功：{sync.GuildCount} 个频道、{sync.ChannelCount} 个版块。";
        }
        catch (Exception ex)
        {
            ManualResultText.Text = $"刷新频道数据失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally { ManualRefreshChannelsButton.IsEnabled = true; }
    }

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

    private async void ManualPublishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_manualPublishing) return;
        if (LoginState != "已登录")
        {
            ManualResultText.Text = "请先在“环境检查”中完成 CLI 官方扫码登录，再发布。";
            return;
        }

        var guild = ManualGuildCombo.SelectedItem as ChannelChoice;
        var channel = ManualChannelCombo.SelectedItem as ChannelChoice;
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
            var result = outcome.Result;
            ManualProgress.IsIndeterminate = false;
            ManualProgress.Value = result.Succeeded ? 1 : 0;
            var details = result.Message;
            if (!string.IsNullOrWhiteSpace(result.Url)) details += $" · 帖子链接：{result.Url}";
            if (!string.IsNullOrWhiteSpace(result.PostId)) details += $" · 帖子 ID：{result.PostId}";
            ManualResultText.Text = result.Succeeded
                ? $"发布成功：{CliDiagnostics.Sanitize(details)}"
                : $"发布失败（{result.Category}）：{CliDiagnostics.Sanitize(details)}";
            if (result.Succeeded)
            {
                _manualMediaPaths.Clear();
                ManualTitleBox.Clear();
                ManualContentBox.Clear();
                UpdateManualMediaText();
                UpdateManualRuleText();
                await RefreshDashboardAsync();
            }
        }
        catch (Exception ex)
        {
            ManualProgress.IsIndeterminate = false;
            ManualProgress.Value = 0;
            ManualResultText.Text = $"发布执行异常：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            _manualPublishing = false;
            ManualRefreshChannelsButton.IsEnabled = true;
            UpdateManualPublishEnabled();
        }
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

    private async void RefreshDashboardButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDashboardButton.IsEnabled = false;
        CenterFooter.Text = "正在刷新看板和运行环境…";
        try
        {
            await Task.WhenAll(RefreshDashboardAsync(), RequestDiagnosticsRefreshAsync());
        }
        catch (Exception ex)
        {
            CenterFooter.Text = $"刷新失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            RefreshDashboardButton.IsEnabled = !Installing;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try { await RequestDiagnosticsRefreshAsync(); }
        catch (Exception ex) { FooterStatus.Text = $"检查失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
    }

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
        DiagnosticState.Ready => Brushes.SeaGreen,
        DiagnosticState.Warning => Brushes.DarkGoldenrod,
        DiagnosticState.Error => Brushes.Firebrick,
        _ => Brushes.DimGray
    };
}
