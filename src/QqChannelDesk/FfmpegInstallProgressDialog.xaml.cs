using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class FfmpegInstallProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _running = true;
    private bool _canCancel = true;

    public FfmpegInstallProgressDialog()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => ElapsedText.Text = $"已用时 {DateTimeOffset.Now - _startedAt:mm\\:ss}";
        _timer.Start();
        Closing += FfmpegInstallProgressDialog_Closing;
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void Update(FfmpegInstallProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Update(progress));
            return;
        }

        InstallProgress.Value = Math.Clamp(progress.Percent, 0, 100);
        PercentText.Text = $"{InstallProgress.Value:0}%";
        if (!string.IsNullOrWhiteSpace(progress.Message))
            StatusText.Text = progress.Message;
        if (!string.IsNullOrWhiteSpace(progress.Detail))
            DetailText.Text = progress.Detail;
        if (!string.IsNullOrWhiteSpace(progress.SourceUrl))
            SourceUrlText.Text = progress.SourceUrl;
        _canCancel = progress.CanCancel;
        CancelButton.IsEnabled = _canCancel && !_cancellation.IsCancellationRequested;
    }

    public void Complete()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Complete);
            return;
        }

        _running = false;
        _timer.Stop();
        if (IsVisible)
            Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_canCancel || _cancellation.IsCancellationRequested) return;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消安装，请稍候…";
        DetailText.Text = "正在停止网络下载或解压，不会删除已有的可用 FFmpeg。";
        _cancellation.Cancel();
    }

    private void FfmpegInstallProgressDialog_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        if (!_canCancel)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        if (_cancellation.IsCancellationRequested) return;
        var result = MessageBox.Show(this,
            "FFmpeg 仍在下载或解压。确定要取消安装吗？",
            "取消 FFmpeg 安装",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            CancelButton_Click(this, new RoutedEventArgs());
    }
}
