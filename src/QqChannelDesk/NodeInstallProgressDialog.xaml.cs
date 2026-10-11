using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class NodeInstallProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _running = true;
    private bool _canCancel = true;

    public NodeInstallProgressDialog()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => ElapsedText.Text = $"已用时 {DateTimeOffset.Now - _startedAt:mm\\:ss}";
        _timer.Start();
        Closing += NodeInstallProgressDialog_Closing;
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void Update(NodeInstallProgress progress)
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
        DetailText.Text = "如果 Windows 安装器已经启动，安装过程可能会继续完成。";
        _cancellation.Cancel();
    }

    private void NodeInstallProgressDialog_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        if (_cancellation.IsCancellationRequested)
        {
            _running = false;
            return;
        }

        e.Cancel = true;
        if (!_canCancel) return;
        var result = MessageBox.Show(this,
            "安装仍在进行。确定要取消安装并关闭窗口吗？",
            "取消 Node.js 安装",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _running = false;
            CancelButton_Click(this, new RoutedEventArgs());
        }
    }
}
