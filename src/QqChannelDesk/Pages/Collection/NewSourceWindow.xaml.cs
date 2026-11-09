using QqChannelDesk.Services.Collection;
using System.Windows;

namespace QqChannelDesk.Pages.Collection;

public partial class NewSourceWindow : Window
{
    private readonly CollectionSource? _source;
    private bool _initialized;

    public NewSourceWindow()
    {
        InitializeComponent();
        _initialized = true;
        TitleText.Text = "新建来源";
    }

    public NewSourceWindow(CollectionSource source) : this()
    {
        _source = source;
        Title = "编辑来源";
        TitleText.Text = "编辑来源";
        SaveButton.Content = "保存修改";
        SelectType(source.SourceType);
        NameBox.Text = source.Name;
        UrlBox.Text = source.EntryUrl;
        FrequencyBox.Text = source.FrequencyMinutes.ToString();
        LimitBox.Text = source.MaxItemsPerRun.ToString();
        IncludeBox.Text = source.IncludeKeywords;
        ExcludeBox.Text = source.ExcludeKeywords;
        RuleConfigBox.Text = source.RuleConfigJson;
        AuthBox.IsChecked = source.AuthConfirmed;
        AiBox.IsChecked = source.AiFallbackEnabled;
    }

    public CollectionSourceDraft Draft { get; private set; } = new("", "rss");
    public bool StartEnabled { get; private set; }

    /// <summary>用户选择 TG 类型并点击跳转时触发，由调用方打开 TG 定时采集页签。</summary>
    public event Action? TgScheduleRequested;

    private void TypeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        var type = SelectedType;
        if (type == "telegram")
        {
            FormPanel.Visibility = Visibility.Collapsed;
            TgHintPanel.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = false;
            return;
        }

        FormPanel.Visibility = Visibility.Visible;
        TgHintPanel.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;
        FrequencyBox.Text = type == "rss" ? "30" : "60";
        LimitBox.Text = type == "single-page" ? "1" : "20";
    }

    internal void TgJumpButton_Click(object sender, RoutedEventArgs e) => TgScheduleRequested?.Invoke();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var url = UrlBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageText.Text = "请输入来源名称。";
            return;
        }

        if (!QqChannelDesk.Services.PublicArticleFetcher.TryNormalizePublicUrl(url, out _, out var error))
        {
            MessageText.Text = error;
            return;
        }

        if (AuthBox.IsChecked != true)
        {
            MessageText.Text = "请先确认内容使用授权。";
            return;
        }

        if (!int.TryParse(FrequencyBox.Text, out var frequency) || frequency < 30 || frequency > 60 * 24 * 30)
        {
            MessageText.Text = "采集频率最小 30 分钟。";
            return;
        }

        if (!int.TryParse(LimitBox.Text, out var limit) || limit < 1 || limit > 200)
        {
            MessageText.Text = "单次上限必须在 1 到 200 之间。";
            return;
        }

        var ruleConfig = RuleConfigBox.Text.Trim();
        if (ruleConfig.Length == 0) ruleConfig = "{}";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(ruleConfig);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                MessageText.Text = "解析规则必须是 JSON 对象。";
                return;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            MessageText.Text = "解析规则 JSON 格式无效。";
            return;
        }

        Draft = new CollectionSourceDraft(
            name,
            SelectedType,
            url,
            Enabled: _source?.Enabled ?? true,
            AuthConfirmed: true,
            AuthConfirmedAt: DateTimeOffset.Now,
            AiFallbackEnabled: AiBox.IsChecked == true,
            FrequencyMinutes: frequency,
            RuleConfigJson: ruleConfig,
            MaxItemsPerRun: limit,
            IncludeKeywords: IncludeBox.Text.Trim(),
            ExcludeKeywords: ExcludeBox.Text.Trim(),
            Id: _source?.Id);
        StartEnabled = _source is null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private string SelectedType =>
        (TypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "rss";

    private void SelectType(string type)
    {
        foreach (var item in TypeBox.Items.Cast<System.Windows.Controls.ComboBoxItem>())
        {
            if (Equals(item.Tag?.ToString(), type))
            {
                TypeBox.SelectedItem = item;
                return;
            }
        }
    }
}
