using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace QqChannelDesk.Pages.Collection;

public partial class RunsView : UserControl
{
    private readonly CollectionSourceStore _sourceStore;
    private readonly CollectionRunStore _runStore;
    private readonly TelegramSourceAdapter? _telegramAdapter;
    private readonly CollectionSchedulerService? _scheduler;
    private readonly ContentLibraryStore? _contentLibrary;
    private readonly ObservableCollection<CollectionRunRow> _rows = [];
    private CancellationTokenSource? _lifetime;

    public RunsView(
        CollectionSourceStore sourceStore,
        CollectionRunStore runStore,
        TelegramSourceAdapter? telegramAdapter = null,
        CollectionSchedulerService? scheduler = null,
        ContentLibraryStore? contentLibrary = null)
    {
        InitializeComponent();
        _sourceStore = sourceStore;
        _runStore = runStore;
        _telegramAdapter = telegramAdapter;
        _scheduler = scheduler;
        _contentLibrary = contentLibrary;
        RunsGrid.ItemsSource = _rows;
        Loaded += (_, _) => StartLifetime();
        Unloaded += (_, _) =>
        {
            _lifetime?.Cancel();
            _lifetime = null;
        };
    }

    private CancellationToken Token => _lifetime?.Token ?? new CancellationToken(canceled: true);

    private void StartLifetime()
    {
        _lifetime?.Cancel();
        _lifetime = new CancellationTokenSource();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (!IsLoaded) return;
        var token = Token;
        try
        {
            var sourceNames = (await _sourceStore.GetSourcesAsync(token)).ToDictionary(item => item.Id, item => item.Name);
            if (_telegramAdapter is not null)
            {
                foreach (var source in await _telegramAdapter.GetSourcesAsync(token))
                    sourceNames[source.Id] = source.Name;
            }

            var runs = new List<CollectionRunRecord>(await _runStore.GetRunsAsync(cancellationToken: token));
            if (_telegramAdapter is not null)
                runs.AddRange(await _telegramAdapter.GetRunHistoryAsync(cancellationToken: token));
            if (token.IsCancellationRequested || !IsLoaded) return;
            _rows.Clear();
            foreach (var run in runs.OrderByDescending(item => item.StartedAt).ThenByDescending(item => item.Id).Take(300))
                _rows.Add(new CollectionRunRow(run, sourceNames.GetValueOrDefault(run.SourceId, "已删除来源")));
            StatsText.Text = $"最近 {_rows.Count} 个批次";
            StatusText.Text = _rows.Count == 0 ? "还没有采集记录。" : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"加载采集记录失败：{CliDiagnostics.Sanitize(exception.Message)}";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => UiAsync.Observe("刷新采集记录", RefreshAsync());

    private void DetailButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CollectionRunRow row) return;
        var window = new RunDetailWindow(row, _scheduler, _sourceStore, _contentLibrary, _telegramAdapter)
        {
            Owner = Window.GetWindow(this)
        };
        window.RunAgainRequested += () => UiAsync.Observe("刷新采集记录", RefreshAsync());
        window.SourcePaused += () => UiAsync.Observe("刷新采集记录", RefreshAsync());
        window.ShowDialog();
    }
}

public sealed class CollectionRunRow : INotifyPropertyChanged
{
    public CollectionRunRow(CollectionRunRecord record, string sourceName)
    {
        Record = record;
        SourceName = sourceName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CollectionRunRecord Record { get; private set; }
    public string SourceName { get; private set; }
    // TG 适配器把旧计划映射为负数 ID，与公开网页来源的正数 ID 区分。
    public bool IsTelegram => Record.SourceId < 0;
    public string StartedLabel => Record.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string TriggerLabel => Record.Trigger switch
    {
        "scheduled" => "定时",
        "retry" => "重试",
        "manual" => "手动",
        // 早期版本写入过中文触发方式，原样展示避免误读为“手动”。
        _ => Record.Trigger.Length == 0 ? "-" : Record.Trigger
    };
    public string ResultLabel => Record.Result switch
    {
        "succeeded" => "成功",
        "partial" => "部分成功",
        "ai-degraded" => "AI降级",
        "cancelled" => "已取消",
        "running" => "运行中",
        "failed" => "失败",
        // 早期版本写入过中文结果，原样展示避免误读为“失败”。
        _ => Record.Result.Length == 0 ? "-" : Record.Result
    };
    public string DurationLabel => (Record.EndedAt - Record.StartedAt) is { } duration && duration > TimeSpan.Zero
        ? $"{(int)duration.TotalSeconds}秒"
        : "-";
    public string ErrorSummary => Record.ErrorSummary;

    public void Update(CollectionRunRecord record, string sourceName)
    {
        Record = record;
        SourceName = sourceName;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
