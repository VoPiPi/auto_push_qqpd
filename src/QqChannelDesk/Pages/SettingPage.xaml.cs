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
    private AppSettings _currentSettings = new();
    private bool _loading;

    public event Action<AppSettings>? SettingsChanged;
    public event EventHandler? UpdateCheckRequested;
    public event EventHandler? OpenReleasesRequested;

    public SettingPage(SystemSettingsStore settingsStore, MediaStorageService? mediaStorage = null)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _mediaStorage = mediaStorage ?? new MediaStorageService(settingsStore);
        Loaded += async (_, _) => await LoadAsync();
        MachineCodeBox.Text = MachineCodeProvider.GetMachineCode();
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
            SettingsStatus.Text = $"读取设置失败：{CliDiagnostics.Sanitize(ex.Message)}";
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
            SettingsStatus.Text = $"保存设置失败：{CliDiagnostics.Sanitize(ex.Message)}";
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
            SettingsStatus.Text = $"保存设置失败：{CliDiagnostics.Sanitize(ex.Message)}";
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
            SettingsStatus.Text = $"保存设置失败：{CliDiagnostics.Sanitize(ex.Message)}";
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
            SettingsStatus.Text = $"保存设置失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private async void ChangeStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择素材文件储存路径",
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
                SettingsStatus.Text = "目录不可用，未保存新的储存路径。";
                return;
            }

            await SaveSettingsAsync(_currentSettings with { MaterialStoragePath = result.FullPath });
            StoragePathBox.Text = result.FullPath;
            StorageStatus.Text = "当前目录可写。";
            SettingsStatus.Text = "素材文件储存路径已保存。已有文件不会自动迁移。";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"保存储存路径失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
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
                startupManager.SetEnabled(settings.StartWithWindows);
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
                try { new StartupManager().SetEnabled(previous.StartWithWindows); }
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
            SettingsStatus.Text = $"复制机器码失败：{CliDiagnostics.Sanitize(ex.Message)}";
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
