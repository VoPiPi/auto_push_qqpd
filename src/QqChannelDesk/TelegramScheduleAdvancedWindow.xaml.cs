using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class TelegramScheduleAdvancedWindow : Window
{
    private readonly ChannelSyncService? _channelSync;
    private readonly ObservableCollection<TelegramDistributionRuleEditorRow> _rules = [];
    private TelegramDistributionRuleEditorRow? _editingRule;

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
        Loaded += async (_, _) => await LoadGuildsAsync();
        UpdateCronPreview();
    }

    private async Task LoadGuildsAsync()
    {
        if (_channelSync is null)
        {
            RuleGuildBox.ItemsSource = Array.Empty<ChannelChoice>();
            CronErrorText.Text = "暂无频道缓存，请先同步频道数据后再配置分发规则。";
            return;
        }
        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync();
            RuleGuildBox.ItemsSource = guilds;
            if (guilds.Count == 0)
                CronErrorText.Text = "暂无频道缓存，请先同步频道数据后再配置分发规则。";
        }
        catch (Exception ex)
        {
            CronErrorText.Text = $"读取频道缓存失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private async void RuleGuildBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleGuildBox.SelectedItem is not ChannelChoice guild || _channelSync is null) return;
        try { RuleChannelBox.ItemsSource = await _channelSync.GetCachedChannelsAsync(guild.Id); }
        catch (Exception ex) { CronErrorText.Text = $"读取板块缓存失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
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

    private void RuleAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (RuleGuildBox.SelectedItem is not ChannelChoice guild || RuleChannelBox.SelectedItem is not ChannelChoice channel)
        {
            CronErrorText.Text = "请先选择目标频道和板块。";
            return;
        }
        var row = new TelegramDistributionRuleEditorRow(
            (RuleTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "text",
            RuleKeywordBox.Text.Trim(), guild.Id, guild.Name, channel.Id, channel.Name);
        if (_editingRule is null) _rules.Add(row);
        else
        {
            var index = _rules.IndexOf(_editingRule);
            if (index >= 0) _rules[index] = row;
            _editingRule = null;
            RuleAddButton.Content = "添加规则";
        }
        ClearRuleInputs();
    }

    private void RuleEditButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TelegramDistributionRuleEditorRow row) return;
        _editingRule = row;
        RuleTypeBox.SelectedIndex = row.MessageType switch { "image" => 1, "video" => 2, _ => 0 };
        RuleKeywordBox.Text = row.Keyword;
        RuleGuildBox.SelectedItem = (RuleGuildBox.ItemsSource as IEnumerable<ChannelChoice>)?.FirstOrDefault(item => item.Id == row.GuildId);
        _ = RestoreRuleChannelAsync(row);
        RuleAddButton.Content = "保存规则";
    }

    private async Task RestoreRuleChannelAsync(TelegramDistributionRuleEditorRow row)
    {
        if (_channelSync is null || string.IsNullOrWhiteSpace(row.GuildId)) return;
        try
        {
            var channels = await _channelSync.GetCachedChannelsAsync(row.GuildId);
            RuleChannelBox.ItemsSource = channels;
            RuleChannelBox.SelectedItem = channels.FirstOrDefault(item => item.Id == row.ChannelId);
        }
        catch (Exception ex)
        {
            CronErrorText.Text = $"读取板块缓存失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private void RuleDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TelegramDistributionRuleEditorRow row)
        {
            _rules.Remove(row);
            if (ReferenceEquals(_editingRule, row)) { _editingRule = null; RuleAddButton.Content = "添加规则"; ClearRuleInputs(); }
        }
    }

    private void ClearRuleInputs()
    {
        RuleKeywordBox.Clear();
        RuleGuildBox.SelectedItem = null;
        RuleChannelBox.ItemsSource = null;
        RuleChannelBox.SelectedItem = null;
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
