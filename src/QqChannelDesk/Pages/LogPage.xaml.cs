using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

public partial class LogPage : UserControl
{
    private bool _showDebugLogs;
    private bool _showAllLogs;
    private bool _scrollToNewestPending;

    public event Action<bool>? DebugModeChanged;

    public LogPage()
    {
        InitializeComponent();
        _showDebugLogs = AppLogger.Instance.DebugEnabled;
        _showAllLogs = _showDebugLogs;
        DebugModeToggle.IsChecked = _showDebugLogs;
        ApplyLogFilterControls();
        ReloadLogs();
    }

    public void AppendLog(string entry)
    {
        if ((!_showDebugLogs || !_showAllLogs) && entry.Contains("[Debug]", StringComparison.Ordinal)) return;
        var separator = DiagnosticLog.Text.Length == 0 ? string.Empty : Environment.NewLine;
        SetDiagnosticLogText(entry + separator + DiagnosticLog.Text);
    }

    private void DebugModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || DebugModeToggle.IsChecked is not bool enabled) return;
        _showDebugLogs = enabled;
        _showAllLogs = enabled;
        ApplyLogFilterControls();
        DebugModeChanged?.Invoke(enabled);
        ReloadLogs();
    }

    // 调试模式关闭时日志只有一种视图，禁用筛选按钮，避免用户以为还能展开更多内容。
    private void ApplyLogFilterControls()
    {
        ShowAllLogsButton.IsEnabled = _showDebugLogs;
        ShowImportantLogsButton.IsEnabled = _showDebugLogs;
        LogFilterText.Text = _showDebugLogs && _showAllLogs ? "全部日志" : "重要日志";
    }

    private void AutoScrollToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && AutoScrollToggle.IsChecked == true) ScrollToNewest();
    }

    private void ShowAllLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllLogs = true;
        LogFilterText.Text = "全部日志";
        ReloadLogs();
    }

    private void ShowImportantLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllLogs = false;
        LogFilterText.Text = "重要日志";
        ReloadLogs();
    }

    private void ReloadLogs()
    {
        SetDiagnosticLogText(string.Join(Environment.NewLine,
            AppLogger.Instance.ReadEntries(_showDebugLogs && _showAllLogs).Reverse()));
    }

    private void SetDiagnosticLogText(string text)
    {
        var autoScroll = AutoScrollToggle.IsChecked == true;
        var scrollViewer = FindVisualChild<ScrollViewer>(DiagnosticLog);
        var verticalOffset = scrollViewer?.VerticalOffset ?? 0;

        DiagnosticLog.Text = text;

        if (autoScroll)
        {
            ScrollToNewest();
            return;
        }

        if (scrollViewer is null) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (AutoScrollToggle.IsChecked == true) return;
            scrollViewer.ScrollToVerticalOffset(verticalOffset);
        });
    }

    private void ScrollToNewest()
    {
        if (AutoScrollToggle.IsChecked != true) return;

        DiagnosticLog.CaretIndex = 0;
        if (_scrollToNewestPending) return;
        _scrollToNewestPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollToNewestPending = false;
            if (AutoScrollToggle.IsChecked != true) return;
            DiagnosticLog.CaretIndex = 0;
            DiagnosticLog.ScrollToHome();
        });
    }

    private void OpenLogDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppLogger.Instance.LogDirectory) { UseShellExecute = true });
            FooterStatus.Text = "已打开日志文件夹";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"未能打开日志文件夹：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }

        return null;
    }
}
