using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using QqChannelDesk.Pages;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class MainWindow : Window
{
    private readonly CliDiagnostics _diagnostics = new();
    private readonly FfmpegManager _ffmpegManager = new();
    private readonly CliWorkflow _workflow = new();
    private readonly ChannelCacheStore _channelCache = new();
    private readonly ChannelSyncService _channelSync;
    private readonly PublishHistoryStore _historyStore = new();
    private readonly AccountSessionStore _accountSessionStore = new();
    private readonly SystemSettingsStore _settingsStore = new();
    private readonly MediaStorageService _mediaStorage;
    private readonly ContentLibraryStore _contentLibrary = new();
    private readonly PublishExecutionService _publisher;
    private readonly PublishScheduler _scheduler;
    private readonly UpdateCheckService _updateCheckService = new();
    private readonly AppDatabaseInitializer _databaseInitializer;
    private readonly AppLogger _logger = AppLogger.Instance;
    private readonly DiagnosticsPage _diagnosticsPage = new();
    private readonly HistoryPage _historyPage;
    private readonly ContentCollectionPage _contentCollectionPage;
    private readonly MaterialsPage _materialsPage;
    private readonly PublishSchedulePage _schedulePage;
    private readonly SettingPage _settingPage;
    private readonly IReadOnlyDictionary<string, UserControl> _pages;
    private bool _isChecking;
    private bool _isInstalling;
    private bool _startupInstallPromptShown;
    private bool _isClosing;
    private int _updateCheckActive;
    private bool _exitRequested;
    private CloseWindowBehavior _closeWindowBehavior = CloseWindowBehavior.ExitApplication;
    private DiagnosticEnvironmentSnapshot _diagnosticSnapshot = DiagnosticEnvironmentSnapshot.Unavailable;
    private Forms.NotifyIcon? _trayIcon;
    private Button? _selectedNavigationButton;

    private const string FeedbackChannelUrl = "https://pd.qq.com/g/pd97255833";

    public MainWindow()
    {
        InitializeComponent();
        _mediaStorage = new MediaStorageService(_settingsStore);
        _channelSync = new ChannelSyncService(new CliWorkflow(), _channelCache);
        _publisher = new PublishExecutionService(_workflow, _historyStore, _mediaStorage, _logger);
        _databaseInitializer = new AppDatabaseInitializer(
            _settingsStore,
            _channelCache,
            _contentLibrary,
            _historyStore);
        _scheduler = new PublishScheduler(
            _contentLibrary,
            _publisher,
            _settingsStore,
            GetScheduleEnvironmentAsync,
            _logger);
        _historyPage = new HistoryPage(_historyStore);
        _contentCollectionPage = new ContentCollectionPage(_contentLibrary);
        _materialsPage = new MaterialsPage(_contentLibrary, _channelSync, _mediaStorage);
        _schedulePage = new PublishSchedulePage(_contentLibrary, _scheduler);
        _settingPage = new SettingPage(_settingsStore, _mediaStorage);
        _settingPage.SettingsChanged += SettingPage_SettingsChanged;
        _settingPage.UpdateCheckRequested += SettingPage_UpdateCheckRequested;
        _settingPage.OpenReleasesRequested += (_, _) => OpenReleasePage(UpdateCheckService.RepositoryReleaseUrl);
        _pages = new Dictionary<string, UserControl>
        {
            ["环境检查"] = _diagnosticsPage,
            ["内容采集"] = _contentCollectionPage,
            ["素材仓库"] = _materialsPage,
            ["发布计划"] = _schedulePage,
            ["发布记录"] = _historyPage,
            ["账号管理"] = new AccountManagementPage(),
            ["系统设置"] = _settingPage
        };

        PageHost.Content = _diagnosticsPage;
        _selectedNavigationButton = DiagnosticsNavButton;
        _logger.EntryWritten += Logger_EntryWritten;
        _diagnosticsPage.RefreshRequested += async (_, _) => await RefreshDiagnosticsAsync();
        _diagnosticsPage.InstallNodeRequested += async (_, _) => await InstallNodeAsync();
        _diagnosticsPage.InstallCliRequested += async (_, _) => await InstallCliAsync(confirm: true);
        _diagnosticsPage.InstallFfmpegRequested += async (_, _) => await InstallFfmpegAsync();
        _diagnosticsPage.LoginRequested += async (_, _) => await LoginAsync();
        _diagnosticsPage.PublishRequested += type => _ = OpenPublishDialogAsync(type);
        _diagnosticsPage.DebugModeChanged += DebugMode_Changed;
        _contentCollectionPage.PublishDraftRequested += draft => _ = OpenDraftPublishDialogAsync(draft);
        _materialsPage.PublishMaterialRequested += material => _ = OpenMaterialPublishDialogAsync(material);
        InitializeTrayIcon();
        Closing += MainWindow_Closing;

        Loaded += async (_, _) =>
        {
            try
            {
                await _databaseInitializer.InitializeAsync();
                //_logger.Info("本地数据库初始化完成，默认配置已检查。\n数据库位置：程序目录\\channels.db");
                _logger.Info("本地数据库初始化完成，默认配置已检查。");
            }
            catch (Exception ex)
            {
                var message = CliDiagnostics.Sanitize(ex.Message);
                _logger.Error($"本地数据库初始化失败：{message}");
                _diagnosticsPage.SetFooter("本地数据初始化失败，发布相关功能暂不可用");
                MessageBox.Show(this,
                    $"程序已启动，但本地数据初始化失败，设置、素材和发布记录暂不可用。\n\n{message}\n\n请确认程序目录可写后重启程序。",
                    "本地数据初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                await _historyStore.MarkInterruptedAsUnverifiedAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"恢复未完成发布记录失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }
            try
            {
                await _contentLibrary.MarkRunningExecutionsAsNeedsVerificationAsync(
                    "应用上次未能确认计划发布结果，请检查目标频道。");
            }
            catch (Exception ex)
            {
                _logger.Error($"恢复未完成计划失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }
            await RefreshDiagnosticsAsync();
            var settings = await _settingsStore.GetAsync();
            _closeWindowBehavior = SystemSettingsStore.NormalizeCloseWindowBehavior(settings.CloseWindowBehavior);
            _scheduler.Start(settings.RunTasksOnStartup);
            if (settings.NotifyUpgrade)
                _ = CheckForUpdatesAsync(showNoUpdate: false, delayed: true);
        };
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowMainWindowFromTray);
        var exitItem = new Forms.ToolStripMenuItem("退出程序");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(ExitFromTray);
        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "腾讯频道发帖助手",
            ContextMenuStrip = menu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMainWindowFromTray);
    }

    private void ShowMainWindowFromTray()
    {
        if (_isClosing) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        if (_trayIcon is not null) _trayIcon.Visible = false;
        _logger.Info("已从系统托盘恢复主界面。");
    }

    private void ExitFromTray()
    {
        if (_isClosing) return;
        _exitRequested = true;
        ShowInTaskbar = true;
        Close();
    }

    private void SettingPage_SettingsChanged(AppSettings settings)
    {
        _closeWindowBehavior = SystemSettingsStore.NormalizeCloseWindowBehavior(settings.CloseWindowBehavior);
    }

    private async void SettingPage_UpdateCheckRequested(object? sender, EventArgs e) =>
        await CheckForUpdatesAsync(showNoUpdate: true);

    private async Task CheckForUpdatesAsync(bool showNoUpdate, bool delayed = false)
    {
        if (Interlocked.CompareExchange(ref _updateCheckActive, 1, 0) != 0) return;

        try
        {
            if (delayed)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (_isClosing || !(await _settingsStore.GetAsync()).NotifyUpgrade) return;
            }

            _settingPage.SetUpdateCheckInProgress(true);
            UpdateCheckResult result;
            try
            {
                result = await _updateCheckService.CheckAsync(ApplicationVersionInfo.CurrentVersion);
            }
            catch (Exception ex)
            {
                result = new(false, false, $"版本检查失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }
            finally
            {
                _settingPage.SetUpdateCheckInProgress(false);
            }

            if (_isClosing) return;
            _settingPage.DisplayUpdateCheckResult(result);
            if (!result.Succeeded)
            {
                _logger.Warning(result.Message);
                if (showNoUpdate)
                    MessageBox.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (result.LatestRelease is not { } release)
            {
                if (showNoUpdate)
                    MessageBox.Show(this, result.Message, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 启动时只提示新版本；手动检查则展示当前 Release 的说明，方便用户确认变更内容。
            if (!result.HasUpdate && !showNoUpdate) return;
            if (!showNoUpdate && !(await _settingsStore.GetAsync()).NotifyUpgrade) return;

            _logger.Info(result.Message);
            var prompt = UpdateCheckService.BuildReleasePrompt(ApplicationVersionInfo.CurrentVersion, result);
            var promptTitle = result.HasUpdate ? "发现新版本" : "检查更新";
            if (MessageBox.Show(this, prompt, promptTitle, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenReleasePage(release.ReleaseUrl);
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                _logger.Warning($"版本检查失败：{CliDiagnostics.Sanitize(ex.Message)}");
                if (showNoUpdate)
                    MessageBox.Show(this, $"版本检查失败：{CliDiagnostics.Sanitize(ex.Message)}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            _settingPage.SetUpdateCheckInProgress(false);
            Interlocked.Exchange(ref _updateCheckActive, 0);
        }
    }

    private void OpenReleasePage(string url)
    {
        if (!UpdateCheckService.IsAllowedReleaseUrl(url)) url = UpdateCheckService.RepositoryReleaseUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开 Gitee Releases：{CliDiagnostics.Sanitize(ex.Message)}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string pageName } button || !_pages.TryGetValue(pageName, out var page)) return;

        if (_selectedNavigationButton is not null)
            _selectedNavigationButton.Style = (Style)FindResource("NavigationButton");
        button.Style = (Style)FindResource("ActiveNavigationButton");
        _selectedNavigationButton = button;
        PageHost.Content = page;

        if (ReferenceEquals(page, _materialsPage))
            await _materialsPage.LoadMaterialsAsync();

        if (ReferenceEquals(page, _schedulePage))
            await _schedulePage.LoadSchedulesAsync();

        if (ReferenceEquals(page, _historyPage))
            await _historyPage.LoadRecordsAsync();
    }

    private async Task LoginAsync()
    {
        if (_diagnosticsPage.CliState != "可用")
        {
            MessageBox.Show(this, "请先安装并确认频道 CLI 版本可用。", "无法扫码登录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetAccountActionsEnabled(false);
            var dialog = new LoginWindow(_workflow) { Owner = this };
            dialog.ShowDialog();
            if (!dialog.LoginSucceeded) return;

            _diagnosticsPage.SetFooter("扫码授权成功，正在同步频道和版块…");
            await RefreshDiagnosticsAsync();
            await RecordLoginSessionAsync();
            var sync = await _channelSync.SynchronizeAsync(new Progress<string>(_diagnosticsPage.SetFooter));
            _diagnosticsPage.SetFooter(sync.Succeeded
                ? $"频道数据已同步：{sync.GuildCount} 个频道、{sync.ChannelCount} 个版块"
                : $"频道数据同步失败，发布已禁用：{sync.Error}");
        }
        finally
        {
            SetAccountActionsEnabled(true);
        }
    }

    private async Task LogoutAsync()
    {
        if (MessageBox.Show(this,
                "将通过腾讯频道 CLI 清除当前登录凭证。退出后需要重新扫码登录才能发布。确定退出吗？",
                "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetAccountActionsEnabled(false);
        _diagnosticsPage.SetFooter("正在退出登录…");
        try
        {
            var result = await _workflow.LogoutAsync();
            if (!result.Succeeded)
            {
                _diagnosticsPage.SetFooter(result.Message);
                MessageBox.Show(this, result.Message, "退出登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshDiagnosticsAsync();
                return;
            }

            await _accountSessionStore.ClearAsync();
            UpdateAccountSessionDisplay("未知账号", null);
            _diagnosticsPage.SetFooter(result.Message);
            await RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            _diagnosticsPage.SetFooter($"退出登录失败：{message}");
            MessageBox.Show(this, message, "退出登录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetAccountActionsEnabled(true);
        }
    }

    private Task OpenPublishDialogAsync(FeedType type)
    {
        if (_diagnosticsPage.LoginState != "已登录")
        {
            MessageBox.Show(this, "请先通过 CLI 官方扫码流程登录，并重新检查登录状态。", "尚未登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        var dialog = new PublishDialog(_workflow, _channelSync, _historyStore, type, mediaStorage: _mediaStorage, publisher: _publisher) { Owner = this };
        _diagnosticsPage.SetFooter($"正在验证{(type == FeedType.Text ? "文本" : type == FeedType.Image ? "图片" : "视频")} Feed…");
        dialog.ShowDialog();
        if (dialog.Result is { } result)
        {
            _diagnosticsPage.SetFeedResult(type, result);
            var details = result.Message;
            if (!string.IsNullOrWhiteSpace(result.Url)) details += $"\n帖子链接：{result.Url}";
            if (!string.IsNullOrWhiteSpace(result.PostId)) details += $"\n帖子 ID：{result.PostId}";
            _logger.Info($"{(result.Succeeded ? "发布成功" : "发布未成功")}：{details}");
            _diagnosticsPage.SetFooter(result.Succeeded ? "发布请求成功" : $"发布失败：{result.Category}");
        }
        else
        {
            _diagnosticsPage.SetFooter("验证窗口已关闭，未发布或发布未成功");
        }
        return Task.CompletedTask;
    }

    private Task OpenDraftPublishDialogAsync(ContentDraft draft)
    {
        if (_diagnosticsPage.LoginState != "已登录")
        {
            MessageBox.Show(this, "请先通过 CLI 官方扫码流程登录，并重新检查登录状态。", "尚未登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        var dialog = new PublishDialog(_workflow, _channelSync, _historyStore, FeedType.Text, draft, _contentLibrary, mediaStorage: _mediaStorage, publisher: _publisher) { Owner = this };
        _diagnosticsPage.SetFooter("正在从草稿箱打开文本发布窗口…");
        dialog.ShowDialog();
        if (dialog.Result is { } result)
        {
            _diagnosticsPage.SetFeedResult(FeedType.Text, result);
            _logger.Info($"草稿发布{(result.Succeeded ? "成功" : "未成功")}：{CliDiagnostics.Sanitize(result.Message)}");
            _diagnosticsPage.SetFooter(result.Succeeded ? "草稿发布成功" : $"草稿发布失败：{result.Category}");
            _ = _contentCollectionPage.LoadDraftsAsync();
        }
        return Task.CompletedTask;
    }

    private async Task OpenMaterialPublishDialogAsync(MaterialRecord material)
    {
        if (_diagnosticsPage.LoginState != "已登录")
        {
            MessageBox.Show(this, "请先通过 CLI 官方扫码流程登录，并重新检查登录状态。", "尚未登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var type = material.Type switch { "image" => FeedType.Image, "video" => FeedType.Video, _ => FeedType.Text };
        var activeExecution = await _contentLibrary.GetActiveScheduleExecutionAsync(material.Id);
        if (activeExecution?.Status == ScheduleExecutionStatus.Running)
        {
            MessageBox.Show(this, "该素材的计划任务正在执行，暂不能重复发布。请先在频道中核实结果。", "任务正在执行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (activeExecution?.Status == ScheduleExecutionStatus.Pending)
        {
            if (MessageBox.Show(this,
                    "该素材已有待执行计划。继续人工发布会取消这个计划，但不会删除素材。是否立即发布？",
                    "覆盖发布计划", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            if (!await _contentLibrary.CancelMaterialScheduleAsync(material.Id))
            {
                MessageBox.Show(this, "计划可能已被后台执行器抢占，请刷新后再操作。", "无法覆盖计划", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            material = await _contentLibrary.GetMaterialAsync(material.Id) ?? material with { Status = "waitsend", PublishAt = null };
        }

        var originalStatus = material.Status;
        await _materialsPage.SetMaterialStatusAsync(material.Id, "queue");
        var dialog = new PublishDialog(_workflow, _channelSync, _historyStore, type, contentLibrary: _contentLibrary, sourceMaterial: material, mediaStorage: _mediaStorage, publisher: _publisher) { Owner = this };
        _diagnosticsPage.SetFooter($"正在发布素材：{material.Title}");
        dialog.ShowDialog();
        if (dialog.Result is { Succeeded: true } result)
        {
            var link = result.Url ?? (string.IsNullOrWhiteSpace(result.PostId) ? "" : result.PostId);
            await _materialsPage.MarkMaterialPublishedAsync(material.Id, link);
            _logger.Info($"素材发布成功：{CliDiagnostics.Sanitize(material.Title)}");
            _diagnosticsPage.SetFooter("素材发布成功");
        }
        else if (dialog.Result is { } failed)
        {
            await _materialsPage.SetMaterialStatusAsync(material.Id, originalStatus);
            _diagnosticsPage.SetFooter($"素材发布失败：{failed.Category}");
        }
        else await _materialsPage.SetMaterialStatusAsync(material.Id, originalStatus);
    }

    private async Task<ScheduleEnvironment> GetScheduleEnvironmentAsync()
    {
        try
        {
            // The scheduler runs on a background thread. Read the last diagnostic
            // result instead of accessing WPF controls from that thread.
            var snapshot = Volatile.Read(ref _diagnosticSnapshot);
            if (!snapshot.IsLoggedIn)
                return new ScheduleEnvironment(false, "当前账号未登录，计划执行器已暂停。请先扫码登录。");
            if (!snapshot.IsCliReady)
                return new ScheduleEnvironment(false, "频道 CLI 当前不可用，计划执行器已暂停。");

            var syncState = await _channelSync.GetStateAsync();
            if (!syncState.Succeeded)
                return new ScheduleEnvironment(false, "频道和版块缓存尚未成功同步，计划执行器已暂停。");

            var guilds = await _channelSync.GetCachedGuildsAsync();
            foreach (var guild in guilds)
            {
                if ((await _channelSync.GetCachedChannelsAsync(guild.Id)).Count > 0)
                    return ScheduleEnvironment.Available;
            }

            return new ScheduleEnvironment(false, "没有可用的频道版块缓存，计划执行器已暂停。");
        }
        catch (Exception ex)
        {
            return new ScheduleEnvironment(false, $"读取发布环境失败，计划执行器已暂停：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private async Task InstallNodeAsync()
    {
        var confirmation = MessageBox.Show(this,
            "将通过 Windows 包管理器 winget 从 OpenJS 安装 Node.js LTS。若 winget 不可用，可打开 nodejs.org 手动下载安装。继续吗？",
            "安装 Node.js", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (confirmation == MessageBoxResult.Cancel)
        {
            CliDiagnostics.OpenNodeDownloadPage();
            return;
        }
        if (confirmation != MessageBoxResult.Yes) return;

        await RunInstallOperationAsync("正在安装 Node.js LTS…", () => _diagnostics.InstallNodeAsync());
        await RefreshDiagnosticsAsync();
        if (_diagnosticsPage.NodeState == "可用" && _diagnosticsPage.CliState != "可用")
            await InstallCliAsync(confirm: false);
    }

    private async Task InstallFfmpegAsync()
    {
        var archivePath = _ffmpegManager.FindLatestArchive();
        if (archivePath is null)
        {
            MessageBox.Show(this,
                $"在本地工具目录中没有找到 FFmpeg 压缩包：\n{_ffmpegManager.ArchiveDirectory}\n\n请放入名称形如 ffmpeg-版本号-描述.7z 或 .zip 的压缩包。",
                "找不到 FFmpeg 安装包", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(this,
            $"将从本地压缩包解压 FFmpeg：\n{archivePath}\n\n部署目录：\n{_ffmpegManager.InstallDirectory}\n\n不会联网下载，也不会修改系统 PATH。此构建受 GPL 许可证约束。继续吗？",
            "手动部署 FFmpeg", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes) return;

        SetInstalling(true);
        _diagnosticsPage.SetFooter("正在从本地压缩包部署 FFmpeg…");
        CliInstallResult result;
        try { result = await _ffmpegManager.InstallAsync(); }
        catch (Exception ex) { result = new CliInstallResult(false, CliDiagnostics.Sanitize(ex.Message), null); }
        finally { SetInstalling(false); }

        _logger.Info($"{(result.Succeeded ? "FFmpeg 部署成功" : "FFmpeg 部署失败")}：{result.Message}");
        await RefreshDiagnosticsAsync();
        _diagnosticsPage.SetFooter(result.Message);
        if (!result.Succeeded)
            MessageBox.Show(this, result.Message, "FFmpeg 部署未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task<bool> InstallCliAsync(bool confirm)
    {
        if (confirm && MessageBox.Show(this,
            "将从 npm 官方仓库下载并全局安装 tencent-channel-cli。安装需要网络连接，可能因 npm 全局目录权限不足而失败。现在继续吗？",
            "确认安装腾讯频道 CLI", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        var result = await RunInstallOperationAsync("正在通过 npm 安装腾讯频道 CLI…", () => _diagnostics.InstallAsync());
        await RefreshDiagnosticsAsync();
        _diagnosticsPage.SetFooter(result.Succeeded
            ? _diagnosticsPage.CliState == "可用" ? "CLI 已安装，版本检查通过" : "npm 安装完成，但 CLI 版本检查未通过"
            : result.Message);
        return result.Succeeded;
    }

    private async Task<CliInstallResult> RunInstallOperationAsync(string status, Func<Task<CliInstallResult>> install)
    {
        SetInstalling(true);
        _diagnosticsPage.SetFooter(status);
        CliInstallResult result;
        try { result = await install(); }
        catch (Exception ex) { result = new CliInstallResult(false, CliDiagnostics.Sanitize(ex.Message), null); }
        finally { SetInstalling(false); }

        _logger.Info($"{(result.Succeeded ? "依赖安装命令完成" : "依赖安装失败")}：{result.Message}");
        if (!string.IsNullOrWhiteSpace(result.Output)) _logger.Debug($"依赖安装输出：\n{result.Output}");
        return result;
    }

    private void SetInstalling(bool installing)
    {
        _isInstalling = installing;
        _diagnosticsPage.Installing = installing;
        _diagnosticsPage.SetInstallEnabled(!installing);
        _diagnosticsPage.SetRefreshEnabled(!installing && !_isChecking);
    }

    private async Task RefreshDiagnosticsAsync()
    {
        if (_isChecking) return;

        var promptForAutomaticInstall = false;
        _isChecking = true;
        _diagnosticsPage.SetRefreshEnabled(false);
        _diagnosticsPage.SetFooter("正在检查运行环境…");
        try
        {
            var report = await _diagnostics.CheckAsync();
            Volatile.Write(ref _diagnosticSnapshot, new DiagnosticEnvironmentSnapshot(
                report.Login.State == DiagnosticState.Ready,
                report.Cli.State == DiagnosticState.Ready));
            _diagnosticsPage.UpdateReport(report);
            var loggedIn = report.Login.State == DiagnosticState.Ready;
            UpdateAccountActionVisibility(loggedIn);
            await RefreshAccountSessionDisplayAsync(report.Login.StateLabel);
            foreach (var line in report.LogLines) _logger.Info(line);
            _logger.Info($"运行环境：{report.OverallMessage}");
            _diagnosticsPage.SetFooter(report.OverallMessage);
            if (!_startupInstallPromptShown && (report.Node.State != DiagnosticState.Ready || report.Cli.State != DiagnosticState.Ready))
            {
                _startupInstallPromptShown = true;
                promptForAutomaticInstall = true;
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _diagnosticSnapshot, DiagnosticEnvironmentSnapshot.Unavailable);
            UpdateAccountActionVisibility(false);
            UpdateAccountSessionDisplay("状态未知", null);
            _diagnosticsPage.SetFooter("检查失败");
            _logger.Error($"检查过程发生错误：{ex.Message}");
        }
        finally
        {
            _diagnosticsPage.SetRefreshEnabled(!_isInstalling);
            _isChecking = false;
        }

        if (!promptForAutomaticInstall) return;
        var confirmation = MessageBox.Show(this,
            "首次启动需要准备运行环境。是否自动安装缺失的 Node.js LTS 和/或腾讯频道 CLI？\n\n安装仅在你确认后开始；自动安装失败或取消后，可在运行环境中手动点击对应按钮重试。",
            "首次运行需要安装环境", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes) return;

        if (_diagnosticsPage.NodeState != "可用")
        {
            var nodeInstall = await RunInstallOperationAsync("正在自动安装 Node.js LTS…", () => _diagnostics.InstallNodeAsync());
            await RefreshDiagnosticsAsync();
            if (!nodeInstall.Succeeded || _diagnosticsPage.NodeState != "可用")
            {
                if (!nodeInstall.Succeeded)
                    MessageBox.Show(this, $"Node.js 自动安装失败：{nodeInstall.Message}\n\n可点击“安装 Node.js”重试，或选择手动下载。",
                        "自动安装未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (_diagnosticsPage.CliState != "可用") await InstallCliAsync(confirm: false);
    }

    private async Task RefreshAccountSessionDisplayAsync(string loginState)
    {
        if (!string.Equals(loginState, "已登录", StringComparison.Ordinal))
        {
            UpdateAccountSessionDisplay(AccountSessionStore.ResolveDisplay(loginState, null, null).Nickname, null);
            return;
        }

        AccountProfile? stored = null;
        try
        {
            stored = await _accountSessionStore.GetAsync();
        }
        catch (Exception) { }

        string? nickname = null;
        AccountProfile? detected = null;
        try
        {
            nickname = await _workflow.GetCurrentAccountAsync();
            if (!string.IsNullOrWhiteSpace(nickname))
                detected = await _accountSessionStore.SaveDetectedAsync(nickname);
        }
        catch (Exception) { }

        var loginAt = detected?.LoginAt ??
            (string.Equals(stored?.Nickname, nickname, StringComparison.Ordinal) ? stored?.LoginAt : null);
        var display = AccountSessionStore.ResolveDisplay(loginState, nickname, loginAt);
        UpdateAccountSessionDisplay(display.Nickname, display.LoginAt);
    }

    private async Task RecordLoginSessionAsync()
    {
        string nickname;
        try { nickname = await _workflow.GetCurrentAccountAsync(); }
        catch (Exception) { nickname = "已登录账号"; }

        try
        {
            var profile = await _accountSessionStore.SaveLoginAsync(nickname, DateTimeOffset.Now);
            UpdateAccountSessionDisplay(profile.Nickname, profile.LoginAt);
        }
        catch (Exception) { UpdateAccountSessionDisplay(nickname, DateTimeOffset.Now); }
    }

    private void UpdateAccountSessionDisplay(string nickname, DateTimeOffset? loginAt)
    {
        AccountNameText.Text = string.IsNullOrWhiteSpace(nickname) ? "未知账号" : nickname.Trim();
        AccountLoginTimeText.Text = loginAt.HasValue
            ? $"本地登录记录：{loginAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "登录时间：未记录";
    }

    private void AccountLoginAction_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => _ = LoginAsync();

    private void AccountLogoutAction_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => _ = LogoutAsync();

    private void UpdateAccountActionVisibility(bool isLoggedIn)
    {
        AccountLoginAction.Visibility = isLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountLogoutAction.Visibility = isLoggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetAccountActionsEnabled(bool enabled)
    {
        AccountLoginAction.IsHitTestVisible = enabled;
        AccountLogoutAction.IsHitTestVisible = enabled;
        AccountLoginAction.Opacity = enabled ? 1 : 0.55;
        AccountLogoutAction.Opacity = enabled ? 1 : 0.55;
    }

    private void FeedbackNavButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(FeedbackChannelUrl) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开反馈链接：{CliDiagnostics.Sanitize(ex.Message)}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DebugMode_Changed(bool enabled)
    {
        _logger.SetDebugEnabled(enabled);
        _logger.Info(enabled ? "调试模式已开启，显示详细 CLI 输出。" : "调试模式已关闭，仅显示操作摘要。");
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;

        if (!_exitRequested && _closeWindowBehavior == CloseWindowBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            if (_trayIcon is not null) _trayIcon.Visible = true;
            _logger.Info("主界面已最小化到系统托盘，计划执行器保持运行。自动发布仍受系统设置开关控制。");
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        try
        {
            await _scheduler.StopAsync();
        }
        finally
        {
            if (_trayIcon is not null) _trayIcon.Visible = false;
            Close();
        }
    }

    private void Logger_EntryWritten(string entry)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => Logger_EntryWritten(entry));
            return;
        }
        _diagnosticsPage.AppendLog(entry);
    }

    protected override void OnClosed(EventArgs e)
    {
        _logger.EntryWritten -= Logger_EntryWritten;
        _settingPage.SettingsChanged -= SettingPage_SettingsChanged;
        _settingPage.UpdateCheckRequested -= SettingPage_UpdateCheckRequested;
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }

    private sealed record DiagnosticEnvironmentSnapshot(bool IsLoggedIn, bool IsCliReady)
    {
        public static DiagnosticEnvironmentSnapshot Unavailable { get; } = new(false, false);
    }
}
