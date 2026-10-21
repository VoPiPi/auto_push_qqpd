using System.Windows;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class UpdateWindow : Window
{
    private readonly ReleaseUpdateInfo _release;

    public event EventHandler? OpenReleaseRequested;

    public ReleaseUpdateInfo Release => _release;

    public bool SkippedVersion { get; private set; }

    public UpdateWindow(string currentVersion, ReleaseUpdateInfo release)
    {
        InitializeComponent();
        _release = release;
        VersionTransitionText.Text = $"当前版本 {currentVersion}  →  最新版本 {release.Version}";
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(release.Title) ? release.Version : release.Title;
        PublishedAtText.Text = release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
        PackageText.Text = BuildPackageText(release);
        ChecksumText.Text = BuildChecksumText(release);
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(release.Notes) ? "暂无更新说明。" : release.Notes.Trim();
    }

    private static string BuildPackageText(ReleaseUpdateInfo release)
    {
        if (release.WindowsPackage is not { } package) return "未识别到 Windows 更新包";
        var size = package.SizeBytes is > 0
            ? $" · {package.SizeBytes.Value / 1_000_000d:0.0} MB"
            : " · 大小未知";
        return package.Name + size;
    }

    private static string BuildChecksumText(ReleaseUpdateInfo release) =>
        release.ChecksumStatus switch
        {
            PackageChecksumStatus.Available when !string.IsNullOrWhiteSpace(release.PackageSha256) =>
                release.PackageSha256,
            PackageChecksumStatus.ReadFailed => "校验文件读取失败",
            _ => "发布方未提供校验值"
        };

    private void LaterButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SkipVersionButton_Click(object sender, RoutedEventArgs e)
    {
        SkippedVersion = true;
        Close();
    }

    private void OpenReleaseButton_Click(object sender, RoutedEventArgs e) =>
        OpenReleaseRequested?.Invoke(this, EventArgs.Empty);
}
