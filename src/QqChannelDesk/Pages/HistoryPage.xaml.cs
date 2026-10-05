using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using System.Windows.Documents;
using System.Windows.Media;
using QqChannelDesk.Services;

namespace QqChannelDesk.Pages;

public partial class HistoryPage : UserControl
{
    private const int PageSize = 100;
    private readonly PublishHistoryStore _store;
    private IReadOnlyList<PublishRecord> _allRecords = [];
    private IReadOnlyList<PublishRecord> _filteredRecords = [];
    private int _currentPage = 1;

    public HistoryPage(PublishHistoryStore store)
    {
        InitializeComponent();
        _store = store;
        SetDefaultDateRange();
    }

    public async Task LoadRecordsAsync()
    {
        StatusText.Text = "正在读取发布记录…";
        StatusText.Foreground = (Brush)FindResource("Muted");
        try
        {
            _allRecords = await _store.GetRecentAsync();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            RecordsGrid.ItemsSource = null;
            _filteredRecords = [];
            UpdatePagination();
            StatusText.Text = $"读取发布记录失败：{CliDiagnostics.Sanitize(ex.Message)}";
            StatusText.Foreground = Brushes.Firebrick;
            EmptyState.Visibility = Visibility.Collapsed;
        }
    }

    private async void HistoryRefreshButton_Click(object sender, RoutedEventArgs e) => await LoadRecordsAsync();
    private void HistoryFilterButton_Click(object sender, RoutedEventArgs e) => ApplyFilters();

    private void HistoryResetButton_Click(object sender, RoutedEventArgs e)
    {
        SetDefaultDateRange();
        TypeFilterComboBox.SelectedIndex = 0;
        KeywordFilterTextBox.Clear();
        ApplyFilters();
    }

    private void SetDefaultDateRange()
    {
        var today = DateTime.Today;
        StartDatePicker.SelectedDate = today.AddDays(-6);
        EndDatePicker.SelectedDate = today;
    }

    private void HistoryFilter_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        ApplyFilters();
        e.Handled = true;
    }

    private void ApplyFilters()
    {
        var startDate = StartDatePicker.SelectedDate;
        var endDate = EndDatePicker.SelectedDate;
        if (startDate.HasValue && endDate.HasValue && startDate.Value.Date > endDate.Value.Date)
        {
            StatusText.Text = "开始日期不能晚于结束日期，请调整筛选条件。";
            StatusText.Foreground = Brushes.Firebrick;
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        var type = TypeFilterComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            ? tag switch { "文本" => FeedType.Text, "图片" => FeedType.Image, "视频" => FeedType.Video, _ => (FeedType?)null }
            : null;
        IReadOnlyList<PublishRecord> filtered;
        try
        {
            filtered = PublishHistoryFilter.Apply(_allRecords,
                new PublishHistoryFilterCriteria(startDate, endDate, type, KeywordFilterTextBox.Text));
        }
        catch (Exception ex)
        {
            RecordsGrid.ItemsSource = null;
            StatusText.Text = $"筛选发布记录失败：{CliDiagnostics.Sanitize(ex.Message)}";
            StatusText.Foreground = Brushes.Firebrick;
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        _filteredRecords = filtered;
        _currentPage = 1;
        RenderCurrentPage();
    }

    private void RenderCurrentPage()
    {
        var pageCount = GetPageCount(_filteredRecords.Count);
        _currentPage = Math.Clamp(_currentPage, 1, pageCount);
        var page = _filteredRecords.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToArray();
        RecordsGrid.ItemsSource = page;
        StatusText.Foreground = (Brush)FindResource("Muted");
        if (_allRecords.Count == 0)
        {
            StatusText.Text = "共 0 条";
            EmptyTitle.Text = "暂无发布记录";
            EmptyMessage.Text = "发布完成后，记录会显示在这里。";
            EmptyState.Visibility = Visibility.Visible;
        }
        else if (_filteredRecords.Count == 0)
        {
            StatusText.Text = "共 0 条";
            EmptyTitle.Text = "没有符合条件的记录";
            EmptyMessage.Text = "调整筛选条件后重试，或点击“重置”查看全部记录。";
            EmptyState.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Text = $"共 {_filteredRecords.Count} 条";
            EmptyState.Visibility = Visibility.Collapsed;
        }
        UpdatePagination(pageCount);
        LastRefreshText.Text = $"刷新于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    private void UpdatePagination(int? pageCount = null)
    {
        var pages = pageCount ?? GetPageCount(_filteredRecords.Count);
        PageText.Text = $"{_currentPage} / {pages}";
        PreviousPageButton.IsEnabled = _currentPage > 1 && _filteredRecords.Count > 0;
        NextPageButton.IsEnabled = _currentPage < pages && _filteredRecords.Count > 0;
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
        if (_currentPage >= GetPageCount(_filteredRecords.Count)) return;
        _currentPage++;
        RenderCurrentPage();
    }

    private void RecordsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not PublishRecord record) return;
        var details = $"状态：{record.StatusName}\n开始：{record.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n完成：{record.CompletedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "未完成"}\n类型：{record.TypeName}\n频道：{record.GuildName}（{record.GuildId}）\n版块：{record.ChannelName}（{record.ChannelId}）\n标题：{(string.IsNullOrWhiteSpace(record.Title) ? "（无）" : record.Title)}\n媒体：{(record.MediaNames.Count == 0 ? "（无）" : string.Join("、", record.MediaNames))}\n结果：{record.Summary}\n错误类别：{(string.IsNullOrWhiteSpace(record.ErrorCategory) ? "（无）" : record.ErrorCategory)}\n帖子 ID：{(string.IsNullOrWhiteSpace(record.PostId) ? "（无）" : record.PostId)}\n链接：{(string.IsNullOrWhiteSpace(record.PostUrl) ? "（无）" : record.PostUrl)}";
        var owner = Window.GetWindow(this);
        if (string.IsNullOrWhiteSpace(record.PostUrl))
        {
            MessageBox.Show(owner, details, "发布记录详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(owner, details + "\n\n是否打开帖子链接？", "发布记录详情", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            OpenPostUrl(record.PostUrl);
    }

    private void OpenPostUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { Tag: string url } && !string.IsNullOrWhiteSpace(url)) OpenPostUrl(url);
    }

    private void OpenPostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            MessageBox.Show(Window.GetWindow(this), "帖子链接无效，无法打开。", "无法打开", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), $"无法打开帖子链接：{CliDiagnostics.Sanitize(ex.Message)}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
