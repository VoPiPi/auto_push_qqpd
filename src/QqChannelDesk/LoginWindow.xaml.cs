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

    public bool LoginSucceeded { get; private set; }

    public LoginWindow(CliWorkflow workflow)
    {
        InitializeComponent();
        _workflow = workflow;
        Loaded += async (_, _) => await BeginLoginAsync();
        Closed += (_, _) => _cancellation.Cancel();
    }

    private async Task BeginLoginAsync()
    {
        try
        {
            var challenge = await _workflow.StartLoginAsync(_cancellation.Token);
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
            LoginStatus.Text = "请使用 QQ 扫码并在手机端确认授权。";
            var result = await _workflow.PollLoginAsync(_cancellation.Token);
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
            LoginStatus.Text = "已取消授权。";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = CliDiagnostics.Sanitize(ex.Message);
            CancelButton.Content = "关闭";
        }
    }

    private void OpenLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(_verificationUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellation.Cancel();
        Close();
    }
}
