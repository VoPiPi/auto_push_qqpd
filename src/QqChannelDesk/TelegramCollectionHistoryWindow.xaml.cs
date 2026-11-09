using QqChannelDesk.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QqChannelDesk;

public partial class TelegramCollectionHistoryWindow : Window
{
    private const int HistoryLimit = TelegramCollectionStore.RunHistoryRetentionLimit;
    private readonly TelegramCollectionStore _store;
    private readonly long? _initialScheduleId;
    private CancellationTokenSource? _loadCancellation;
    private int _loadVersion;
    private bool _scheduleFiltersLoaded;

    public TelegramCollectionHistoryWindow(
        TelegramCollectionStore store,
        long? scheduleId = null)
    {
        InitializeComponent();
        _store = store;
        _initialScheduleId = scheduleId is > 0 ? scheduleId : null;
        Loaded += TelegramCollectionHistoryWindow_Loaded;
        Closed += TelegramCollectionHistoryWindow_Closed;
    }

    private void TelegramCollectionHistoryWindow_Loaded(object sender, RoutedEventArgs e) =>
        UiAsync.Run("加载 TG 采集历史", LoadAsync);

    private void TelegramCollectionHistoryWindow_Closed(object? sender, EventArgs e)
    {
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        cancellation?.Cancel();
    }

    private async Task LoadAsync()
    {
        if (!_scheduleFiltersLoaded)
        {
            await LoadScheduleFiltersAsync();
            _scheduleFiltersLoaded = true;
        }
        await LoadHistoryAsync();
    }

    private async Task LoadScheduleFiltersAsync()
    {
        try
        {
            var schedules = await _store.GetAsync();
            var options = new List<ScheduleFilterOption>
            {
                new(null, "全部计划")
            };
            options.AddRange(schedules.Select(schedule =>
                new ScheduleFilterOption(schedule.Id, $"当前计划：{schedule.ChannelName}")));
            ScheduleFilterBox.ItemsSource = options;
            ScheduleFilterBox.SelectedItem = options.FirstOrDefault(option => option.Id == _initialScheduleId)
                ?? options[0];
        }
        catch (Exception ex)
        {
            ScheduleFilterBox.ItemsSource = new[] { new ScheduleFilterOption(null, "全部计划") };
            ScheduleFilterBox.SelectedIndex = 0;
            SetStatus($"读取计划筛选失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
    }

    private async Task LoadHistoryAsync()
    {
        var previous = _loadCancellation;
        previous?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var version = ++_loadVersion;
        SetLoading(true);

        try
        {
            var scheduleId = (ScheduleFilterBox.SelectedItem as ScheduleFilterOption)?.Id;
            var status = (StatusFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var records = await _store.GetRunHistoryAsync(
                HistoryLimit,
                SearchBox.Text.Trim(),
                scheduleId,
                status,
                cancellation.Token);
            if (version != _loadVersion || cancellation.IsCancellationRequested) return;

            HistoryGrid.ItemsSource = records;
            UpdateEmptyState(records.Count);
            SetStatus(records.Count == 0
                ? "没有符合条件的采集记录。"
                : $"共显示 {records.Count} 条记录，刷新于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}。",
                true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version != _loadVersion || cancellation.IsCancellationRequested) return;
            HistoryGrid.ItemsSource = null;
            EmptyTitle.Text = "读取失败";
            EmptyMessage.Text = "请检查数据库状态后刷新重试。";
            EmptyState.Visibility = Visibility.Visible;
            SetStatus($"读取采集历史失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loadCancellation = null;
                SetLoading(false);
            }
            cancellation.Dispose();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("刷新 TG 采集历史", LoadAsync);

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        StatusFilterBox.SelectedIndex = 0;
        SearchBox.Clear();
        if (ScheduleFilterBox.ItemsSource is IEnumerable<ScheduleFilterOption> options)
            ScheduleFilterBox.SelectedItem = options.FirstOrDefault(option => option.Id == _initialScheduleId)
                ?? options.FirstOrDefault();
        UiAsync.Run("重置并刷新 TG 采集历史", LoadHistoryAsync);
    }

    private void HistoryFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        UiAsync.Run("筛选 TG 采集历史", LoadHistoryAsync);
    }

    private void UpdateEmptyState(int count)
    {
        if (count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyTitle.Text = "没有符合条件的采集记录";
        EmptyMessage.Text = "调整计划、状态或关键词后重试；采集完成后记录会自动写入这里。";
        EmptyState.Visibility = Visibility.Visible;
    }

    private void SetLoading(bool loading)
    {
        RefreshButton.IsEnabled = !loading;
        ScheduleFilterBox.IsEnabled = !loading;
        StatusFilterBox.IsEnabled = !loading;
        SearchBox.IsEnabled = !loading;
        if (loading) SetStatus("正在查询采集历史…", true);
    }

    private void SetStatus(string message, bool normal)
    {
        StatusText.Text = message;
        StatusText.Foreground = normal ? (Brush)FindResource("Muted") : UiStatusBrushes.Danger;
    }

    private sealed record ScheduleFilterOption(long? Id, string DisplayName);
}
