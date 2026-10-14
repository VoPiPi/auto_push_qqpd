using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class TelegramScheduleAdvancedWindow : Window
{
    private readonly ChannelSyncService? _channelSync;
    private readonly ObservableCollection<TelegramDistributionRuleEditorRow> _rules = [];

    public TelegramScheduleAdvancedSettings Settings { get; private set; } = TelegramScheduleAdvancedSettings.Default;

    public TelegramScheduleAdvancedWindow(
        TelegramScheduleAdvancedSettings? settings = null,
        ChannelSyncService? channelSync = null)
    {
        InitializeComponent();
        _channelSync = channelSync;
        RulesGrid.ItemsSource = _rules;
        CronBox.Text = settings?.CronExpression ?? TelegramScheduleAdvancedSettings.Default.CronExpression;
        KeywordBox.Text = settings?.Keyword ?? "";
        if (settings is not null)
        {
            foreach (var rule in settings.DistributionRules)
                _rules.Add(TelegramDistributionRuleEditorRow.From(rule));
        }
        UpdateRulesState();
        Loaded += async (_, _) => await LoadGuildsAsync();
        UpdateCronPreview();
    }

    private async Task LoadGuildsAsync()
    {
        if (_channelSync is null)
        {
            NewRuleButton.IsEnabled = false;
            ShowFormMessage("暂无频道缓存，请先同步频道数据后再配置分发规则。");
            return;
        }
        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync();
            if (guilds.Count == 0)
            {
                NewRuleButton.IsEnabled = false;
                ShowFormMessage("暂无频道缓存，请先同步频道数据后再配置分发规则。");
            }
            else
            {
                NewRuleButton.IsEnabled = true;
                HideFormMessage();
            }
        }
        catch (Exception ex)
        {
            NewRuleButton.IsEnabled = false;
            ShowFormMessage($"读取频道缓存失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private void CronBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCronPreview();

    private void UpdateCronPreview()
    {
        if (!IsInitialized || CronPreviewText is null) return;
        if (!TelegramCronSchedule.TryParse(CronBox.Text, out var cron, out var error))
        {
            CronPreviewText.Text = "";
            CronErrorText.Text = error;
            ConfirmButton.IsEnabled = false;
            return;
        }
        CronErrorText.Text = "";
        ConfirmButton.IsEnabled = true;
        CronPreviewText.Text = cron.GetNextOccurrence(DateTime.Now) is { } next
            ? $"下次执行：{next:yyyy-MM-dd HH:mm}"
            : "下次执行：未来 366 天内没有匹配时间";
    }

    private void RuleEditButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TelegramDistributionRuleEditorRow row) return;
        var dialog = new TelegramDistributionRuleWindow(_channelSync, row) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Rule is null) return;

        var index = _rules.IndexOf(row);
        if (index >= 0) _rules[index] = dialog.Rule;
        UpdateRulesState();
    }

    private void RuleDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TelegramDistributionRuleEditorRow row)
        {
            _rules.Remove(row);
            UpdateRulesState();
        }
    }

    private void NewRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TelegramDistributionRuleWindow(_channelSync) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Rule is null) return;

        _rules.Add(dialog.Rule);
        UpdateRulesState();
    }

    private void UpdateRulesState()
    {
        var hasRules = _rules.Count > 0;
        RulesGrid.Visibility = hasRules ? Visibility.Visible : Visibility.Collapsed;
        RulesEmptyState.Visibility = hasRules ? Visibility.Collapsed : Visibility.Visible;
        RuleCountText.Text = $"共 {_rules.Count} 条规则";
    }

    private void ShowFormMessage(string message)
    {
        FormMessageText.Text = message;
        FormMessageBorder.Visibility = Visibility.Visible;
    }

    private void HideFormMessage()
    {
        FormMessageText.Text = "";
        FormMessageBorder.Visibility = Visibility.Collapsed;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TelegramCronSchedule.TryParse(CronBox.Text, out var parsed, out var error))
        {
            CronErrorText.Text = error;
            return;
        }
        Settings = new TelegramScheduleAdvancedSettings(
            parsed.Expression,
            KeywordBox.Text.Trim(),
            _rules.Select((item, index) => item.ToDraft(index)).ToArray());
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

internal sealed class TelegramDistributionRuleEditorRow
{
    public TelegramDistributionRuleEditorRow(string messageType, string keyword, string guildId, string guildName, string channelId, string channelName)
    { MessageType = messageType; Keyword = keyword; GuildId = guildId; GuildName = guildName; ChannelId = channelId; ChannelName = channelName; }
    public string MessageType { get; }
    public string Keyword { get; }
    public string GuildId { get; }
    public string GuildName { get; }
    public string ChannelId { get; }
    public string ChannelName { get; }
    public string MessageTypeLabel => MessageType switch { "image" => "图片", "video" => "视频", _ => "文本" };
    public string KeywordDisplay => string.IsNullOrWhiteSpace(Keyword) ? "全部" : Keyword;
    public static TelegramDistributionRuleEditorRow From(TelegramDistributionRuleDraft rule) => new(rule.MessageType, rule.Keyword, rule.GuildId, rule.GuildName, rule.ChannelId, rule.ChannelName);
    public TelegramDistributionRuleDraft ToDraft(int sortOrder) => new(MessageType, Keyword, GuildId, GuildName, ChannelId, ChannelName, sortOrder);
}
