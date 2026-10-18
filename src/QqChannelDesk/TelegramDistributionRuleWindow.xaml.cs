using System.Windows;
using System.Windows.Controls;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class TelegramDistributionRuleWindow : Window
{
    private readonly ChannelSyncService? _channelSync;
    private readonly TelegramDistributionRuleEditorRow? _initialRule;
    private bool _suppressGuildSelection;

    internal TelegramDistributionRuleEditorRow? Rule { get; private set; }

    internal TelegramDistributionRuleWindow(
        ChannelSyncService? channelSync,
        TelegramDistributionRuleEditorRow? initialRule = null)
    {
        InitializeComponent();
        _channelSync = channelSync;
        _initialRule = initialRule;

        if (initialRule is not null)
        {
            Title = "编辑分发规则";
            PageHeading.Text = "编辑分发规则";
            ConfirmButton.Content = "保存修改";
            RuleTypeBox.SelectedIndex = initialRule.MessageType switch
            {
                "image" => 1,
                "video" => 2,
                _ => 0
            };
            RuleKeywordBox.Text = initialRule.Keyword;
        }

        Loaded += (_, _) => UiAsync.Run("TG 分发规则初始化", LoadGuildsAsync);
    }

    private async Task LoadGuildsAsync()
    {
        if (_channelSync is null)
        {
            DisableConfirm("暂无频道数据，请先刷新频道后再配置分发规则。");
            return;
        }

        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync();
            RuleGuildBox.ItemsSource = guilds;
            if (guilds.Count == 0)
            {
                DisableConfirm("暂无频道数据，请先刷新频道后再配置分发规则。");
                return;
            }

            ConfirmButton.IsEnabled = true;
            HideFormMessage();

            if (_initialRule is null || string.IsNullOrWhiteSpace(_initialRule.GuildId)) return;
            var guild = guilds.FirstOrDefault(item => item.Id == _initialRule.GuildId);
            if (guild is null) return;

            _suppressGuildSelection = true;
            RuleGuildBox.SelectedItem = guild;
            _suppressGuildSelection = false;
            await LoadChannelsAsync(guild.Id, _initialRule.ChannelId);
        }
        catch (Exception ex)
        {
            DisableConfirm($"读取频道数据失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private void RuleGuildBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("TG 分发规则频道切换", async () =>
        {
        if (_suppressGuildSelection) return;
        if (RuleGuildBox.SelectedItem is not ChannelChoice guild)
        {
            RuleChannelBox.ItemsSource = null;
            RuleChannelBox.SelectedItem = null;
            return;
        }

        await LoadChannelsAsync(guild.Id, null);
        });

    private async Task LoadChannelsAsync(string guildId, string? selectedChannelId)
    {
        RuleChannelBox.ItemsSource = null;
        RuleChannelBox.SelectedItem = null;
        if (_channelSync is null) return;

        try
        {
            var channels = await _channelSync.GetCachedChannelsAsync(guildId);
            RuleChannelBox.ItemsSource = channels;
            if (!string.IsNullOrWhiteSpace(selectedChannelId))
                RuleChannelBox.SelectedItem = channels.FirstOrDefault(item => item.Id == selectedChannelId);
            HideFormMessage();
        }
        catch (Exception ex)
        {
            ShowFormMessage($"读取版块数据失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        RuleValidationText.Text = "";
        if (RuleGuildBox.SelectedItem is not ChannelChoice guild ||
            RuleChannelBox.SelectedItem is not ChannelChoice channel)
        {
            RuleValidationText.Text = "请先选择目标频道和版块。";
            return;
        }

        Rule = new TelegramDistributionRuleEditorRow(
            (RuleTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "text",
            RuleKeywordBox.Text.Trim(),
            guild.Id,
            guild.Name,
            channel.Id,
            channel.Name);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void DisableConfirm(string message)
    {
        ConfirmButton.IsEnabled = false;
        ShowFormMessage(message);
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
}
