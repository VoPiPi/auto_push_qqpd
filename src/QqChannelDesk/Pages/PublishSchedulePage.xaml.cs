using System.Windows.Media;
using QqChannelDesk.Services;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows;

namespace QqChannelDesk.Pages;

public partial class PublishSchedulePage : UserControl
{
    private const int PageSize = 100;
    private readonly ContentLibraryStore _store;
    private readonly PublishScheduler _scheduler;
    private bool _loading;
    private IReadOnlyList<ScheduleExecutionRecord> _allSchedules = [];
    private int _currentPage = 1;

    public PublishSchedulePage(ContentLibraryStore store, PublishScheduler scheduler)
    {
        InitializeComponent();
        _store = store;
        _scheduler = scheduler;
        _scheduler.StatusChanged += Scheduler_StatusChanged;
        SetSchedulerStatus(_scheduler.Status);
        Loaded += async (_, _) => await LoadSchedulesAsync();
    }

    public async Task LoadSchedulesAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusText.Text = "正在读取发布计划…";
        StatusText.Foreground = (Brush)FindResource("Muted");
        try
        {
            _allSchedules = await _store.GetScheduleExecutionsAsync();
            _currentPage = 1;
            RenderCurrentPage();
            EmptyTitle.Text = "暂无发布计划";
            EmptyMessage.Text = "在素材仓库设置未来发布时间后，计划会显示在这里；执行结果会保留在列表中。";
            LastRefreshText.Text = $"刷新于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _allSchedules = [];
            SchedulesGrid.ItemsSource = null;
            UpdatePagination();
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "发布计划读取失败";
            EmptyMessage.Text = "请刷新重试，或检查本地数据库是否可访问。";
            StatusText.Text = $"读取发布计划失败：{CliDiagnostics.Sanitize(ex.Message)}";
            StatusText.Foreground = Brushes.Firebrick;
            LastRefreshText.Text = "刷新失败";
        }
        finally { _loading = false; }
    }

    private void RenderCurrentPage()
    {
        var pageCount = GetPageCount(_allSchedules.Count);
        _currentPage = Math.Clamp(_currentPage, 1, pageCount);
        SchedulesGrid.ItemsSource = _allSchedules.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToArray();
        StatusText.Text = $"共 {_allSchedules.Count} 条";
        EmptyState.Visibility = _allSchedules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePagination(pageCount);
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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadSchedulesAsync();

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        try
        {
            StatusText.Text = "正在检查到期计划…";
            var result = await _scheduler.CheckNowAsync();
            StatusText.Text = result.EnvironmentReady
                ? result.StartedCount == 0 ? "立即检查完成，没有可执行计划" : $"已启动 {result.StartedCount} 条计划"
                : $"计划未执行：{result.Message}";
            await LoadSchedulesAsync();
        }
        finally
        {
            if (sender is Button checkButton) checkButton.IsEnabled = true;
        }
    }

    private async void CancelScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ScheduleExecutionRecord record) return;
        if (record.Status != ScheduleExecutionStatus.Pending)
        {
            MessageBox.Show(Window.GetWindow(this), "只有等待执行的计划可以取消；已执行或已结束的记录请保留用于核对。", "无法取消计划", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var owner = Window.GetWindow(this);
        if (MessageBox.Show(owner,
                $"确定取消“{record.Title}”的发布时间计划吗？\n\n取消后素材会回到“待发布”，不会删除素材。",
                "取消发布计划", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var cancelled = await _store.CancelMaterialScheduleAsync(record.MaterialId);
            if (!cancelled)
            {
                MessageBox.Show(owner, "计划已被其他操作修改，请刷新后重试。", "取消计划失败", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await LoadSchedulesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"取消计划失败：{CliDiagnostics.Sanitize(ex.Message)}", "取消计划失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Scheduler_StatusChanged(PublishSchedulerStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetSchedulerStatus(status));
            return;
        }
        SetSchedulerStatus(status);
    }

    private void SetSchedulerStatus(PublishSchedulerStatus status)
    {
        SchedulerStatusText.Text = status.Paused
            ? $"计划执行器已暂停：{status.Message}"
            : status.Running ? status.Message : "计划执行器已停止";
        SchedulerStatusText.Foreground = status.Paused ? Brushes.Firebrick : (Brush)FindResource("Ink");
    }
}
