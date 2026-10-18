using QqChannelDesk.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QqChannelDesk.Pages;

public partial class ContentCollectionPage : UserControl
{
    private const int PageSize = 100;
    private readonly ContentLibraryStore _store;
    private readonly ProxyPoolStore _proxyPoolStore;
    private readonly ProxyPoolService _proxyPoolService;
    private readonly PublicArticleFetcher _fetcher = new();
    private readonly TelegramCollectorService _telegramCollector = new();
    private readonly TelegramMaterializer _telegramMaterializer;
    private readonly List<string> _pendingMedia = [];
    private CollectedItem? _selectedItem;
    private IReadOnlyList<CollectedItem> _allItems = [];
    private int _itemsPage = 1;
    private int _itemsLoadVersion;
    private string _parseError = "";
    private readonly ObservableCollection<TelegramTempItem> _tempItems = [];
    private readonly ObservableCollection<string> _channelHistory = [];
    private readonly WebProxyProviderRegistry _proxyRegistry = new();
    private string _proxyCookie = "";
    private CancellationTokenSource? _collectCts;
    private bool _tgPermissionConfirmed;
    private bool _syncingTgSelection;
    private CheckBox? _tgSelectionClickCheckBox;
    private bool _tgSelectionClickWasChecked;

    private readonly ObservableCollection<ProxySource> _proxySources = [];
    private readonly ObservableCollection<ProxyNodeRow> _proxyNodes = [];
    private bool _syncingProxySelection;
    private CheckBox? _proxySelectionClickCheckBox;
    private bool _proxySelectionClickWasChecked;
    private readonly TelegramCollectionStore? _telegramCollectionStore;
    private readonly TelegramCollectionScheduler? _telegramCollectionScheduler;
    private readonly ChannelSyncService? _channelSync;
    private readonly ObservableCollection<TelegramScheduleRow> _telegramSchedules = [];
    private TelegramCollectionSchedule? _editingTelegramSchedule;
    private string _scheduleCronExpression = "0 0 * * *";
    private string _scheduleKeyword = "";
    private IReadOnlyList<TelegramDistributionRuleDraft> _scheduleRules = [];
    private bool _scheduleAutoScheduleEnabled;
    private int _scheduleAutoScheduleDelayMinutes = 30;
    private int _scheduleAutoScheduleMaxPerRun = 20;
    // 每一行采集各自持有一个令牌：只有正在采集（或排队等待执行）的那一行会被锁定。
    private readonly Dictionary<long, CancellationTokenSource> _telegramScheduleRuns = [];
    private readonly HashSet<long> _runningScheduleIds = [];
    private CancellationTokenSource? _telegramScheduleCheckCts;
    private int _telegramSchedulesLoadVersion;
    private bool _schedulerEventsSubscribed;
    private int _proxySourcesLoadVersion;
    private int _proxyNodesLoadVersion;
    private CancellationTokenSource? _pageLifetimeCts;

    public ContentCollectionPage(ContentLibraryStore? store = null, ProxyPoolStore? proxyPoolStore = null, TelegramCollectionStore? telegramCollectionStore = null, TelegramCollectionScheduler? telegramCollectionScheduler = null, ChannelSyncService? channelSync = null)
    {
        InitializeComponent();
        _store = store ?? new ContentLibraryStore();
        _proxyPoolStore = proxyPoolStore ?? new ProxyPoolStore();
        _proxyPoolService = new ProxyPoolService(_proxyPoolStore);
        _telegramMaterializer = new TelegramMaterializer(_telegramCollector, _store);
        _telegramCollectionStore = telegramCollectionStore;
        _telegramCollectionScheduler = telegramCollectionScheduler;
        _channelSync = channelSync;
        TgGrid.ItemsSource = _tempItems;
        TgChannelBox.ItemsSource = _channelHistory;
        TgProxyBox.ItemsSource = _proxyRegistry.Options;
        TgProxyBox.SelectedIndex = 0;
        TgLimitBox.Text = "20";
        UpdateTgSelectionCount();
        ProxySourceBox.ItemsSource = _proxySources;
        ProxyNodesGrid.ItemsSource = _proxyNodes;
        TgSchedulesGrid.ItemsSource = _telegramSchedules;
        TgScheduleProxyBox.ItemsSource = _proxyRegistry.Options;
        TgScheduleProxyBox.SelectedIndex = 0;
        TgScheduleLimitBox.Text = "20";
        ProxyConcurrencyBox.Text = ProxyPoolService.DefaultConcurrency.ToString();
        Loaded += (_, _) => StartPageLifetime();
        Unloaded += (_, _) => EndPageLifetime();
    }

    private CancellationToken CurrentPageToken => _pageLifetimeCts?.Token ?? new CancellationToken(canceled: true);

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

        SubscribeSchedulerEvents();
        SetTgBusy(false);
        SetProxyBusy(false);
        TgScheduleCheckButton.IsEnabled = true;
        var token = lifetime.Token;
        UiAsync.Run("内容采集初始化收件箱", () => LoadItemsAsync(token));
        UiAsync.Run("内容采集初始化代理源", () => LoadProxySourcesAsync(token));
        UiAsync.Run("内容采集初始化定时任务", () => LoadTelegramSchedulesAsync(cancellationToken: token));
    }

    private void EndPageLifetime()
    {
        UnsubscribeSchedulerEvents();
        var lifetime = _pageLifetimeCts;
        _pageLifetimeCts = null;
        lifetime?.Cancel();

        var collect = _collectCts;
        _collectCts = null;
        collect?.Cancel();

        var scheduleCheck = _telegramScheduleCheckCts;
        _telegramScheduleCheckCts = null;
        scheduleCheck?.Cancel();

        foreach (var scheduleId in _telegramScheduleRuns.Keys.ToArray())
            CancelTelegramScheduleRun(scheduleId);
    }

    public Task LoadItemsAsync() => LoadItemsAsync(CurrentPageToken);

    private async Task LoadItemsAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_itemsLoadVersion;
        try
        {
            var status = (ItemStatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
            var items = await _store.GetItemsAsync(
                ItemSearchBox.Text,
                status is "全部状态" ? null : status,
                cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _itemsLoadVersion) return;
            _allItems = items;
            _itemsPage = 1;
            RenderItemsPage();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken) || version != _itemsLoadVersion) return;
            _allItems = [];
            ItemsGrid.ItemsSource = null;
            UpdateItemsPagination();
            ItemsStatus.Text = $"未能读取收件箱，请刷新重试：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private void RenderItemsPage()
    {
        var pageCount = GetPageCount(_allItems.Count);
        _itemsPage = Math.Clamp(_itemsPage, 1, pageCount);
        ItemsGrid.ItemsSource = _allItems.Skip((_itemsPage - 1) * PageSize).Take(PageSize).ToArray();
        ItemsStatus.Text = $"共 {_allItems.Count} 条";
        UpdateItemsPagination(pageCount);
    }

    private void UpdateItemsPagination(int? pageCount = null)
    {
        var pages = pageCount ?? GetPageCount(_allItems.Count);
        ItemsPageText.Text = $"{_itemsPage} / {pages}";
        ItemsPreviousPageButton.IsEnabled = _itemsPage > 1 && _allItems.Count > 0;
        ItemsNextPageButton.IsEnabled = _itemsPage < pages && _allItems.Count > 0;
    }

    private static int GetPageCount(int total) => Math.Max(1, (total + PageSize - 1) / PageSize);

    private void ItemsPreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_itemsPage <= 1) return;
        _itemsPage--;
        RenderItemsPage();
    }

    private void ItemsNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_itemsPage >= GetPageCount(_allItems.Count)) return;
        _itemsPage++;
        RenderItemsPage();
    }

    private void FetchButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("网页采集", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (!PublicArticleFetcher.TryNormalizePublicUrl(UrlBox.Text, out var normalized, out var error))
        {
            MessageBox.Show(Window.GetWindow(this), error, "链接无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (TelegramChannelParser.IsTelegramHost(normalized.Host))
        {
            var answer = MessageBox.Show(Window.GetWindow(this), "TG 频道和消息请使用“TG采集”页签。\n\n是否立即切换？", "请使用 TG采集", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) WorkspaceTabs.SelectedIndex = 1;
            return;
        }
        try
        {
            var existing = await _store.FindByUrlAsync(normalized.AbsoluteUri, pageToken);
            if (!IsPageActive(pageToken)) return;
            if (existing is not null)
            {
                var answer = MessageBox.Show(Window.GetWindow(this), $"该链接已在收件箱中：\n{existing.Title}\n\n是否打开已有素材？", "链接已存在", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    await LoadItemsAsync(pageToken);
                    if (!IsPageActive(pageToken)) return;
                    ItemsGrid.SelectedItem = ((IEnumerable<CollectedItem>)ItemsGrid.ItemsSource!).FirstOrDefault(item => item.Id == existing.Id);
                }
                return;
            }
            FetchButtonIsEnabled(false);
            ItemMessage.Text = "正在读取公开网页…";
            var result = await _fetcher.FetchAsync(normalized.AbsoluteUri, pageToken);
            if (!IsPageActive(pageToken)) return;
            _parseError = result.Succeeded ? "" : result.Error;
            UrlBox.Text = result.Url.Length > 0 ? result.Url : normalized.AbsoluteUri;
            ItemUrlBox.Text = result.Url.Length > 0 ? result.Url : normalized.AbsoluteUri;
            ItemTitleBox.Text = result.Title;
            ItemContentBox.Text = result.Content;
            _selectedItem = null;
            ItemsGrid.SelectedItem = null;
            ItemNotesBox.Clear();
            ItemTagsBox.Clear();
            _pendingMedia.Clear();
            ShowMediaInfo();
            ItemMessage.Text = result.Succeeded
                ? $"已提取自 {result.SourceHost}。请检查并确认内容使用权限后保存。"
                : $"{result.Error}\n可补全标题和正文后保存。";
            ItemMessage.Foreground = result.Succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
        }
        finally
        {
            if (IsPageActive(pageToken)) FetchButtonIsEnabled(true);
        }
        });

    private void FetchButtonIsEnabled(bool enabled)
    {
        if (!IsPageActiveForUpdates) return;
        FetchButton.IsEnabled = enabled;
    }

    private void ShowMediaInfo(IReadOnlyList<string>? media = null)
    {
        var paths = media ?? _pendingMedia;
        ItemMediaInfo.Text = paths.Count == 0 ? "" : $"本地媒体 {paths.Count} 个：{string.Join("、", paths.Select(Path.GetFileName))}";
    }

    private void NewItemButton_Click(object sender, RoutedEventArgs e)
    {
        ItemsGrid.SelectedItem = null;
        _selectedItem = null;
        UrlBox.Clear(); ItemUrlBox.Clear(); ItemTitleBox.Clear(); ItemContentBox.Clear(); ItemNotesBox.Clear(); ItemTagsBox.Clear();
        _pendingMedia.Clear();
        ShowMediaInfo();
        ItemMessage.Text = "手动素材不需要来源链接。";
        _parseError = "";
    }

    private void SaveItemButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("保存采集项", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var media = _pendingMedia.Count > 0 ? _pendingMedia : _selectedItem?.Media;
        if (string.IsNullOrWhiteSpace(ItemTitleBox.Text) && string.IsNullOrWhiteSpace(ItemContentBox.Text) && (media is null || media.Count == 0))
        {
            MessageBox.Show(Window.GetWindow(this), "标题、正文和媒体至少填写一项。", "内容为空", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var rawUrl = ItemUrlBox.Text.Trim();
        var normalizedUrl = "";
        var host = "手动录入";
        if (rawUrl.Length > 0)
        {
            if (!PublicArticleFetcher.TryNormalizePublicUrl(rawUrl, out var uri, out var error))
            {
                MessageBox.Show(Window.GetWindow(this), error, "链接无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            normalizedUrl = uri.AbsoluteUri;
            host = uri.DnsSafeHost;
            var duplicate = await _store.FindByUrlAsync(normalizedUrl, pageToken);
            if (!IsPageActive(pageToken)) return;
            if (duplicate is not null && duplicate.Id != _selectedItem?.Id)
            {
                var answer = MessageBox.Show(Window.GetWindow(this), $"该链接已存在于收件箱：\n{duplicate.Title}\n\n是否查看已有素材？本次内容不会覆盖或合并到已有条目。", "重复链接", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    await LoadItemsAsync(pageToken);
                    if (!IsPageActive(pageToken)) return;
                    ItemsGrid.SelectedItem = ((IEnumerable<CollectedItem>)ItemsGrid.ItemsSource!).FirstOrDefault(item => item.Id == duplicate.Id);
                }
                return;
            }
        }
        var tags = ItemTagsBox.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var status = _selectedItem?.Status ?? "待处理";
        var model = new CollectedItemDraft(normalizedUrl, host, ItemTitleBox.Text, ItemContentBox.Text, status, ItemNotesBox.Text, tags, _parseError, _selectedItem?.Id, MediaList: media);
        try
        {
            if (_selectedItem is null) await _store.SaveItemAsync(model);
            else await _store.UpdateItemAsync(model);
            if (!IsPageActive(pageToken)) return;
            ItemMessage.Text = "素材已保存。";
            ItemMessage.Foreground = UiStatusBrushes.Success;
            await LoadItemsAsync(pageToken);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            if (IsPageActive(pageToken))
                MessageBox.Show(Window.GetWindow(this), "该网页链接已存在于收件箱。", "重复链接", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                ItemMessage.Text = $"未能保存，请检查内容后重试：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        });

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedItem = ItemsGrid.SelectedItem as CollectedItem;
        if (_selectedItem is null) return;
        ItemUrlBox.Text = _selectedItem.Url;
        ItemTitleBox.Text = _selectedItem.Title;
        ItemContentBox.Text = _selectedItem.Content;
        ItemNotesBox.Text = _selectedItem.Notes;
        ItemTagsBox.Text = string.Join(", ", _selectedItem.Tags);
        ItemMessage.Text = _selectedItem.ParseError;
        _parseError = _selectedItem.ParseError;
        _pendingMedia.Clear();
        ShowMediaInfo(_selectedItem.Media);
    }

    private void KeepItemButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("保留采集项", async () =>
        {
        var pageToken = CurrentPageToken;
        if (_selectedItem is null || !IsPageActive(pageToken)) return;
        await SaveCurrentItemAsync("保留", pageToken);
        });

    private void IgnoreItemButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("忽略采集项", async () =>
        {
        var pageToken = CurrentPageToken;
        if (_selectedItem is null || !IsPageActive(pageToken)) return;
        await SaveCurrentItemAsync("忽略", pageToken);
        });

    private async Task SaveCurrentItemAsync(string status, CancellationToken cancellationToken)
    {
        if (_selectedItem is null || !IsPageActive(cancellationToken)) return;
        var tags = ItemTagsBox.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var media = _pendingMedia.Count > 0 ? _pendingMedia : _selectedItem.Media;
        await _store.UpdateItemAsync(new CollectedItemDraft(_selectedItem.Url, _selectedItem.SourceHost, ItemTitleBox.Text, ItemContentBox.Text, status, ItemNotesBox.Text, tags, _selectedItem.ParseError, _selectedItem.Id, MediaList: media));
        if (!IsPageActive(cancellationToken)) return;
        await LoadItemsAsync(cancellationToken);
    }

    private void AddItemToMaterialsButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("采集项加入素材库", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var media = _pendingMedia.Count > 0 ? _pendingMedia : _selectedItem?.Media;
        if (string.IsNullOrWhiteSpace(ItemTitleBox.Text) && string.IsNullOrWhiteSpace(ItemContentBox.Text) && (media is null || media.Count == 0))
        {
            MessageBox.Show(Window.GetWindow(this), "标题、正文和媒体至少填写一项后才能加入素材库。", "内容为空", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var source = ItemUrlBox.Text.Trim();
        if (source.Length > 0 && MessageBox.Show(Window.GetWindow(this), "请确认你有权保存、编辑并发布这份内容。\n来源地址仅作记录，不会加入帖子正文。继续加入素材库？", "内容使用提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var mediaList = media ?? [];
        var type = MaterialMediaValidator.InferType(mediaList) ?? (mediaList.Count == 0 ? "text" : "");
        if (type.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "媒体类型无法识别，请确认图片和视频没有混用，且文件或链接带有正确扩展名。", "媒体无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var materialId = await _store.SaveMaterialAsync(new MaterialDraft(
                null, ItemTitleBox.Text, type, ItemContentBox.Text, "", "", "", "", null, "waitsend", "", mediaList,
                source.Length > 0 ? "网络采集" : "手工添加", source));
            if (_selectedItem is not null) await _store.SetItemStatusAsync(_selectedItem.Id, "已入素材库");
            if (!IsPageActive(pageToken)) return;
            ItemMessage.Text = $"已加入素材库（编号 {materialId}），状态为待发布。";
            ItemMessage.Foreground = UiStatusBrushes.Success;
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
            {
                ItemMessage.Text = $"未能加入素材库，请检查媒体后重试：{CliDiagnostics.Sanitize(ex.Message)}";
                ItemMessage.Foreground = UiStatusBrushes.Warning;
            }
            return;
        }
        await LoadItemsAsync(pageToken);
        });

    private void DeleteItemButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("删除采集项", async () =>
        {
        var pageToken = CurrentPageToken;
        if (_selectedItem is null || !IsPageActive(pageToken)) return;
        if (MessageBox.Show(Window.GetWindow(this), "删除该采集素材？此操作无法撤销。", "删除素材", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _store.DeleteItemAsync(_selectedItem.Id);
        if (!IsPageActive(pageToken)) return;
        _selectedItem = null;
        await LoadItemsAsync(pageToken);
        });

    private void SearchItemsButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("搜索采集项", LoadItemsAsync);
    private void ItemStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("筛选采集项", async () => { if (IsLoaded) await LoadItemsAsync(); });
    private void SearchBox_KeyDown(object sender, KeyEventArgs e) =>
        UiAsync.Run("采集项搜索快捷键", async () =>
        {
        if (e.Key != Key.Enter) return;
        if (ReferenceEquals(sender, ItemSearchBox)) await LoadItemsAsync();
        else await LoadItemsAsync();
        });

    #region TG采集

    private void SetTgStatus(string message, bool succeeded)
    {
        if (!IsPageActiveForUpdates) return;
        TgStatusText.Text = message;
        TgStatusText.Foreground = succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
    }

    /// <summary>采集或入库期间锁住可能冲突的操作；allowCancel 只在采集时打开取消按钮。</summary>
    private void SetTgBusy(bool busy, bool allowCancel = false)
    {
        if (!IsPageActiveForUpdates) return;
        TgCollectButton.IsEnabled = !busy;
        TgBatchDraftButton.IsEnabled = !busy;
        TgChannelBox.IsEnabled = !busy;
        TgLimitBox.IsEnabled = !busy;
        TgProxyBox.IsEnabled = !busy;
        TgKeywordBox.IsEnabled = !busy;
        TgCancelButton.IsEnabled = busy && allowCancel && _collectCts is not null;
    }

    private void TgCollectButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("TG 采集", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var channelUrl = TelegramCollectorService.NormalizeChannelUrl(TgChannelBox.Text);
        if (channelUrl.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "请输入有效的 TG 频道地址，例如 t.me/频道名、@频道名 或 t.me/s/频道名。", "频道地址无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            TgChannelBox.Focus();
            return;
        }
        if (!_tgPermissionConfirmed)
        {
            var granted = MessageBox.Show(Window.GetWindow(this),
                "将通过网络代理读取 TG 频道消息。\n\n结果先临时展示，确认后下载媒体并加入素材库。\n请确认你拥有合法使用权限。是否继续？",
                "确认采集", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (granted != MessageBoxResult.Yes) return;
            _tgPermissionConfirmed = true;
        }
        var limitText = TgLimitBox.Text.Trim();
        var limit = 0;
        if (limitText.Length > 0 && (!int.TryParse(limitText, out limit) || limit < 0))
        {
            MessageBox.Show(Window.GetWindow(this), "数量上限请填写不小于 0 的整数，留空表示全部。", "数量上限无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            TgLimitBox.Focus();
            return;
        }
        RememberChannel(TgChannelBox.Text.Trim());
        var previousCollect = _collectCts;
        var collectCts = new CancellationTokenSource();
        _collectCts = collectCts;
        previousCollect?.Cancel();
        var token = collectCts.Token;
        SetTgBusy(true, allowCancel: true);
        SetTgStatus("正在采集…", false);
        try
        {
            var proxySelection = _proxyRegistry.ParseSelection(
                TgProxyBox.Text,
                (TgProxyBox.SelectedItem as WebProxyOption)?.Value);
            var preview = await _telegramCollector.PreviewChannelAsync(
                channelUrl, limit, TgKeywordBox.Text,
                status => { if (IsPageActive(pageToken)) SetTgStatus(status, false); },
                token, proxySelection);
            if (!IsPageActive(pageToken) || !ReferenceEquals(_collectCts, collectCts)) return;
            _proxyCookie = preview.CookieHeader;
            if (preview.Messages.Count == 0)
            {
                SetTgStatus(preview.Error.Length > 0 ? preview.Error : "没有采集到符合筛选条件的消息。", false);
                return;
            }
            var (added, skipped) = MergeTempMessages(preview.Messages);
            var summary = $"采集完成：解析 {preview.Total} 条，新增 {added} 条，跳过 {skipped} 条重复。";
            if (preview.Total > preview.Messages.Count) summary += $"（筛选后保留 {preview.Messages.Count} 条）";
            if (preview.ProviderName.Length > 0) summary += $" 使用 {preview.ProviderName}。";
            SetTgStatus(summary, true);
            AppLogger.Instance.Info($"TG采集 {channelUrl}：{summary}");
        }
        catch (OperationCanceledException)
        {
            if (IsPageActive(pageToken) && ReferenceEquals(_collectCts, collectCts))
                SetTgStatus("已取消采集。", false);
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken) && ReferenceEquals(_collectCts, collectCts))
                SetTgStatus($"采集未完成，请检查频道地址或代理后重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            if (ReferenceEquals(_collectCts, collectCts))
            {
                _collectCts = null;
                if (IsPageActive(pageToken)) SetTgBusy(false);
            }
            collectCts.Dispose();
        }
        });

    private void SubscribeSchedulerEvents()
    {
        if (_telegramCollectionScheduler is null || _schedulerEventsSubscribed) return;
        _telegramCollectionScheduler.StatusChanged += TelegramCollectionScheduler_StatusChanged;
        _telegramCollectionScheduler.ProgressChanged += TelegramCollectionScheduler_ProgressChanged;
        _schedulerEventsSubscribed = true;
    }

    private void UnsubscribeSchedulerEvents()
    {
        if (_telegramCollectionScheduler is null || !_schedulerEventsSubscribed) return;
        _telegramCollectionScheduler.StatusChanged -= TelegramCollectionScheduler_StatusChanged;
        _telegramCollectionScheduler.ProgressChanged -= TelegramCollectionScheduler_ProgressChanged;
        _schedulerEventsSubscribed = false;
    }

    private void TgCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_collectCts is null || _collectCts.IsCancellationRequested) return;
        _collectCts.Cancel();
        TgCancelButton.IsEnabled = false;
        SetTgStatus("正在取消…", false);
    }

    /// <summary>按原帖链接去重合并，发布时间倒序排列，超出上限时丢掉最早的消息。</summary>
    private (int Added, int Skipped) MergeTempMessages(IReadOnlyList<TelegramChannelMessage> messages)
    {
        var seen = _tempItems.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;
        foreach (var message in messages)
        {
            if (message.Url.Length == 0 || !seen.Add(message.Url))
            {
                skipped++;
                continue;
            }
            var item = new TelegramTempItem(message);
            item.PropertyChanged += TgTempItem_PropertyChanged;
            _tempItems.Add(item);
            added++;
        }
        if (added > 0)
        {
            var ordered = _tempItems
                .OrderByDescending(item => item.PublishedAt ?? DateTimeOffset.MinValue)
                .ToArray();
            _syncingTgSelection = true;
            try
            {
                _tempItems.Clear();
                foreach (var item in ordered) _tempItems.Add(item);
                while (_tempItems.Count > TelegramCollectorService.TempListLimit)
                    _tempItems.RemoveAt(_tempItems.Count - 1);
            }
            finally
            {
                _syncingTgSelection = false;
            }
            RenumberTempItems();
        }
        SyncTgGridSelectionFromModels();
        UpdateTgSelectionCount();
        return (added, skipped);
    }

    private void TgTempItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsPageActiveForUpdates) return;
        if (_syncingTgSelection) return;
        if (e.PropertyName == nameof(TelegramTempItem.IsSelected)) UpdateTgSelectionCount();
    }

    private void TgGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTgSelection) return;
        _syncingTgSelection = true;
        try
        {
            foreach (var item in e.AddedItems.OfType<TelegramTempItem>()) item.IsSelected = true;
            foreach (var item in e.RemovedItems.OfType<TelegramTempItem>()) item.IsSelected = false;
        }
        finally
        {
            _syncingTgSelection = false;
        }
        UpdateTgSelectionCount();
    }

    private void TgGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None) return;
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        if (IsInteractiveRowElement(source)) return;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow { Item: TelegramTempItem item }) return;

        var selectedBefore = grid.SelectedItems.OfType<TelegramTempItem>().ToArray();
        var shouldSelect = !item.IsSelected;
        UiAsync.Post(Dispatcher, () =>
        {
            if (!IsPageActiveForUpdates) return;
            _syncingTgSelection = true;
            try
            {
                foreach (var selected in selectedBefore) selected.IsSelected = true;
                item.IsSelected = shouldSelect;
            }
            finally
            {
                _syncingTgSelection = false;
            }
            SyncTgGridSelectionFromModels();
            UpdateTgSelectionCount();
        }, DispatcherPriority.Background);
    }

    private void SyncTgGridSelectionFromModels()
    {
        _syncingTgSelection = true;
        try
        {
            TgGrid.UnselectAll();
            foreach (var item in _tempItems.Where(item => item.IsSelected))
                TgGrid.SelectedItems.Add(item);
        }
        finally
        {
            _syncingTgSelection = false;
        }
    }

    private void TgItemSelectionCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox) return;
        _tgSelectionClickCheckBox = checkBox;
        _tgSelectionClickWasChecked = checkBox.IsChecked == true;
    }

    private void TgItemSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not TelegramTempItem item) return;
        var shouldSelect = ReferenceEquals(_tgSelectionClickCheckBox, checkBox)
            ? !_tgSelectionClickWasChecked
            : checkBox.IsChecked == true;
        _tgSelectionClickCheckBox = null;
        item.IsSelected = shouldSelect;
        checkBox.IsChecked = shouldSelect;
        UiAsync.Post(Dispatcher, () =>
        {
            if (!IsPageActiveForUpdates) return;
            SyncTgGridSelectionFromModels();
            UpdateTgSelectionCount();
        }, DispatcherPriority.Background);
    }

    private void UpdateTgSelectionCount()
    {
        var selected = _tempItems.Count(item => item.IsSelected);
        TgSelectionText.Text = _tempItems.Count == 0
            ? "共 0 条消息"
            : selected == 0 ? $"共 {_tempItems.Count} 条临时消息" : $"已选 {selected} / {_tempItems.Count} 条";
        TgEmptyHint.Visibility = _tempItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenumberTempItems()
    {
        for (var index = 0; index < _tempItems.Count; index++) _tempItems[index].DisplayIndex = index + 1;
    }

    private void RememberChannel(string display)
    {
        if (display.Length == 0) return;
        var existing = _channelHistory.FirstOrDefault(item => item.Equals(display, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _channelHistory.Remove(existing);
        _channelHistory.Insert(0, display);
        while (_channelHistory.Count > 10) _channelHistory.RemoveAt(_channelHistory.Count - 1);
    }

    private void TgSelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        _syncingTgSelection = true;
        try
        {
            foreach (var item in _tempItems) item.IsSelected = true;
        }
        finally
        {
            _syncingTgSelection = false;
        }
        SyncTgGridSelectionFromModels();
        UpdateTgSelectionCount();
    }

    private void TgInvertButton_Click(object sender, RoutedEventArgs e)
    {
        _syncingTgSelection = true;
        try
        {
            foreach (var item in _tempItems) item.IsSelected = !item.IsSelected;
        }
        finally
        {
            _syncingTgSelection = false;
        }
        SyncTgGridSelectionFromModels();
        UpdateTgSelectionCount();
    }

    private void TgClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tempItems.Count == 0)
        {
            SetTgStatus("列表已经是空的。", false);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this), $"确定清空列表里的 {_tempItems.Count} 条临时消息？\n\n已经下载到 temp 目录的文件不会被删除。", "清空列表", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _syncingTgSelection = true;
        try
        {
            _tempItems.Clear();
        }
        finally
        {
            _syncingTgSelection = false;
        }
        UpdateTgSelectionCount();
        SetTgStatus("已清空临时列表。", true);
    }

    private void TgBatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = _tempItems.Where(item => item.IsSelected).ToArray();
        if (targets.Length == 0)
        {
            SetTgStatus("请先勾选要删除的临时消息。", false);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this), $"确定删除选中的 {targets.Length} 条临时消息？\n\n已经下载到 temp 目录的文件不会被删除。", "批量删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var item in targets) _tempItems.Remove(item);
        RenumberTempItems();
        UpdateTgSelectionCount();
        SetTgStatus($"已删除 {targets.Length} 条临时消息。", true);
    }

    private void TgBatchDraftButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("TG 批量加入素材库", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var targets = _tempItems.Where(item => item.IsSelected && item.CanMaterialize).ToArray();
        if (targets.Length == 0)
        {
            var drafted = _tempItems.Count(item => item.IsSelected && !item.CanMaterialize);
            SetTgStatus(drafted > 0 ? "选中的消息都已加入素材库，不能重复入库。" : "请先勾选要加入素材库的临时消息。", false);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this), $"将下载选中的 {targets.Length} 条消息媒体并加入素材库。\n\n已入库的消息会自动跳过。是否继续？", "批量加入素材库", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await DraftTempItemsAsync(targets, pageToken);
        });

    /// <summary>逐条下载媒体并写入素材仓库；单条失败不中断，最后统一汇总。</summary>
    private async Task DraftTempItemsAsync(
        IReadOnlyList<TelegramTempItem> targets,
        CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = CurrentPageToken;
        if (!IsPageActive(cancellationToken)) return;
        SetTgBusy(true);
        var drafted = 0;
        var failed = 0;
        var mediaFailed = 0;
        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                if (!IsPageActive(cancellationToken)) return;
                var item = targets[index];
                if (!item.CanMaterialize) continue;
                SetTgStatus($"正在加入素材库 {index + 1}/{targets.Count}：{item.TitleDisplay}…", false);
                try
                {
                    var result = await _telegramMaterializer.MaterializeAsync(
                        item.Message,
                        _proxyCookie,
                        cancellationToken: cancellationToken);
                    if (!IsPageActive(cancellationToken)) return;
                    if (result.SkippedDuplicate)
                    {
                        item.MarkMaterialized(result.MaterialId ?? 0, result.Directory);
                        continue;
                    }
                    if (result.SkippedEmpty)
                    {
                        failed++;
                        mediaFailed += result.FailedMediaCount;
                        AppLogger.Instance.Warning($"tg 入库未生成空白素材：{item.Url} 的媒体全部下载失败。");
                        continue;
                    }
                    if (!result.Succeeded || result.MaterialId is null)
                    {
                        failed++;
                        mediaFailed += result.FailedMediaCount;
                        AppLogger.Instance.Warning($"tg 入库失败：{item.Url} {result.Error}");
                        continue;
                    }
                    item.MarkMaterialized(result.MaterialId.Value, result.Directory);
                    drafted++;
                    mediaFailed += result.FailedMediaCount;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    failed++;
                    AppLogger.Instance.Warning($"tg 入库失败 {item.Url}：{ex.Message}");
                }
            }
            var summary = $"加入素材库完成：成功 {drafted} 条，失败 {failed} 条";
            if (mediaFailed > 0) summary += $"，{mediaFailed} 个媒体下载失败（相关消息未入库）";
            summary += "。可在“素材库”查看。";
            SetTgStatus(summary, failed == 0);
        }
        finally
        {
            if (IsPageActive(cancellationToken))
            {
                SetTgBusy(false);
                SyncTgGridSelectionFromModels();
                UpdateTgSelectionCount();
            }
        }
    }

    private void TgViewButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not TelegramTempItem item) return;
        var window = new TelegramMessagePreviewWindow(item, _proxyCookie, MaterializeSingleAsync, DeleteTempItem)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private void TgDeleteRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not TelegramTempItem item) return;
        DeleteTempItem(item);
    }

    private void DeleteTempItem(TelegramTempItem item)
    {
        _tempItems.Remove(item);
        RenumberTempItems();
        UpdateTgSelectionCount();
        SetTgStatus($"已删除临时消息：{item.TitleDisplay}", true);
    }

    private void TgDraftRowButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("TG 单条加入素材库", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if ((sender as Button)?.DataContext is not TelegramTempItem item) return;
        await DraftTempItemsAsync(new[] { item }, pageToken);
        });

    /// <summary>预览窗里的单条入库回调，返回是否成功入库。</summary>
    private async Task<bool> MaterializeSingleAsync(TelegramTempItem item)
    {
        try
        {
            await DraftTempItemsAsync(new[] { item });
            return item.MaterialId is not null;
        }
        catch
        {
            return false;
        }
    }

    private void TgOpenTempButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "temp", "telegram");
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"打开目录失败：{CliDiagnostics.Sanitize(ex.Message)}\n{directory}", "打开 temp 目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #region TG定时采集

    private async Task LoadTelegramSchedulesAsync(bool updateSummary = true, CancellationToken cancellationToken = default)
    {
        if (_telegramCollectionStore is null) return;
        if (!cancellationToken.CanBeCanceled) cancellationToken = CurrentPageToken;
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_telegramSchedulesLoadVersion;
        try
        {
            var schedules = await _telegramCollectionStore.GetAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _telegramSchedulesLoadVersion) return;
            // 一次批量读取全部分发规则，避免按 Schedule 逐个查询。
            var rulesBySchedule = await _telegramCollectionStore.GetRulesByScheduleIdsAsync(
                schedules.Select(schedule => schedule.Id).ToArray(), cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _telegramSchedulesLoadVersion) return;
            var rows = new List<TelegramScheduleRow>();
            foreach (var schedule in schedules)
            {
                IReadOnlyList<TelegramDistributionRule> rules =
                    rulesBySchedule.TryGetValue(schedule.Id, out var loadedRules) ? loadedRules : [];
                var targetSummary = rules.Count == 0
                    ? "未配置规则"
                    : $"{rules[0].TargetDisplay}（{rules.Count}条规则）";
                var row = new TelegramScheduleRow(schedule with { DistributionDisplay = targetSummary });
                row.IsRunning = _runningScheduleIds.Contains(schedule.Id);
                rows.Add(row);
            }
            if (!IsPageActive(cancellationToken) || version != _telegramSchedulesLoadVersion) return;
            _telegramSchedules.Clear();
            foreach (var row in rows) _telegramSchedules.Add(row);
            if (updateSummary)
            {
                TgScheduleStatusText.Text = _telegramSchedules.Count == 0
                    ? "还没有配置定时采集频道。"
                    : $"已配置 {_telegramSchedules.Count} 个频道，执行器每 2 分钟检查一次到期配置。";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken) || version != _telegramSchedulesLoadVersion) return;
            TgScheduleStatusText.Text = $"未能读取定时采集配置，请刷新重试：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private void TgSchedulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 正在采集的那一行不允许被选中，避免误操作看起来像在编辑它。
        if (TgSchedulesGrid.SelectedItem is TelegramScheduleRow { IsRunning: true })
        {
            TgSchedulesGrid.SelectedItem = null;
        }
    }

    private void ClearTelegramScheduleEditor()
    {
        _editingTelegramSchedule = null;
        TgScheduleChannelBox.Text = "";
        TgScheduleLimitBox.Text = "20";
        TgScheduleProxyBox.SelectedIndex = 0;
        _scheduleCronExpression = "0 0 * * *";
        _scheduleKeyword = "";
        _scheduleRules = [];
        _scheduleAutoScheduleEnabled = false;
        _scheduleAutoScheduleDelayMinutes = 30;
        _scheduleAutoScheduleMaxPerRun = 20;
        TgScheduleSaveButton.Content = "保存配置";
    }

    private void TgScheduleClearButton_Click(object sender, RoutedEventArgs e) => ClearTelegramScheduleEditor();

    private void TgScheduleSaveButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("保存 TG 定时采集配置", async () =>
        {
        await SaveTelegramScheduleAsync();
        });

    private async Task<long?> SaveTelegramScheduleAsync(CancellationToken cancellationToken = default)
    {
        if (_telegramCollectionStore is null) return null;
        var pageToken = cancellationToken.CanBeCanceled ? cancellationToken : CurrentPageToken;
        if (!IsPageActive(pageToken)) return null;
        var url = TelegramCollectorService.NormalizeChannelUrl(TgScheduleChannelBox.Text);
        if (url.Length == 0)
        {
            TgScheduleStatusText.Text = "请输入有效的公开 TG 频道地址。";
            TgScheduleChannelBox.Focus();
            return null;
        }
        var limit = 20;
        if (TgScheduleLimitBox.Text.Trim().Length > 0 &&
            (!int.TryParse(TgScheduleLimitBox.Text.Trim(), out limit) || limit < 0 || limit > TelegramCollectorService.TempListLimit))
        {
            TgScheduleStatusText.Text = "数量上限请填写 0 到 500 的整数，0 表示不限制。";
            return null;
        }
        if (!TelegramCronSchedule.TryParse(_scheduleCronExpression, out var cron, out var cronError))
        {
            TgScheduleStatusText.Text = $"计划表达式无效：{cronError}";
            return null;
        }
        var proxySelection = _proxyRegistry.ParseSelection(
            TgScheduleProxyBox.Text,
            (TgScheduleProxyBox.SelectedItem as WebProxyOption)?.Value).Value;
        var editingId = _editingTelegramSchedule?.Id;
        var rules = _scheduleRules;
        try
        {
            var id = await _telegramCollectionStore.SaveAsync(new TelegramCollectionScheduleDraft(
                editingId,
                url,
                TelegramCollectorService.ReadChannelName(url),
                0,
                _editingTelegramSchedule?.Enabled ?? true,
                proxySelection,
                limit,
                _scheduleKeyword,
                "daily",
                1,
                1,
                "00:00",
                cron.Expression,
                _scheduleAutoScheduleEnabled,
                _scheduleAutoScheduleDelayMinutes,
                _scheduleAutoScheduleMaxPerRun),
                CancellationToken.None);
            await _telegramCollectionStore.ReplaceRulesAsync(id, rules, CancellationToken.None);
            if (!IsPageActive(pageToken)) return id;
            await LoadTelegramSchedulesAsync(cancellationToken: pageToken);
            if (!IsPageActive(pageToken)) return id;
            TgScheduleStatusText.Text = $"定时采集配置已保存（编号 {id}）。";
            return id;
        }
        catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            if (!IsPageActive(pageToken)) return null;
            TgScheduleStatusText.Text = $"未能保存定时采集配置，请检查输入后重试：{CliDiagnostics.Sanitize(ex.Message)}";
            return null;
        }
    }

    private void TgScheduleEditButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("编辑 TG 定时采集配置", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if ((sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
        var schedule = row.Schedule;
        var rules = Array.Empty<TelegramDistributionRuleDraft>();
        Exception? rulesError = null;
        if (_telegramCollectionStore is not null)
        {
            try
            {
                var loadedRules = await _telegramCollectionStore.GetRulesAsync(schedule.Id, pageToken);
                rules = loadedRules.Select(rule => new TelegramDistributionRuleDraft(rule.MessageType, rule.Keyword, rule.GuildId, rule.GuildName, rule.ChannelId, rule.ChannelName, rule.SortOrder)).ToArray();
            }
            catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { return; }
            catch (Exception ex) { rulesError = ex; }
        }
        if (!IsPageActive(pageToken)) return;
        _editingTelegramSchedule = schedule;
        TgScheduleChannelBox.Text = schedule.ChannelUrl;
        TgScheduleLimitBox.Text = schedule.MessageLimit.ToString();
        _scheduleCronExpression = schedule.EffectiveCronExpression;
        _scheduleKeyword = schedule.Keyword;
        _scheduleRules = rules;
        _scheduleAutoScheduleEnabled = schedule.AutoScheduleEnabled;
        _scheduleAutoScheduleDelayMinutes = schedule.AutoScheduleDelayMinutes;
        _scheduleAutoScheduleMaxPerRun = schedule.AutoScheduleMaxPerRun;
        var option = _proxyRegistry.Options.FirstOrDefault(item => string.Equals(item.Value, schedule.ProxySelection, StringComparison.OrdinalIgnoreCase));
        if (option is not null) TgScheduleProxyBox.SelectedItem = option;
        else TgScheduleProxyBox.Text = schedule.ProxySelection;
        TgScheduleSaveButton.Content = "保存修改";
        TgScheduleStatusText.Text = rulesError is null
            ? $"正在编辑：{schedule.ChannelName}"
            : $"未能读取分发规则，请重试：{CliDiagnostics.Sanitize(rulesError.Message)}";
        });

    private void TgScheduleToggleButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("切换 TG 定时采集状态", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (_telegramCollectionStore is null || (sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
        var schedule = row.Schedule;
        try
        {
            await _telegramCollectionStore.SetEnabledAsync(schedule.Id, !schedule.Enabled);
            if (!IsPageActive(pageToken)) return;
            await LoadTelegramSchedulesAsync(cancellationToken: pageToken);
            if (!IsPageActive(pageToken)) return;
            TgScheduleStatusText.Text = schedule.Enabled ? "定时采集已关闭。" : "定时采集已开启。";
        }
        catch (Exception ex)
        {
            if (!IsPageActive(pageToken)) return;
            TgScheduleStatusText.Text = $"未能切换定时采集状态，请重试：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        });

    private void TgScheduleDeleteButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("删除 TG 定时采集配置", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (_telegramCollectionStore is null || (sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
        var schedule = row.Schedule;
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除频道“{schedule.ChannelName}”的定时采集配置？\n\n已入素材库的内容不会删除。", "删除定时采集", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!IsPageActive(pageToken)) return;
        try
        {
            await _telegramCollectionStore.DeleteAsync(schedule.Id);
            if (!IsPageActive(pageToken)) return;
            if (_editingTelegramSchedule?.Id == schedule.Id) ClearTelegramScheduleEditor();
            await LoadTelegramSchedulesAsync(cancellationToken: pageToken);
        }
        catch (Exception ex)
        {
            if (!IsPageActive(pageToken)) return;
            TgScheduleStatusText.Text = $"未能删除定时采集配置，请重试：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        });

    private void TgScheduleRunButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("立即执行 TG 定时采集", async () =>
        {
        if (_telegramCollectionScheduler is null || (sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (row.IsRunning)
        {
            CancelTelegramScheduleRun(row.Id);
            return;
        }
        var schedule = row.Schedule;
        var cts = new CancellationTokenSource();
        _telegramScheduleRuns[schedule.Id] = cts;
        _runningScheduleIds.Add(schedule.Id);
        RefreshTelegramScheduleRowStates();
        TgScheduleProgressBar.Visibility = Visibility.Visible;
        TgScheduleProgressBar.IsIndeterminate = true;
        TgScheduleStatusText.Text = _runningScheduleIds.Count > 1
            ? $"{schedule.ChannelName}：已排队，等待当前采集结束…"
            : $"{schedule.ChannelName}：正在启动采集…";
        try
        {
            var result = await _telegramCollectionScheduler.RunNowAsync(schedule.Id, cts.Token);
            if (!IsPageActive(pageToken)) return;
            TgScheduleStatusText.Text = result.Succeeded
                ? $"立即采集完成：新增 {result.AddedCount} 条，跳过 {result.SkippedCount} 条。"
                : $"立即采集未完成，请检查频道或代理后重试：{result.Message}";
        }
        catch (OperationCanceledException)
        {
            if (IsPageActive(pageToken))
                TgScheduleStatusText.Text = "立即采集已取消，已完成的入库内容会保留。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                TgScheduleStatusText.Text = $"立即采集未完成，请检查频道或代理后重试：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            _telegramScheduleRuns.Remove(schedule.Id);
            _runningScheduleIds.Remove(schedule.Id);
            cts.Dispose();
            if (IsPageActive(pageToken))
            {
                RefreshTelegramScheduleRowStates();
                if (_runningScheduleIds.Count == 0) TgScheduleProgressBar.Visibility = Visibility.Collapsed;
                await LoadTelegramSchedulesAsync(updateSummary: false, cancellationToken: pageToken);
            }
        }
        });

    private void TgScheduleCancelButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var id in _telegramScheduleRuns.Keys.ToArray()) CancelTelegramScheduleRun(id);
        _telegramCollectionScheduler?.CancelCurrentRun();
    }

    private void CancelTelegramScheduleRun(long scheduleId)
    {
        if (_telegramScheduleRuns.TryGetValue(scheduleId, out var cts)) cts.Cancel();
        else _telegramCollectionScheduler?.CancelCurrentRun();
    }

    private void TgScheduleCheckButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("检查 TG 定时采集", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken) || _telegramCollectionScheduler is null || _telegramScheduleCheckCts is not null) return;
        var checkCts = new CancellationTokenSource();
        _telegramScheduleCheckCts = checkCts;
        TgScheduleCheckButton.IsEnabled = false;
        try
        {
            await _telegramCollectionScheduler.CheckNowAsync(checkCts.Token);
            if (!IsPageActive(pageToken)) return;
            await LoadTelegramSchedulesAsync(cancellationToken: pageToken);
        }
        catch (OperationCanceledException)
        {
            if (IsPageActive(pageToken))
                TgScheduleStatusText.Text = "定时采集检查已取消。";
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken))
                TgScheduleStatusText.Text = $"未能检查定时采集，请重试：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            if (ReferenceEquals(_telegramScheduleCheckCts, checkCts))
            {
                _telegramScheduleCheckCts = null;
                if (IsPageActive(pageToken)) TgScheduleCheckButton.IsEnabled = true;
            }
            checkCts.Dispose();
        }
        });

    private void TgScheduleAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var window = new TelegramScheduleAdvancedWindow(
            new TelegramScheduleAdvancedSettings(
                _scheduleCronExpression,
                _scheduleKeyword,
                _scheduleRules,
                _scheduleAutoScheduleEnabled,
                _scheduleAutoScheduleDelayMinutes,
                _scheduleAutoScheduleMaxPerRun),
            _channelSync)
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        if (!IsPageActive(pageToken)) return;
        _scheduleCronExpression = window.Settings.CronExpression;
        _scheduleKeyword = window.Settings.Keyword;
        _scheduleRules = window.Settings.DistributionRules;
        _scheduleAutoScheduleEnabled = window.Settings.AutoScheduleEnabled;
        _scheduleAutoScheduleDelayMinutes = window.Settings.AutoScheduleDelayMinutes;
        _scheduleAutoScheduleMaxPerRun = window.Settings.AutoScheduleMaxPerRun;
        UiAsync.Observe("保存 TG 采集高级设置", SaveAdvancedSettingsAsync(pageToken));
    }

    private async Task SaveAdvancedSettingsAsync(CancellationToken cancellationToken = default)
    {
        var pageToken = cancellationToken.CanBeCanceled ? cancellationToken : CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (string.IsNullOrWhiteSpace(TgScheduleChannelBox.Text))
        {
            TgScheduleStatusText.Text = "高级设置已更新；请输入频道地址后保存配置。";
            return;
        }
        await SaveTelegramScheduleAsync(pageToken);
    }

    private void RefreshTelegramScheduleRowStates()
    {
        if (!IsPageActiveForUpdates) return;
        // 只有正在采集（或排队等待）的行被锁定，其它行保持可编辑、可立即采集。
        foreach (var row in _telegramSchedules)
        {
            row.IsRunning = _runningScheduleIds.Contains(row.Id);
        }
        TgScheduleCancelButton.Visibility = _runningScheduleIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TelegramCollectionScheduler_StatusChanged(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            UiAsync.Post(Dispatcher, () => TelegramCollectionScheduler_StatusChanged(message));
            return;
        }
        if (!IsPageActiveForUpdates || !_schedulerEventsSubscribed) return;
        TgScheduleStatusText.Text = message;
    }

    private void TelegramCollectionScheduler_ProgressChanged(TelegramCollectionProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            UiAsync.Post(Dispatcher, () => TelegramCollectionScheduler_ProgressChanged(progress));
            return;
        }
        if (!IsPageActiveForUpdates || !_schedulerEventsSubscribed) return;
        var terminal = progress.Stage is "完成" or "失败" or "已取消";
        if (terminal)
        {
            // 采集结束后只恢复这一行；其它行不受影响。
            _runningScheduleIds.Remove(progress.ScheduleId);
        }
        else
        {
            // 后台执行器触发采集时，同样把对应行切换成“取消采集”。
            _runningScheduleIds.Add(progress.ScheduleId);
        }
        RefreshTelegramScheduleRowStates();
        TgScheduleStatusText.Text = $"{progress.ChannelName}：{progress.Message}";
        TgScheduleProgressBar.Visibility = Visibility.Visible;
        if (progress.Total > 0)
        {
            TgScheduleProgressBar.IsIndeterminate = false;
            TgScheduleProgressBar.Maximum = progress.Total;
            TgScheduleProgressBar.Value = Math.Min(progress.Current, progress.Total);
        }
        else
        {
            TgScheduleProgressBar.IsIndeterminate = progress.Stage is not ("完成" or "失败" or "已取消");
        }
        if (terminal)
        {
            TgScheduleProgressBar.Visibility = Visibility.Collapsed;
            // 页面手动触发的采集由调用方负责刷新；后台调度触发的在这里刷新。
            if (IsPageActiveForUpdates && !_telegramScheduleRuns.ContainsKey(progress.ScheduleId))
                UiAsync.Observe(
                    "刷新 TG 定时采集列表",
                    LoadTelegramSchedulesAsync(updateSummary: false, cancellationToken: CurrentPageToken));
        }
    }

    #endregion

    #region 代理池

    private ProxySource? SelectedProxySource => ProxySourceBox.SelectedItem as ProxySource;

    private Task LoadProxySourcesAsync() => LoadProxySourcesAsync(CurrentPageToken);

    private async Task LoadProxySourcesAsync(CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_proxySourcesLoadVersion;
        try
        {
            var selectedId = SelectedProxySource?.Id;
            var sources = await _proxyPoolStore.GetSourcesAsync(cancellationToken);
            if (!IsPageActive(cancellationToken) || version != _proxySourcesLoadVersion) return;
            _proxySources.Clear();
            foreach (var source in sources) _proxySources.Add(source);
            if (_proxySources.Count > 0)
            {
                var selected = _proxySources.FirstOrDefault(item => item.Id == selectedId) ?? _proxySources[0];
                ProxySourceBox.SelectedItem = selected;
                await LoadProxyNodesAsync(selected, cancellationToken);
            }
            else
            {
                _proxyNodes.Clear();
                ProxyEmptyHint.Visibility = Visibility.Visible;
                UpdateProxySummary();
                UpdateProxySelectionSummary();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsPageActive(cancellationToken) || version != _proxySourcesLoadVersion) return;
            SetProxyStatus($"未能读取代理源，请刷新重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
    }

    private Task LoadProxyNodesAsync() => LoadProxyNodesAsync(SelectedProxySource, CurrentPageToken);

    private async Task LoadProxyNodesAsync(ProxySource? source, CancellationToken cancellationToken)
    {
        if (!IsPageActive(cancellationToken)) return;
        var version = ++_proxyNodesLoadVersion;
        if (source is null)
        {
            _syncingProxySelection = true;
            try
            {
                _proxyNodes.Clear();
            }
            finally
            {
                _syncingProxySelection = false;
            }
            ProxyEmptyHint.Visibility = Visibility.Visible;
            UpdateProxySummary();
            UpdateProxySelectionSummary();
            return;
        }
        var nodes = await _proxyPoolStore.GetNodesAsync(source.Id, cancellationToken);
        if (!IsPageActive(cancellationToken)
            || version != _proxyNodesLoadVersion
            || SelectedProxySource?.Id != source.Id)
            return;
        _syncingProxySelection = true;
        try
        {
            _proxyNodes.Clear();
            foreach (var node in nodes) _proxyNodes.Add(new ProxyNodeRow(node));
        }
        finally
        {
            _syncingProxySelection = false;
        }
        ProxyEmptyHint.Visibility = _proxyNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProxySummary();
        UpdateProxySelectionSummary();
    }

    private void UpdateProxySummary()
    {
        if (!IsPageActiveForUpdates) return;
        var total = _proxyNodes.Count;
        var alive = _proxyNodes.Count(item => item.Node.IsAlive);
        var tg = _proxyNodes.Count(item => item.Node.TgAccessible is true);
        var checkedCount = _proxyNodes.Count(item => item.Node.LastCheckedAt is not null);
        var uncheckedCount = Math.Max(0, total - checkedCount);
        ProxyTotalCountText.Text = total.ToString();
        ProxyAliveCountText.Text = alive.ToString();
        ProxyUncheckedCountText.Text = uncheckedCount.ToString();
        ProxyTgCountText.Text = tg.ToString();
    }

    private void UpdateProxySelectionSummary()
    {
        if (!IsPageActiveForUpdates) return;
        var selected = _proxyNodes.Count(item => item.IsSelected);
        ProxySelectionText.Text = $"已选 {selected} 个";
    }

    private void SetProxyStatus(string message, bool succeeded)
    {
        if (!IsPageActiveForUpdates) return;
        ProxyPoolStatusText.Text = message;
        ProxyPoolStatusText.Foreground = succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
    }

    private void SetProxyBusy(bool busy)
    {
        if (!IsPageActiveForUpdates) return;
        ProxySourceBox.IsEnabled = !busy;
        ProxyAddSourceButton.IsEnabled = !busy;
        ProxyEditSourceButton.IsEnabled = !busy;
        ProxyConcurrencyBox.IsEnabled = !busy;
        ProxyFetchButton.IsEnabled = !busy;
        ProxyBatchActionBox.IsEnabled = !busy && _proxyNodes.Count > 0;
        ProxyNodesGrid.IsEnabled = !busy;
    }

    private void ProxyNodesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProxySelection) return;
        _syncingProxySelection = true;
        try
        {
            foreach (var row in e.AddedItems.OfType<ProxyNodeRow>()) row.IsSelected = true;
            foreach (var row in e.RemovedItems.OfType<ProxyNodeRow>()) row.IsSelected = false;
        }
        finally
        {
            _syncingProxySelection = false;
        }
        UpdateProxySelectionSummary();
    }

    private void ProxyNodesGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None) return;
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        if (IsInteractiveRowElement(source)) return;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow { Item: ProxyNodeRow row }) return;

        var selectedBefore = grid.SelectedItems.OfType<ProxyNodeRow>().ToArray();
        var shouldSelect = !row.IsSelected;
        UiAsync.Post(Dispatcher, () =>
        {
            if (!IsPageActiveForUpdates) return;
            _syncingProxySelection = true;
            try
            {
                foreach (var selected in selectedBefore) selected.IsSelected = true;
                row.IsSelected = shouldSelect;
            }
            finally
            {
                _syncingProxySelection = false;
            }
            SyncProxyGridSelectionFromModels();
            UpdateProxySelectionSummary();
        }, DispatcherPriority.Background);
    }

    private void SyncProxyGridSelectionFromModels()
    {
        _syncingProxySelection = true;
        try
        {
            ProxyNodesGrid.UnselectAll();
            foreach (var row in _proxyNodes.Where(row => row.IsSelected))
                ProxyNodesGrid.SelectedItems.Add(row);
        }
        finally
        {
            _syncingProxySelection = false;
        }
    }

    private void ProxyNodeSelectionCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox) return;
        _proxySelectionClickCheckBox = checkBox;
        _proxySelectionClickWasChecked = checkBox.IsChecked == true;
    }

    private void ProxyNodeSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not ProxyNodeRow row) return;
        var shouldSelect = ReferenceEquals(_proxySelectionClickCheckBox, checkBox)
            ? !_proxySelectionClickWasChecked
            : checkBox.IsChecked == true;
        _proxySelectionClickCheckBox = null;
        row.IsSelected = shouldSelect;
        checkBox.IsChecked = shouldSelect;
        UiAsync.Post(Dispatcher, () =>
        {
            if (!IsPageActiveForUpdates) return;
            SyncProxyGridSelectionFromModels();
            UpdateProxySelectionSummary();
        }, DispatcherPriority.Background);
    }

    private void ProxyBatchActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("代理节点批量操作", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (ProxyBatchActionBox.SelectedItem is not ComboBoxItem item || item.Tag is not string action) return;
        ProxyBatchActionBox.SelectedIndex = 0;
        switch (action)
        {
            case "select-all":
                _syncingProxySelection = true;
                try
                {
                    foreach (var row in _proxyNodes) row.IsSelected = true;
                }
                finally
                {
                    _syncingProxySelection = false;
                }
                SyncProxyGridSelectionFromModels();
                UpdateProxySelectionSummary();
                break;
            case "invert":
                _syncingProxySelection = true;
                try
                {
                    foreach (var row in _proxyNodes) row.IsSelected = !row.IsSelected;
                }
                finally
                {
                    _syncingProxySelection = false;
                }
                SyncProxyGridSelectionFromModels();
                UpdateProxySelectionSummary();
                break;
            case "health":
                ProxyHealthButton_Click(sender, e);
                break;
            case "telegram":
                ProxyCheckTgButton_Click(sender, e);
                break;
            case "delete-selected":
                await DeleteProxyNodesAsync(_proxyNodes.Where(row => row.IsSelected).Select(row => row.Node).ToArray(), "已选节点", pageToken);
                break;
            case "delete-invalid":
                var invalidNodes = _proxyNodes
                    .Where(row => row.Node.LastCheckedAt is not null && !row.Node.IsAlive &&
                                  row.Node.LastTgCheckedAt is not null && row.Node.TgAccessible is false)
                    .Select(row => row.Node)
                    .ToArray();
                await DeleteProxyNodesAsync(invalidNodes, "测试不可用且 TG 不可访问的失效节点", pageToken);
                break;
        }
        });

    private async Task DeleteProxyNodesAsync(
        IReadOnlyList<ProxyNode> nodes,
        string description,
        CancellationToken cancellationToken = default)
    {
        var pageToken = cancellationToken.CanBeCanceled ? cancellationToken : CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if (nodes.Count == 0)
        {
            SetProxyStatus(description == "已选节点" ? "请先勾选要删除的节点。" : "没有同时满足测试不可用且 TG 不可访问的节点。", false);
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除 {nodes.Count} 个{description}？删除后，下次同步代理源时这些节点可能重新出现。", "批量删除代理节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!IsPageActive(pageToken)) return;

        SetProxyBusy(true);
        var deleted = 0;
        var source = SelectedProxySource;
        try
        {
            foreach (var node in nodes)
            {
                await _proxyPoolStore.DeleteNodeAsync(node);
                deleted++;
            }
            if (!IsPageActive(pageToken)) return;
            await LoadProxyNodesAsync(source, pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus($"批量删除完成：删除 {deleted} 个代理节点。", true);
        }
        catch (Exception ex)
        {
            if (!IsPageActive(pageToken)) return;
            await LoadProxyNodesAsync(source, pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus($"批量删除部分完成：已删除 {deleted} 个，请重试剩余项。\n{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally { if (IsPageActive(pageToken)) SetProxyBusy(false); }
    }

    private void ProxySourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsPageActiveForUpdates || e.AddedItems.Count == 0) return;
        var pageToken = CurrentPageToken;
        UiAsync.Observe("刷新代理节点", LoadProxyNodesAsync(SelectedProxySource, pageToken));
    }

    private void ProxyFetchButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("获取代理节点", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var source = SelectedProxySource;
        if (source is null) { SetProxyStatus("请先选择代理源。", false); return; }
        SetProxyBusy(true);
        try
        {
            var result = await _proxyPoolService.FetchSourceAsync(
                source,
                message => { if (IsPageActive(pageToken)) SetProxyStatus(message, false); },
                pageToken);
            if (!IsPageActive(pageToken)) return;
            if (!result.Succeeded)
            {
                SetProxyStatus($"获取未完成，请检查代理源后重试：{result.Error}", false);
                return;
            }
            await LoadProxySourcesAsync(pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus($"获取完成：读取 {result.ReadCount} 条，保留 HTTP/HTTPS {result.AcceptedCount} 条，过滤其他协议或无效数据 {result.FilteredCount} 条。", true);
        }
        catch (OperationCanceledException)
        {
            if (IsPageActive(pageToken)) SetProxyStatus("已取消获取。", false);
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken)) SetProxyStatus($"获取未完成，请检查代理源后重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally { if (IsPageActive(pageToken)) SetProxyBusy(false); }
        });

    private void ProxyHealthButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("代理节点健康检查", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var targets = _proxyNodes.Where(item => item.IsSelected).Select(item => item.Node).ToArray();
        if (targets.Length == 0) targets = _proxyNodes.Select(item => item.Node).ToArray();
        if (targets.Length == 0) { SetProxyStatus("当前代理源没有 HTTP/HTTPS 节点。", false); return; }
        if (!int.TryParse(ProxyConcurrencyBox.Text.Trim(), out var concurrency) || concurrency is < 1 or > ProxyPoolService.MaxConcurrency)
        {
            SetProxyStatus($"测试并发数请输入 1-{ProxyPoolService.MaxConcurrency} 的整数。", false);
            ProxyConcurrencyBox.Focus();
            return;
        }
        await RunProxyHealthAsync(targets, concurrency, pageToken);
        });

    private async Task RunProxyHealthAsync(
        IReadOnlyList<ProxyNode> targets,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        var pageToken = cancellationToken.CanBeCanceled ? cancellationToken : CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        SetProxyBusy(true);
        try
        {
            var result = await _proxyPoolService.TestHealthAsync(
                targets,
                concurrency,
                message => { if (IsPageActive(pageToken)) SetProxyStatus(message, false); },
                pageToken);
            if (!IsPageActive(pageToken)) return;
            await LoadProxyNodesAsync(SelectedProxySource, pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus($"测试完成：成功 {result.SuccessCount} 条，失败 {result.FailedCount} 条，过滤/跳过 {result.SkippedCount} 条。", result.FailedCount == 0);
        }
        catch (OperationCanceledException)
        {
            if (IsPageActive(pageToken)) SetProxyStatus("已取消测试。", false);
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken)) SetProxyStatus($"测试未完成，请重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally { if (IsPageActive(pageToken)) SetProxyBusy(false); }
    }

    private void ProxyRowHealthButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("单行代理节点健康检查", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if ((sender as Button)?.DataContext is not ProxyNodeRow row) return;
        await RunProxyHealthAsync(new[] { row.Node }, 1, pageToken);
        });

    private void ProxyCheckTgButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("批量验证代理节点 TG 可用性", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var targets = _proxyNodes.Where(item => item.IsSelected).Select(item => item.Node).ToArray();
        if (targets.Length == 0)
        {
            SetProxyStatus("请先勾选要验证 TG 的 HTTP/HTTPS 节点。", false);
            return;
        }
        SetProxyBusy(true);
        var success = 0;
        try
        {
            for (var index = 0; index < targets.Length; index++)
            {
                if (!IsPageActive(pageToken)) return;
                SetProxyStatus($"正在验证 TG {index + 1}/{targets.Length}：{targets[index].Address}…", false);
                var result = await _proxyPoolService.CheckTelegramAsync(targets[index], pageToken);
                if (!IsPageActive(pageToken)) return;
                if (result.Succeeded) success++;
            }
            await LoadProxyNodesAsync(SelectedProxySource, pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus($"TG 验证完成：成功 {success} 条，失败 {targets.Length - success} 条。", success == targets.Length);
        }
        catch (OperationCanceledException)
        {
            if (IsPageActive(pageToken)) SetProxyStatus("已取消 TG 验证。", false);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.WriteException("批量代理节点 TG 验证异常", ex);
            if (IsPageActive(pageToken)) SetProxyStatus($"TG 验证未完成，请重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            if (IsPageActive(pageToken))
            {
                try { SetProxyBusy(false); }
                catch (Exception ex) { AppLogger.Instance.WriteException("恢复 TG 验证界面状态失败", ex); }
            }
        }
        });

    private void ProxyRowTgButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("验证单行代理节点 TG 可用性", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if ((sender as Button)?.DataContext is not ProxyNodeRow row) return;
        SetProxyBusy(true);
        try
        {
            var result = await _proxyPoolService.CheckTelegramAsync(row.Node, pageToken);
            if (!IsPageActive(pageToken)) return;
            await LoadProxyNodesAsync(SelectedProxySource, pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus(result.Succeeded ? $"TG 验证成功：{row.Address} 解析到 {result.MessageCount} 条消息。" : $"TG 验证未完成，请检查节点后重试：{result.Error}", result.Succeeded);
        }
        catch (OperationCanceledException)
        {
            if (IsPageActive(pageToken)) SetProxyStatus("已取消 TG 验证。", false);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.WriteException($"代理节点 TG 验证异常：{row.Address}", ex);
            if (IsPageActive(pageToken)) SetProxyStatus($"TG 验证未完成，请重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            if (IsPageActive(pageToken))
            {
                try { SetProxyBusy(false); }
                catch (Exception ex) { AppLogger.Instance.WriteException("恢复单行 TG 验证界面状态失败", ex); }
            }
        }
        });

    private void ProxyAddSourceButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("添加代理源", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var window = new ProxySourceEditorWindow { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        if (!IsPageActive(pageToken)) return;
        try
        {
            await _proxyPoolStore.CreateCustomSourceAsync(window.SourceName, window.SourceUrl, window.SourceIsEnabled);
            if (!IsPageActive(pageToken)) return;
            await LoadProxySourcesAsync(pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus("自定义代理源已添加。", true);
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken)) SetProxyStatus($"未能添加代理源，请检查地址后重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        });

    private void ProxyEditSourceButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("编辑代理源", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        var source = SelectedProxySource;
        if (source is null) { SetProxyStatus("请先选择代理源。", false); return; }
        var window = new ProxySourceEditorWindow(source) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        if (!IsPageActive(pageToken)) return;
        try
        {
            if (window.DeleteRequested)
            {
                await _proxyPoolStore.DeleteCustomSourceAsync(source);
                if (!IsPageActive(pageToken)) return;
                await LoadProxySourcesAsync(pageToken);
                if (!IsPageActive(pageToken)) return;
                SetProxyStatus("代理源已删除。", true);
                return;
            }
            await _proxyPoolStore.UpdateSourceAsync(source, window.SourceName, window.SourceUrl, window.SourceIsEnabled);
            if (!IsPageActive(pageToken)) return;
            await LoadProxySourcesAsync(pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus("代理源已更新。", true);
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken)) SetProxyStatus($"未能更新代理源，请检查地址后重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        });

    private void ProxyRowDeleteButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("删除代理节点", async () =>
        {
        var pageToken = CurrentPageToken;
        if (!IsPageActive(pageToken)) return;
        if ((sender as Button)?.DataContext is not ProxyNodeRow row) return;
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除代理节点“{row.Address}”？", "删除代理节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!IsPageActive(pageToken)) return;
        SetProxyBusy(true);
        try
        {
            await _proxyPoolStore.DeleteNodeAsync(row.Node);
            if (!IsPageActive(pageToken)) return;
            await LoadProxyNodesAsync(SelectedProxySource, pageToken);
            if (!IsPageActive(pageToken)) return;
            SetProxyStatus($"已删除代理节点：{row.Address}", true);
        }
        catch (Exception ex)
        {
            if (IsPageActive(pageToken)) SetProxyStatus($"未能删除代理节点，请重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally { if (IsPageActive(pageToken)) SetProxyBusy(false); }
        });

    private static bool IsInteractiveRowElement(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or ComboBox) return true;
            if (current is DataGridRow) return false;
        }
        return false;
    }

    #endregion

    #endregion
}
