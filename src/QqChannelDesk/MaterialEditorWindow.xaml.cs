using QqChannelDesk.Services;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace QqChannelDesk;

public partial class MaterialEditorWindow : Window
{
    private readonly ChannelSyncService _channelSync;
    private readonly MediaStorageService _mediaStorage;
    private readonly MaterialRecord? _existing;
    private bool _loading;
    private bool _settingType;
    private bool _settingSchedule;

    public MaterialDraft? Material { get; private set; }

    public MaterialEditorWindow(ChannelSyncService channelSync, MaterialRecord? existing = null, MediaStorageService? mediaStorage = null)
    {
        InitializeComponent();
        _channelSync = channelSync;
        _mediaStorage = mediaStorage ?? new MediaStorageService();
        _existing = existing;
        if (existing is not null)
        {
            Heading.Text = "编辑素材";
            TitleBox.Text = existing.Title;
            ContentBox.Text = existing.Content;
            MediaLinksBox.Text = string.Join(Environment.NewLine, existing.MediaLinks);
            TypeCombo.SelectedIndex = existing.Type switch { "image" => 1, "video" => 2, _ => 0 };
            if (existing.PublishAt is { } scheduled)
            {
                _settingSchedule = true;
                ScheduleEnabledCheckBox.IsChecked = true;
                PublishDatePicker.SelectedDate = scheduled.LocalDateTime.Date;
                PublishTimeBox.Text = scheduled.LocalDateTime.ToString("HH:mm:ss");
                ScheduleControls.Visibility = Visibility.Visible;
                _settingSchedule = false;
            }
        }
        Loaded += async (_, _) => await LoadGuildsAsync();
    }

    private async Task LoadGuildsAsync()
    {
        _loading = true;
        try
        {
            var guilds = await _channelSync.GetCachedGuildsAsync();
            GuildCombo.ItemsSource = guilds;
            GuildCombo.SelectedItem = _existing is null ? null : guilds.FirstOrDefault(item => item.Id == _existing.GuildId);
            if (GuildCombo.SelectedItem is ChannelChoice guild) await LoadChannelsAsync(guild);
            else ChannelCombo.ItemsSource = null;
        }
        catch (Exception ex) { FormMessage.Text = $"讀取频道缓存失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
        finally { _loading = false; }
    }

    private async Task LoadChannelsAsync(ChannelChoice guild)
    {
        var channels = await _channelSync.GetCachedChannelsAsync(guild.Id);
        if ((GuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id) return;
        ChannelCombo.ItemsSource = channels;
        ChannelCombo.SelectedItem = channels.FirstOrDefault(item => item.Id == _existing?.ChannelId) ?? channels.FirstOrDefault();
    }

    private async void GuildCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (GuildCombo.SelectedItem is not ChannelChoice guild)
        {
            ChannelCombo.ItemsSource = null;
            return;
        }
        try { await LoadChannelsAsync(guild); }
        catch (Exception ex) { FormMessage.Text = $"读取版块缓存失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FormMessage.Text = "正在刷新频道和版块…";
            var result = await _channelSync.SynchronizeAsync();
            if (!result.Succeeded) { FormMessage.Text = $"刷新失败：{result.Error}"; return; }
            await LoadGuildsAsync();
            FormMessage.Text = "频道数据已刷新。";
        }
        catch (Exception ex) { FormMessage.Text = $"刷新失败：{CliDiagnostics.Sanitize(ex.Message)}"; }
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingType || MediaLinksBox is null) return;
        UpdateMediaSelectionHint();
        InferTypeFromLinks();
    }

    private void MediaLinksBox_TextChanged(object sender, TextChangedEventArgs e) => InferTypeFromLinks();

    private void SelectMediaButton_Click(object sender, RoutedEventArgs e)
    {
        var type = SelectedType;
        if (type == "text")
        {
            FormMessage.Text = "请先将素材类型改为图片或视频，再选择本地媒体文件。";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Multiselect = type == "image",
            Filter = type == "image"
                ? "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|所有文件|*.*"
                : "视频文件|*.mp4;*.mov;*.m4v;*.webm;*.avi;*.mkv|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        var sources = ParseMediaLinks().Concat(dialog.FileNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MediaLinksBox.Text = string.Join(Environment.NewLine, sources);
        FormMessage.Text = $"已添加 {dialog.FileNames.Length} 个本地文件。";
    }

    private void ClearMediaButton_Click(object sender, RoutedEventArgs e) => MediaLinksBox.Clear();

    private void InferTypeFromLinks()
    {
        if (_settingType || TypeCombo is null || MediaLinksBox is null) return;
        var links = ParseMediaLinks();
        if (links.Count == 0) return;
        var inferred = MaterialMediaValidator.InferType(links);
        var desired = inferred switch { "video" => 2, "image" => 1, _ => TypeCombo.SelectedIndex };
        if (desired == TypeCombo.SelectedIndex) return;
        _settingType = true;
        TypeCombo.SelectedIndex = desired;
        _settingType = false;
        UpdateMediaSelectionHint();
    }

    private void ScheduleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ScheduleControls is null) return;
        var enabled = ScheduleEnabledCheckBox.IsChecked == true;
        ScheduleControls.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled || _settingSchedule) return;
        var now = DateTime.Now;
        PublishDatePicker.SelectedDate = now.Date;
        PublishTimeBox.Text = now.ToString("HH:mm:ss");
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        var content = ContentBox.Text.Trim();
        var type = SelectedType;
        var links = ParseMediaLinks();
        if (title.Length == 0 || content.Length == 0)
        {
            FormMessage.Text = "标题和正文均为必填项。";
            return;
        }
        var mediaError = MaterialMediaValidator.Validate(type, title, links);
        if (mediaError.Length > 0) { FormMessage.Text = mediaError; return; }

        var storedLinks = new List<string>(links.Count);
        try
        {
            foreach (var link in links)
            {
                if (!MaterialMediaValidator.IsLocalPath(link) || await _mediaStorage.IsManagedLibraryPathAsync(link))
                {
                    storedLinks.Add(link);
                    continue;
                }

                FormMessage.Text = $"正在托管媒体文件：{Path.GetFileName(link)}…";
                storedLinks.Add(await _mediaStorage.ImportToLibraryAsync(link, type));
            }
        }
        catch (Exception ex)
        {
            FormMessage.Text = $"保存媒体文件失败：{CliDiagnostics.Sanitize(ex.Message)}";
            return;
        }

        var guild = GuildCombo.SelectedItem as ChannelChoice;
        var channel = ChannelCombo.SelectedItem as ChannelChoice;
        DateTimeOffset? publishAt = null;
        if (ScheduleEnabledCheckBox.IsChecked == true)
        {
            if (PublishDatePicker.SelectedDate is not { } date)
            {
                FormMessage.Text = "请选择发布时间日期。";
                return;
            }
            if (!TimeSpan.TryParseExact(PublishTimeBox.Text.Trim(), "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out var time) &&
                !TimeSpan.TryParseExact(PublishTimeBox.Text.Trim(), "h\\:mm\\:ss", CultureInfo.InvariantCulture, out time))
            {
                FormMessage.Text = "发布时间格式应为 时:分:秒，例如 14:30:00。";
                return;
            }
            if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) { FormMessage.Text = "发布时间无效。"; return; }
            publishAt = new DateTimeOffset(date.Date + time, TimeZoneInfo.Local.GetUtcOffset(date.Date + time));
        }
        var status = ResolveMaterialStatus(publishAt, _existing?.Status, DateTimeOffset.Now);
        var materialLink = status == "published" ? _existing?.Link ?? "" : "";
        Material = new MaterialDraft(_existing?.Id, title, type, content, guild?.Id ?? "", guild?.Name ?? "", channel?.Id ?? "", channel?.Name ?? "", publishAt, status, materialLink, storedLinks);
        DialogResult = true;
    }

    public static string ResolveMaterialStatus(DateTimeOffset? publishAt, string? existingStatus, DateTimeOffset now)
    {
        if (publishAt is { } scheduled && scheduled > now) return "queue";
        return publishAt is null && existingStatus == "published" ? "published" : "waitsend";
    }

    private string SelectedType => (TypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "text";

    private IReadOnlyList<string> ParseMediaLinks() => MediaLinksBox.Text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void UpdateMediaSelectionHint()
    {
        if (MediaSelectionHint is null) return;
        MediaSelectionHint.Text = SelectedType switch
        {
            "image" => "图片可多选；也可在下方粘贴公开图片链接。",
            "video" => "视频单选；也可在下方粘贴公开视频链接。",
            _ => "文本素材不需要媒体文件。"
        };
    }
}
