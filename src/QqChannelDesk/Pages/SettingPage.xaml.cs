using QqChannelDesk.Services;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace QqChannelDesk.Pages;

public partial class SettingPage : UserControl
{
    private readonly SystemSettingsStore _settingsStore;
    private readonly MediaStorageService _mediaStorage;
    private readonly FfmpegManager _ffmpegManager;
    private AppSettings _currentSettings = new();
    private bool _loading;

    public event Action<AppSettings>? SettingsChanged;
    public event EventHandler? UpdateCheckRequested;
    public event EventHandler? OpenReleasesRequested;

    public SettingPage(
        SystemSettingsStore settingsStore,
        MediaStorageService? mediaStorage = null,
        FfmpegManager? ffmpegManager = null)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _mediaStorage = mediaStorage ?? new MediaStorageService(settingsStore);
        _ffmpegManager = ffmpegManager ?? new FfmpegManager(settingsStore: settingsStore);
        MachineCodeBox.Text = "正在生成机器码…";
        Loaded += async (_, _) =>
        {
            var machineCodeTask = Task.Run(MachineCodeProvider.GetMachineCode);
            await LoadAsync();
            MachineCodeBox.Text = await machineCodeTask;
        };
        VersionText.Text = ApplicationVersionInfo.CurrentVersion;
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            _currentSettings = await _settingsStore.GetAsync();
            NotifyUpgradeCheckBox.IsChecked = _currentSettings.NotifyUpgrade;
            RunTasksOnStartupCheckBox.IsChecked = _currentSettings.RunTasksOnStartup;
            AutoExecuteSchedulesCheckBox.IsChecked = _currentSettings.AutoExecuteSchedules;
            StartWithWindowsCheckBox.IsChecked = _currentSettings.StartWithWindows;
            ScheduleConcurrencyCombo.SelectedItem = ScheduleConcurrencyCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == _currentSettings.MaxScheduleConcurrency.ToString());
            StoragePathBox.Text = _currentSettings.EffectiveMaterialStoragePath;
            await RefreshFfmpegPathAsync();
            RetentionPolicyCombo.SelectedItem = RetentionPolicyCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _currentSettings.MaterialRetentionPolicy.ToString(), StringComparison.Ordinal));
            CloseWindowBehaviorCombo.SelectedItem = CloseWindowBehaviorCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _currentSettings.CloseWindowBehavior.ToString(), StringComparison.Ordinal));
            var directory = await MediaStorageService.ValidateDirectoryAsync(_currentSettings.EffectiveMaterialStoragePath);
            StorageStatus.Text = directory.Succeeded ? "当前目录可写。" : directory.Message;
            SettingsStatus.Text = $"设置已加载，机器码：{MachineCodeBox.Text}";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"读取设置未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试。";
        }
        finally
        {
            _loading = false;
        }
    }

    private async void SettingCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with
            {
                NotifyUpgrade = NotifyUpgradeCheckBox.IsChecked == true,
                RunTasksOnStartup = RunTasksOnStartupCheckBox.IsChecked == true,
                AutoExecuteSchedules = AutoExecuteSchedulesCheckBox.IsChecked == true,
                StartWithWindows = StartWithWindowsCheckBox.IsChecked == true
            });
            SettingsStatus.Text = "设置已保存。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
    }

    private async void CloseWindowBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CloseWindowBehaviorCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<CloseWindowBehavior>(item.Tag?.ToString(), out var behavior)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with
            {
                CloseWindowBehavior = SystemSettingsStore.NormalizeCloseWindowBehavior(behavior)
            });
            SettingsStatus.Text = "关闭窗口操作已保存。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
    }

    private async void ScheduleConcurrencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ScheduleConcurrencyCombo.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var concurrency)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with { MaxScheduleConcurrency = SystemSettingsStore.NormalizeConcurrency(concurrency) });
            SettingsStatus.Text = "计划最大并发数已保存。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
    }

    private async void RetentionPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RetentionPolicyCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<MaterialRetentionPolicy>(item.Tag?.ToString(), out var policy)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with { MaterialRetentionPolicy = policy });
            SettingsStatus.Text = "文件保留期限已保存。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
    }

    private async void ChangeStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择素材文件存储路径",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        try
        {
            SettingsStatus.Text = "正在检查所选目录…";
            var result = await MediaStorageService.ValidateDirectoryAsync(dialog.FolderName);
            if (!result.Succeeded)
            {
                StorageStatus.Text = result.Message;
                SettingsStatus.Text = "目录不可用，未保存新路径。请选择可写目录。";
                return;
            }

            await SaveSettingsAsync(_currentSettings with { MaterialStoragePath = result.FullPath });
            StoragePathBox.Text = result.FullPath;
            StorageStatus.Text = "当前目录可写。";
            SettingsStatus.Text = "存储路径已保存。已有文件不会自动迁移。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"存储路径未保存：{CliDiagnostics.Sanitize(ex.Message)}。请重试或选择其他目录。";
        }
    }

    private async void ChangeFfmpegPathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 FFmpeg 可执行文件",
            Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "ffmpeg.exe"
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        try
        {
            var path = FfmpegManager.ValidateExecutablePath(dialog.FileName);
            await SaveSettingsAsync(_currentSettings with { FfmpegPath = path });
            await RefreshFfmpegPathAsync();
            SettingsStatus.Text = "FFmpeg 路径已保存，后续频道工具操作将使用此路径。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"FFmpeg 路径未保存：{CliDiagnostics.Sanitize(ex.Message)}。请重试或重新选择文件。";
        }
    }

    private async void ResetFfmpegPathButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync(_currentSettings with { FfmpegPath = null });
            await RefreshFfmpegPathAsync();
            SettingsStatus.Text = "FFmpeg 已恢复自动选择：系统路径优先，项目 tools 版本后备。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"未恢复自动选择：{CliDiagnostics.Sanitize(ex.Message)}。请重试。";
        }
    }

    private async Task RefreshFfmpegPathAsync()
    {
        var resolution = await _ffmpegManager.ResolveAsync();
        FfmpegPathBox.Text = resolution.ExecutablePath ?? "未检测到 ffmpeg.exe";
        if (resolution.ExecutablePath is null)
        {
            FfmpegPathStatus.Text = resolution.ConfiguredPathInvalid
                ? "已保存的路径不可用，也未找到 FFmpeg。请重新选择或在线安装。"
                : "未找到 FFmpeg，请选择文件或在线安装。";
            return;
        }

        var fallbackNotice = resolution.ConfiguredPathInvalid
            ? "；已保存的路径不可用，当前已回退"
            : string.Empty;
        FfmpegPathStatus.Text = $"当前使用：{resolution.SourceLabel}{fallbackNotice}。";
    }

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        var previous = _currentSettings;
        var startupChanged = previous.StartWithWindows != settings.StartWithWindows;
        if (startupChanged)
        {
            var startupManager = new StartupManager();
            try
            {
                await Task.Run(() => startupManager.SetEnabled(settings.StartWithWindows));
            }
            catch
            {
                StartWithWindowsCheckBox.IsChecked = previous.StartWithWindows;
                throw;
            }
        }

        try
        {
            await _settingsStore.SaveAsync(settings);
        }
        catch
        {
            if (startupChanged)
            {
                try { await Task.Run(() => new StartupManager().SetEnabled(previous.StartWithWindows)); }
                catch { }
                StartWithWindowsCheckBox.IsChecked = previous.StartWithWindows;
            }
            throw;
        }

        _currentSettings = settings;
        SettingsChanged?.Invoke(settings);
    }

    private void CopyMachineCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MachineCodeBox.Text);
            SettingsStatus.Text = "机器码已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"机器码未复制：{CliDiagnostics.Sanitize(ex.Message)}。请重试。";
        }
    }

    public void SetUpdateCheckInProgress(bool inProgress)
    {
        if (inProgress) UpdateStatusText.Text = "正在查询 Gitee 最新发布…";
        CheckUpdateButton.IsEnabled = !inProgress;
    }

    public void DisplayUpdateCheckResult(UpdateCheckResult result)
    {
        UpdateStatusText.Text = result.Message;
    }

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e) => UpdateCheckRequested?.Invoke(this, EventArgs.Empty);

    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e) => OpenReleasesRequested?.Invoke(this, EventArgs.Empty);

}
