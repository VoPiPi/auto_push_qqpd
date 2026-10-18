using System.Globalization;
using System.Windows;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class PublishGuildBudgetEditorWindow : Window
{
    private readonly ChannelSyncService? _channelSync;
    private readonly PublishGuildBudgetDraft? _initialBudget;

    internal PublishGuildBudgetDraft? Budget { get; private set; }

    internal PublishGuildBudgetEditorWindow(ChannelSyncService? channelSync, PublishGuildBudgetDraft? initialBudget = null)
    {
        InitializeComponent();
        _channelSync = channelSync;
        _initialBudget = initialBudget;

        if (initialBudget is not null)
        {
            Title = "编辑频道预算";
            PageHeading.Text = "编辑频道预算";
            ConfirmButton.Content = "保存修改";
            EnabledBox.IsChecked = initialBudget.Enabled;
            DailyLimitBox.Text = initialBudget.DailyLimit.ToString(CultureInfo.InvariantCulture);
        }

        Loaded += (_, _) => UiAsync.Run("频道预算编辑初始化", LoadGuildsAsync);
    }

    private async Task LoadGuildsAsync()
    {
        var guilds = new List<ChannelChoice>();
        if (_channelSync is not null)
        {
            try
            {
                guilds.AddRange(await _channelSync.GetCachedGuildsAsync());
            }
            catch (Exception ex)
            {
                ShowFormMessage($"读取频道数据失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }
        }

        if (_initialBudget is { } initial && guilds.All(item => item.Id != initial.GuildId))
            guilds.Insert(0, new ChannelChoice(initial.GuildId, initial.GuildId, ""));

        GuildBox.ItemsSource = guilds;
        if (guilds.Count == 0)
        {
            ConfirmButton.IsEnabled = false;
            ShowFormMessage("暂无频道数据，请先在运行总览刷新频道，再配置频道预算。");
            return;
        }

        ConfirmButton.IsEnabled = true;
        if (_initialBudget is not { } budget) return;
        GuildBox.SelectedItem = guilds.FirstOrDefault(item => item.Id == budget.GuildId);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        RuleValidationText.Text = "";
        if (GuildBox.SelectedItem is not ChannelChoice guild)
        {
            RuleValidationText.Text = "请先选择目标频道。";
            return;
        }

        if (!int.TryParse(DailyLimitBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dailyLimit) ||
            dailyLimit is < 1 or > 100_000)
        {
            RuleValidationText.Text = "每日自动发布上限必须是 1-100000 之间的整数。";
            return;
        }

        Budget = new PublishGuildBudgetDraft(guild.Id, EnabledBox.IsChecked == true, dailyLimit);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowFormMessage(string message)
    {
        FormMessageText.Text = message;
        FormMessageBorder.Visibility = Visibility.Visible;
    }
}
