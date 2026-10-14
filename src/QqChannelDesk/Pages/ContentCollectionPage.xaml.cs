using QqChannelDesk.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _loading;
    private string _parseError = "";
    private readonly ObservableCollection<TelegramTempItem> _tempItems = [];
    private readonly ObservableCollection<string> _channelHistory = [];
    private readonly WebProxyProviderRegistry _proxyRegistry = new();
    private string _proxyCookie = "";
    private CancellationTokenSource? _collectCts;
    private bool _tgPermissionConfirmed;

    private readonly ObservableCollection<ProxySource> _proxySources = [];
    private readonly ObservableCollection<ProxyNodeRow> _proxyNodes = [];
    private readonly TelegramCollectionStore? _telegramCollectionStore;
    private readonly TelegramCollectionScheduler? _telegramCollectionScheduler;
    private readonly ChannelSyncService? _channelSync;
    private readonly ObservableCollection<TelegramScheduleRow> _telegramSchedules = [];
    private TelegramCollectionSchedule? _editingTelegramSchedule;
    private string _scheduleCronExpression = "0 0 * * *";
    private string _scheduleKeyword = "";
    private IReadOnlyList<TelegramDistributionRuleDraft> _scheduleRules = [];
    // 每一行采集各自持有一个令牌：只有正在采集（或排队等待执行）的那一行会被锁定。
    private readonly Dictionary<long, CancellationTokenSource> _telegramScheduleRuns = [];
    private readonly HashSet<long> _runningScheduleIds = [];
    private CancellationTokenSource? _telegramScheduleCheckCts;
    private bool _telegramSchedulesLoading;

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
        if (_telegramCollectionScheduler is not null)
        {
            _telegramCollectionScheduler.StatusChanged += TelegramCollectionScheduler_StatusChanged;
            _telegramCollectionScheduler.ProgressChanged += TelegramCollectionScheduler_ProgressChanged;
        }
        ProxyConcurrencyBox.Text = ProxyPoolService.DefaultConcurrency.ToString();
        Loaded += async (_, _) => await LoadItemsAsync();
        Loaded += async (_, _) => await LoadProxySourcesAsync();
        Loaded += async (_, _) => await LoadTelegramSchedulesAsync();
    }

    public async Task LoadItemsAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var status = (ItemStatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
            _allItems = await _store.GetItemsAsync(ItemSearchBox.Text, status is "全部状态" ? null : status);
            _itemsPage = 1;
            RenderItemsPage();
        }
        catch (Exception ex)
        {
            _allItems = [];
            ItemsGrid.ItemsSource = null;
            UpdateItemsPagination();
            ItemsStatus.Text = $"读取收件箱失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally { _loading = false; }
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

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PublicArticleFetcher.TryNormalizePublicUrl(UrlBox.Text, out var normalized, out var error))
        {
            MessageBox.Show(Window.GetWindow(this), error, "链接无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (TelegramChannelParser.IsTelegramHost(normalized.Host))
        {
            var answer = MessageBox.Show(Window.GetWindow(this), "tg 频道和消息请使用「TG采集」页签采集：\n\n结果先临时展示在列表中，确认后下载媒体并直接加入素材仓库。\n\n是否立即切换到该页签？", "请使用 TG采集", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) WorkspaceTabs.SelectedIndex = 1;
            return;
        }
        try
        {
            var existing = await _store.FindByUrlAsync(normalized.AbsoluteUri);
            if (existing is not null)
            {
                var answer = MessageBox.Show(Window.GetWindow(this), $"该链接已在收件箱中：\n{existing.Title}\n\n是否打开已有素材？", "链接已存在", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    await LoadItemsAsync();
                    ItemsGrid.SelectedItem = ((IEnumerable<CollectedItem>)ItemsGrid.ItemsSource!).FirstOrDefault(item => item.Id == existing.Id);
                }
                return;
            }
            FetchButtonIsEnabled(false);
            ItemMessage.Text = "正在读取公开网页…";
            var result = await _fetcher.FetchAsync(normalized.AbsoluteUri);
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
                : $"{result.Error} 可手动补全标题和正文后保存。";
            ItemMessage.Foreground = result.Succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
        }
        finally { FetchButtonIsEnabled(true); }
    }

    private void FetchButtonIsEnabled(bool enabled)
    {
        FetchButton.IsEnabled = enabled;
    }

    private void SetItemMessage(string message, bool succeeded)
    {
        ItemMessage.Text = message;
        ItemMessage.Foreground = succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
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

    private async void SaveItemButton_Click(object sender, RoutedEventArgs e)
    {
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
            var duplicate = await _store.FindByUrlAsync(normalizedUrl);
            if (duplicate is not null && duplicate.Id != _selectedItem?.Id)
            {
                var answer = MessageBox.Show(Window.GetWindow(this), $"该链接已存在于收件箱：\n{duplicate.Title}\n\n是否查看已有素材？本次内容不会覆盖或合并到已有条目。", "重复链接", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    await LoadItemsAsync();
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
            if (_selectedItem is null) _ = await _store.SaveItemAsync(model);
            else await _store.UpdateItemAsync(model);
            ItemMessage.Text = "素材已保存。";
            ItemMessage.Foreground = UiStatusBrushes.Success;
            await LoadItemsAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            MessageBox.Show(Window.GetWindow(this), "该网页链接已存在于收件箱。", "重复链接", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ItemMessage.Text = $"保存失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
    }

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

    private async void KeepItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;
        await SaveCurrentItemAsync("保留");
    }

    private async void IgnoreItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;
        await SaveCurrentItemAsync("忽略");
    }

    private async Task SaveCurrentItemAsync(string status)
    {
        if (_selectedItem is null) return;
        var tags = ItemTagsBox.Text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var media = _pendingMedia.Count > 0 ? _pendingMedia : _selectedItem.Media;
        await _store.UpdateItemAsync(new CollectedItemDraft(_selectedItem.Url, _selectedItem.SourceHost, ItemTitleBox.Text, ItemContentBox.Text, status, ItemNotesBox.Text, tags, _selectedItem.ParseError, _selectedItem.Id, MediaList: media));
        await LoadItemsAsync();
    }

    private async void AddItemToMaterialsButton_Click(object sender, RoutedEventArgs e)
    {
        var media = _pendingMedia.Count > 0 ? _pendingMedia : _selectedItem?.Media;
        if (string.IsNullOrWhiteSpace(ItemTitleBox.Text) && string.IsNullOrWhiteSpace(ItemContentBox.Text) && (media is null || media.Count == 0))
        {
            MessageBox.Show(Window.GetWindow(this), "标题、正文和媒体至少填写一项后才能加入素材仓库。", "内容为空", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var source = ItemUrlBox.Text.Trim();
        if (source.Length > 0 && MessageBox.Show(Window.GetWindow(this), "请确认你有权保存、编辑并发布这份内容。来源地址会保留在素材来源中，不会自动加入帖子正文。继续加入素材仓库？", "内容使用提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
            ItemMessage.Text = $"已加入素材仓库（编号 {materialId}），状态为待发布。";
            ItemMessage.Foreground = UiStatusBrushes.Success;
        }
        catch (Exception ex)
        {
            ItemMessage.Text = $"加入素材仓库失败：{CliDiagnostics.Sanitize(ex.Message)}";
            ItemMessage.Foreground = UiStatusBrushes.Warning;
            return;
        }
        await LoadItemsAsync();
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;
        if (MessageBox.Show(Window.GetWindow(this), "删除该采集素材？关联草稿会保留，但不再关联此素材。", "删除素材", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _store.DeleteItemAsync(_selectedItem.Id);
        _selectedItem = null;
        await LoadItemsAsync();
    }

    private async void SearchItemsButton_Click(object sender, RoutedEventArgs e) => await LoadItemsAsync();
    private async void ItemStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await LoadItemsAsync(); }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ReferenceEquals(sender, ItemSearchBox)) await LoadItemsAsync();
        else await LoadItemsAsync();
    }

    #region TG采集

    private void SetTgStatus(string message, bool succeeded)
    {
        TgStatusText.Text = message;
        TgStatusText.Foreground = succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
    }

    /// <summary>采集或入库期间锁住可能冲突的操作；allowCancel 只在采集时打开取消按钮。</summary>
    private void SetTgBusy(bool busy, bool allowCancel = false)
    {
        TgCollectButton.IsEnabled = !busy;
        TgBatchDraftButton.IsEnabled = !busy;
        TgChannelBox.IsEnabled = !busy;
        TgLimitBox.IsEnabled = !busy;
        TgProxyBox.IsEnabled = !busy;
        TgKeywordBox.IsEnabled = !busy;
        TgCancelButton.IsEnabled = busy && allowCancel && _collectCts is not null;
    }

    private async void TgCollectButton_Click(object sender, RoutedEventArgs e)
    {
        var channelUrl = TelegramCollectorService.NormalizeChannelUrl(TgChannelBox.Text);
        if (channelUrl.Length == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "请输入有效的 tg 频道地址，例如 t.me/频道名、@频道名 或 t.me/s/频道名。", "频道地址无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            TgChannelBox.Focus();
            return;
        }
        if (!_tgPermissionConfirmed)
        {
            var granted = MessageBox.Show(Window.GetWindow(this),
                "将通过网络代理打开 tg 频道预览页并解析其中的消息。\n\n采集结果仅临时展示在列表中，确认后会下载媒体并直接加入素材仓库。\n\n请确认你对这些内容拥有合法使用权限。是否继续？",
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
        _collectCts?.Cancel();
        _collectCts?.Dispose();
        _collectCts = new CancellationTokenSource();
        var token = _collectCts.Token;
        SetTgBusy(true, allowCancel: true);
        SetTgStatus("正在采集…", false);
        try
        {
            var proxySelection = _proxyRegistry.ParseSelection(
                TgProxyBox.Text,
                (TgProxyBox.SelectedItem as WebProxyOption)?.Value);
            var preview = await _telegramCollector.PreviewChannelAsync(
                channelUrl, limit, TgKeywordBox.Text, status => SetTgStatus(status, false), token, proxySelection);
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
            SetTgStatus("已取消采集。", false);
        }
        catch (Exception ex)
        {
            SetTgStatus($"采集失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            SetTgBusy(false);
        }
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
            _tempItems.Clear();
            foreach (var item in ordered) _tempItems.Add(item);
            while (_tempItems.Count > TelegramCollectorService.TempListLimit)
                _tempItems.RemoveAt(_tempItems.Count - 1);
            RenumberTempItems();
        }
        UpdateTgSelectionCount();
        return (added, skipped);
    }

    private void TgTempItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TelegramTempItem.IsSelected)) UpdateTgSelectionCount();
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
        foreach (var item in _tempItems) item.IsSelected = true;
        UpdateTgSelectionCount();
    }

    private void TgInvertButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _tempItems) item.IsSelected = !item.IsSelected;
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
        _tempItems.Clear();
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

    private async void TgBatchDraftButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = _tempItems.Where(item => item.IsSelected && item.CanMaterialize).ToArray();
        if (targets.Length == 0)
        {
            var drafted = _tempItems.Count(item => item.IsSelected && !item.CanMaterialize);
            SetTgStatus(drafted > 0 ? "选中的消息都已加入素材仓库，不能重复入库。" : "请先勾选要加入素材仓库的临时消息。", false);
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this), $"将把选中的 {targets.Length} 条消息下载图片和视频并加入素材仓库：\n\ntemp\\telegram\\yyyyMMdd\\频道\\\n\n已入素材库的消息会自动跳过。是否继续？", "批量加入素材仓库", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await DraftTempItemsAsync(targets);
    }

    /// <summary>逐条下载媒体并写入素材仓库；单条失败不中断，最后统一汇总。</summary>
    private async Task DraftTempItemsAsync(IReadOnlyList<TelegramTempItem> targets)
    {
        SetTgBusy(true);
        var drafted = 0;
        var failed = 0;
        var mediaFailed = 0;
        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                var item = targets[index];
                if (!item.CanMaterialize) continue;
                SetTgStatus($"正在加入素材库 {index + 1}/{targets.Count}：{item.TitleDisplay}…", false);
                try
                {
                    var result = await _telegramMaterializer.MaterializeAsync(item.Message, _proxyCookie);
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
                catch (Exception ex)
                {
                    failed++;
                    AppLogger.Instance.Warning($"tg 入库失败 {item.Url}：{ex.Message}");
                }
            }
            var summary = $"加入素材库完成：成功 {drafted} 条，失败 {failed} 条";
            if (mediaFailed > 0) summary += $"，{mediaFailed} 个媒体下载失败（相关消息未入库）";
            summary += "。可在「素材仓库」查看。";
            SetTgStatus(summary, failed == 0);
        }
        finally
        {
            SetTgBusy(false);
            UpdateTgSelectionCount();
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

    private async void TgDraftRowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not TelegramTempItem item) return;
        await DraftTempItemsAsync(new[] { item });
    }

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

    private async Task LoadTelegramSchedulesAsync(bool updateSummary = true)
    {
        if (_telegramCollectionStore is null || _telegramSchedulesLoading) return;
        _telegramSchedulesLoading = true;
        try
        {
            var schedules = await _telegramCollectionStore.GetAsync();
            _telegramSchedules.Clear();
            foreach (var schedule in schedules)
            {
                var rules = await _telegramCollectionStore.GetRulesAsync(schedule.Id);
                var targetSummary = rules.Count == 0
                    ? "未配置规则"
                    : $"{rules[0].TargetDisplay}（{rules.Count}条规则）";
                var row = new TelegramScheduleRow(schedule with { DistributionDisplay = targetSummary });
                row.IsRunning = _runningScheduleIds.Contains(schedule.Id);
                _telegramSchedules.Add(row);
            }
            if (updateSummary)
            {
                TgScheduleStatusText.Text = _telegramSchedules.Count == 0
                    ? "还没有配置定时采集频道。"
                    : $"已配置 {_telegramSchedules.Count} 个频道，调度器每 2 分钟检查一次到期配置。";
            }
        }
        catch (Exception ex)
        {
            TgScheduleStatusText.Text = $"读取定时采集配置失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            _telegramSchedulesLoading = false;
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
        TgScheduleSaveButton.Content = "保存配置";
    }

    private void TgScheduleClearButton_Click(object sender, RoutedEventArgs e) => ClearTelegramScheduleEditor();

    private async void TgScheduleSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveTelegramScheduleAsync();
    }

    private async Task<long?> SaveTelegramScheduleAsync()
    {
        if (_telegramCollectionStore is null) return null;
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
            TgScheduleStatusText.Text = $"Cron 表达式无效：{cronError}";
            return null;
        }
        var proxySelection = _proxyRegistry.ParseSelection(
            TgScheduleProxyBox.Text,
            (TgScheduleProxyBox.SelectedItem as WebProxyOption)?.Value).Value;
        try
        {
            var id = await _telegramCollectionStore.SaveAsync(new TelegramCollectionScheduleDraft(
                _editingTelegramSchedule?.Id,
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
                cron.Expression));
            await _telegramCollectionStore.ReplaceRulesAsync(id, _scheduleRules);
            await LoadTelegramSchedulesAsync();
            TgScheduleStatusText.Text = $"定时采集配置已保存（编号 {id}）。";
            return id;
        }
        catch (Exception ex)
        {
            TgScheduleStatusText.Text = $"保存定时采集配置失败：{CliDiagnostics.Sanitize(ex.Message)}";
            return null;
        }
    }

    private async void TgScheduleEditButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
        var schedule = row.Schedule;
        _editingTelegramSchedule = schedule;
        TgScheduleChannelBox.Text = schedule.ChannelUrl;
        TgScheduleLimitBox.Text = schedule.MessageLimit.ToString();
        _scheduleCronExpression = schedule.EffectiveCronExpression;
        _scheduleKeyword = schedule.Keyword;
        _scheduleRules = [];
        if (_telegramCollectionStore is not null)
        {
            try
            {
                var rules = await _telegramCollectionStore.GetRulesAsync(schedule.Id);
                _scheduleRules = rules.Select(rule => new TelegramDistributionRuleDraft(rule.MessageType, rule.Keyword, rule.GuildId, rule.GuildName, rule.ChannelId, rule.ChannelName, rule.SortOrder)).ToArray();
            }
            catch (Exception ex) { TgScheduleStatusText.Text = $"读取分发规则失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
        }
        var option = _proxyRegistry.Options.FirstOrDefault(item => string.Equals(item.Value, schedule.ProxySelection, StringComparison.OrdinalIgnoreCase));
        if (option is not null) TgScheduleProxyBox.SelectedItem = option;
        else TgScheduleProxyBox.Text = schedule.ProxySelection;
        TgScheduleSaveButton.Content = "保存修改";
        TgScheduleStatusText.Text = $"正在编辑：{schedule.ChannelName}";
    }

    private async void TgScheduleToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_telegramCollectionStore is null || (sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
        var schedule = row.Schedule;
        try
        {
            await _telegramCollectionStore.SetEnabledAsync(schedule.Id, !schedule.Enabled);
            await LoadTelegramSchedulesAsync();
            TgScheduleStatusText.Text = schedule.Enabled ? "定时采集已关闭。" : "定时采集已开启。";
        }
        catch (Exception ex)
        {
            TgScheduleStatusText.Text = $"切换定时采集状态失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private async void TgScheduleDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_telegramCollectionStore is null || (sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
        var schedule = row.Schedule;
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除频道“{schedule.ChannelName}”的定时采集配置？已入素材仓库的内容不会删除。", "删除定时采集", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _telegramCollectionStore.DeleteAsync(schedule.Id);
            if (_editingTelegramSchedule?.Id == schedule.Id) ClearTelegramScheduleEditor();
            await LoadTelegramSchedulesAsync();
        }
        catch (Exception ex) { TgScheduleStatusText.Text = $"删除失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
    }

    private async void TgScheduleRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_telegramCollectionScheduler is null || (sender as FrameworkElement)?.DataContext is not TelegramScheduleRow row) return;
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
            TgScheduleStatusText.Text = result.Succeeded
                ? $"立即采集完成：新增 {result.AddedCount} 条，跳过 {result.SkippedCount} 条。"
                : $"立即采集失败：{result.Message}";
        }
        catch (OperationCanceledException)
        {
            TgScheduleStatusText.Text = "立即采集已取消，已完成的入库内容会保留。";
        }
        catch (Exception ex) { TgScheduleStatusText.Text = $"立即采集失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
        finally
        {
            _telegramScheduleRuns.Remove(schedule.Id);
            _runningScheduleIds.Remove(schedule.Id);
            cts.Dispose();
            RefreshTelegramScheduleRowStates();
            if (_runningScheduleIds.Count == 0) TgScheduleProgressBar.Visibility = Visibility.Collapsed;
            await LoadTelegramSchedulesAsync(updateSummary: false);
        }
    }

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

    private async void TgScheduleCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_telegramCollectionScheduler is null || _telegramScheduleCheckCts is not null) return;
        _telegramScheduleCheckCts = new CancellationTokenSource();
        TgScheduleCheckButton.IsEnabled = false;
        try
        {
            await _telegramCollectionScheduler.CheckNowAsync(_telegramScheduleCheckCts.Token);
            await LoadTelegramSchedulesAsync();
        }
        catch (OperationCanceledException)
        {
            TgScheduleStatusText.Text = "定时采集检查已取消。";
        }
        catch (Exception ex) { TgScheduleStatusText.Text = $"检查定时采集失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
        finally
        {
            _telegramScheduleCheckCts.Dispose();
            _telegramScheduleCheckCts = null;
            TgScheduleCheckButton.IsEnabled = true;
        }
    }

    private void TgScheduleAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new TelegramScheduleAdvancedWindow(
            new TelegramScheduleAdvancedSettings(_scheduleCronExpression, _scheduleKeyword, _scheduleRules),
            _channelSync)
        { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        _scheduleCronExpression = window.Settings.CronExpression;
        _scheduleKeyword = window.Settings.Keyword;
        _scheduleRules = window.Settings.DistributionRules;
        _ = SaveAdvancedSettingsAsync();
    }

    private async Task SaveAdvancedSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(TgScheduleChannelBox.Text))
        {
            TgScheduleStatusText.Text = "高级设置已更新；请输入频道地址后保存配置。";
            return;
        }
        await SaveTelegramScheduleAsync();
    }

    private void RefreshTelegramScheduleRowStates()
    {
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
            _ = Dispatcher.BeginInvoke(() => TelegramCollectionScheduler_StatusChanged(message));
            return;
        }
        TgScheduleStatusText.Text = message;
    }

    private void TelegramCollectionScheduler_ProgressChanged(TelegramCollectionProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => TelegramCollectionScheduler_ProgressChanged(progress));
            return;
        }
        var terminal = progress.Stage is "完成" or "失败";
        if (terminal)
        {
            // 采集结束后只恢复这一行；其它行不受影响。
            _runningScheduleIds.Remove(progress.ScheduleId);
        }
        else
        {
            // 后台调度器触发采集时，同样把对应行切换成“取消采集”。
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
            TgScheduleProgressBar.IsIndeterminate = progress.Stage is not ("完成" or "失败");
        }
        if (terminal)
        {
            TgScheduleProgressBar.Visibility = Visibility.Collapsed;
            // 页面手动触发的采集由调用方负责刷新；后台调度触发的在这里刷新。
            if (IsLoaded && !_telegramScheduleRuns.ContainsKey(progress.ScheduleId))
                _ = LoadTelegramSchedulesAsync(updateSummary: false);
        }
    }

    #endregion

    #region 代理池

    private ProxySource? SelectedProxySource => ProxySourceBox.SelectedItem as ProxySource;

    private async Task LoadProxySourcesAsync()
    {
        try
        {
            var selectedId = SelectedProxySource?.Id;
            var sources = await _proxyPoolStore.GetSourcesAsync();
            _proxySources.Clear();
            foreach (var source in sources) _proxySources.Add(source);
            if (_proxySources.Count > 0)
            {
                ProxySourceBox.SelectedItem = _proxySources.FirstOrDefault(item => item.Id == selectedId) ?? _proxySources[0];
                await LoadProxyNodesAsync();
            }
            else
            {
                _proxyNodes.Clear();
                ProxyEmptyHint.Visibility = Visibility.Visible;
                UpdateProxySummary();
                UpdateProxySelectionSummary();
            }
        }
        catch (Exception ex)
        {
            SetProxyStatus($"读取代理源失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
    }

    private async Task LoadProxyNodesAsync()
    {
        var source = SelectedProxySource;
        if (source is null)
        {
            _proxyNodes.Clear();
            ProxyEmptyHint.Visibility = Visibility.Visible;
            UpdateProxySummary();
            UpdateProxySelectionSummary();
            return;
        }
        var nodes = await _proxyPoolStore.GetNodesAsync(source.Id);
        _proxyNodes.Clear();
        foreach (var node in nodes) _proxyNodes.Add(new ProxyNodeRow(node));
        ProxyEmptyHint.Visibility = _proxyNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProxySummary();
        UpdateProxySelectionSummary();
    }

    private void UpdateProxySummary()
    {
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
        var selected = _proxyNodes.Count(item => item.IsSelected);
        ProxySelectionText.Text = $"已选 {selected} 个";
    }

    private void SetProxyStatus(string message, bool succeeded)
    {
        ProxyPoolStatusText.Text = message;
        ProxyPoolStatusText.Foreground = succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
    }

    private void SetProxyBusy(bool busy)
    {
        ProxySourceBox.IsEnabled = !busy;
        ProxyAddSourceButton.IsEnabled = !busy;
        ProxyEditSourceButton.IsEnabled = !busy;
        ProxyConcurrencyBox.IsEnabled = !busy;
        ProxyFetchButton.IsEnabled = !busy;
        ProxyBatchActionBox.IsEnabled = !busy && _proxyNodes.Count > 0;
        ProxyNodesGrid.IsEnabled = !busy;
    }

    private void ProxyNodeSelectionCheckBox_Click(object sender, RoutedEventArgs e) => UpdateProxySelectionSummary();

    private async void ProxyBatchActionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProxyBatchActionBox.SelectedItem is not ComboBoxItem item || item.Tag is not string action) return;
        ProxyBatchActionBox.SelectedIndex = 0;
        switch (action)
        {
            case "select-all":
                foreach (var row in _proxyNodes) row.IsSelected = true;
                UpdateProxySelectionSummary();
                break;
            case "invert":
                foreach (var row in _proxyNodes) row.IsSelected = !row.IsSelected;
                UpdateProxySelectionSummary();
                break;
            case "health":
                ProxyHealthButton_Click(sender, e);
                break;
            case "telegram":
                ProxyCheckTgButton_Click(sender, e);
                break;
            case "delete-selected":
                await DeleteProxyNodesAsync(_proxyNodes.Where(row => row.IsSelected).Select(row => row.Node).ToArray(), "已选节点");
                break;
            case "delete-invalid":
                var invalidNodes = _proxyNodes
                    .Where(row => row.Node.LastCheckedAt is not null && !row.Node.IsAlive &&
                                  row.Node.LastTgCheckedAt is not null && row.Node.TgAccessible is false)
                    .Select(row => row.Node)
                    .ToArray();
                await DeleteProxyNodesAsync(invalidNodes, "测活不可用且 TG 不可访问的失效节点");
                break;
        }
    }

    private async Task DeleteProxyNodesAsync(IReadOnlyList<ProxyNode> nodes, string description)
    {
        if (nodes.Count == 0)
        {
            SetProxyStatus(description == "已选节点" ? "请先勾选要删除的节点。" : "没有同时满足测活不可用且 TG 不可访问的节点。", false);
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除 {nodes.Count} 个{description}？删除后，下次同步代理源时这些节点可能重新出现。", "批量删除代理节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetProxyBusy(true);
        var deleted = 0;
        try
        {
            foreach (var node in nodes)
            {
                await _proxyPoolStore.DeleteNodeAsync(node);
                deleted++;
            }
            await LoadProxyNodesAsync();
            SetProxyStatus($"批量删除完成：删除 {deleted} 个代理节点。", true);
        }
        catch (Exception ex)
        {
            await LoadProxyNodesAsync();
            SetProxyStatus($"批量删除部分完成：已删除 {deleted} 个，错误：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally { SetProxyBusy(false); }
    }

    private void ProxySourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.AddedItems.Count == 0) return;
        _ = LoadProxyNodesAsync();
    }

    private async void ProxyFetchButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedProxySource;
        if (source is null) { SetProxyStatus("请先选择代理源。", false); return; }
        SetProxyBusy(true);
        try
        {
            var result = await _proxyPoolService.FetchSourceAsync(source, message => SetProxyStatus(message, false));
            if (!result.Succeeded)
            {
                SetProxyStatus($"获取失败：{result.Error}", false);
                return;
            }
            await LoadProxySourcesAsync();
            SetProxyStatus($"获取完成：读取 {result.ReadCount} 条，保留 HTTP/HTTPS {result.AcceptedCount} 条，过滤其他协议或无效数据 {result.FilteredCount} 条。", true);
        }
        catch (OperationCanceledException) { SetProxyStatus("已取消获取。", false); }
        catch (Exception ex) { SetProxyStatus($"获取失败：{CliDiagnostics.Sanitize(ex.Message)}", false); }
        finally { SetProxyBusy(false); }
    }

    private async void ProxyHealthButton_Click(object sender, RoutedEventArgs e)
    {
        var targets = _proxyNodes.Where(item => item.IsSelected).Select(item => item.Node).ToArray();
        if (targets.Length == 0) targets = _proxyNodes.Select(item => item.Node).ToArray();
        if (targets.Length == 0) { SetProxyStatus("当前代理源没有 HTTP/HTTPS 节点。", false); return; }
        if (!int.TryParse(ProxyConcurrencyBox.Text.Trim(), out var concurrency) || concurrency is < 1 or > ProxyPoolService.MaxConcurrency)
        {
            SetProxyStatus($"测活并发数请输入 1-{ProxyPoolService.MaxConcurrency} 的整数。", false);
            ProxyConcurrencyBox.Focus();
            return;
        }
        await RunProxyHealthAsync(targets, concurrency);
    }

    private async Task RunProxyHealthAsync(IReadOnlyList<ProxyNode> targets, int concurrency)
    {
        SetProxyBusy(true);
        try
        {
            var result = await _proxyPoolService.TestHealthAsync(targets, concurrency, message => SetProxyStatus(message, false));
            await LoadProxyNodesAsync();
            SetProxyStatus($"测活完成：成功 {result.SuccessCount} 条，失败 {result.FailedCount} 条，过滤/跳过 {result.SkippedCount} 条。", result.FailedCount == 0);
        }
        catch (OperationCanceledException) { SetProxyStatus("已取消测活。", false); }
        catch (Exception ex) { SetProxyStatus($"测活失败：{CliDiagnostics.Sanitize(ex.Message)}", false); }
        finally { SetProxyBusy(false); }
    }

    private async void ProxyRowHealthButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ProxyNodeRow row) return;
        await RunProxyHealthAsync(new[] { row.Node }, 1);
    }

    private async void ProxyCheckTgButton_Click(object sender, RoutedEventArgs e)
    {
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
                SetProxyStatus($"正在验证 TG {index + 1}/{targets.Length}：{targets[index].Address}…", false);
                var result = await _proxyPoolService.CheckTelegramAsync(targets[index]);
                if (result.Succeeded) success++;
            }
            await LoadProxyNodesAsync();
            SetProxyStatus($"TG 验证完成：成功 {success} 条，失败 {targets.Length - success} 条。", success == targets.Length);
        }
        catch (OperationCanceledException) { SetProxyStatus("已取消 TG 验证。", false); }
        catch (Exception ex)
        {
            AppLogger.Instance.WriteException("批量代理节点 TG 验证异常", ex);
            SetProxyStatus($"TG 验证失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            try { SetProxyBusy(false); }
            catch (Exception ex) { AppLogger.Instance.WriteException("恢复 TG 验证界面状态失败", ex); }
        }
    }

    private async void ProxyRowTgButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ProxyNodeRow row) return;
        SetProxyBusy(true);
        try
        {
            var result = await _proxyPoolService.CheckTelegramAsync(row.Node);
            await LoadProxyNodesAsync();
            SetProxyStatus(result.Succeeded ? $"TG 验证成功：{row.Address} 解析到 {result.MessageCount} 条消息。" : $"TG 验证失败：{result.Error}", result.Succeeded);
        }
        catch (OperationCanceledException) { SetProxyStatus("已取消 TG 验证。", false); }
        catch (Exception ex)
        {
            AppLogger.Instance.WriteException($"代理节点 TG 验证异常：{row.Address}", ex);
            SetProxyStatus($"TG 验证失败：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            try { SetProxyBusy(false); }
            catch (Exception ex) { AppLogger.Instance.WriteException("恢复单行 TG 验证界面状态失败", ex); }
        }
    }

    private async void ProxyAddSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProxySourceEditorWindow { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        try
        {
            await _proxyPoolStore.CreateCustomSourceAsync(window.SourceName, window.SourceUrl, window.SourceIsEnabled);
            await LoadProxySourcesAsync();
            SetProxyStatus("自定义代理源已添加。", true);
        }
        catch (Exception ex) { SetProxyStatus($"添加代理源失败：{CliDiagnostics.Sanitize(ex.Message)}", false); }
    }

    private async void ProxyEditSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedProxySource;
        if (source is null) { SetProxyStatus("请先选择代理源。", false); return; }
        var window = new ProxySourceEditorWindow(source) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        try
        {
            if (window.DeleteRequested)
            {
                await _proxyPoolStore.DeleteCustomSourceAsync(source);
                await LoadProxySourcesAsync();
                SetProxyStatus("代理源已删除。", true);
                return;
            }
            await _proxyPoolStore.UpdateSourceAsync(source, window.SourceName, window.SourceUrl, window.SourceIsEnabled);
            await LoadProxySourcesAsync();
            SetProxyStatus("代理源已更新。", true);
        }
        catch (Exception ex) { SetProxyStatus($"更新代理源失败：{CliDiagnostics.Sanitize(ex.Message)}", false); }
    }

    private async void ProxyRowDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ProxyNodeRow row) return;
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除代理节点“{row.Address}”？", "删除代理节点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetProxyBusy(true);
        try
        {
            await _proxyPoolStore.DeleteNodeAsync(row.Node);
            await LoadProxyNodesAsync();
            SetProxyStatus($"已删除代理节点：{row.Address}", true);
        }
        catch (Exception ex) { SetProxyStatus($"删除代理节点失败：{CliDiagnostics.Sanitize(ex.Message)}", false); }
        finally { SetProxyBusy(false); }
    }

    #endregion

    #endregion
}
