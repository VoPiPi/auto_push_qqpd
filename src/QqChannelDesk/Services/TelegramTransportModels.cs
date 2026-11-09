using System.Net;
using System.Net.Http;

namespace QqChannelDesk.Services;

/// <summary>
/// 一次 TG 采集实际使用的传输上下文。直连/系统代理/代理池都请求原始 t.me 地址，
/// 只有在线解析需要把原地址与网页代理 Cookie 一起交给下载器。
/// </summary>
public sealed record TelegramTransportContext
{
    private TelegramTransportContext(string selectionValue, string mode)
    {
        SelectionValue = selectionValue;
        Mode = mode;
    }

    public string SelectionValue { get; }

    public string Mode { get; }

    public bool IsOnline => string.Equals(Mode, TelegramTransportModes.Online, StringComparison.Ordinal);

    public static TelegramTransportContext Direct() => new(TelegramTransportSelection.Direct, TelegramTransportModes.Direct);

    public static TelegramTransportContext System() => new(TelegramTransportSelection.System, TelegramTransportModes.System);

    public static TelegramTransportContext ProxyPool() =>
        new(TelegramTransportSelection.ProxyPool, TelegramTransportModes.ProxyPool);

    public static TelegramTransportContext Online(string providerId = "") =>
        new(
            string.IsNullOrWhiteSpace(providerId) ? TelegramTransportSelection.Online : TelegramTransportSelection.ForOnlineProvider(providerId),
            TelegramTransportModes.Online);

    /// <summary>在线解析使用代理地址 + 网页代理 Cookie；其余通道坚持原始地址且不携带 Cookie。</summary>
    public (string Url, string CookieHeader) SelectMediaSource(TelegramMediaRef media)
    {
        if (!IsOnline) return (media.OriginalUrl, "");
        var url = string.IsNullOrWhiteSpace(media.ProxiedUrl) ? media.OriginalUrl : media.ProxiedUrl;
        return (url, string.Equals(url, media.OriginalUrl, StringComparison.OrdinalIgnoreCase) ? "" : CookieHeader);
    }

    public string CookieHeader { get; init; } = "";

    /// <summary>
    /// Only automatic collection enables this. Explicit selections keep their
    /// chosen transport all the way through media download.
    /// </summary>
    public bool AllowFallback { get; init; }

    public PublicArticleRequestContext ToRequestContext() => Mode switch
    {
        TelegramTransportModes.Direct => new PublicArticleRequestContext { UseSystemProxy = false },
        TelegramTransportModes.ProxyPool => new PublicArticleRequestContext
        {
            UseSystemProxy = false,
            ProxyUri = ProxyPoolProxyUri() ?? throw new InvalidOperationException("代理池节点已失效，请重新采集或改为其他通道。")
        },
        _ => PublicArticleRequestContext.SystemProxyOrDefault,
    };

    private Uri? ProxyPoolProxyUri()
    {
        var proxy = ProxyNode;
        if (proxy is null) return null;
        return Uri.TryCreate(proxy.Address, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>代理池通道在采集成功时解析到的可用节点；离线节点不允许静默回退到其它通道。</summary>
    public ProxyNode? ProxyNode { get; init; }
}

public static class TelegramTransportModes
{
    public const string Direct = "direct";
    public const string System = "system";
    public const string ProxyPool = "proxy-pool";
    public const string Online = "online";
}

/// <summary>传输结果：解析后的消息、实际使用的通道、逐步失败说明。</summary>
public sealed record TelegramTransportPreview(
    bool Succeeded,
    IReadOnlyList<TelegramChannelMessage> Messages,
    TelegramTransportContext? Context,
    string ProviderId,
    string ProviderName,
    string Error,
    IReadOnlyList<string> Attempts);

/// <summary>单页采集结果。PagingSupported=false 时调用方必须停止继续翻页。</summary>
public sealed record TelegramTransportPageResult(
    bool Succeeded,
    IReadOnlyList<TelegramChannelMessage> Messages,
    TelegramTransportContext? Context,
    string ProviderId,
    string ProviderName,
    string Error,
    IReadOnlyList<string> Attempts,
    bool HasMore,
    bool PagingSupported)
{
    public static TelegramTransportPageResult FromPreview(TelegramTransportPreview preview) =>
        new(
            preview.Succeeded,
            preview.Messages,
            preview.Context,
            preview.ProviderId,
            preview.ProviderName,
            preview.Error,
            preview.Attempts,
            false,
            false);
}

/// <summary>
/// 传输层契约。实现内部按选择器决定直连/系统代理/代理池/在线解析，
/// 便于单测注入假实现而不访问真实网络。
/// </summary>
public interface ITelegramTransport
{
    Task<TelegramTransportPreview> FetchChannelAsync(
        string channelUrl,
        TelegramTransportSelection selection,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取频道的一页消息。默认回退到旧单页契约，便于测试替身和第三方实现平滑迁移。
    /// </summary>
    async Task<TelegramTransportPageResult> FetchChannelPageAsync(
        string channelUrl,
        TelegramTransportSelection selection,
        long? beforeMessageId,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var preview = await FetchChannelAsync(channelUrl, selection, progress, cancellationToken);
        return TelegramTransportPageResult.FromPreview(preview);
    }

    Task<string> DownloadMediaAsync(
        TelegramMediaRef media,
        TelegramTransportContext context,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional media-only fallback contract. It is separate from
/// <see cref="ITelegramTransport"/> so existing test doubles remain source-compatible.
/// </summary>
public interface ITelegramMediaFallbackTransport
{
    Task<string> DownloadMediaWithFallbackAsync(
        TelegramMediaRef media,
        TelegramTransportContext context,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 请求上下文。null 表示保留系统代理行为；<see cref="UseSystemProxy"/> 为 false 时直连；
/// <see cref="ProxyUri"/> 有值时使用显式 HTTP(S) 代理，仅代理端点自身允许放宽证书。
/// </summary>
public sealed record PublicArticleRequestContext
{
    public static PublicArticleRequestContext SystemProxyOrDefault { get; } = new();

    public bool UseSystemProxy { get; init; } = true;

    public Uri? ProxyUri { get; init; }

    public TimeSpan? Timeout { get; init; }

    public long? MaximumMediaBytes { get; init; }

    public static PublicArticleRequestContext Direct(TimeSpan? timeout = null) =>
        new() { UseSystemProxy = false, Timeout = timeout };

    public static PublicArticleRequestContext SystemProxy(TimeSpan? timeout = null) =>
        new() { UseSystemProxy = true, Timeout = timeout };

    public static PublicArticleRequestContext ExplicitProxy(Uri proxyUri, TimeSpan? timeout = null) =>
        new() { UseSystemProxy = false, ProxyUri = proxyUri, Timeout = timeout };

    public IWebProxy? ResolveProxy(Uri target)
    {
        if (ProxyUri is not null) return new WebProxy(ProxyUri);
        if (!UseSystemProxy) return null;
        return HttpClient.DefaultProxy;
    }
}

/// <summary>原始 HTML 抓取结果：要么是重定向目标，要么是最终页面，要么是错误。</summary>
public sealed record RawPublicFetchResult(
    string Html,
    string FinalUrl,
    Uri? RedirectUri,
    string Error)
{
    public static RawPublicFetchResult Failure(string error) => new("", "", null, error);
}
