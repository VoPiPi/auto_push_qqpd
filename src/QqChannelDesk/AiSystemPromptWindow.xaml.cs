using System.Windows;
using QqChannelDesk.Services.Collection;

namespace QqChannelDesk;

public partial class AiSystemPromptWindow : Window
{
    public string SystemPrompt { get; private set; } = "";
    internal string PromptText => PromptBox.Text;
    internal bool IsRestoreDefaultChecked => RestoreDefaultCheckBox.IsChecked == true;

    public AiSystemPromptWindow(string currentPrompt)
    {
        InitializeComponent();
        PromptBox.Text = string.IsNullOrWhiteSpace(currentPrompt)
            ? AiSystemPrompts.DefaultArticle
            : currentPrompt.Trim();
        RestoreDefaultCheckBox.IsChecked = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var prompt = NormalizePrompt(PromptBox.Text);
        SystemPrompt = RestoreDefaultCheckBox.IsChecked == true
            || prompt.Length == 0
            || prompt == NormalizePrompt(AiSystemPrompts.DefaultArticle)
                ? ""
                : prompt;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string NormalizePrompt(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
