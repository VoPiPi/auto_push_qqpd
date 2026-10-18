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
    private int _closed;
    private int _resourcesDisposed;
    private bool _running = true;
    private bool _canCancel = true;

    public FfmpegInstallProgressDialog()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
        Closing += FfmpegInstallProgressDialog_Closing;
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public void Update(FfmpegInstallProgress progress)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (!Dispatcher.CheckAccess())
        {
            UiAsync.Post(Dispatcher, () => Update(progress));
            return;
        }
        if (Volatile.Read(ref _closed) != 0) return;

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
            UiAsync.Post(Dispatcher, Complete);
            return;
        }

        _running = false;
        _timer.Stop();
        if (IsVisible)
            Close();
        DisposeResources();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _closed) != 0 ||
            Volatile.Read(ref _resourcesDisposed) != 0 ||
            !_canCancel ||
            _cancellation.IsCancellationRequested)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消安装，请稍候…";
        DetailText.Text = "正在停止网络下载或解压，不会删除已有的可用 FFmpeg。";
        CancelOperation();
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

    private void Timer_Tick(object? sender, EventArgs e) =>
        ElapsedText.Text = $"已用时 {DateTimeOffset.Now - _startedAt:mm\\:ss}";

    private void CancelOperation()
    {
        if (Volatile.Read(ref _resourcesDisposed) != 0) return;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _cancellation.Dispose();
    }

    protected override void OnClosed(EventArgs e)
    {
        Volatile.Write(ref _closed, 1);
        _timer.Stop();
        Closing -= FfmpegInstallProgressDialog_Closing;
        CancelOperation();
        DisposeResources();
        base.OnClosed(e);
    }
}
