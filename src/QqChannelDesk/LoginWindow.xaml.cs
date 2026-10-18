using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class LoginWindow : Window
{
    private readonly CliWorkflow _workflow;
    private readonly CancellationTokenSource _cancellation = new();
    private string? _verificationUri;
    private int _closed;
    private int _cancellationDisposed;

    public bool LoginSucceeded { get; private set; }

    public LoginWindow(CliWorkflow workflow)
    {
        InitializeComponent();
        _workflow = workflow;
        Loaded += (_, _) => UiAsync.Run("登录窗口初始化", BeginLoginAsync);
        Closed += LoginWindow_Closed;
    }

    private async Task BeginLoginAsync()
    {
        try
        {
            var challenge = await _workflow.StartLoginAsync(_cancellation.Token);
            if (Volatile.Read(ref _closed) != 0) return;
            _verificationUri = challenge.VerificationUri;
            var bytes = Convert.FromBase64String(challenge.QrCodeBase64);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            QrImage.Source = image;
            OpenLinkButton.IsEnabled = Uri.TryCreate(_verificationUri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
            LoginStatus.Text = "请用 QQ 扫码，并在手机端确认。";
            var result = await _workflow.PollLoginAsync(_cancellation.Token);
            if (Volatile.Read(ref _closed) != 0) return;
            if (result.Succeeded)
            {
                LoginSucceeded = true;
                LoginStatus.Text = result.Message;
                DialogResult = true;
            }
            else if (!result.Cancelled)
            {
                LoginStatus.Text = CliDiagnostics.Sanitize(result.Message);
                CancelButton.Content = "关闭";
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消登录。");
        }
        catch (Exception ex)
        {
            SetStatus(CliDiagnostics.Sanitize(ex.Message));
            if (Volatile.Read(ref _closed) == 0) CancelButton.Content = "关闭";
        }
        finally
        {
            DisposeCancellation();
        }
    }

    private void OpenLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (!Uri.TryCreate(_verificationUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        CancelLogin();
        Close();
    }

    private void LoginWindow_Closed(object? sender, EventArgs e)
    {
        Volatile.Write(ref _closed, 1);
        CancelLogin();
    }

    private void SetStatus(string message)
    {
        if (Volatile.Read(ref _closed) != 0 ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        LoginStatus.Text = message;
    }

    private void CancelLogin()
    {
        if (Volatile.Read(ref _cancellationDisposed) != 0) return;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DisposeCancellation()
    {
        if (Interlocked.Exchange(ref _cancellationDisposed, 1) != 0) return;
        _cancellation.Dispose();
    }
}
