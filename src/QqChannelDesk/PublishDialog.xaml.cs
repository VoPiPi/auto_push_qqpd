using Microsoft.Win32;
using QqChannelDesk.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace QqChannelDesk;

public partial class PublishDialog : Window
{
    private readonly CliWorkflow _workflow;
    private readonly ChannelSyncService _channelSync;
    private readonly PublishHistoryStore _historyStore;
    private readonly PublishExecutionService _publisher;
    private readonly ContentLibraryStore? _contentLibrary;
    private readonly FeedType _feedType;
    private readonly ContentDraft? _sourceDraft;
    private readonly MaterialRecord? _sourceMaterial;
    private readonly MediaStorageService _mediaStorage;
    private bool _loadingGuilds;
    private int _guildLoadVersion;
    private int _channelLoadVersion;
    private bool _syncSucceeded;
    private readonly List<string> _mediaPaths = [];

    public PublishResult? Result { get; private set; }

    public PublishDialog(CliWorkflow workflow, ChannelSyncService channelSync, PublishHistoryStore historyStore, FeedType feedType,
        ContentDraft? sourceDraft = null, ContentLibraryStore? contentLibrary = null, MaterialRecord? sourceMaterial = null,
        MediaStorageService? mediaStorage = null, PublishExecutionService? publisher = null)
    {
        InitializeComponent();
        _workflow = workflow;
        _channelSync = channelSync;
        _historyStore = historyStore;
        _sourceDraft = sourceDraft;
        _sourceMaterial = sourceMaterial;
        _contentLibrary = contentLibrary;
        _mediaStorage = mediaStorage ?? new MediaStorageService();
        _publisher = publisher ?? new PublishExecutionService(_workflow, _historyStore, _mediaStorage);
        _feedType = feedType;
        var name = feedType switch { FeedType.Image => "图片", FeedType.Video => "视频", _ => "文本" };
        Heading.Text = $"{name} 验证";
        Description.Text = feedType switch
        {
            FeedType.Image => "可选择多张图片；媒体将作为帖子附件发布。",
            FeedType.Video => "选择一个本地视频文件；频道工具首次处理视频时，可能需要安装 FFmpeg。",
            _ => "标题可留空发布短贴；填写标题则发布长贴。"
        };
        MediaPanel.Visibility = feedType == FeedType.Text ? Visibility.Collapsed : Visibility.Visible;
        LimitText.Text = feedType == FeedType.Image
            ? "短贴最多 18 张图片；带标题的长贴最多 50 张。正文限制分别为 1000 / 10000 字。"
            : feedType == FeedType.Video
                ? "短贴最多 1 个视频；带标题的长贴最多 5 个。正文限制分别为 1000 / 10000 字。"
                : "标题留空时正文最多 1000 字；填写标题后正文最多 10000 字。";
        if (sourceDraft is not null)
        {
            var isTelegramDraft = TelegramChannelParser.IsTelegramHost(
                Uri.TryCreate(sourceDraft.SourceUrl, UriKind.Absolute, out var sourceUri) ? sourceUri.Host : null);
            TitleBox.Text = isTelegramDraft ? "" : sourceDraft.Title;
            ContentBox.Text = sourceDraft.Content;
            SourceInfo.Text = string.IsNullOrWhiteSpace(sourceDraft.SourceUrl) ? "来源：手动素材" : $"来源：{sourceDraft.SourceUrl}";
            SourceInfo.Visibility = Visibility.Visible;
            Heading.Text = "发布内容草稿";
            Description.Text = "来源信息仅供核对，不会自动加入帖子正文。";
            if (isTelegramDraft)
            {
                TitleLabel.Visibility = Visibility.Collapsed;
                TitleBox.Visibility = Visibility.Collapsed;
                Description.Text = "TG 采集内容固定发布为短帖子；来源信息仅供核对，不会自动加入帖子正文。";
            }
            if (sourceDraft.Media.Count > 0 && _feedType != FeedType.Text)
            {
                MediaPanel.Visibility = Visibility.Visible;
                MediaFilesText.Text = string.Join("、", sourceDraft.Media.Select(DisplayMediaSource));
                MediaFilesText.ToolTip = string.Join(Environment.NewLine, sourceDraft.Media);
                SelectMediaButton.IsEnabled = false;
                Description.Text = isTelegramDraft
                    ? "TG 采集内容固定发布为短帖子；草稿自带的本地媒体将作为附件上传。来源信息仅供核对。"
                    : "来源信息仅供核对；草稿自带的本地媒体将作为附件上传。";
            }
        }
        if (sourceMaterial is not null)
        {
            TitleBox.Text = sourceMaterial.Title;
            ContentBox.Text = sourceMaterial.Content;
            SourceInfo.Text = "素材库内容";
            SourceInfo.Visibility = Visibility.Visible;
            Heading.Text = "发布素材";
            Description.Text = "本地媒体将直接上传，公开链接会先下载为附件；发布前会再次确认内容和目标。";
            MediaPanel.Visibility = feedType == FeedType.Text ? Visibility.Collapsed : Visibility.Visible;
            MediaFilesText.Text = sourceMaterial.MediaLinks.Count == 0
                ? "尚未选择文件"
                : string.Join("、", sourceMaterial.MediaLinks.Select(DisplayMediaSource));
            MediaFilesText.ToolTip = string.Join(Environment.NewLine, sourceMaterial.MediaLinks.Select(DisplayMediaSource));
            SelectMediaButton.IsEnabled = false;
        }
        Loaded += (_, _) => UiAsync.Run("发布对话框初始化", LoadCachedGuildsAsync);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("刷新发布频道", RefreshGuildsAsync);

    private void GuildCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UiAsync.Run("发布频道切换", async () =>
        {
        if (_loadingGuilds || GuildCombo.SelectedItem is not ChannelChoice guild) return;
        await LoadCachedChannelsAsync(guild);
        });

    private async Task LoadCachedGuildsAsync()
    {
        var version = ++_guildLoadVersion;
        _loadingGuilds = true;
        InvalidateCachedChannelSelection();
        GuildCombo.IsEnabled = false;
        ChannelCombo.IsEnabled = false;
        PublishButton.IsEnabled = false;
        FooterStatus.Text = "正在读取已保存的频道数据…";
        try
        {
            var previous = _sourceMaterial?.GuildId ?? (GuildCombo.SelectedItem as ChannelChoice)?.Id;
            var guilds = await _channelSync.GetCachedGuildsAsync();
            var state = await _channelSync.GetStateAsync();
            if (version != _guildLoadVersion) return;
            _syncSucceeded = state.Succeeded;
            GuildCombo.ItemsSource = guilds;
            GuildCombo.SelectedItem = guilds.FirstOrDefault(item => item.Id == previous) ?? guilds.FirstOrDefault();
            GuildCombo.IsEnabled = guilds.Count > 0;
            if (GuildCombo.SelectedItem is not ChannelChoice selected)
            {
                ChannelCombo.ItemsSource = null;
                FooterStatus.Text = "当前账号没有可用频道";
            }
            else
            {
                await LoadCachedChannelsAsync(selected);
                if (version != _guildLoadVersion) return;
                FooterStatus.Text = state.Succeeded
                    ? $"已加载频道数据：{guilds.Count} 个频道{(state.SyncedAt is null ? "" : $"，上次刷新于 {state.SyncedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}")}"
                    : state.Error is null ? "频道数据尚未同步，请刷新频道。" : $"上次刷新失败：{state.Error}。已保留旧数据，发布已暂停；请刷新频道后重试。";
            }
            PublishButton.IsEnabled = state.Succeeded && guilds.Count > 0 && ChannelCombo.Items.Count > 0;
            if (_sourceMaterial is not null && ChannelCombo.ItemsSource is IEnumerable<ChannelChoice> channels)
                ChannelCombo.SelectedItem = channels.FirstOrDefault(item => item.Id == _sourceMaterial.ChannelId) ?? ChannelCombo.SelectedItem;
            DialogStatus.Text = state.Succeeded ? "频道和版块来自已保存数据。" : "频道数据不可用于发布，请刷新频道后重试。";
        }
        catch (Exception ex)
        {
            if (version != _guildLoadVersion) return;
            FooterStatus.Text = $"频道数据读取失败：{CliDiagnostics.Sanitize(ex.Message)}";
            DialogStatus.Text = "频道数据读取失败，发布已暂停。请刷新频道后重试。";
        }
        finally
        {
            if (version == _guildLoadVersion) _loadingGuilds = false;
        }
    }

    private async Task RefreshGuildsAsync()
    {
        RefreshButton.IsEnabled = false;
        PublishButton.IsEnabled = false;
        FooterStatus.Text = "正在刷新频道和版块…";
        try
        {
            var sync = await _channelSync.SynchronizeAsync(new Progress<string>(message => FooterStatus.Text = message));
            if (!sync.Succeeded)
            {
                DialogStatus.Text = "刷新未完成；已保留旧数据，发布已暂停。请重试刷新。";
                await LoadCachedGuildsAsync();
                PublishButton.IsEnabled = false;
                return;
            }
            await LoadCachedGuildsAsync();
            FooterStatus.Text = $"刷新成功：{sync.GuildCount} 个频道、{sync.ChannelCount} 个版块";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task LoadCachedChannelsAsync(ChannelChoice guild)
    {
        var version = ++_channelLoadVersion;
        ChannelCombo.ItemsSource = null;
        ChannelCombo.SelectedItem = null;
        ChannelCombo.IsEnabled = false;
        PublishButton.IsEnabled = false;
        try
        {
            var channels = await _channelSync.GetCachedChannelsAsync(guild.Id);
            if (version != _channelLoadVersion
                || (GuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id)
                return;
            ChannelCombo.ItemsSource = channels;
            ChannelCombo.SelectedItem = channels.FirstOrDefault();
            ChannelCombo.IsEnabled = channels.Count > 0;
            PublishButton.IsEnabled = _syncSucceeded && ChannelCombo.SelectedItem is ChannelChoice;
            if (channels.Count == 0) FooterStatus.Text = "该频道没有可用版块";
        }
        catch (Exception ex)
        {
            if (version != _channelLoadVersion) return;
            ChannelCombo.ItemsSource = null;
            PublishButton.IsEnabled = false;
            FooterStatus.Text = $"版块数据读取失败：{CliDiagnostics.Sanitize(ex.Message)}";
        }
    }

    private void InvalidateCachedChannelSelection()
    {
        _channelLoadVersion++;
        ChannelCombo.ItemsSource = null;
        ChannelCombo.SelectedItem = null;
        ChannelCombo.IsEnabled = false;
        PublishButton.IsEnabled = false;
    }

    private void SelectMediaButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = _feedType == FeedType.Image,
            Filter = _feedType == FeedType.Image
                ? "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|所有文件|*.*"
                : "视频文件|*.mp4;*.mov;*.m4v;*.webm;*.avi;*.mkv|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        _mediaPaths.Clear();
        _mediaPaths.AddRange(dialog.FileNames);
        MediaFilesText.Text = string.Join("、", _mediaPaths.Select(Path.GetFileName));
        MediaFilesText.ToolTip = string.Join(Environment.NewLine, _mediaPaths);
    }

    private void PublishButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("执行发布", async () =>
        {
        var guild = GuildCombo.SelectedItem as ChannelChoice;
        var channel = ChannelCombo.SelectedItem as ChannelChoice;
        if (guild is null
            || channel is null
            || !await IsCachedChannelSelectionValidAsync(guild, channel))
        {
            MessageBox.Show(this, "频道或版块已变化，请重新选择后再发布。", "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var sourceMedia = _sourceMaterial?.MediaLinks ?? (_sourceDraft?.Media is { Count: > 0 } draftMedia ? draftMedia : _mediaPaths);
        var previewRequest = new PublishRequest(guild?.Id ?? "", channel?.Id ?? "", ContentBox.Text, TitleBox.Text, _feedType, sourceMedia);
        var validation = CliWorkflow.ValidatePublishRequestSources(previewRequest, allowUnknownSources: true);
        if (validation.Length > 0)
        {
            MessageBox.Show(this, validation, "请检查输入", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var media = previewRequest.MediaPaths.Count == 0 ? "（无）" : string.Join("\n", previewRequest.MediaPaths.Select(DisplayMediaSource));
        var confirmation = MessageBox.Show(this,
            $"将发布真实{(_feedType == FeedType.Text ? "文本" : _feedType == FeedType.Image ? "图片" : "视频")}帖子：\n\n频道：{guild!.Name}（{guild.Id}）\n版块：{channel!.Name}（{channel.Id}）\n标题：{(string.IsNullOrWhiteSpace(previewRequest.Title) ? "（无标题，短贴）" : previewRequest.Title.Trim())}\n来源：{GetSourceDisplay()}\n\n正文：\n{(string.IsNullOrWhiteSpace(previewRequest.Content) ? "（无正文）" : previewRequest.Content)}\n\n媒体文件：\n{media}\n\n发布后可能无法撤回。确认发布？",
            "确认真实发帖", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        PublishButton.IsEnabled = false;
        FooterStatus.Text = _feedType == FeedType.Text ? "正在发布，请稍候…" : "正在准备媒体文件…";
        try
        {
            var outcome = await _publisher.ExecuteAsync(previewRequest, guild!.Name, channel!.Name);
            Result = outcome.Result;
            if (Result.Succeeded && _sourceDraft is not null && _contentLibrary is not null)
                await _contentLibrary.MarkDraftPublishedAsync(_sourceDraft.Id);
            var details = Result.Message;
            if (!string.IsNullOrWhiteSpace(Result.Url)) details += $"\n帖子链接：{Result.Url}";
            if (!string.IsNullOrWhiteSpace(Result.PostId)) details += $"\n帖子 ID：{Result.PostId}";
            if (!Result.Succeeded && Result.Category == PublishErrorCategory.Timeout)
                details += "\n请先核实目标频道，不要立即重试。";
            DialogStatus.Text = details;
            FooterStatus.Text = Result.Succeeded ? "发布成功" : $"发布未完成：{Result.Category}";
            MessageBox.Show(this, details, Result.Succeeded ? "发布结果" : "发布未成功", MessageBoxButton.OK,
                Result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "发布结果待确认；请先核实频道。";
            MessageBox.Show(this, $"发布结果尚未确认，请先核实目标频道是否已出现帖子。\n\n{CliDiagnostics.Sanitize(ex.Message)}\n\n不要立即重试。", "发布结果待确认", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PublishButton.IsEnabled = true;
        }
        });

    private async Task<bool> IsCachedChannelSelectionValidAsync(ChannelChoice guild, ChannelChoice channel)
    {
        if ((GuildCombo.SelectedItem as ChannelChoice)?.Id != guild.Id
            || (ChannelCombo.SelectedItem as ChannelChoice)?.Id != channel.Id)
            return false;

        var channels = await _channelSync.GetCachedChannelsAsync(guild.Id);
        return (GuildCombo.SelectedItem as ChannelChoice)?.Id == guild.Id
            && (ChannelCombo.SelectedItem as ChannelChoice)?.Id == channel.Id
            && channels.Any(item => string.Equals(item.Id, channel.Id, StringComparison.Ordinal));
    }

    private string GetSourceDisplay()
    {
        if (_sourceDraft is not null)
            return string.IsNullOrWhiteSpace(_sourceDraft.SourceUrl) ? "手动草稿" : _sourceDraft.SourceUrl;
        if (_sourceMaterial is not null)
            return "素材库";
        return "（无）";
    }

    private static string DisplayMediaSource(string source) => MaterialMediaValidator.IsLocalPath(source)
        ? Path.GetFileName(source) ?? "本地媒体"
        : source;
}
