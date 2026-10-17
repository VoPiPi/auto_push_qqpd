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
    private int _loadVersion;
    private int _activeLoadVersion;
    private CancellationTokenSource? _pageLifetimeCts;

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
        Loaded += (_, _) => StartPageLifetime();
        Unloaded += (_, _) => EndPageLifetime();
        VersionText.Text = ApplicationVersionInfo.CurrentVersion;
    }

    private CancellationToken CurrentPageToken =>
        _pageLifetimeCts?.Token ?? new CancellationToken(canceled: true);

    private bool IsPageActiveForUpdates =>
        IsLoaded
        && _pageLifetimeCts is { IsCancellationRequested: false };

    private bool IsPageActive(CancellationToken token) =>
        IsPageActiveForUpdates
        && !token.IsCancellationRequested
        && _pageLifetimeCts is { IsCancellationRequested: false }
        && _pageLifetimeCts.Token == token;

    private void StartPageLifetime()
    {
        var previous = _pageLifetimeCts;
        var lifetime = new CancellationTokenSource();
        _pageLifetimeCts = lifetime;
        previous?.Cancel();
        UiAsync.Observe("设置页初始化", InitializeAsync(lifetime.Token));
    }

    private void EndPageLifetime()
    {
        _loadVersion++;
        _activeLoadVersion = 0;
        var lifetime = _pageLifetimeCts;
        _pageLifetimeCts = null;
        lifetime?.Cancel();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        MachineCodeBox.Text = "正在生成机器码…";
        var machineCodeTask = Task.Run(MachineCodeProvider.GetMachineCode);
        await LoadAsync(cancellationToken);

        string? machineCode = null;
        Exception? machineCodeError = null;
        try
        {
            machineCode = await machineCodeTask;
        }
        catch (Exception ex)
        {
            machineCodeError = ex;
        }

        if (!IsPageActive(cancellationToken)) return;
        if (machineCodeError is not null)
        {
            MachineCodeBox.Text = "机器码生成失败";
            SettingsStatus.Text = $"设置已加载，但机器码生成失败：{CliDiagnostics.Sanitize(machineCodeError.Message)}";
            return;
        }

        MachineCodeBox.Text = machineCode ?? "机器码生成失败";
        SettingsStatus.Text = $"设置已加载，机器码：{MachineCodeBox.Text}";
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_loadVersion;
        _activeLoadVersion = version;
        try
        {
            var settings = await _settingsStore.GetAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;
            _currentSettings = settings;
            NotifyUpgradeCheckBox.IsChecked = _currentSettings.NotifyUpgrade;
            RunTasksOnStartupCheckBox.IsChecked = _currentSettings.RunTasksOnStartup;
            AutoExecuteSchedulesCheckBox.IsChecked = _currentSettings.AutoExecuteSchedules;
            StartWithWindowsCheckBox.IsChecked = _currentSettings.StartWithWindows;
            ScheduleConcurrencyCombo.SelectedItem = ScheduleConcurrencyCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == _currentSettings.MaxScheduleConcurrency.ToString());
            StoragePathBox.Text = _currentSettings.EffectiveMaterialStoragePath;
            await RefreshFfmpegPathAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;
            RetentionPolicyCombo.SelectedItem = RetentionPolicyCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _currentSettings.MaterialRetentionPolicy.ToString(), StringComparison.Ordinal));
            CloseWindowBehaviorCombo.SelectedItem = CloseWindowBehaviorCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _currentSettings.CloseWindowBehavior.ToString(), StringComparison.Ordinal));
            var directory = await MediaStorageService.ValidateDirectoryAsync(
                _currentSettings.EffectiveMaterialStoragePath,
                cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;
            StorageStatus.Text = directory.Succeeded ? "当前目录可写。" : directory.Message;
            SettingsStatus.Text = "设置已加载。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsPageActive(cancellationToken) && version == _loadVersion)
                SettingsStatus.Text = $"读取设置未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试。";
        }
        finally
        {
            if (_activeLoadVersion == version) _activeLoadVersion = 0;
        }
    }

    private void SettingCheckBox_Changed(object sender, RoutedEventArgs e) =>
        UiAsync.Run("保存常规设置", async () =>
        {
        var pageToken = CurrentPageToken;
        if (_activeLoadVersion != 0 || !IsPageActive(pageToken)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with
            {
                NotifyUpgrade = NotifyUpgradeCheckBox.IsChecked == true,
                RunTasksOnStartup = RunTasksOnStartupCheckBox.IsChecked == true,
                AutoExecuteSchedules = AutoExecuteSchedulesCheckBox.IsChecked == true,
                StartWithWindows = StartWithWindowsCheckBox.IsChecked == true
            });
            if (IsPageActive(pageToken)) SettingsStatus.Text = "设置已保存。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
        });

    private void CloseWindowBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("保存关闭窗口行为", async () =>
        {
        var pageToken = CurrentPageToken;
        if (_activeLoadVersion != 0
            || !IsPageActive(pageToken)
            || CloseWindowBehaviorCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<CloseWindowBehavior>(item.Tag?.ToString(), out var behavior)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with
            {
                CloseWindowBehavior = SystemSettingsStore.NormalizeCloseWindowBehavior(behavior)
            });
            if (IsPageActive(pageToken)) SettingsStatus.Text = "关闭窗口操作已保存。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
        });

    private void ScheduleConcurrencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("保存计划最大并发数", async () =>
        {
        var pageToken = CurrentPageToken;
        if (_activeLoadVersion != 0
            || !IsPageActive(pageToken)
            || ScheduleConcurrencyCombo.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var concurrency)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with { MaxScheduleConcurrency = SystemSettingsStore.NormalizeConcurrency(concurrency) });
            if (IsPageActive(pageToken)) SettingsStatus.Text = "发帖最大并发数已保存（仅作用于发布计划）。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
        });

    private void RetentionPolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("保存素材保留期限", async () =>
        {
        var pageToken = CurrentPageToken;
        if (_activeLoadVersion != 0
            || !IsPageActive(pageToken)
            || RetentionPolicyCombo.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<MaterialRetentionPolicy>(item.Tag?.ToString(), out var policy)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with { MaterialRetentionPolicy = policy });
            if (IsPageActive(pageToken)) SettingsStatus.Text = "文件保留期限已保存。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                SettingsStatus.Text = $"保存未完成：{CliDiagnostics.Sanitize(ex.Message)}。请重试或检查程序目录权限。";
        }
        });

    private void ChangeStoragePathButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("更改素材存储路径", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var dialog = new OpenFolderDialog
        {
            Title = "选择素材文件存储路径",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        if (!IsPageActive(pageToken)) return;

        try
        {
            SettingsStatus.Text = "正在检查所选目录…";
            var result = await MediaStorageService.ValidateDirectoryAsync(dialog.FolderName, pageToken);
            if (!IsPageActive(pageToken)) return;
            if (!result.Succeeded)
            {
                StorageStatus.Text = result.Message;
                SettingsStatus.Text = "目录不可用，未保存新路径。请选择可写目录。";
                return;
            }

            await SaveSettingsAsync(_currentSettings with { MaterialStoragePath = result.FullPath });
            if (!IsPageActive(pageToken)) return;
            StoragePathBox.Text = result.FullPath;
            StorageStatus.Text = "当前目录可写。";
            SettingsStatus.Text = "存储路径已保存。已有文件不会自动迁移。";
        }
        catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                SettingsStatus.Text = $"存储路径未保存：{CliDiagnostics.Sanitize(ex.Message)}。请重试或选择其他目录。";
        }
        });

    private void ChangeFfmpegPathButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("更改 FFmpeg 路径", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择 FFmpeg 可执行文件",
            Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "ffmpeg.exe"
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;
        if (!IsPageActive(pageToken)) return;

        try
        {
            var path = FfmpegManager.ValidateExecutablePath(dialog.FileName);
            await SaveSettingsAsync(_currentSettings with { FfmpegPath = path });
            if (!IsPageActive(pageToken)) return;
            await RefreshFfmpegPathAsync(pageToken);
            if (!IsPageActive(pageToken)) return;
            SettingsStatus.Text = "FFmpeg 路径已保存，后续频道工具操作将使用此路径。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                SettingsStatus.Text = $"FFmpeg 路径未保存：{CliDiagnostics.Sanitize(ex.Message)}。请重试或重新选择文件。";
        }
        });

    private void ResetFfmpegPathButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("恢复 FFmpeg 自动选择", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        try
        {
            await SaveSettingsAsync(_currentSettings with { FfmpegPath = null });
            if (!IsPageActive(pageToken)) return;
            await RefreshFfmpegPathAsync(pageToken);
            if (!IsPageActive(pageToken)) return;
            SettingsStatus.Text = "FFmpeg 已恢复自动选择：系统路径优先，项目 tools 版本后备。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                SettingsStatus.Text = $"未恢复自动选择：{CliDiagnostics.Sanitize(ex.Message)}。请重试。";
        }
        });

    private async Task RefreshFfmpegPathAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var resolution = await _ffmpegManager.ResolveAsync(cancellationToken);
        if (!IsPageActive(cancellationToken)) return;
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
                if (IsPageActiveForUpdates)
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
                if (IsPageActiveForUpdates)
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
        if (!IsPageActiveForUpdates) return;
        if (inProgress) UpdateStatusText.Text = "正在查询 Gitee 最新发布…";
        CheckUpdateButton.IsEnabled = !inProgress;
    }

    public void DisplayUpdateCheckResult(UpdateCheckResult result)
    {
        if (!IsPageActiveForUpdates) return;
        UpdateStatusText.Text = result.Message;
    }

    private void CheckUpdateButton_Click(object sender, RoutedEventArgs e) => UpdateCheckRequested?.Invoke(this, EventArgs.Empty);

    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e) => OpenReleasesRequested?.Invoke(this, EventArgs.Empty);

}
