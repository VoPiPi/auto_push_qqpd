using QqChannelDesk.Services;
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
    private readonly PublicArticleFetcher _fetcher = new();
    private CollectedItem? _selectedItem;
    private ContentDraft? _selectedDraft;
    private IReadOnlyList<CollectedItem> _allItems = [];
    private IReadOnlyList<ContentDraft> _allDrafts = [];
    private int _itemsPage = 1;
    private int _draftsPage = 1;
    private bool _loading;
    private string _parseError = "";

    public event Action<ContentDraft>? PublishDraftRequested;
    public ContentCollectionPage(ContentLibraryStore? store = null)
    {
        InitializeComponent();
        _store = store ?? new ContentLibraryStore();
        Loaded += async (_, _) => { await LoadItemsAsync(); await LoadDraftsAsync(); };
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

    public async Task LoadDraftsAsync()
    {
        try
        {
            _allDrafts = await _store.GetDraftsAsync(DraftSearchBox.Text);
            _draftsPage = 1;
            RenderDraftsPage();
        }
        catch (Exception ex)
        {
            _allDrafts = [];
            DraftsGrid.ItemsSource = null;
            UpdateDraftsPagination();
            DraftsStatus.Text = $"读取草稿箱失败：{CliDiagnostics.Sanitize(ex.Message)}";
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

    private void RenderDraftsPage()
    {
        var pageCount = GetPageCount(_allDrafts.Count);
        _draftsPage = Math.Clamp(_draftsPage, 1, pageCount);
        DraftsGrid.ItemsSource = _allDrafts.Skip((_draftsPage - 1) * PageSize).Take(PageSize).ToArray();
        DraftsStatus.Text = $"共 {_allDrafts.Count} 条";
        UpdateDraftsPagination(pageCount);
    }

    private void UpdateItemsPagination(int? pageCount = null)
    {
        var pages = pageCount ?? GetPageCount(_allItems.Count);
        ItemsPageText.Text = $"{_itemsPage} / {pages}";
        ItemsPreviousPageButton.IsEnabled = _itemsPage > 1 && _allItems.Count > 0;
        ItemsNextPageButton.IsEnabled = _itemsPage < pages && _allItems.Count > 0;
    }

    private void UpdateDraftsPagination(int? pageCount = null)
    {
        var pages = pageCount ?? GetPageCount(_allDrafts.Count);
        DraftsPageText.Text = $"{_draftsPage} / {pages}";
        DraftsPreviousPageButton.IsEnabled = _draftsPage > 1 && _allDrafts.Count > 0;
        DraftsNextPageButton.IsEnabled = _draftsPage < pages && _allDrafts.Count > 0;
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

    private void DraftsPreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draftsPage <= 1) return;
        _draftsPage--;
        RenderDraftsPage();
    }

    private void DraftsNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draftsPage >= GetPageCount(_allDrafts.Count)) return;
        _draftsPage++;
        RenderDraftsPage();
    }

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PublicArticleFetcher.TryNormalizePublicUrl(UrlBox.Text, out var normalized, out var error))
        {
            MessageBox.Show(Window.GetWindow(this), error, "链接无效", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            ItemMessage.Text = result.Succeeded
                ? $"已提取自 {result.SourceHost}。请检查并确认内容使用权限后保存。"
                : $"{result.Error} 可手动补全标题和正文后保存。";
            ItemMessage.Foreground = result.Succeeded ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.DarkGoldenrod;
        }
        finally { FetchButtonIsEnabled(true); }
    }

    private void FetchButtonIsEnabled(bool enabled)
    {
        FetchButton.IsEnabled = enabled;
    }

    private void NewItemButton_Click(object sender, RoutedEventArgs e)
    {
        ItemsGrid.SelectedItem = null;
        _selectedItem = null;
        UrlBox.Clear(); ItemUrlBox.Clear(); ItemTitleBox.Clear(); ItemContentBox.Clear(); ItemNotesBox.Clear(); ItemTagsBox.Clear();
        ItemMessage.Text = "手动素材不需要来源链接。";
        _parseError = "";
    }

    private async void SaveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ItemTitleBox.Text) && string.IsNullOrWhiteSpace(ItemContentBox.Text))
        {
            MessageBox.Show(Window.GetWindow(this), "标题和正文至少填写一项。", "内容为空", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var model = new CollectedItemDraft(normalizedUrl, host, ItemTitleBox.Text, ItemContentBox.Text, status, ItemNotesBox.Text, tags, _parseError, _selectedItem?.Id);
        try
        {
            if (_selectedItem is null) _ = await _store.SaveItemAsync(model);
            else await _store.UpdateItemAsync(model);
            ItemMessage.Text = "素材已保存。";
            ItemMessage.Foreground = System.Windows.Media.Brushes.SeaGreen;
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
        await _store.UpdateItemAsync(new CollectedItemDraft(_selectedItem.Url, _selectedItem.SourceHost, ItemTitleBox.Text, ItemContentBox.Text, status, ItemNotesBox.Text, tags, _selectedItem.ParseError, _selectedItem.Id));
        await LoadItemsAsync();
    }

    private async void CreateDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ItemTitleBox.Text) && string.IsNullOrWhiteSpace(ItemContentBox.Text))
        {
            MessageBox.Show(Window.GetWindow(this), "标题和正文至少填写一项后才能创建草稿。", "内容为空", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var source = ItemUrlBox.Text.Trim();
        if (source.Length > 0 && MessageBox.Show(Window.GetWindow(this), "请确认你有权保存、编辑并发布这份内容。来源链接会保留在草稿中，但不会自动加入帖子正文。继续创建草稿？", "内容使用提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var id = await _store.CreateDraftAsync(_selectedItem?.Id, ItemTitleBox.Text, ItemContentBox.Text, source);
        await LoadItemsAsync();
        await LoadDraftsAsync();
        WorkspaceTabs.SelectedIndex = 1;
        DraftsGrid.SelectedItem = ((IEnumerable<ContentDraft>)DraftsGrid.ItemsSource!).FirstOrDefault(draft => draft.Id == id);
    }

    private async void DeleteItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is null) return;
        if (MessageBox.Show(Window.GetWindow(this), "删除该采集素材？关联草稿会保留，但不再关联此素材。", "删除素材", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _store.DeleteItemAsync(_selectedItem.Id);
        _selectedItem = null;
        await LoadItemsAsync();
    }

    private void DraftsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDraft = DraftsGrid.SelectedItem as ContentDraft;
        if (_selectedDraft is null) return;
        DraftTitleBox.Text = _selectedDraft.Title;
        DraftContentBox.Text = _selectedDraft.Content;
        DraftSourceBox.Text = _selectedDraft.SourceUrl;
        DraftMessage.Text = $"{_selectedDraft.PublishStatus}{(_selectedDraft.LastPublishedAt is null ? "" : $" · 最近發布 { _selectedDraft.LastPublishedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}")}";
    }

    private async void SaveDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDraft is null) return;
        await _store.UpdateDraftAsync(_selectedDraft with { Title = DraftTitleBox.Text, Content = DraftContentBox.Text });
        DraftMessage.Text = "草稿已保存。";
        await LoadDraftsAsync();
    }

    private void PublishDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDraft is null) return;
        PublishDraftRequested?.Invoke(_selectedDraft with { Title = DraftTitleBox.Text, Content = DraftContentBox.Text });
    }

    private async void DeleteDraftButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDraft is null) return;
        if (MessageBox.Show(Window.GetWindow(this), "确定删除此草稿？", "删除草稿", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _store.DeleteDraftAsync(_selectedDraft.Id);
        _selectedDraft = null;
        await LoadDraftsAsync();
    }

    private async void SearchItemsButton_Click(object sender, RoutedEventArgs e) => await LoadItemsAsync();
    private async void ItemStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await LoadItemsAsync(); }
    private async void SearchDraftsButton_Click(object sender, RoutedEventArgs e) => await LoadDraftsAsync();
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ReferenceEquals(sender, ItemSearchBox)) await LoadItemsAsync();
        else if (ReferenceEquals(sender, DraftSearchBox)) await LoadDraftsAsync();
        else await LoadDraftsAsync();
    }
}
