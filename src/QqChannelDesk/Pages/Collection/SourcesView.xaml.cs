using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace QqChannelDesk.Pages.Collection;

public partial class SourcesView : UserControl
{
    private readonly CollectionSourceStore _sourceStore;
    private readonly CollectionRunStore _runStore;
    private readonly CollectionSchedulerService? _scheduler;
    private readonly TelegramSourceAdapter? _telegramAdapter;
    private readonly ContentLibraryStore? _contentLibrary;
    private readonly ObservableCollection<CollectionSourceRow> _rows = [];
    private CancellationTokenSource? _lifetime;
    public event Action<string>? AdvancedTabRequested;

    /// <summary>对 TG 来源的启停/立即采集会改动旧 TG 定时配置；宿主页面据此联动刷新 TG 定时页签。</summary>
    public event Action? TelegramScheduleChanged;

    public SourcesView(
        CollectionSourceStore sourceStore,
        CollectionRunStore runStore,
        CollectionSchedulerService? scheduler = null,
        TelegramSourceAdapter? telegramAdapter = null,
        ContentLibraryStore? contentLibrary = null)
    {
        InitializeComponent();
        _sourceStore = sourceStore;
        _runStore = runStore;
        _scheduler = scheduler;
        _telegramAdapter = telegramAdapter;
        _contentLibrary = contentLibrary;
        SourcesGrid.ItemsSource = _rows;
        if (_telegramAdapter is not null) _telegramAdapter.RunCompleted += TelegramRunCompleted;
        // 统一调度器事件可能来自后台线程；运行中的行置灰、结束后解锁并刷新。
        if (_scheduler is not null)
        {
            _scheduler.RunStarted += CollectionSchedulerRunStarted;
            _scheduler.RunCompleted += CollectionSchedulerRunCompleted;
        }
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
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        _ = RefreshAsync();
    }

    private void TelegramRunCompleted(TelegramSourceRunCompleted completed) =>
        Dispatcher.Invoke(() => UiAsync.Observe("刷新采集来源", RefreshAsync()));

    private void CollectionSchedulerRunStarted(CollectionSchedulerRunStarted started)
    {
        Dispatcher.Invoke(() =>
        {
            var row = _rows.FirstOrDefault(item => item.Id == started.SourceId);
            if (row is not null) row.IsRunning = true;
        });
    }

    private void CollectionSchedulerRunCompleted(CollectionSchedulerRunCompleted completed)
    {
        Dispatcher.Invoke(() =>
        {
            var row = _rows.FirstOrDefault(item => item.Id == completed.SourceId);
            if (row is not null) row.IsRunning = false;
            UiAsync.Observe("刷新采集来源", RefreshAsync());
        });
    }

    public async Task RefreshAsync()
    {
        if (!IsLoaded) return;
        var token = Token;
        try
        {
            var sources = await _sourceStore.GetSourcesAsync(token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            var telegram = _telegramAdapter is null ? [] : await _telegramAdapter.GetSourcesAsync(token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            var pendingCounts = _contentLibrary is null
                ? new Dictionary<long, int>()
                : await _contentLibrary.GetPendingCountsBySourceAsync(token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            var todayStart = new DateTimeOffset(DateTime.Now.Date);
            var todayPending = _contentLibrary is null ? 0 : await _contentLibrary.GetTodayPendingCountAsync(todayStart, token);
            var todaySucceeded = await _runStore.GetTodaySucceededCountAsync(todayStart, token);
            if (token.IsCancellationRequested || !IsLoaded) return;
            _rows.Clear();
            foreach (var source in sources.Concat(telegram).OrderBy(item => item.SourceType).ThenBy(item => item.Name))
                _rows.Add(new CollectionSourceRow(source, source.SourceType == "telegram" ? 0 : pendingCounts.GetValueOrDefault(source.Id)));
            var enabled = _rows.Count(item => item.Source.Enabled);
            var failed = _rows.Count(item => item.Source.HealthStatus is "failed" or "degraded");
            StatsText.Text = $"来源 {_rows.Count} · 启用 {enabled} · 今日待审 {todayPending} · 异常 {failed} · 今日成功 {todaySucceeded}";
            StatusText.Text = _rows.Count == 0 ? "还没有来源。选择 RSS、网页栏目或单页链接开始。" : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"加载来源失败：{CliDiagnostics.Sanitize(exception.Message)}";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => UiAsync.Observe("刷新采集来源", RefreshAsync());

    private void AdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tab) AdvancedTabRequested?.Invoke(tab);
    }

    private void NewSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new NewSourceWindow { Owner = Window.GetWindow(this) };
        window.TgScheduleRequested += () =>
        {
            window.Close();
            AdvancedTabRequested?.Invoke("tg-schedule");
        };
        if (window.ShowDialog() != true) return;
        UiAsync.Run("新建采集来源", async () =>
        {
            try
            {
                var id = await _sourceStore.CreateSourceAsync(window.Draft, Token);
                await RefreshAsync();
                if (window.StartEnabled)
                    await RunSourceAsync(id, "已创建并开始首次采集。", Token);
                else
                    SetStatus("来源已保存。");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await RefreshAsync();
                SetStatus($"新建来源失败：{CliDiagnostics.Sanitize(exception.Message)}");
            }
        });
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CollectionSourceRow row) return;
        UiAsync.Run("立即采集", () => RunSourceAsync(row.Source.Id, "采集批次已完成。", Token));
    }

    private async Task RunSourceAsync(long viewId, string successText, CancellationToken cancellationToken)
    {
        try
        {
            if (viewId < 0)
            {
                if (_telegramAdapter is null) throw new InvalidOperationException("TG 采集调度器未初始化。");
                // TG 立即采集同步等待整批完成：运行期间置灰该行，结束后解锁。
                var telegramRow = _rows.FirstOrDefault(item => item.Id == viewId);
                if (telegramRow is not null) telegramRow.IsRunning = true;
                try
                {
                    await _telegramAdapter.RunNowAsync(viewId, cancellationToken);
                }
                finally
                {
                    if (telegramRow is not null) telegramRow.IsRunning = false;
                }

                TelegramScheduleChanged?.Invoke();
            }
            else
            {
                if (_scheduler is null) throw new InvalidOperationException("统一采集调度器未初始化。");
                await _scheduler.RunSourceAsync(viewId, "manual", cancellationToken);
            }

            await RefreshAsync();
            SetStatus(successText);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RefreshAsync();
            SetStatus($"采集未完成：{CliDiagnostics.Sanitize(exception.Message)}");
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // “更多”菜单项不在行的视觉树里，通过 ContextMenu 的 PlacementTarget 找回所在行。
    private static CollectionSourceRow? RowFromMenu(object sender)
    {
        if (sender is not MenuItem menuItem ||
            ItemsControl.ItemsControlFromItemContainer(menuItem) is not ContextMenu menu) return null;
        return (menu.PlacementTarget as FrameworkElement)?.DataContext as CollectionSourceRow;
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CollectionSourceRow row) return;
        var enabled = !row.Source.Enabled;
        UiAsync.Run("切换来源状态", async () =>
        {
            if (row.IsTelegram)
            {
                if (_telegramAdapter is null) throw new InvalidOperationException("TG 来源适配器未初始化。");
                await _telegramAdapter.SetEnabledAsync(row.Source.Id, enabled, Token);
                TelegramScheduleChanged?.Invoke();
            }
            else
            {
                await _sourceStore.SetSourceEnabledAsync(row.Source.Id, enabled, Token);
            }

            await RefreshAsync();
            SetStatus(enabled ? "来源已启用。" : "来源已暂停。");
        });
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { IsTelegram: false } row) return;
        var window = new NewSourceWindow(row.Source) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        UiAsync.Run("保存采集来源", async () =>
        {
            await _sourceStore.UpdateSourceAsync(window.Draft, Token);
            await RefreshAsync();
            SetStatus("来源已更新。");
        });
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { IsTelegram: false } row) return;
        var owner = Window.GetWindow(this);
        if (owner is null || MessageBox.Show(owner, $"确定删除来源“{row.Source.Name}”吗？\n仅停用来源，不会删除已采集内容。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UiAsync.Run("删除采集来源", async () =>
        {
            await _sourceStore.SoftDeleteSourceAsync(row.Source.Id, Token);
            await RefreshAsync();
            SetStatus("来源已删除。");
        });
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (RowFromMenu(sender) is not { } row) return;
        UiAsync.Run("查看采集记录", async () =>
        {
            var runs = row.IsTelegram
                ? _telegramAdapter is null ? [] : await _telegramAdapter.GetRunHistoryAsync(50, row.Source.Id, cancellationToken: Token)
                : await _runStore.GetRunsAsync(row.Source.Id, cancellationToken: Token);
            var detail = string.Join(Environment.NewLine, runs.Take(20).Select(item =>
                $"{item.StartedAt.ToLocalTime():MM-dd HH:mm} · {item.Result} · 新增 {item.NewItemCount} · 重复 {item.DuplicateCount}" +
                (item.ErrorSummary.Length == 0 ? "" : $" · {item.ErrorSummary}")));
            ShowTextWindow($"采集记录 · {row.Source.Name}", detail.Length == 0 ? "暂无采集记录。" : detail);
        });
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void ShowTextWindow(string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 720,
            Height = 480,
            Content = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(14)
            }
        };
        window.Show();
    }
}

public sealed class CollectionSourceRow : INotifyPropertyChanged
{
    public CollectionSourceRow(CollectionSource source, int pendingCount = 0)
    {
        Source = source;
        PendingCount = pendingCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CollectionSource Source { get; private set; }
    public long Id => Source.Id;
    public bool IsTelegram => Source.SourceType == "telegram";
    public string Name => Source.Name;
    public string TypeLabel => Source.SourceType switch
    {
        "rss" => "RSS",
        "web-column" => "网页栏目",
        "telegram" => "TG",
        _ => "单页"
    };
    public string EntryUrl => Source.EntryUrl;
    public string FrequencyLabel => Source.CronExpression.Length > 0 ? Source.CronExpression : $"每 {Source.FrequencyMinutes} 分钟";
    public string LastAttemptLabel => Source.LastAttemptAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
    public string LastSuccessLabel => Source.LastSuccessAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
    public string NextRunLabel => Source.NextRunAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-";
    public string HealthLabel => Source.HealthStatus switch
    {
        "healthy" => "正常",
        "degraded" => "降级",
        "failed" => "失败",
        _ => "已停用"
    };
    public string HealthToolTip => Source.HealthMessage.Length == 0 ? HealthLabel : $"{HealthLabel}：{Source.HealthMessage}";
    public string ToggleLabel => Source.Enabled ? "暂停" : "启用";
    public int PendingCount { get; private set; }
    public string PendingLabel => Source.SourceType == "telegram" ? "-" : PendingCount.ToString();
    public string PendingSummary => IsTelegram ? "" : $"待审 {PendingCount} 条";

    private bool _isRunning;

    /// <summary>采集批次执行中：行内操作置灰，批次结束后恢复。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public bool CanAct => !IsRunning;
    public bool CanEdit => !IsTelegram;
    public bool CanDelete => !IsTelegram;

    public void Update(CollectionSource source)
    {
        Source = source;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
