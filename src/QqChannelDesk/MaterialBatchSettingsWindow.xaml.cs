using QqChannelDesk.Services;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace QqChannelDesk;

public enum MaterialBatchSettingsMode
{
    Target,
    Time
}

public partial class MaterialBatchSettingsWindow : Window
{
    private readonly MaterialBatchSettingsMode _mode;
    private readonly ChannelSyncService _channelSync;
    private readonly int _selectedCount;
    private bool _loading;

    public ChannelChoice? SelectedGuild { get; private set; }
    public ChannelChoice? SelectedChannel { get; private set; }
    public DateTimeOffset? PublishAt { get; private set; }

    public MaterialBatchSettingsWindow(
        MaterialBatchSettingsMode mode,
        ChannelSyncService channelSync,
        int selectedCount)
    {
        InitializeComponent();
        _mode = mode;
        _channelSync = channelSync;
        _selectedCount = selectedCount;

        if (_mode == MaterialBatchSettingsMode.Target)
        {
            Title = "批量发布频道";
            Heading.Text = "批量发布频道";
            Description.Text = $"为所选的 {_selectedCount} 条素材设置目标频道与版块;仅保存目标,不立即发布。";
            TargetPanel.Visibility = Visibility.Visible;
            TimePanel.Visibility = Visibility.Collapsed;
            Loaded += async (_, _) => await LoadGuildsAsync();
        }
        else
        {
            Title = "批量设置发布时间";
            Heading.Text = "批量设置发布时间";
            Description.Text = $"为所选的 {_selectedCount} 条素材统一设置未来发布时间。";
            TargetPanel.Visibility = Visibility.Collapsed;
            TimePanel.Visibility = Visibility.Visible;
            var defaultTime = DateTimeOffset.Now.AddHours(1);
            PublishDatePicker.SelectedDate = defaultTime.LocalDateTime.Date;
            PublishTimeBox.Text = defaultTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    private async Task LoadGuildsAsync()
    {
        _loading = true;
        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync();
            if (guilds.Count == 0)
            {
                FormMessage.Text = "当前账号没有频道缓存,请先同步频道数据。";
                return;
            }

            GuildCombo.ItemsSource = guilds;
            GuildCombo.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            FormMessage.Text = $"读取频道缓存失败:{CliDiagnostics.Sanitize(ex.Message)}";
        }
        finally
        {
            _loading = false;
            if (GuildCombo.SelectedItem is ChannelChoice guild) await LoadChannelsAsync(guild);
        }
    }

    private async void GuildCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GuildCombo.SelectedItem is not ChannelChoice guild) return;
        await LoadChannelsAsync(guild);
    }

    private async Task LoadChannelsAsync(ChannelChoice guild)
    {
        try
        {
            var channels = await _channelSync.GetCachedChannelsAsync(guild.Id);
            if ((GuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id) return;
            ChannelCombo.ItemsSource = channels;
            ChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;
            if (channels.Count == 0) FormMessage.Text = "所选频道下没有可用版块。";
        }
        catch (Exception ex)
        {
            FormMessage.Text = $"读取版块缓存失败:{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == MaterialBatchSettingsMode.Target)
        {
            if (GuildCombo.SelectedItem is not ChannelChoice guild || ChannelCombo.SelectedItem is not ChannelChoice channel)
            {
                FormMessage.Text = "请选择目标频道和版块。";
                return;
            }

            SelectedGuild = guild;
            SelectedChannel = channel;
            DialogResult = true;
            return;
        }

        PublishAt = ResolvePublishAt(PublishDatePicker.SelectedDate, PublishTimeBox.Text, DateTimeOffset.Now);
        if (PublishAt is null)
        {
            FormMessage.Text = "请选择有效的未来发布时间,格式为 时:分:秒。";
            return;
        }

        DialogResult = true;
    }

    internal static DateTimeOffset? ResolvePublishAt(DateTime? selectedDate, string timeText, DateTimeOffset now)
    {
        if (selectedDate is not { } date) return null;
        if (!TimeSpan.TryParseExact(timeText?.Trim() ?? "", @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var time) &&
            !TimeSpan.TryParseExact(timeText?.Trim() ?? "", @"h\:mm\:ss", CultureInfo.InvariantCulture, out time))
        {
            return null;
        }

        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return null;
        var local = date.Date + time;
        var publishAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return publishAt > now ? publishAt : null;
    }
}
