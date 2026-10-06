using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

public partial class LogPage : UserControl
{
    private bool _showDebugLogs;
    private bool _showAllLogs;

    public event Action<bool>? DebugModeChanged;

    public LogPage()
    {
        InitializeComponent();
        _showDebugLogs = AppLogger.Instance.DebugEnabled;
        DebugModeToggle.IsChecked = _showDebugLogs;
        ReloadLogs();
    }

    public void AppendLog(string entry)
    {
        if (!_showAllLogs && !_showDebugLogs && entry.Contains("[Debug]", StringComparison.Ordinal)) return;
        DiagnosticLog.AppendText(entry + Environment.NewLine);
        if (AutoScrollToggle.IsChecked == true) DiagnosticLog.ScrollToEnd();
    }

    private void DebugModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || DebugModeToggle.IsChecked is not bool enabled) return;
        _showDebugLogs = enabled;
        DebugModeChanged?.Invoke(enabled);
        ReloadLogs();
    }

    private void AutoScrollToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && AutoScrollToggle.IsChecked == true) DiagnosticLog.ScrollToEnd();
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
        DiagnosticLog.Text = string.Join(Environment.NewLine,
            AppLogger.Instance.ReadEntries(_showAllLogs || _showDebugLogs));
        if (AutoScrollToggle.IsChecked == true) DiagnosticLog.ScrollToEnd();
    }

    private void OpenLogDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppLogger.Instance.LogDirectory) { UseShellExecute = true });
            FooterStatus.Text = "已打开日志目录";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"无法打开日志目录：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }
}
