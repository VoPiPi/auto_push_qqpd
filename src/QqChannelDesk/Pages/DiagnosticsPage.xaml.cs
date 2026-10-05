using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Documents;
using System.Windows.Media;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

public partial class DiagnosticsPage : UserControl
{
    public event EventHandler? RefreshRequested;
    public event EventHandler? InstallNodeRequested;
    public event EventHandler? InstallCliRequested;
    public event EventHandler? InstallFfmpegRequested;
    public event EventHandler? LoginRequested;
    public event Action<FeedType>? PublishRequested;
    public event Action<bool>? DebugModeChanged;

    public DiagnosticsPage()
    {
        InitializeComponent();
        DebugModeToggle.IsChecked = AppLogger.Instance.DebugEnabled;
        DiagnosticLog.Text = string.Join(Environment.NewLine, AppLogger.Instance.ReadEntries(AppLogger.Instance.DebugEnabled));
    }

    public string LoginState => LoginBadge.Text;
    public string CliState => CliBadge.Text;
    public string NodeState => NodeBadge.Text;
    public bool Installing { get; set; }
    public void SetFooter(string value) => FooterStatus.Text = value;

    public void SetRefreshEnabled(bool enabled) => RefreshButton.IsEnabled = enabled;
    public void SetInstallEnabled(bool enabled)
    {
        InstallNodeButton.IsEnabled = enabled;
        InstallCliButton.IsEnabled = enabled;
        InstallFfmpegButton.IsEnabled = enabled;
    }

    public void UpdateReport(DiagnosticReport report)
    {
        NodeValue.Text = report.Node.Detail;
        SetState(NodeBadge, report.Node);
        CliValue.Text = report.Cli.Detail;
        SetState(CliBadge, report.Cli);
        InstallNodeButton.Visibility = report.Node.State != DiagnosticState.Ready ? Visibility.Visible : Visibility.Collapsed;
        InstallNodeButton.IsEnabled = !Installing;
        InstallCliButton.Visibility = report.Cli.State == DiagnosticState.Warning && report.Node.State == DiagnosticState.Ready ? Visibility.Visible : Visibility.Collapsed;
        InstallCliButton.IsEnabled = !Installing;
        LoginValue.Text = report.Login.State == DiagnosticState.Ready ? "CLI 授权有效，服务连通正常" : report.Login.Detail;
        SetState(LoginBadge, report.Login);
        LoginAction.Visibility = report.Cli.State == DiagnosticState.Ready && report.Login.State == DiagnosticState.Warning && report.Login.StateLabel == "未登录"
            ? Visibility.Visible
            : Visibility.Collapsed;
        FfmpegValue.Text = report.Ffmpeg.Detail;
        SetState(FfmpegBadge, report.Ffmpeg);
        InstallFfmpegButton.Visibility = report.Ffmpeg.State == DiagnosticState.Ready ? Visibility.Collapsed : Visibility.Visible;
        InstallFfmpegButton.IsEnabled = !Installing;
    }

    public void AppendLog(string entry)
    {
        if (!AppLogger.Instance.DebugEnabled && entry.Contains("[Debug]", StringComparison.Ordinal)) return;
        DiagnosticLog.AppendText(entry + Environment.NewLine);
        DiagnosticLog.ScrollToEnd();
    }

    public void SetFeedResult(FeedType type, PublishResult result)
    {
        var status = type switch { FeedType.Text => TextFeedStatus, FeedType.Image => ImageFeedStatus, _ => VideoFeedStatus };
        status.Text = result.Succeeded ? $"已发布 · {DateTime.Now:HH:mm}" : $"验证失败 · {result.Category}";
        status.Foreground = result.Succeeded ? Brushes.SeaGreen : Brushes.Firebrick;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void InstallNodeButton_Click(object sender, RoutedEventArgs e) => InstallNodeRequested?.Invoke(this, EventArgs.Empty);
    private void InstallCliButton_Click(object sender, RoutedEventArgs e) => InstallCliRequested?.Invoke(this, EventArgs.Empty);
    private void InstallFfmpegButton_Click(object sender, RoutedEventArgs e) => InstallFfmpegRequested?.Invoke(this, EventArgs.Empty);
    private void LoginAction_Click(object sender, RoutedEventArgs e) => LoginRequested?.Invoke(this, EventArgs.Empty);
    private void TextFeedButton_Click(object sender, RoutedEventArgs e) => PublishRequested?.Invoke(FeedType.Text);
    private void ImageFeedButton_Click(object sender, RoutedEventArgs e) => PublishRequested?.Invoke(FeedType.Image);
    private void VideoFeedButton_Click(object sender, RoutedEventArgs e) => PublishRequested?.Invoke(FeedType.Video);

    private void DebugModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && DebugModeToggle.IsChecked is bool enabled)
            DebugModeChanged?.Invoke(enabled);
    }

    private static void SetState(TextBlock target, DiagnosticItem item)
    {
        target.Text = item.StateLabel;
        target.Foreground = item.State switch
        {
            DiagnosticState.Ready => Brushes.SeaGreen,
            DiagnosticState.Warning => Brushes.DarkGoldenrod,
            DiagnosticState.Error => Brushes.Firebrick,
            _ => Brushes.DimGray
        };
    }
}
