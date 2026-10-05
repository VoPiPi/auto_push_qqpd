using QqChannelDesk.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QqChannelDesk.Pages;

public partial class MaterialsPage : UserControl
{
    private const int PageSize = 100;
    private readonly ContentLibraryStore _store;
    private readonly ChannelSyncService _channelSync;
    private readonly MediaStorageService _mediaStorage;
    private bool _loading;
    private IReadOnlyList<MaterialRecord> _allMaterials = [];
    private int _currentPage = 1;

    public event Action<MaterialRecord>? PublishMaterialRequested;

    public MaterialsPage(ContentLibraryStore store, ChannelSyncService channelSync, MediaStorageService? mediaStorage = null)
    {
        InitializeComponent();
        _store = store;
        _channelSync = channelSync;
        _mediaStorage = mediaStorage ?? new MediaStorageService();
        Loaded += async (_, _) => await LoadMaterialsAsync();
    }

    public async Task LoadMaterialsAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var filter = MaterialStatusFilter.SelectedItem as ComboBoxItem;
            _allMaterials = await _store.GetMaterialsAsync(MaterialSearchBox.Text, filter?.Tag?.ToString());
            _currentPage = 1;
            RenderCurrentPage();
        }
        catch (Exception ex)
        {
            _allMaterials = [];
            MaterialsGrid.ItemsSource = null;
            UpdatePagination();
            MaterialsStatus.Text = $"读取素材仓库失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally { _loading = false; }
    }

    private void RenderCurrentPage()
    {
        var pageCount = GetPageCount(_allMaterials.Count);
        _currentPage = Math.Clamp(_currentPage, 1, pageCount);
        MaterialsGrid.ItemsSource = _allMaterials.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToArray();
        MaterialsStatus.Text = $"共 {_allMaterials.Count} 条";
        UpdatePagination(pageCount);
    }

    private void UpdatePagination(int? pageCount = null)
    {
        var pages = pageCount ?? GetPageCount(_allMaterials.Count);
        PageText.Text = $"{_currentPage} / {pages}";
        PreviousPageButton.IsEnabled = _currentPage > 1 && _allMaterials.Count > 0;
        NextPageButton.IsEnabled = _currentPage < pages && _allMaterials.Count > 0;
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
        if (_currentPage >= GetPageCount(_allMaterials.Count)) return;
        _currentPage++;
        RenderCurrentPage();
    }

    private async Task EditMaterialAsync(MaterialRecord? existing)
    {
        var editor = new MaterialEditorWindow(_channelSync, existing, _mediaStorage) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Material is null) return;
        try
        {
            await _store.SaveMaterialAsync(editor.Material);
            await LoadMaterialsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), CliDiagnostics.Sanitize(ex.Message), "保存素材失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AddMaterialButton_Click(object sender, RoutedEventArgs e) => await EditMaterialAsync(null);

    private async void EditMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MaterialRecord record) await EditMaterialAsync(record);
    }

    private async void DeleteMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MaterialRecord record) return;
        if (MessageBox.Show(Window.GetWindow(this), $"确定删除素材“{record.Title}”？素材仅标记为删除，不会从数据库物理移除。", "删除素材", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _store.UpdateMaterialStatusAsync(record.Id, "delete");
        await LoadMaterialsAsync();
    }

    private void PublishMaterialButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MaterialRecord record) return;
        var error = ValidateMaterialForPublish(record);
        if (error.Length > 0)
        {
            MessageBox.Show(Window.GetWindow(this), error, "素材不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PublishMaterialRequested?.Invoke(record);
    }

    public static string ValidateMaterialForPublish(MaterialRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Title)) return "发布素材必须填写标题。";
        if (string.IsNullOrWhiteSpace(record.Content)) return "发布素材必须填写正文。";
        if (string.IsNullOrWhiteSpace(record.GuildId) || string.IsNullOrWhiteSpace(record.ChannelId)) return "请先编辑素材并指定发布频道和版块。";
        if (record.Type is not ("text" or "image" or "video")) return "素材类型无效。";
        return MaterialMediaValidator.Validate(record.Type, record.Title, record.MediaLinks);
    }

    public async Task MarkMaterialPublishedAsync(long id, string? link)
    {
        await _store.UpdateMaterialStatusAsync(id, "published", link);
        await LoadMaterialsAsync();
    }

    public async Task SetMaterialStatusAsync(long id, string status)
    {
        await _store.UpdateMaterialStatusAsync(id, status);
        await LoadMaterialsAsync();
    }

    private async void SearchMaterialsButton_Click(object sender, RoutedEventArgs e) => await LoadMaterialsAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await LoadMaterialsAsync();
    }
}
