using QqChannelDesk.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace QqChannelDesk.Pages;

public partial class PublishSchedulePage : UserControl
{
    private const int PageSize = 100;
    private readonly ContentLibraryStore _store;
    private readonly PublishScheduler _scheduler;
    private IReadOnlyList<PublishScheduleRow> _allSchedules = [];
    private int _currentPage = 1;
    private int _loadVersion;
    private bool _schedulerEventSubscribed;
    private bool _syncingScheduleSelection;
    private bool _batchBusy;
    private bool _resettingBatchAction;
    private CheckBox? _scheduleSelectionClickCheckBox;
    private bool _scheduleSelectionClickWasChecked;
    private CancellationTokenSource? _pageLifetimeCts;
    private PublishSchedulerStatus _lastSchedulerStatus;

    public PublishSchedulePage(ContentLibraryStore store, PublishScheduler scheduler)
    {
        InitializeComponent();
        _store = store;
        _scheduler = scheduler;
        _lastSchedulerStatus = _scheduler.Status;
        Loaded += (_, _) => StartPageLifetime();
        Unloaded += (_, _) => EndPageLifetime();
    }

    private CancellationToken CurrentPageToken =>
        _pageLifetimeCts?.Token ?? new CancellationToken(canceled: true);

    private bool IsPageActive(CancellationToken token) =>
        IsLoaded
        && !token.IsCancellationRequested
        && _pageLifetimeCts is { IsCancellationRequested: false }
        && _pageLifetimeCts.Token == token;

    private void StartPageLifetime()
    {
        var previous = _pageLifetimeCts;
        var lifetime = new CancellationTokenSource();
        _pageLifetimeCts = lifetime;
        previous?.Cancel();

        SubscribeSchedulerEvent();
        ApplySchedulerStatus(_lastSchedulerStatus);
        UiAsync.Observe("发布计划初始化", LoadSchedulesAsync(lifetime.Token, resetPage: true));
    }

    private void EndPageLifetime()
    {
        _loadVersion++;
        UnsubscribeSchedulerEvent();
        var lifetime = _pageLifetimeCts;
        _pageLifetimeCts = null;
        lifetime?.Cancel();
    }

    private void SubscribeSchedulerEvent()
    {
        if (_schedulerEventSubscribed) return;
        _scheduler.StatusChanged += Scheduler_StatusChanged;
        _schedulerEventSubscribed = true;
        _lastSchedulerStatus = _scheduler.Status;
    }

    private void UnsubscribeSchedulerEvent()
    {
        if (!_schedulerEventSubscribed) return;
        _scheduler.StatusChanged -= Scheduler_StatusChanged;
        _schedulerEventSubscribed = false;
    }

    public Task LoadSchedulesAsync() => LoadSchedulesAsync(CurrentPageToken);

    private async Task LoadSchedulesAsync(CancellationToken cancellationToken, bool resetPage = false)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_loadVersion;
        var selectedIds = _allSchedules
            .Where(row => row.IsSelected)
            .Select(row => row.Id)
            .ToHashSet();

        StatusText.Text = "正在读取发布计划…";
        StatusText.Foreground = (Brush)FindResource("Muted");
        try
        {
            var schedules = await _store.GetScheduleExecutionsAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;

            _allSchedules = schedules
                .Select(record => new PublishScheduleRow(record)
                {
                    IsSelected = selectedIds.Contains(record.Id)
                })
                .ToArray();
            if (resetPage) _currentPage = 1;
            RenderCurrentPage();
            EmptyTitle.Text = "暂无发布计划";
            EmptyMessage.Text = "在素材库设置未来发布时间后，计划会显示在这里。";
            LastRefreshText.Text = $"刷新于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken) || version != _loadVersion) return;
            _allSchedules = [];
            SchedulesGrid.ItemsSource = null;
            UpdatePagination();
            UpdateSelectionCount();
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "未能读取发布计划";
            EmptyMessage.Text = "请刷新重试，或检查本地数据库是否可访问。";
            StatusText.Text = $"读取发布计划失败：{CliDiagnostics.Sanitize(ex.Message)}";
            StatusText.Foreground = UiStatusBrushes.Danger;
            LastRefreshText.Text = "刷新未完成";
        }
    }

    private void RenderCurrentPage()
    {
        var pageCount = GetPageCount(_allSchedules.Count);
        _currentPage = Math.Clamp(_currentPage, 1, pageCount);
        var pageItems = _allSchedules
            .Skip((_currentPage - 1) * PageSize)
            .Take(PageSize)
            .ToArray();

        _syncingScheduleSelection = true;
        try
        {
            SchedulesGrid.ItemsSource = pageItems;
            SchedulesGrid.UnselectAll();
            foreach (var row in pageItems.Where(row => row.IsSelected))
                SchedulesGrid.SelectedItems.Add(row);
        }
        finally
        {
            _syncingScheduleSelection = false;
        }

        StatusText.Text = $"共 {_allSchedules.Count} 条";
        StatusText.Foreground = (Brush)FindResource("Muted");
        EmptyState.Visibility = _allSchedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePagination(pageCount);
        UpdateSelectionCount();
    }

    private void UpdatePagination(int? pageCount = null)
    {
        var pages = pageCount ?? GetPageCount(_allSchedules.Count);
        PageText.Text = $"{_currentPage} / {pages}";
        PreviousPageButton.IsEnabled = _currentPage > 1 && _allSchedules.Count > 0;
        NextPageButton.IsEnabled = _currentPage < pages && _allSchedules.Count > 0;
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
        if (_currentPage >= GetPageCount(_allSchedules.Count)) return;
        _currentPage++;
        RenderCurrentPage();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("刷新发布计划", () => LoadSchedulesAsync(CurrentPageToken));

    private void CheckNowButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("立即检查发布计划", async () =>
        {
            var pageToken = CurrentPageToken;
            if (!IsPageActive(pageToken)) return;
            if (sender is Button button) button.IsEnabled = false;
            try
            {
                StatusText.Text = "正在检查到期计划…";
                var result = await _scheduler.CheckNowAsync(pageToken);
                if (!IsPageActive(pageToken)) return;
                StatusText.Text = result.EnvironmentReady
                    ? result.StartedCount == 0 ? "立即检查完成，没有可执行计划" : $"已启动 {result.StartedCount} 条计划"
                    : $"计划未执行：{result.Message}";
                await LoadSchedulesAsync(pageToken);
            }
            finally
            {
                if (sender is Button checkButton) checkButton.IsEnabled = true;
            }
        });

    private void SchedulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingScheduleSelection) return;

        _syncingScheduleSelection = true;
        try
        {
            foreach (var row in e.AddedItems.OfType<PublishScheduleRow>()) row.IsSelected = true;
            foreach (var row in e.RemovedItems.OfType<PublishScheduleRow>()) row.IsSelected = false;
        }
        finally
        {
            _syncingScheduleSelection = false;
        }

        UpdateSelectionCount();
    }

    private void SchedulesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None) return;
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        if (IsInteractiveRowElement(source)) return;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow { Item: PublishScheduleRow row }) return;

        var selectedBefore = grid.SelectedItems.OfType<PublishScheduleRow>().ToArray();
        var shouldSelect = !row.IsSelected;
        UiAsync.Post(Dispatcher, () =>
        {
            _syncingScheduleSelection = true;
            try
            {
                foreach (var selected in selectedBefore) selected.IsSelected = true;
                row.IsSelected = shouldSelect;
            }
            finally
            {
                _syncingScheduleSelection = false;
            }

            SyncSchedulesGridSelectionFromModels();
            UpdateSelectionCount();
        }, DispatcherPriority.Background);
    }

    private void SyncSchedulesGridSelectionFromModels()
    {
        _syncingScheduleSelection = true;
        try
        {
            SchedulesGrid.UnselectAll();
            foreach (var row in SchedulesGrid.Items.OfType<PublishScheduleRow>().Where(row => row.IsSelected))
                SchedulesGrid.SelectedItems.Add(row);
        }
        finally
        {
            _syncingScheduleSelection = false;
        }
    }

    private void ScheduleSelectionCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox) return;
        _scheduleSelectionClickCheckBox = checkBox;
        _scheduleSelectionClickWasChecked = checkBox.IsChecked == true;
    }

    private void ScheduleSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not PublishScheduleRow row) return;
        var shouldSelect = ReferenceEquals(_scheduleSelectionClickCheckBox, checkBox)
            ? !_scheduleSelectionClickWasChecked
            : checkBox.IsChecked == true;
        _scheduleSelectionClickCheckBox = null;
        row.IsSelected = shouldSelect;
        checkBox.IsChecked = shouldSelect;
        UiAsync.Post(Dispatcher, () =>
        {
            SyncSchedulesGridSelectionFromModels();
            UpdateSelectionCount();
        }, DispatcherPriority.Background);
    }

    private IReadOnlyList<PublishScheduleRow> GetSelectedSchedules() =>
        _allSchedules.Where(row => row.IsSelected).ToArray();

    private void UpdateSelectionCount()
    {
        if (SelectedCountText is null) return;
        var selected = _allSchedules.Count(row => row.IsSelected);
        SelectedCountText.Text = $"已选 {selected} 条";
    }

    private void SelectAll()
    {
        _syncingScheduleSelection = true;
        try
        {
            foreach (var row in _allSchedules) row.IsSelected = true;
        }
        finally
        {
            _syncingScheduleSelection = false;
        }

        SyncSchedulesGridSelectionFromModels();
        UpdateSelectionCount();
    }

    private void InvertSelection()
    {
        _syncingScheduleSelection = true;
        try
        {
            foreach (var row in _allSchedules) row.IsSelected = !row.IsSelected;
        }
        finally
        {
            _syncingScheduleSelection = false;
        }

        SyncSchedulesGridSelectionFromModels();
        UpdateSelectionCount();
    }

    private void BatchActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("执行发布计划批量操作", async () =>
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
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    CliDiagnostics.Sanitize(ex.Message),
                    "批量操作未完成",
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
        });

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
            case "pause":
                await BatchSetPausedAsync(true);
                return;
            case "start":
                await BatchSetPausedAsync(false);
                return;
            case "delete":
                await BatchDeleteAsync();
                return;
            default:
                throw new InvalidOperationException($"未知批量操作：{action}");
        }
    }

    private async Task BatchSetPausedAsync(bool isPaused)
    {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var selected = GetSelectedSchedules();
        if (selected.Count == 0)
        {
            ShowNoSelectionMessage();
            return;
        }

        var action = isPaused ? "批量暂停" : "批量启动";
        var result = await _store.SetScheduleExecutionsPausedAsync(
            selected.Select(row => row.Id).ToArray(),
            isPaused,
            pageToken);
        if (!IsPageActive(pageToken)) return;

        await LoadSchedulesAsync(pageToken);
        ShowBatchMutationResult(action, result, isPaused ? "暂停" : "启动");
    }

    private async Task BatchDeleteAsync()
    {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var selected = GetSelectedSchedules();
        if (selected.Count == 0)
        {
            ShowNoSelectionMessage();
            return;
        }

        if (MessageBox.Show(
                Window.GetWindow(this),
                $"确定删除已选择的 {selected.Count} 条发布计划？\n\n只删除计划记录，不删除素材和发布记录。待执行素材没有其他计划时会恢复为“待发布”。",
                "批量删除发布计划",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await _store.DeleteScheduleExecutionsAsync(
            selected.Select(row => row.Id).ToArray(),
            pageToken);
        if (!IsPageActive(pageToken)) return;

        await LoadSchedulesAsync(pageToken);
        ShowBatchMutationResult("批量删除", result, "删除");
    }

    private void ShowNoSelectionMessage()
    {
        MessageBox.Show(
            Window.GetWindow(this),
            "请先勾选需要操作的发布计划。",
            "批量操作",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ShowBatchMutationResult(
        string title,
        ScheduleExecutionMutationResult result,
        string action)
    {
        var message = $"{title}完成：成功{action} {result.AffectedIds.Count} 条。";
        var skipped = BuildScheduleSkipMessage(result);
        if (skipped.Length > 0) message += $"\n{skipped}";
        MessageBox.Show(
            Window.GetWindow(this),
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        if (IsPageActive(CurrentPageToken)) StatusText.Text = message.Replace('\n', ' ');
    }

    private static string BuildScheduleSkipMessage(ScheduleExecutionMutationResult result)
    {
        var parts = new List<string>(3);
        if (result.RunningIds.Count > 0) parts.Add($"正在执行 {result.RunningIds.Count} 条");
        if (result.FinishedIds.Count > 0) parts.Add($"状态已变化或已结束 {result.FinishedIds.Count} 条");
        if (result.MissingIds.Count > 0) parts.Add($"不存在 {result.MissingIds.Count} 条");
        return parts.Count == 0 ? "" : $"已跳过：{string.Join("、", parts)}。";
    }

    private void ToggleScheduleButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("切换发布计划状态", async () =>
        {
            var pageToken = CurrentPageToken;
            if (!IsPageActive(pageToken)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishScheduleRow row) return;
            var owner = Window.GetWindow(this);
            if (!row.CanToggle)
            {
                MessageBox.Show(owner, "只有等待执行的计划可以暂停或启动。", "无法修改计划", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var isPaused = !row.IsPaused;
            try
            {
                var result = await _store.SetScheduleExecutionsPausedAsync([row.Id], isPaused, pageToken);
                if (!IsPageActive(pageToken)) return;
                if (result.AffectedIds.Count == 0)
                {
                    MessageBox.Show(
                        owner,
                        BuildScheduleSkipMessage(result),
                        isPaused ? "未能暂停计划" : "未能启动计划",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                await LoadSchedulesAsync(pageToken);
                if (result.AffectedIds.Count > 0 && IsPageActive(pageToken))
                    StatusText.Text = isPaused ? "已暂停 1 条计划" : "已启动 1 条计划";
            }
            catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsPageActive(pageToken))
                    MessageBox.Show(owner, $"未能修改计划：{CliDiagnostics.Sanitize(ex.Message)}", "未能修改计划", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });

    private void DeleteScheduleButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("删除发布计划", async () =>
        {
            var pageToken = CurrentPageToken;
            if (!IsPageActive(pageToken)) return;
            if ((sender as FrameworkElement)?.DataContext is not PublishScheduleRow row) return;
            var owner = Window.GetWindow(this);
            if (!row.CanDelete)
            {
                MessageBox.Show(owner, "正在执行的计划不能删除，请等待执行结束后重试。", "无法删除计划", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(
                    owner,
                    $"确定删除计划“{row.TitleContentDisplay}”吗？\n\n只删除计划记录，不删除素材和发布记录。待执行素材没有其他计划时会恢复为“待发布”。",
                    "删除发布计划",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var result = await _store.DeleteScheduleExecutionsAsync([row.Id], pageToken);
                if (!IsPageActive(pageToken)) return;
                if (result.AffectedIds.Count == 0)
                {
                    MessageBox.Show(
                        owner,
                        BuildScheduleSkipMessage(result),
                        "未能删除计划",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                await LoadSchedulesAsync(pageToken);
                if (result.AffectedIds.Count > 0 && IsPageActive(pageToken))
                    StatusText.Text = "已删除 1 条计划";
            }
            catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (IsPageActive(pageToken))
                    MessageBox.Show(owner, $"未能删除计划：{CliDiagnostics.Sanitize(ex.Message)}", "未能删除计划", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });

    private void Scheduler_StatusChanged(PublishSchedulerStatus status)
    {
        _lastSchedulerStatus = status;
        if (!Dispatcher.CheckAccess())
        {
            UiAsync.Post(Dispatcher, () => ApplySchedulerStatus(status));
            return;
        }
        ApplySchedulerStatus(status);
    }

    private void ApplySchedulerStatus(PublishSchedulerStatus status)
    {
        if (!IsPageActive(CurrentPageToken) || !Equals(_lastSchedulerStatus, status)) return;
        SchedulerStatusText.Text = status.Paused
            ? $"计划执行器已暂停：{status.Message}"
            : status.Running ? status.Message : "计划执行器已停止";
        SchedulerStatusText.Foreground = status.Paused
            ? UiStatusBrushes.Warning
            : (Brush)FindResource("Ink");
    }

    private static bool IsInteractiveRowElement(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or ComboBox) return true;
            if (current is DataGridRow) return false;
        }
        return false;
    }
}
