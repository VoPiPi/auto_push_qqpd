using QqChannelDesk.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QqChannelDesk.Pages;

public partial class MaterialsPage : UserControl
{
    private const int PageSize = 100;
    private readonly ContentLibraryStore _store;
    private readonly ChannelSyncService _channelSync;
    private readonly MediaStorageService _mediaStorage;
    private readonly PublishScheduler? _scheduler;
    private bool _loading;
    private bool _batchBusy;
    private bool _resettingBatchAction;
    private IReadOnlyList<MaterialRecord> _allMaterials = [];
    private int _currentPage = 1;

    public event Action<MaterialRecord>? PublishMaterialRequested;

    public MaterialsPage(
        ContentLibraryStore store,
        ChannelSyncService channelSync,
        MediaStorageService? mediaStorage = null,
        PublishScheduler? scheduler = null)
    {
        InitializeComponent();
        _store = store;
        _channelSync = channelSync;
        _mediaStorage = mediaStorage ?? new MediaStorageService();
        _scheduler = scheduler;
        Loaded += async (_, _) => await LoadMaterialsAsync();
    }

    public async Task LoadMaterialsAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var filter = MaterialStatusFilter.SelectedItem as ComboBoxItem;
            _allMaterials = await _store.GetMaterialsAsync(MaterialSearchBox.Text, filter?.Tag?.ToString());
            _currentPage = 1;
            RenderCurrentPage();
        }
        catch (Exception ex)
        {
            _allMaterials = [];
            MaterialsGrid.ItemsSource = null;
            UpdatePagination();
            UpdateSelectionCount();
            MaterialsStatus.Text = $"读取素材仓库失败:{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally { _loading = false; }
    }

    private void RenderCurrentPage()
    {
        var pageCount = GetPageCount(_allMaterials.Count);
        _currentPage = Math.Clamp(_currentPage, 1, pageCount);
        MaterialsGrid.ItemsSource = _allMaterials.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToArray();
        MaterialsStatus.Text = $"共 {_allMaterials.Count} 条";
        UpdatePagination(pageCount);
        UpdateSelectionCount();
    }

    private void UpdatePagination(int? pageCount = null)
    {
        var pages = pageCount ?? GetPageCount(_allMaterials.Count);
        PageText.Text = $"{_currentPage} / {pages}";
        PreviousPageButton.IsEnabled = _currentPage > 1 && _allMaterials.Count > 0;
        NextPageButton.IsEnabled = _currentPage < pages && _allMaterials.Count > 0;
    }

    private static int GetPageCount(int total) => Math.Max(1, (total + PageSize - 1) / PageSize);

    private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage <= 1) return;
        _currentPage--;
        RenderCurrentPage();
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage >= GetPageCount(_allMaterials.Count)) return;
        _currentPage++;
        RenderCurrentPage();
    }

    private async Task EditMaterialAsync(MaterialRecord? existing)
    {
        var editor = new MaterialEditorWindow(_channelSync, existing, _mediaStorage) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Material is null) return;
        try
        {
            await _store.SaveMaterialAsync(editor.Material);
            await LoadMaterialsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), CliDiagnostics.Sanitize(ex.Message), "保存素材失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AddMaterialButton_Click(object sender, RoutedEventArgs e) => await EditMaterialAsync(null);

    private async void DownloadTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var targets = await LoadTemplateTargetsAsync();
            if (targets.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this), "当前账号没有可用的频道和板块,请先同步频道数据。", "无法下载模板", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "保存素材导入模板",
                Filter = "Excel 工作簿|*.xlsx",
                FileName = $"素材导入模板-{DateTime.Now:yyyyMMdd}.xlsx",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var bytes = await Task.Run(() => MaterialTemplateService.CreateWorkbook(targets));
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            MaterialsStatus.Text = $"模板已保存:{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), CliDiagnostics.Sanitize(ex.Message), "下载模板失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ImportDataButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择素材导入模板",
            Filter = "Excel 工作簿|*.xlsx|所有文件|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var targets = await LoadTemplateTargetsAsync();
            if (targets.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this), "当前账号没有可用的频道和板块,请先同步频道数据。", "无法导入素材", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var fileHash = await Task.Run(() => MaterialTemplateService.ComputeFileHash(dialog.FileName));
            if (await _store.HasImportedFileAsync(fileHash))
            {
                MessageBox.Show(Window.GetWindow(this), "该导入文件已经处理过,不能重复导入。请修改后另存为新文件,或使用预览中的错误行导出文件。", "重复导入", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = await Task.Run(() => MaterialTemplateService.ParseWorkbook(dialog.FileName, targets, DateTimeOffset.Now));
            var preview = new MaterialImportPreviewWindow(result, targets) { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true) return;

            await ImportRowsAsync(preview.RowsToImport, preview.FailureCount, fileHash, Path.GetFileName(dialog.FileName), Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), CliDiagnostics.Sanitize(ex.Message), "导入素材失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<IReadOnlyList<MaterialTemplateTarget>> LoadTemplateTargetsAsync()
    {
        var guilds = await _channelSync.GetCachedGuildsAsync();
        var targets = new List<MaterialTemplateTarget>(guilds.Count);
        foreach (var guild in guilds)
        {
            var channels = await _channelSync.GetCachedChannelsAsync(guild.Id);
            targets.Add(new MaterialTemplateTarget(
                guild.Id,
                guild.Name,
                guild.Role,
                channels.Select(channel => new MaterialTemplateChannel(channel.Id, channel.Name)).ToArray()));
        }
        return targets.Where(target => target.Channels.Count > 0).ToArray();
    }

    private async Task ImportRowsAsync(
        IReadOnlyList<MaterialTemplateImportRow> rows,
        int failureCount,
        string fileHash,
        string sourceFileName,
        Window? owner)
    {
        var drafts = new List<MaterialDraft>(rows.Count);
        var importedMedia = new List<string>();
        try
        {
            foreach (var row in rows)
            {
                var storedLinks = new List<string>(row.MediaLinks.Count);
                foreach (var link in row.MediaLinks)
                {
                    if (!MaterialMediaValidator.IsLocalPath(link) || await _mediaStorage.IsManagedLibraryPathAsync(link))
                    {
                        storedLinks.Add(link);
                        continue;
                    }

                    var stored = await _mediaStorage.ImportToLibraryAsync(link, row.Type);
                    storedLinks.Add(stored);
                    importedMedia.Add(stored);
                }

                drafts.Add(new MaterialDraft(
                    null,
                    row.Title,
                    row.Type,
                    row.Content,
                    row.GuildId,
                    row.GuildName,
                    row.ChannelId,
                    row.ChannelName,
                    row.PublishAt,
                    row.Status,
                    "",
                    storedLinks,
                    "批量导入",
                    sourceFileName));
            }

            await _store.SaveMaterialsAsync(drafts, fileHash);
        }
        catch
        {
            foreach (var path in importedMedia)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
            }
            throw;
        }

        try
        {
            await LoadMaterialsAsync();
            MessageBox.Show(owner, $"导入完成:成功 {drafts.Count} 条,失败 {failureCount} 条。", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"导入完成:成功 {drafts.Count} 条,失败 {failureCount} 条,但刷新列表失败:{CliDiagnostics.Sanitize(ex.Message)}", "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void EditMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MaterialRecord record) await EditMaterialAsync(record);
    }

    private async void DeleteMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MaterialRecord record) return;
        var display = string.IsNullOrWhiteSpace(record.Title) ? $"素材 #{record.Id}" : record.Title;
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除素材“{display}”?素材仅标记为删除,不会从数据库物理移除。", "删除素材", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var result = await _store.DeleteMaterialsAsync([record.Id]);
        if (result.DeletedIds.Count == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                BuildSkipMessage(result.RunningIds, result.PublishedIds, result.MissingIds),
                "未删除素材",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        await LoadMaterialsAsync();
    }

    private void PublishMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MaterialRecord record) return;
        var error = ValidateMaterialForPublish(record);
        if (error.Length > 0)
        {
            MessageBox.Show(Window.GetWindow(this), error, "素材不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PublishMaterialRequested?.Invoke(record);
    }

    public static string ValidateMaterialForPublish(MaterialRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.GuildId) || string.IsNullOrWhiteSpace(record.ChannelId)) return "请先编辑素材并指定发布频道和版块。";
        if (record.Type is not ("text" or "image" or "video")) return "素材类型无效。";
        return MaterialMediaValidator.ValidatePost(record.Type, record.Title, record.Content, record.MediaLinks);
    }

    public async Task MarkMaterialPublishedAsync(long id, string? link)
    {
        await _store.UpdateMaterialStatusAsync(id, "published", link);
        await LoadMaterialsAsync();
    }

    public async Task SetMaterialStatusAsync(long id, string status)
    {
        await _store.UpdateMaterialStatusAsync(id, status);
        await LoadMaterialsAsync();
    }

    private async void SearchMaterialsButton_Click(object sender, RoutedEventArgs e) => await LoadMaterialsAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await LoadMaterialsAsync();
    }

    private IReadOnlyList<MaterialRecord> GetSelectedMaterials() =>
        _allMaterials.Where(material => material.IsSelected).ToArray();

    private void UpdateSelectionCount()
    {
        if (SelectedCountText is null) return;
        var selected = _allMaterials.Count(material => material.IsSelected);
        SelectedCountText.Text = $"已选 {selected} / 共 {_allMaterials.Count}";
    }

    private void SelectAll()
    {
        foreach (var material in _allMaterials) material.IsSelected = true;
        UpdateSelectionCount();
    }

    private void InvertSelection()
    {
        foreach (var material in _allMaterials) material.IsSelected = !material.IsSelected;
        UpdateSelectionCount();
    }

    private async void BatchActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_resettingBatchAction || _batchBusy) return;
        if (BatchActionBox.SelectedItem is not ComboBoxItem { Tag: string action } ||
            string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        _batchBusy = true;
        BatchActionBox.IsEnabled = false;
        try
        {
            await ExecuteBatchActionAsync(action);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                CliDiagnostics.Sanitize(ex.Message),
                "批量操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _batchBusy = false;
            _resettingBatchAction = true;
            BatchActionBox.SelectedIndex = 0;
            _resettingBatchAction = false;
            BatchActionBox.IsEnabled = true;
            UpdateSelectionCount();
        }
    }

    private async Task ExecuteBatchActionAsync(string action)
    {
        switch (action)
        {
            case "select-all":
                SelectAll();
                return;
            case "invert":
                InvertSelection();
                return;
            case "delete":
                await BatchDeleteAsync();
                return;
            case "publish":
                await BatchPublishAsync();
                return;
            case "target":
                await BatchSetTargetAsync();
                return;
            case "time":
                await BatchSetPublishAtAsync();
                return;
            default:
                throw new InvalidOperationException($"未知批量操作:{action}");
        }
    }

    private async Task BatchDeleteAsync()
    {
        var selected = GetSelectedMaterials();
        if (selected.Count == 0)
        {
            ShowNoSelectionMessage();
            return;
        }

        if (MessageBox.Show(
                Window.GetWindow(this),
                $"确定删除已选择的 {selected.Count} 条素材?仅标记为删除,不会从数据库物理移除。",
                "批量删除素材",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await _store.DeleteMaterialsAsync(selected.Select(material => material.Id).ToArray());
        await LoadMaterialsAsync();
        ShowBatchResult(
            "批量删除",
            result.DeletedIds.Count,
            result.RunningIds,
            result.PublishedIds,
            result.MissingIds);
    }

    private async Task BatchPublishAsync()
    {
        var selected = GetSelectedMaterials()
            .Where(material => material.Status is "waitsend" or "queue")
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "所选素材中没有可加入发布队列的记录。已发布素材不参与批量发布。",
                "批量发布",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        foreach (var material in selected)
        {
            var error = ValidateMaterialForPublish(material);
            if (error.Length > 0)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"素材 #{material.Id}:{error}",
                    "素材不完整",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        var executions = await _store.GetScheduleExecutionsAsync();
        var runningIds = executions
            .Where(execution => execution.Status == ScheduleExecutionStatus.Running)
            .Select(execution => execution.MaterialId)
            .ToHashSet();
        var publishable = selected.Where(material => !runningIds.Contains(material.Id)).ToArray();
        var skippedRunning = selected.Where(material => runningIds.Contains(material.Id)).Select(material => material.Id).ToArray();
        if (publishable.Length == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                BuildSkipMessage(skippedRunning, [], []),
                "批量发布",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var queuedIds = await _store.QueueMaterialsForPublishAsync(
            publishable.Select(material => material.Id).ToArray(),
            DateTimeOffset.Now);
        await LoadMaterialsAsync();

        if (_scheduler is null)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"已将 {queuedIds.Count} 条素材加入发布队列,但当前没有可用的发布调度器。",
                "批量发布",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var check = await _scheduler.CheckNowAsync();
        var runningSummary = BuildSkipMessage(skippedRunning, [], []);
        var message = check.EnvironmentReady
            ? $"已加入发布队列 {queuedIds.Count} 条;调度器已启动 {check.StartedCount} 条,已完成 {check.CompletedCount} 条。"
            : $"已加入发布队列 {queuedIds.Count} 条,但发布环境未就绪:{check.Message}";
        if (runningSummary.Length > 0) message += $"\n{runningSummary}";
        MessageBox.Show(Window.GetWindow(this), message, "批量发布", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task BatchSetTargetAsync()
    {
        var selected = GetSelectedMaterials()
            .Where(material => material.Status is not ("published" or "delete"))
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "所选素材中没有可设置频道的记录。已发布素材不参与批量设置。",
                "批量发布频道",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new MaterialBatchSettingsWindow(
            MaterialBatchSettingsMode.Target,
            _channelSync,
            selected.Length)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true || dialog.SelectedGuild is not { } guild || dialog.SelectedChannel is not { } channel)
            return;

        var result = await _store.UpdateMaterialsTargetAsync(
            selected.Select(material => material.Id).ToArray(),
            guild.Id,
            guild.Name,
            channel.Id,
            channel.Name);
        await LoadMaterialsAsync();
        ShowBatchResult(
            "批量发布频道",
            result.UpdatedIds.Count,
            result.RunningIds,
            result.PublishedIds,
            result.MissingIds);
    }

    private async Task BatchSetPublishAtAsync()
    {
        var selected = GetSelectedMaterials()
            .Where(material => material.Status is not ("published" or "delete"))
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "所选素材中没有可设置发布时间的记录。已发布素材不参与批量设置。",
                "批量设置发布时间",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new MaterialBatchSettingsWindow(
            MaterialBatchSettingsMode.Time,
            _channelSync,
            selected.Length)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true || dialog.PublishAt is not { } publishAt) return;

        var result = await _store.UpdateMaterialsPublishAtAsync(
            selected.Select(material => material.Id).ToArray(),
            publishAt);
        await LoadMaterialsAsync();
        ShowBatchResult(
            "批量设置发布时间",
            result.UpdatedIds.Count,
            result.RunningIds,
            result.PublishedIds,
            result.MissingIds);
    }

    private void ShowNoSelectionMessage()
    {
        MessageBox.Show(
            Window.GetWindow(this),
            "请先勾选需要操作的素材。",
            "批量操作",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ShowBatchResult(
        string action,
        int successCount,
        IReadOnlyList<long> runningIds,
        IReadOnlyList<long> publishedIds,
        IReadOnlyList<long> missingIds)
    {
        var skipMessage = BuildSkipMessage(runningIds, publishedIds, missingIds);
        var message = $"{action}完成:成功处理 {successCount} 条。";
        if (skipMessage.Length > 0) message += $"\n{skipMessage}";
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            action,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string BuildSkipMessage(
        IReadOnlyList<long> runningIds,
        IReadOnlyList<long> publishedIds,
        IReadOnlyList<long> missingIds)
    {
        var parts = new List<string>(3);
        if (runningIds.Count > 0) parts.Add($"正在发布 {runningIds.Count} 条");
        if (publishedIds.Count > 0) parts.Add($"已发布 {publishedIds.Count} 条");
        if (missingIds.Count > 0) parts.Add($"不存在或已删除 {missingIds.Count} 条");
        return parts.Count == 0 ? "" : $"已跳过:{string.Join("、", parts)}。";
    }
}
