using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using System.Windows;

namespace QqChannelDesk.Pages.Collection;

public partial class RunDetailWindow : Window
{
    private readonly CollectionRunRow _row;
    private readonly CollectionSchedulerService? _scheduler;
    private readonly CollectionSourceStore? _sourceStore;
    private readonly ContentLibraryStore? _contentLibrary;
    private readonly TelegramSourceAdapter? _telegramAdapter;
    private bool _itemsLoaded;

    public RunDetailWindow(
        CollectionRunRow row,
        CollectionSchedulerService? scheduler = null,
        CollectionSourceStore? sourceStore = null,
        ContentLibraryStore? contentLibrary = null,
        TelegramSourceAdapter? telegramAdapter = null)
    {
        InitializeComponent();
        _row = row;
        _scheduler = scheduler;
        _sourceStore = sourceStore;
        _contentLibrary = contentLibrary;
        _telegramAdapter = telegramAdapter;
        var record = row.Record;
        Title = $"采集批次 · {row.SourceName}";
        SummaryText.Text = $"{record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {row.ResultLabel}";
        DetailBox.Text = BuildTimeline(record, row);
        RunAgainButton.IsEnabled = row.IsTelegram ? _telegramAdapter is not null : _scheduler is not null;
        PauseSourceButton.IsEnabled = row.IsTelegram ? _telegramAdapter is not null : _sourceStore is not null;
        ViewItemsButton.IsEnabled = !row.IsTelegram && _contentLibrary is not null && record.SourceId > 0;
    }

    /// <summary>用户在窗口内再次运行或暂停来源后通知，供调用方刷新批次列表。</summary>
    public event Action? RunAgainRequested;

    public event Action? SourcePaused;

    private static string BuildTimeline(CollectionRunRecord record, CollectionRunRow row)
    {
        var endedAt = record.EndedAt?.ToLocalTime();
        var duration = (record.EndedAt - record.StartedAt) is { } span && span > TimeSpan.Zero
            ? $"{(int)span.TotalSeconds} 秒"
            : "-";
        return $"""
            来源：{row.SourceName}
            1. 批次开始 · {record.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 触发方式：{row.TriggerLabel}
            2. 采集执行 · 发现 {record.FoundCount} 条，新增 {record.NewItemCount} 条，重复 {record.DuplicateCount} 条，AI 兜底 {record.AiFallbackCount} 条
            3. 批次结束 · {endedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"} · 耗时 {duration} · 结果：{row.ResultLabel}
            错误码：{(record.ErrorCode.Length == 0 ? "-" : record.ErrorCode)}
            错误摘要：{(record.ErrorSummary.Length == 0 ? "-" : record.ErrorSummary)}
            """;
    }

    private void ViewItemsButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("查看批次新增条目", async () =>
        {
            if (!_itemsLoaded)
            {
                if (_contentLibrary is null) return;
                var record = _row.Record;
                var to = record.EndedAt ?? DateTimeOffset.Now;
                var items = await _contentLibrary.GetItemsBySourceWindowAsync(record.SourceId, record.StartedAt, to);
                ItemsBox.Text = items.Count == 0
                    ? "该批次时间窗内没有找到新增条目。"
                    : string.Join(Environment.NewLine, items.Select(item =>
                        $"{item.CollectedAt.ToLocalTime():MM-dd HH:mm} · {item.Status} · {(item.Title.Length == 0 ? "(无标题)" : item.Title)}"));
                _itemsLoaded = true;
                ItemsHeader.Visibility = Visibility.Visible;
                ItemsBox.Visibility = Visibility.Visible;
                ViewItemsButton.Content = "收起新增条目";
            }
            else
            {
                var visible = ItemsBox.Visibility == Visibility.Visible;
                ItemsHeader.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
                ItemsBox.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
                ViewItemsButton.Content = visible ? "展开新增条目" : "收起新增条目";
            }
        });

    private void RunAgainButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("再次运行采集批次", async () =>
        {
            try
            {
                if (_row.IsTelegram)
                {
                    if (_telegramAdapter is null) throw new InvalidOperationException("TG 采集适配器未初始化。");
                    await _telegramAdapter.RunNowAsync(_row.Record.SourceId);
                }
                else
                {
                    if (_scheduler is null) throw new InvalidOperationException("统一采集调度器未初始化。");
                    await _scheduler.RunSourceAsync(_row.Record.SourceId, "retry");
                }

                StatusText.Text = "已再次运行采集，批次完成后列表会自动刷新。";
                RunAgainRequested?.Invoke();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusText.Text = $"再次运行未完成：{CliDiagnostics.Sanitize(exception.Message)}";
            }
        });

    private void PauseSourceButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("暂停采集来源", async () =>
        {
            try
            {
                if (_row.IsTelegram)
                {
                    if (_telegramAdapter is null) throw new InvalidOperationException("TG 采集适配器未初始化。");
                    await _telegramAdapter.SetEnabledAsync(_row.Record.SourceId, enabled: false);
                }
                else
                {
                    if (_sourceStore is null) throw new InvalidOperationException("来源存储未初始化。");
                    await _sourceStore.SetSourceEnabledAsync(_row.Record.SourceId, enabled: false);
                }

                StatusText.Text = "来源已暂停，可在来源管理页签手动恢复。";
                PauseSourceButton.IsEnabled = false;
                SourcePaused?.Invoke();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusText.Text = $"暂停来源未完成：{CliDiagnostics.Sanitize(exception.Message)}";
            }
        });

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DetailBox.Text);
            StatusText.Text = "已复制脱敏诊断信息。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制失败：{CliDiagnostics.Sanitize(exception.Message)}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
