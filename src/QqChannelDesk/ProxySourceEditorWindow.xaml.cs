using QqChannelDesk.Services;
using System.Windows;

namespace QqChannelDesk;

public partial class ProxySourceEditorWindow : Window
{
    private readonly ProxySource? _source;
    public string SourceName => NameBox.Text.Trim();
    public string SourceUrl => UrlBox.Text.Trim();
    public bool SourceIsEnabled => EnabledBox.IsChecked == true;

    public ProxySourceEditorWindow(ProxySource? source = null)
    {
        InitializeComponent();
        _source = source;
        if (source is not null)
        {
            Title = "编辑代理源";
            NameBox.Text = source.DisplayName;
            UrlBox.Text = source.SourceUrl;
            EnabledBox.IsChecked = source.IsEnabled;
        }
        NameBox.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SourceName.Length == 0)
        {
            MessageBox.Show(this, "请输入代理源别名。", "代理源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SourceUrl.Length == 0)
        {
            MessageBox.Show(this, "请输入代理源地址。", "代理源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            _ = ProxyPoolService.NormalizeSourceUrl(SourceUrl);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "代理源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
