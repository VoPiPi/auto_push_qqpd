using System.Windows;

namespace QqChannelDesk;

public partial class MaterialImportConfirmationWindow : Window
{
    public MaterialImportConfirmationWindow(int importCount, int skipCount)
    {
        InitializeComponent();
        MessageText.Text = $"将导入 {importCount} 条，跳过 {skipCount} 条。";
    }

    private void SkipAndImportButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
