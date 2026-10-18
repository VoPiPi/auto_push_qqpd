using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using QqChannelDesk.Services;

namespace QqChannelDesk;

public partial class PublishPolicyEditorWindow : Window
{
    private readonly ChannelSyncService? _channelSync;
    private readonly PublishPolicyDraft? _initialPolicy;
    private bool _suppressGuildSelection;

    internal PublishPolicyDraft? Policy { get; private set; }

    internal PublishPolicyEditorWindow(ChannelSyncService? channelSync, PublishPolicyDraft? initialPolicy = null)
    {
        InitializeComponent();
        _channelSync = channelSync;
        _initialPolicy = initialPolicy;

        if (initialPolicy is not null)
        {
            Title = "编辑发布策略";
            PageHeading.Text = "编辑发布策略";
            ConfirmButton.Content = "保存修改";
            EnabledBox.IsChecked = initialPolicy.Enabled;
            DailyLimitBox.Text = initialPolicy.DailyLimit.ToString(CultureInfo.InvariantCulture);
            WindowStartBox.Text = initialPolicy.WindowStart;
            WindowEndBox.Text = initialPolicy.WindowEnd;
            MinIntervalBox.Text = initialPolicy.MinIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            MaxIntervalBox.Text = initialPolicy.MaxIntervalMinutes.ToString(CultureInfo.InvariantCulture);
            QueueLimitBox.Text = initialPolicy.QueueLimit.ToString(CultureInfo.InvariantCulture);
            ConcurrencyBox.Text = initialPolicy.MaxConcurrency.ToString(CultureInfo.InvariantCulture);
            RateLimitBaseBox.Text = initialPolicy.RateLimitBaseMinutes.ToString(CultureInfo.InvariantCulture);
            RateLimitMaxBox.Text = initialPolicy.RateLimitMaxMinutes.ToString(CultureInfo.InvariantCulture);
        }

        Loaded += (_, _) => UiAsync.Run("发布策略编辑初始化", LoadGuildsAsync);
    }

    private async Task LoadGuildsAsync()
    {
        var guilds = new List<ChannelChoice>();
        var cacheAvailable = _channelSync is not null;
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

        if (_initialPolicy is { } initial && guilds.All(item => item.Id != initial.GuildId))
            guilds.Insert(0, new ChannelChoice(initial.GuildId, initial.GuildId, ""));

        GuildBox.ItemsSource = guilds;
        if (guilds.Count == 0)
        {
            ConfirmButton.IsEnabled = false;
            ShowFormMessage("暂无频道数据，请先在运行总览刷新频道，再配置发布策略。");
            return;
        }

        ConfirmButton.IsEnabled = true;
        if (!cacheAvailable) ShowFormMessage("频道缓存不可用，只能沿用已保存的频道和版块编号。");

        if (_initialPolicy is not { } policy) return;
        var guild = guilds.FirstOrDefault(item => item.Id == policy.GuildId);
        if (guild is null) return;

        _suppressGuildSelection = true;
        GuildBox.SelectedItem = guild;
        _suppressGuildSelection = false;
        await LoadChannelsAsync(policy.GuildId, policy.ChannelId);
    }

    private void GuildBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("发布策略频道切换", async () =>
        {
            if (_suppressGuildSelection) return;
            if (GuildBox.SelectedItem is not ChannelChoice guild)
            {
                ChannelBox.ItemsSource = null;
                ChannelBox.SelectedItem = null;
                return;
            }

            await LoadChannelsAsync(guild.Id, null);
        });

    private async Task LoadChannelsAsync(string guildId, string? selectedChannelId)
    {
        var channels = new List<ChannelChoice>();
        if (_channelSync is not null)
        {
            try
            {
                channels.AddRange(await _channelSync.GetCachedChannelsAsync(guildId));
            }
            catch (Exception ex)
            {
                ShowFormMessage($"读取版块数据失败：{CliDiagnostics.Sanitize(ex.Message)}");
            }
        }

        if (_initialPolicy is { } initial &&
            initial.GuildId == guildId &&
            channels.All(item => item.Id != initial.ChannelId))
        {
            channels.Insert(0, new ChannelChoice(initial.ChannelId, initial.ChannelId, ""));
        }

        ChannelBox.ItemsSource = channels;
        ChannelBox.SelectedItem = string.IsNullOrWhiteSpace(selectedChannelId)
            ? null
            : channels.FirstOrDefault(item => item.Id == selectedChannelId);

        if (channels.Count == 0)
            ShowFormMessage("该频道暂无可选版块，请先在运行总览刷新频道，再配置发布策略。");
        else
            HideFormMessage();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        RuleValidationText.Text = "";
        if (GuildBox.SelectedItem is not ChannelChoice guild || ChannelBox.SelectedItem is not ChannelChoice channel)
        {
            RuleValidationText.Text = "请先选择目标频道和版块。";
            return;
        }

        if (!TryReadInt(DailyLimitBox, "每日发布上限", 1, 100_000, out var dailyLimit, out var error) ||
            !TryReadInt(MinIntervalBox, "最小间隔", 1, 10_080, out var minInterval, out error) ||
            !TryReadInt(MaxIntervalBox, "最大间隔", 1, 10_080, out var maxInterval, out error) ||
            !TryReadInt(QueueLimitBox, "排队上限", 1, 100_000, out var queueLimit, out error) ||
            !TryReadInt(ConcurrencyBox, "同时执行数", 1, 32, out var concurrency, out error) ||
            !TryReadInt(RateLimitBaseBox, "限流退避基础", 1, 1440, out var rateLimitBase, out error) ||
            !TryReadInt(RateLimitMaxBox, "限流退避上限", 1, 10_080, out var rateLimitMax, out error))
        {
            RuleValidationText.Text = error;
            return;
        }

        var windowStart = WindowStartBox.Text.Trim();
        var windowEnd = WindowEndBox.Text.Trim();
        if (!IsValidTime(windowStart) || !IsValidTime(windowEnd))
        {
            RuleValidationText.Text = "发布窗口时间格式必须是 HH:mm，例如 09:00。";
            return;
        }

        if (string.Equals(windowStart, windowEnd, StringComparison.Ordinal))
        {
            RuleValidationText.Text = "发布窗口开始和结束时间不能相同。";
            return;
        }

        if (maxInterval < minInterval)
        {
            RuleValidationText.Text = "最大间隔不能小于最小间隔。";
            return;
        }

        if (rateLimitMax < rateLimitBase)
        {
            RuleValidationText.Text = "限流退避上限不能小于基础值。";
            return;
        }

        Policy = new PublishPolicyDraft(
            guild.Id,
            channel.Id,
            EnabledBox.IsChecked == true,
            windowStart,
            windowEnd,
            minInterval,
            maxInterval,
            dailyLimit,
            queueLimit,
            concurrency,
            rateLimitBase,
            rateLimitMax);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool IsValidTime(string value) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool TryReadInt(
        TextBox box,
        string label,
        int min,
        int max,
        out int value,
        out string error)
    {
        error = "";
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"{label}必须是 {min}-{max} 之间的整数。";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{label}必须是 {min}-{max} 之间的整数。";
            return false;
        }

        return true;
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
