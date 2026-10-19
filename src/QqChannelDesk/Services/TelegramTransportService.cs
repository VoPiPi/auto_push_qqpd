using System.Net;

namespace QqChannelDesk.Services;

/// <summary>
/// TG 采集的传输调度：直连/系统代理/代理池统一走标准 HTTP(S) 请求，只改“怎么连”，
/// 不改目标主机、端口、路径或查询串；在线解析保留 WebView2 网页代理。
/// 自动选择按“直连 → 系统代理 → 代理池 TG 节点 → 在线解析”顺序回退；显式选择失败后不回退。
/// </summary>
public sealed class TelegramTransportService : ITelegramTransport
{
    private const int MaximumProxyPoolNodes = 5;
    private const int MaximumRedirects = 5;
    private const long MaximumMediaBytes = 100L * 1024 * 1024;
    private static readonly TimeSpan OnlineMediaTimeout = TimeSpan.FromMinutes(3);

    private readonly PublicArticleFetcher _fetcher;
    private readonly ProxyPoolStore _proxyPoolStore;
    private readonly WebProxyProviderRegistry _onlineRegistry;

    public TelegramTransportService(
        ProxyPoolStore? proxyPoolStore = null,
        WebProxyProviderRegistry? onlineRegistry = null,
        PublicArticleFetcher? fetcher = null)
    {
        _proxyPoolStore = proxyPoolStore ?? new ProxyPoolStore();
        _onlineRegistry = onlineRegistry ?? new WebProxyProviderRegistry();
        _fetcher = fetcher ?? new PublicArticleFetcher();
    }

    public WebProxyProviderRegistry OnlineProviders => _onlineRegistry;

    public ProxyPoolStore ProxyPoolStore => _proxyPoolStore;

    /// <summary>自动化测试注入点：替换原始 HTTP 抓取，避免单测访问真实网络。</summary>
    internal Func<Uri, PublicArticleRequestContext, CancellationToken, Task<RawPublicFetchResult>>? RawFetchOverride { get; set; }

    public async Task<TelegramTransportPreview> FetchChannelAsync(
        string channelUrl,
        TelegramTransportSelection selection,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var channelName = TelegramCollectorService.ReadChannelName(channelUrl);
        var attempts = new List<string>();
        var automatic = selection.IsAutomatic;

        if (automatic || selection.IsDirect)
        {
            var result = await TryRawAsync(channelUrl, channelName, TelegramTransportContext.Direct(), "直连", progress, attempts, cancellationToken);
            if (result is not null) return result;
            if (!automatic) return Failed(attempts, selection, "直连请求失败，请检查网络或改用其他通道");
        }

        if (automatic || selection.IsSystem)
        {
            var result = await TryRawAsync(channelUrl, channelName, TelegramTransportContext.System(), "系统代理", progress, attempts, cancellationToken);
            if (result is not null) return result;
            if (!automatic) return Failed(attempts, selection, $"{TelegramTransportSelector.SystemDisplayName}请求失败，请检查系统代理设置");
        }

        if (automatic || selection.IsProxyPool)
        {
            var result = await TryProxyPoolAsync(channelUrl, channelName, progress, attempts, cancellationToken);
            if (result is not null) return result;
            if (!automatic) return Failed(attempts, selection, $"{TelegramTransportSelector.ProxyPoolDisplayName}没有可用节点，请先在代理池测试 TG 可用性");
        }

        if (automatic || selection.IsOnline)
        {
            return await TryOnlineAsync(channelUrl, channelName, selection, progress, attempts, cancellationToken);
        }

        return new TelegramTransportPreview(
            false, [], null, "", "", $"无法识别的传输通道：{selection.Value}。请重新选择。", attempts);
    }

    public async Task<string> DownloadMediaAsync(
        TelegramMediaRef media,
        TelegramTransportContext context,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(context);
        var (url, cookie) = context.SelectMediaSource(media);
        var requestContext = context.IsOnline
            ? PublicArticleRequestContext.SystemProxyOrDefault with
            {
                Timeout = OnlineMediaTimeout,
                MaximumMediaBytes = MaximumMediaBytes
            }
            : context.ToRequestContext() with
            {
                Timeout = PublicArticleFetcher.DefaultMediaTimeout,
                MaximumMediaBytes = MaximumMediaBytes
            };
        return await _fetcher.DownloadMediaAsync(
            url,
            media.Kind,
            requestContext,
            targetDirectory,
            cancellationToken,
            context.IsOnline ? cookie : null);
    }

    private async Task<TelegramTransportPreview?> TryRawAsync(
        string channelUrl,
        string channelName,
        TelegramTransportContext context,
        string displayName,
        Action<string>? progress,
        List<string> attempts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke($"正在通过{displayName}打开 TG 频道…");
        try
        {
            // 传输层不自动跟随重定向，这里逐跳校验公网地址后重建请求，避免把代理页当成频道页解析。
            var currentUrl = channelUrl;
            var html = "";
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var currentUri))
                    throw new InvalidOperationException("TG 频道地址无效，请重新填写。");
                var raw = RawFetchOverride is null
                    ? await _fetcher.FetchRawHtmlAsync(currentUri, context.ToRequestContext(), cancellationToken)
                    : await RawFetchOverride(currentUri, context.ToRequestContext(), cancellationToken);
                if (raw.Error.Length > 0) throw new InvalidOperationException(raw.Error);
                if (raw.RedirectUri is null)
                {
                    html = raw.Html;
                    break;
                }
                if (redirect == MaximumRedirects)
                    throw new InvalidOperationException("频道页跳转次数过多，请更换通道后重试。");
                if (!PublicArticleFetcher.TryNormalizePublicUrl(raw.RedirectUri.AbsoluteUri, out var next, out var error))
                    throw new InvalidOperationException(error);
                currentUrl = next.AbsoluteUri;
            }
            if (html.Length == 0)
                throw new InvalidOperationException("频道页没有返回内容，请更换通道后重试。");
            var messages = TelegramChannelParser.ParseChannelPage(html, channelName);
            if (messages.Count == 0)
            {
                attempts.Add($"{displayName}：页面没有解析到频道消息");
                return null;
            }
            return new TelegramTransportPreview(true, messages, context, context.Mode, displayName, "", attempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            attempts.Add($"{displayName}：请求超时");
            AppLogger.Instance.Warning($"tg 采集{displayName}超时");
            return null;
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            attempts.Add($"{displayName}：{message}");
            AppLogger.Instance.Warning($"tg 采集{displayName}失败：{message}");
            return null;
        }
    }

    private async Task<TelegramTransportPreview?> TryProxyPoolAsync(
        string channelUrl,
        string channelName,
        Action<string>? progress,
        List<string> attempts,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProxyNode> nodes;
        try
        {
            nodes = await _proxyPoolStore.GetTelegramNodesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            attempts.Add($"{TelegramTransportSelector.ProxyPoolDisplayName}：读取节点失败（{message}）");
            AppLogger.Instance.Warning($"tg 采集读取代理池节点失败：{message}");
            return null;
        }

        if (nodes.Count == 0)
        {
            attempts.Add($"{TelegramTransportSelector.ProxyPoolDisplayName}：没有 TG 可访问的存活节点");
            return null;
        }

        var candidates = nodes.Take(MaximumProxyPoolNodes).ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = candidates[index];
            var label = $"{TelegramTransportSelector.ProxyPoolDisplayName} {index + 1}/{candidates.Length}（{node.Address}）";
            var result = await TryRawAsync(
                channelUrl,
                channelName,
                TelegramTransportContext.ProxyPool() with { ProxyNode = node },
                label,
                progress,
                attempts,
                cancellationToken);
            if (result is not null) return result;
        }
        return null;
    }

    private async Task<TelegramTransportPreview> TryOnlineAsync(
        string channelUrl,
        string channelName,
        TelegramTransportSelection selection,
        Action<string>? progress,
        List<string> attempts,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IWebProxyProvider> providers;
        try
        {
            providers = _onlineRegistry.ResolveOnlineProviders(selection);
        }
        catch (InvalidOperationException ex)
        {
            attempts.Add($"{TelegramTransportSelector.OnlineDisplayName}：{CliDiagnostics.Sanitize(ex.Message)}");
            return new TelegramTransportPreview(false, [], null, "", "", ex.Message, attempts);
        }

        for (var index = 0; index < providers.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = providers[index];
            progress?.Invoke($"正在通过在线解析（{provider.DisplayName}）打开 TG 频道，节点不稳定时需要稍等片刻…");
            try
            {
                var page = await provider.FetchAsync(channelUrl, ".tgme_widget_message", cancellationToken);
                var messages = TelegramChannelParser.ParseChannelPage(page.Html, channelName);
                if (messages.Count == 0)
                {
                    attempts.Add($"{provider.DisplayName}：页面没有解析到频道消息");
                    continue;
                }
                var context = TelegramTransportContext.Online(
                    page.ProviderId.Length == 0 ? provider.Id : page.ProviderId) with { CookieHeader = page.CookieHeader };
                return new TelegramTransportPreview(
                    true,
                    messages,
                    context,
                    page.ProviderId.Length == 0 ? provider.Id : page.ProviderId,
                    page.ProviderName.Length == 0 ? provider.DisplayName : page.ProviderName,
                    "",
                    attempts);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = CliDiagnostics.Sanitize(ex.Message);
                attempts.Add($"{provider.DisplayName}：{message}");
                AppLogger.Instance.Warning($"tg 在线解析 {provider.DisplayName} 失败：{message}");
            }
        }

        var detail = attempts.Count == 0 ? "没有可用的在线解析服务" : string.Join("；", attempts);
        return new TelegramTransportPreview(
            false, [], null, "", "",
            $"{TelegramTransportSelector.OnlineDisplayName}失败，请检查网络或更换通道后重试：{detail}",
            attempts);
    }

    private static TelegramTransportPreview Failed(
        IReadOnlyList<string> attempts,
        TelegramTransportSelection selection,
        string prefix)
    {
        var detail = attempts.Count == 0 ? "没有可用的传输通道" : string.Join("；", attempts);
        return new TelegramTransportPreview(false, [], null, "", "", $"{prefix}：{detail}", attempts);
    }
}
