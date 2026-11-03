using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace QqChannelDesk.Pages.Collection;

/// <summary>
/// 草稿编辑弹窗：采集结果预览（再次解析/保留草稿）、草稿编辑、手动新增共用。
/// 采集结果模式下“再次解析”直接用缓存 HTML 本地换规则重解析，不重复下载；
/// 规则用尽或抓取失败时，按现有 AI 配置兜底。
/// </summary>
public partial class DraftEditorWindow : Window
{
    private readonly ContentLibraryStore _store;
    private readonly PublicArticleFetcher _fetcher = new();
    private readonly IAiAssistService? _aiAssist;
    private readonly CollectedItem? _editingItem;
    private readonly HashSet<string> _triedRules = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _windowCts = new();
    private string _rawHtml = "";
    private string _baseUrl = "";
    private string _parsedBy = "";
    private string _parseError = "";
    private string _originalParsedContent = "";
    private IReadOnlyList<string> _media = [];

    internal int MediaCount => _media.Count;

    /// <summary>采集结果预览模式：展示解析结果，可换规则重解析或保留为草稿。</summary>
    public DraftEditorWindow(ContentLibraryStore store, ArticleFetchResult result, IAiAssistService? aiAssist = null)
    {
        InitializeComponent();
        _store = store;
        _aiAssist = aiAssist;
        Title = "采集结果";
        TitleText.Text = "采集结果";
        SubtitleText.Text = "已自动适配采集规则，确认内容后保留为草稿。";
        UrlBox.Text = result.Url;
        UrlBox.IsReadOnly = true;
        TitleBox.Text = result.Title;
        ContentBox.Text = result.Content;
        _rawHtml = result.RawHtml;
        _baseUrl = result.Url;
        _media = result.Media;
        _parsedBy = "rule";
        _originalParsedContent = result.Content;
        if (result.Rule.Length > 0) _triedRules.Add(result.Rule);
        SaveButton.Content = "保留草稿";
        UpdateParsedByLabel();
        UpdateMediaInfo();
    }

    /// <summary>编辑模式：修改草稿列表中的已有条目。</summary>
    public DraftEditorWindow(ContentLibraryStore store, CollectedItem item, IAiAssistService? aiAssist = null)
    {
        InitializeComponent();
        _store = store;
        _aiAssist = aiAssist;
        _editingItem = item;
        Title = "编辑草稿";
        TitleText.Text = "编辑草稿";
        SubtitleText.Text = "修改标题或正文后保存。";
        UrlBox.Text = item.Url;
        UrlBox.IsReadOnly = true;
        TitleBox.Text = item.Title;
        ContentBox.Text = item.Content;
        _baseUrl = item.Url;
        _media = item.Media;
        _parsedBy = item.ParsedBy;
        _parseError = item.ParseError;
        _originalParsedContent = item.OriginalContent;
        ReparseButton.IsEnabled = item.Url.Length > 0;
        ReparseButton.ToolTip = item.Url.Length > 0
            ? "按原文链接重新抓取并解析；规则失败时尝试 AI 兜底"
            : "手动录入的草稿没有来源链接，无法再次解析";
        SaveButton.Content = "保存修改";
        if (item.ParseError.Length > 0) SetMessage(item.ParseError, false);
        UpdateParsedByLabel();
        UpdateMediaInfo();
    }

    /// <summary>手动新增模式：手工录入草稿，来源链接可留空。</summary>
    public DraftEditorWindow(ContentLibraryStore store)
    {
        InitializeComponent();
        _store = store;
        Title = "新增草稿";
        TitleText.Text = "新增草稿";
        SubtitleText.Text = "手工录入素材，来源链接可留空。";
        ReparseButton.Visibility = Visibility.Collapsed;
        SaveButton.Content = "保留草稿";
        UpdateParsedByLabel();
        UpdateMediaInfo();
    }

    private void ReparseButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("再次解析草稿", ReparseAsync);

    private async Task ReparseAsync()
    {
        var token = _windowCts.Token;
        // 采集结果模式：用缓存 HTML 本地换规则重解析，不重复下载。
        if (_rawHtml.Length > 0)
        {
            var parsed = PublicArticleFetcher.ParseHtmlWithRule(
                _rawHtml,
                excludeSelectors: _triedRules,
                baseUrl: _baseUrl.Length > 0 ? _baseUrl : UrlBox.Text);
            if (parsed.Rule.Length > 0)
            {
                ApplyParsed(parsed.Title, parsed.Content, parsed.Rule, "rule", parsed.Media);
                SetMessage("已换一种规则重新解析，请检查内容。", true);
                return;
            }

            await ReparseWithAiAsync("内置规则已全部尝试过", token);
            return;
        }

        // 编辑模式首次重解析：按原文链接重新抓取；成功后缓存 HTML，后续再次解析走本地换规则。
        if (!PublicArticleFetcher.TryNormalizePublicUrl(UrlBox.Text, out var normalized, out var urlError))
        {
            SetMessage($"无法再次解析：{urlError}", false);
            return;
        }

        try
        {
            SetMessage("正在重新解析…", true);
            ReparseButton.IsEnabled = false;
            var result = await _fetcher.FetchAsync(normalized.AbsoluteUri, token);
            if (result.Succeeded)
            {
                _rawHtml = result.RawHtml;
                _baseUrl = result.Url;
                _triedRules.Clear();
                ApplyParsed(result.Title, result.Content, result.Rule, "rule", result.Media);
                SetMessage("已重新解析，请检查后保存。", true);
                return;
            }

            _parseError = result.Error;
            if (result.Url.Length > 0) _baseUrl = result.Url;
            if (result.Media.Count > 0)
            {
                _media = result.Media;
                UpdateMediaInfo();
            }
            if (result.RawHtml.Length > 0) _rawHtml = result.RawHtml;
            await ReparseWithAiAsync($"重新解析未成功：{result.Error}", token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetMessage($"重新解析未成功：{CliDiagnostics.Sanitize(exception.Message)}", false);
        }
        finally
        {
            ReparseButton.IsEnabled = _rawHtml.Length > 0 || UrlBox.Text.Trim().Length > 0;
        }
    }

    private async Task ReparseWithAiAsync(string ruleSummary, CancellationToken token)
    {
        if (_aiAssist is not null && _rawHtml.Length > 0)
        {
            SetMessage($"{ruleSummary}，正在尝试 AI 兜底…", true);
            var aiResult = await _aiAssist.ExtractAsync(_rawHtml, "single-page", token);
            if (aiResult.Succeeded)
            {
                var media = PublicArticleFetcher.ParseHtmlWithRule(
                    _rawHtml,
                    baseUrl: _baseUrl.Length > 0 ? _baseUrl : UrlBox.Text).Media;
                ApplyParsed(aiResult.Title, aiResult.Content, "", "ai-fallback", media);
                SetMessage($"{ruleSummary}，已用 AI 兜底生成候选稿，请检查后再保留。", true);
                return;
            }

            SetMessage($"{ruleSummary}；AI 兜底：{aiResult.ErrorSummary}", false);
            return;
        }

        SetMessage($"{ruleSummary}，请手动调整内容。", false);
    }

    private void ApplyParsed(
        string title,
        string content,
        string rule,
        string parsedBy,
        IReadOnlyList<string> media)
    {
        TitleBox.Text = title;
        ContentBox.Text = content;
        _media = media;
        _originalParsedContent = content;
        _parsedBy = parsedBy;
        _parseError = "";
        if (rule.Length > 0) _triedRules.Add(rule);
        UpdateParsedByLabel();
        UpdateMediaInfo();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) =>
        UiAsync.Run("保存草稿", SaveAsync);

    private async Task SaveAsync()
    {
        var token = _windowCts.Token;
        if (string.IsNullOrWhiteSpace(TitleBox.Text) && string.IsNullOrWhiteSpace(ContentBox.Text) && _media.Count == 0)
        {
            SetMessage("标题、正文和媒体至少填写一项。", false);
            return;
        }

        try
        {
            SaveButton.IsEnabled = false;
            if (_editingItem is not null)
            {
                await _store.UpdateItemAsync(new CollectedItemDraft(
                    _editingItem.Url, _editingItem.SourceHost, TitleBox.Text.Trim(), ContentBox.Text,
                    _editingItem.Status, _editingItem.Notes, _editingItem.Tags, _parseError, _editingItem.Id,
                    MediaList: _media, OriginalContent: _originalParsedContent, ParsedBy: _parsedBy), token);
            }
            else
            {
                var normalizedUrl = "";
                var host = "手动录入";
                var rawUrl = UrlBox.Text.Trim();
                if (rawUrl.Length > 0)
                {
                    if (!PublicArticleFetcher.TryNormalizePublicUrl(rawUrl, out var uri, out var error))
                    {
                        SetMessage(error, false);
                        return;
                    }

                    normalizedUrl = uri.AbsoluteUri;
                    host = uri.DnsSafeHost;
                    var duplicate = await _store.FindByUrlAsync(normalizedUrl, token);
                    if (duplicate is not null)
                    {
                        SetMessage($"该链接已在草稿列表中：{duplicate.Title}", false);
                        return;
                    }
                }

                await _store.SaveItemAsync(new CollectedItemDraft(
                    normalizedUrl, host, TitleBox.Text.Trim(), ContentBox.Text, "待处理",
                    "", [], _parseError, null,
                    MediaList: _media, OriginalContent: _originalParsedContent, ParsedBy: _parsedBy), token);
            }

            DialogResult = true;
            Close();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            SetMessage("该网页链接已存在于草稿列表。", false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetMessage($"未能保存，请检查内容后重试：{CliDiagnostics.Sanitize(ex.Message)}", false);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void UpdateParsedByLabel()
    {
        var label = _parsedBy switch
        {
            "rule" => "规则解析",
            "ai-fallback" => "AI 兜底",
            "ai-only" => "AI 专用",
            _ => ""
        };
        ParsedByText.Text = label.Length == 0 ? "" : $"解析方式：{label}";
        ParsedByText.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateMediaInfo()
    {
        // 只展示文件名或远程媒体名称，不泄露本地完整路径。
        MediaInfoText.Text = _media.Count == 0 ? "" : $"媒体 {_media.Count} 个（可逐项移除）";
        MediaItems.ItemsSource = _media
            .Select((source, index) => new MediaRow(index, MediaDisplayName(source)))
            .ToArray();
        MediaSection.Visibility = _media.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RemoveMediaButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        RemoveMediaAt(index);
        SetMessage("已从当前草稿中移除该媒体。", true);
    }

    internal void RemoveMediaAt(int index)
    {
        if ((uint)index >= (uint)_media.Count) return;
        _media = _media.Where((_, itemIndex) => itemIndex != index).ToArray();
        UpdateMediaInfo();
    }

    private static string MediaDisplayName(string source)
    {
        if (MaterialMediaValidator.IsLocalPath(source))
            return Path.GetFileName(source) is { Length: > 0 } localName ? localName : "本地媒体";
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            var remoteName = Path.GetFileName(uri.AbsolutePath);
            if (remoteName.Length > 0) return Uri.UnescapeDataString(remoteName);
            return uri.Host;
        }
        return Path.GetFileName(source.Trim()) is { Length: > 0 } relativeName ? relativeName : "媒体";
    }

    private void SetMessage(string message, bool succeeded)
    {
        MessageText.Text = message;
        MessageText.Foreground = succeeded ? UiStatusBrushes.Success : UiStatusBrushes.Warning;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowCts.Cancel();
        _windowCts.Dispose();
    }

    private sealed record MediaRow(int Index, string DisplayName);
}
