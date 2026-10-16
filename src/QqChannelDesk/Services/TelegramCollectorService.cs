using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QqChannelDesk.Services;

/// <summary>
/// 采集 tg 频道：经网页代理打开 t.me/s/ 预览页解析消息。解析结果先以临时列表展示，不落库、
/// 不下载媒体；仅在选择「加入素材库」时才把图片和视频下载到 temp/telegram/日期/频道目录。
/// </summary>
public sealed class TelegramCollectorService
{
    /// <summary>临时列表单次上限，防止代理页面异常时涌入过多数据。</summary>
    public const int TempListLimit = 500;

    private static readonly TimeSpan MediaTimeout = TimeSpan.FromSeconds(45);
    private const int MediaAttempts = 2;
    private readonly PublicArticleFetcher _fetcher = new();
    private readonly WebProxyProviderRegistry _proxyRegistry;

    public TelegramCollectorService(WebProxyProviderRegistry? proxyRegistry = null)
    {
        _proxyRegistry = proxyRegistry ?? new WebProxyProviderRegistry();
    }

    public static string ReadChannelName(string channelUrl)
    {
        if (!Uri.TryCreate(channelUrl, UriKind.Absolute, out var uri)) return "";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2 && segments[0].Equals("s", StringComparison.OrdinalIgnoreCase) ? segments[1] : uri.Host;
    }

    /// <summary>把用户输入归一化为 t.me/s/ 预览页地址；无法识别时返回空串。</summary>
    public static string NormalizeChannelUrl(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return "";
        var withoutAt = trimmed.TrimStart('@').Trim();
        if (withoutAt.Length == 0) return "";
        if (withoutAt.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase) ||
            withoutAt.StartsWith("telegram.me/", StringComparison.OrdinalIgnoreCase))
            withoutAt = "https://" + withoutAt;
        if (Uri.TryCreate(withoutAt, UriKind.Absolute, out var uri) && TelegramChannelParser.IsTelegramHost(uri.Host))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) return "";
            // t.me/c/ 是私有频道、joinchat 之类是邀请和设置页，网页代理都打不开，直接拒绝。
            if (ReservedPaths.Contains(segments[0])) return "";
            var name = segments.Length >= 2 && segments[0].Equals("s", StringComparison.OrdinalIgnoreCase)
                ? segments[1]
                : segments.FirstOrDefault() ?? "";
            return IsChannelName(name) ? $"https://t.me/s/{name}" : "";
        }
        // 其余按频道名处理：只接受字母、数字和下划线。
        return IsChannelName(withoutAt) ? $"https://t.me/s/{withoutAt}" : "";
    }

    /// <summary>t.me 上不是公开频道的路径段，出现即认为不是可采集的频道地址。</summary>
    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "joinchat", "addstickers", "addtheme", "proxy", "socks", "iv", "confirmphone",
        "setlanguage", "login", "invoice", "contact", "download", "faq", "apps",
    };

    private static bool IsChannelName(string name) =>
        name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// 预览式采集：只抓取和解析，返回临时消息列表与代理会话 cookie，不写库、不下载媒体。
    /// </summary>
    public async Task<TelegramChannelPreview> PreviewChannelAsync(
        string channelUrl,
        int limit = 0,
        string keyword = "",
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        WebProxySelection? proxySelection = null)
    {
        var channelName = ReadChannelName(channelUrl);
        var selection = proxySelection ?? new WebProxySelection(WebProxySelection.Automatic);
        var providers = _proxyRegistry.Resolve(selection);
        var failures = new List<string>();
        for (var index = 0; index < providers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = providers[index];
            if (index > 0)
                progress?.Invoke($"{providers[index - 1].DisplayName} 失败，正在尝试 {provider.DisplayName}…");
            else
                progress?.Invoke($"正在通过 {provider.DisplayName} 打开 tg 频道，节点不稳定时需要稍等片刻…");
            try
            {
                var page = await provider.FetchAsync(channelUrl, ".tgme_widget_message", cancellationToken);
                var messages = TelegramChannelParser.ParseChannelPage(page.Html, channelName);
                if (messages.Count == 0)
                {
                    failures.Add($"{provider.DisplayName}：页面没有解析到频道消息");
                    continue;
                }
                var filtered = ApplyPreviewFilters(messages, limit, keyword);
                return new TelegramChannelPreview(filtered, page.CookieHeader, messages.Count, "", page.ProviderId.Length == 0 ? provider.Id : page.ProviderId, page.ProviderName.Length == 0 ? provider.DisplayName : page.ProviderName, failures);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{provider.DisplayName}：{CliDiagnostics.Sanitize(ex.Message)}");
                AppLogger.Instance.Warning($"tg 网页代理 {provider.DisplayName} 失败：{ex.Message}");
                if (providers.Count == 1)
                    return new TelegramChannelPreview([], "", 0, $"{provider.DisplayName} 请求失败，请检查网络或更换代理后重试：{CliDiagnostics.Sanitize(ex.Message)}", provider.Id, provider.DisplayName, failures);
            }
        }

        var detail = string.Join("；", failures);
        if (!selection.IsAutomatic)
        {
            var provider = providers.FirstOrDefault();
            var name = provider?.DisplayName ?? "当前网页代理";
            return new TelegramChannelPreview([], "", 0, $"{name} 请求失败，请检查网络或更换代理后重试：{detail}", provider?.Id ?? "", name, failures);
        }
        return new TelegramChannelPreview([], "", 0, $"网页代理均不可用，请更换代理或稍后重试：{detail}", "", "", failures);
    }

    /// <summary>
    /// 数量上限与关键词过滤（标题/正文包含即命中，大小写不敏感）；limit 不大于 0 表示全部，
    /// 且整体不超过 <see cref="TempListLimit"/>。
    /// </summary>
    internal static IReadOnlyList<TelegramChannelMessage> ApplyPreviewFilters(
        IReadOnlyList<TelegramChannelMessage> messages, int limit, string keyword)
    {
        IEnumerable<TelegramChannelMessage> result = messages;
        var keywords = SplitKeywords(keyword);
        if (keywords.Count > 0)
        {
            result = result.Where(message =>
                keywords.Any(item =>
                    message.Title.Contains(item, StringComparison.OrdinalIgnoreCase) ||
                    message.Text.Contains(item, StringComparison.OrdinalIgnoreCase)));
        }
        var take = limit > 0 ? Math.Min(limit, TempListLimit) : TempListLimit;
        return result.Take(take).ToArray();
    }

    internal static IReadOnlyList<string> SplitKeywords(string? keyword) =>
        (keyword ?? string.Empty)
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// 将一条临时消息加入素材库所需的图片和视频下载到
    /// temp/telegram/{yyyyMMdd}/{频道}/。不再生成元信息文件。
    /// 单个媒体失败不中断，只累计失败数量。
    /// </summary>
    public async Task<TelegramDraftResult> DraftMessageAsync(
        TelegramChannelMessage message,
        string cookieHeader,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var directory = BuildDraftDirectory(
            AppContext.BaseDirectory, now, ReadMessageChannel(message.Url));
        var media = SelectDraftMedia(message.Media);
        if (media.Count > 0) Directory.CreateDirectory(directory);
        var download = await DownloadMediaAsync(media, cookieHeader, progress, cancellationToken, directory);
        return new TelegramDraftResult(directory, download.Paths, download.Failed);
    }

    internal static IReadOnlyList<TelegramMediaRef> SelectDraftMedia(IReadOnlyList<TelegramMediaRef> media) =>
        media
            .Where(item => string.Equals(item.Kind, "image", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(item.Kind, "video", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    internal static string BuildDraftDirectory(string root, DateTimeOffset now, string channelName)
    {
        var channel = SanitizeSegment(channelName);
        return Path.Combine(root, "temp", "telegram", now.ToString("yyyyMMdd"), channel);
    }

    internal static string BuildPreviewDirectory(string root, string messageId) =>
        Path.Combine(root, "temp", "telegram", "preview", SanitizeSegment(messageId));

    /// <summary>从消息链接里读消息 ID（t.me/频道/消息ID 的最后一段），读不到时用链接哈希兜底。</summary>
    internal static string ReadMessageId(string messageUrl)
    {
        if (Uri.TryCreate(messageUrl, UriKind.Absolute, out var uri))
        {
            var segment = uri.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (segment.Length > 0) return segment;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageUrl)))[..12];
    }

    /// <summary>从消息链接里读频道名，兼容 t.me/频道/ID 与 t.me/s/频道 两种形态。</summary>
    internal static string ReadMessageChannel(string messageUrl)
    {
        if (!Uri.TryCreate(messageUrl, UriKind.Absolute, out var uri)) return "";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return "";
        return segments[0].Equals("s", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2 ? segments[1] : segments[0];
    }

    private static string SanitizeSegment(string value)
    {
        var trimmed = value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(invalid, '_');
        if (trimmed.Length > 40) trimmed = trimmed[..40].TrimEnd();
        return trimmed.Length == 0 ? "unknown" : trimmed;
    }

    /// <summary>
    /// 用代理节点会话 cookie 下载消息媒体，单个文件失败不中断，只累计失败数量。
    /// </summary>
    public async Task<TelegramMediaDownloadResult> DownloadMediaAsync(
        IReadOnlyList<TelegramMediaRef> media,
        string cookieHeader,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? targetDirectory = null)
    {
        var paths = new List<string>();
        var failed = 0;
        for (var index = 0; index < media.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"正在下载媒体 {index + 1}/{media.Count}…");
            for (var attempt = 1; attempt <= MediaAttempts; attempt++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(MediaTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                    paths.Add(await _fetcher.DownloadMediaAsync(
                        media[index].ProxiedUrl, media[index].Kind, targetDirectory, linked.Token, cookieHeader));
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    if (attempt == MediaAttempts)
                    {
                        failed++;
                        AppLogger.Instance.Warning($"tg 媒体下载失败：{ex.Message}");
                    }
                    else
                    {
                        AppLogger.Instance.Warning($"tg 媒体下载第 {attempt} 次尝试失败，重试：{ex.Message}");
                    }
                }
            }
        }
        return new TelegramMediaDownloadResult(paths, failed);
    }
}

public sealed record TelegramChannelPreview(
    IReadOnlyList<TelegramChannelMessage> Messages,
    string CookieHeader,
    int Total,
    string Error,
    string ProviderId = "",
    string ProviderName = "",
    IReadOnlyList<string>? Attempts = null)
{
    public bool Succeeded => Error.Length == 0;
}

public sealed record TelegramDraftResult(string Directory, IReadOnlyList<string> MediaPaths, int Failed);

public sealed record TelegramMediaDownloadResult(IReadOnlyList<string> Paths, int Failed);
