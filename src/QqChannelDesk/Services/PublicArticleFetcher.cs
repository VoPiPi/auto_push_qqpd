using HtmlAgilityPack;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace QqChannelDesk.Services;

public sealed class PublicArticleFetcher
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan DefaultMediaTimeout = TimeSpan.FromSeconds(45);

    public async Task<ArticleFetchResult> FetchAsync(string input, CancellationToken cancellationToken = default)
        => await FetchAsync(input, PublicArticleRequestContext.SystemProxyOrDefault, cancellationToken);

    /// <summary>带自定义正文选择器获取文章；选择器为空时使用内置通用规则。</summary>
    public async Task<ArticleFetchResult> FetchAsync(
        string input,
        string? contentSelector,
        CancellationToken cancellationToken = default)
        => await FetchAsync(input, PublicArticleRequestContext.SystemProxyOrDefault, contentSelector, cancellationToken);

    /// <summary>直连/显式代理模式使用的入口；<paramref name="requestContext"/> 为 null 表示保留系统代理行为。</summary>
    internal async Task<ArticleFetchResult> FetchAsync(
        string input,
        PublicArticleRequestContext? requestContext,
        CancellationToken cancellationToken = default)
        => await FetchAsync(input, requestContext, null, cancellationToken);

    internal async Task<ArticleFetchResult> FetchAsync(
        string input,
        PublicArticleRequestContext? requestContext,
        string? contentSelector,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePublicUrl(input, out var current, out var error)) return new(false, "", "", "", "", error);
        try
        {
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var raw = await FetchRawHtmlAsync(current, requestContext, cancellationToken);
                if (raw.RedirectUri is not null)
                {
                    if (redirect == MaximumRedirects) return Failed("网页跳转次数过多，请换一个链接。");
                    if (!TryNormalizePublicUrl(raw.RedirectUri.AbsoluteUri, out current, out error)) return Failed(error);
                    continue;
                }
                if (raw.Error.Length > 0) return Failed(raw.Error);

                var finalUrl = raw.FinalUrl.Length == 0 ? current.AbsoluteUri : raw.FinalUrl;
                var parsed = await Task.Run(
                    () => ParseHtmlWithRule(raw.Html, contentSelector, baseUrl: finalUrl),
                    cancellationToken);
                if (parsed.Title.Length == 0 && parsed.Content.Length == 0)
                    return Failed("网页没有可用标题或正文，请换一个链接。", raw.Html);
                return new(
                    true,
                    finalUrl,
                    current.DnsSafeHost,
                    parsed.Title,
                    parsed.Content,
                    "",
                    raw.Html,
                    parsed.Rule,
                    parsed.MediaList);
            }
            return Failed("无法读取网页，请检查链接或网络后重试。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed("读取网页超时，请重试。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or InvalidOperationException)
        {
            return Failed($"读取网页失败，请检查网络后重试：{ex.Message}");
        }
    }

    /// <summary>
    /// 抓取单个公开 HTTP(S) 地址的原始 HTML。不跟随自动重定向，逐跳公网校验由 ConnectCallback 完成；
    /// 直连模式不允许域名解析到本机/私有地址，显式代理/系统代理只放宽代理端点自身。
    /// </summary>
    internal async Task<RawPublicFetchResult> FetchRawHtmlAsync(
        Uri url,
        PublicArticleRequestContext? requestContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutSource = new CancellationTokenSource(
                requestContext?.Timeout ?? DefaultRequestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            using var handler = CreateHandler(url, requestContext);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("QqChannelDesk/1.0 (+public-page-reader)");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null) return RawPublicFetchResult.Failure("网页跳转地址无效，请换一个链接。");
                return new("", "", location.IsAbsoluteUri ? location : new Uri(url, location), "");
            }
            if (!response.IsSuccessStatusCode)
                return RawPublicFetchResult.Failure($"网页无法打开（HTTP {(int)response.StatusCode}），请检查链接后重试。");
            if (response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                !(mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
                return RawPublicFetchResult.Failure("该链接不是网页，请使用公开网页链接。");
            if (response.Content.Headers.ContentLength > MaximumBytes)
                return RawPublicFetchResult.Failure("网页内容过大，已停止读取，请换一个页面。");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, linked.Token);
                if (read == 0) break;
                if (buffer.Length + read > MaximumBytes)
                    return RawPublicFetchResult.Failure("网页内容过大，已停止读取，请换一个页面。");
                buffer.Write(chunk, 0, read);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            var encoding = TryEncoding(charset);
            var html = encoding.GetString(buffer.ToArray());
            return new(html, response.RequestMessage?.RequestUri?.AbsoluteUri ?? url.AbsoluteUri, null, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RawPublicFetchResult.Failure("读取网页超时，请重试。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or InvalidOperationException)
        {
            return RawPublicFetchResult.Failure($"读取网页失败，请检查网络后重试：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    public Task<string> DownloadMediaAsync(string input, string expectedType, CancellationToken cancellationToken = default) =>
        DownloadMediaAsync(input, expectedType, Path.Combine(AppContext.BaseDirectory, "tools", "media-cache"), cancellationToken);

    public Task<string> DownloadMediaAsync(
        string input,
        string expectedType,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default,
        string? cookieHeader = null) =>
        DownloadMediaAsync(input, expectedType, null, targetDirectory, cancellationToken, cookieHeader);

    /// <summary>
    /// 下载图片/视频。非在线通道只请求原始 t.me 媒体地址；在线解析通道才带代理地址与 Cookie。
    /// 每一跳重定向都重新校验公网地址，实际连接在 ConnectCallback 内解析，避免 DNS 重绑定窗口。
    /// </summary>
    internal async Task<string> DownloadMediaAsync(
        string input,
        string expectedType,
        PublicArticleRequestContext? requestContext,
        string? targetDirectory = null,
        CancellationToken cancellationToken = default,
        string? cookieHeader = null)
    {
        if (!TryNormalizePublicUrl(input, out var current, out var error)) throw new InvalidOperationException(error);
        if (expectedType is not ("image" or "video")) throw new ArgumentOutOfRangeException(nameof(expectedType));
        var saveDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(targetDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "tools", "media-cache")
            : targetDirectory);
        var cookie = string.IsNullOrWhiteSpace(cookieHeader) ? "" : cookieHeader.Trim();
        var maximumMediaBytes = requestContext?.MaximumMediaBytes ?? 100L * 1024 * 1024;
        var timeout = requestContext?.Timeout ?? DefaultMediaTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var token = linked.Token;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            token.ThrowIfCancellationRequested();
            using var handler = CreateHandler(current, requestContext);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("QqChannelDesk/1.0 (+public-media-reader)");
            if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects || response.Headers.Location is null) throw new InvalidOperationException("媒体链接跳转无效或次数过多，请更换链接。");
                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                if (!TryNormalizePublicUrl(next.AbsoluteUri, out current, out error)) throw new InvalidOperationException(error);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"媒体下载未完成（HTTP {(int)response.StatusCode}），请稍后重试。");
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (expectedType == "image" && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("该链接不是图片，请更换链接。");
            if (expectedType == "video" && !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("该链接不是视频，请更换链接。");
            if (response.Content.Headers.ContentLength > maximumMediaBytes) throw new InvalidOperationException("媒体文件超过 100 MB，请压缩或更换文件。");
            var extension = Path.GetExtension(current.AbsolutePath);
            if (extension.Length is < 2 or > 8 || extension.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.')) extension = expectedType == "image" ? ".img" : ".video";
            Directory.CreateDirectory(saveDirectory);
            var path = Path.Combine(saveDirectory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
            try
            {
                await using var inputStream = await response.Content.ReadAsStreamAsync(token);
                await using var outputStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await inputStream.ReadAsync(buffer, token);
                    if (read == 0) break;
                    total += read;
                    if (total > maximumMediaBytes) throw new InvalidOperationException("媒体文件超过 100 MB，请压缩或更换文件。");
                    await outputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                return path;
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        }
        throw new InvalidOperationException("无法下载媒体，请检查链接或网络后重试。");
    }

    public static bool TryNormalizePublicUrl(string input, out Uri uri, out string error)
    {
        error = "";
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(candidate.Host) || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = null!;
            error = "请输入有效的公开 HTTP 或 HTTPS 网页链接。";
            return false;
        }
        if (candidate.IsLoopback || candidate.HostNameType == UriHostNameType.IPv4 && !IsPublic(IPAddress.Parse(candidate.Host)) ||
            candidate.HostNameType == UriHostNameType.IPv6 && !IsPublic(IPAddress.Parse(candidate.Host)))
        {
            uri = null!;
            error = "为保护本机安全，不能读取本机、私有或链路本地地址。";
            return false;
        }
        var builder = new UriBuilder(candidate) { Fragment = "" };
        if ((builder.Scheme == "http" && builder.Port == 80) || (builder.Scheme == "https" && builder.Port == 443)) builder.Port = -1;
        uri = builder.Uri;
        return true;
    }

    /// <summary>
    /// 通用文章内容提取：按优先级尝试常见正文容器、JSON-LD 结构化数据和 meta 摘要，
    /// 以覆盖市面上大多数新闻/博客站点，而不是只识别 article/main 标签。
    /// </summary>
    public static (string Title, string Content) ParseHtml(string html, string? contentSelector = null)
    {
        var result = ParseHtmlWithRule(html, contentSelector);
        return (result.Title, result.Content);
    }

    /// <summary>
    /// 解析 HTML 并返回命中的正文规则（内置 XPath 或自定义选择器）。
    /// <paramref name="excludeSelectors"/> 用于“换一种规则重新解析”：传入已命中的规则后按优先级尝试下一规则；
    /// 所有内置规则都被排除时返回空结果（Rule 为空），由调用方决定 AI 兜底或提示失败。
    /// </summary>
    public static ArticleParseResult ParseHtmlWithRule(
        string html,
        string? contentSelector = null,
        IReadOnlyCollection<string>? excludeSelectors = null,
        string? baseUrl = null)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);
        var mediaBaseUri = ResolveMediaBaseUri(doc, baseUrl);

        // JSON-LD 结构化数据通常比 DOM 更可靠，先解析但不立即采用，作为提取不到 DOM 时的兜底。
        var jsonLd = TryParseJsonLd(doc);
        var title = Clean(
            doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "") ??
            doc.DocumentNode.SelectSingleNode("//meta[@name='twitter:title']")?.GetAttributeValue("content", "") ??
            doc.DocumentNode.SelectSingleNode("//title")?.InnerText ??
            doc.DocumentNode.SelectSingleNode("//h1")?.InnerText ??
            jsonLd.Title);

        RemoveNoiseNodes(doc);
        var body = SelectArticleBody(doc, contentSelector, excludeSelectors, out var rule);
        if (body is null) return new ArticleParseResult("", "", "", []);
        var lines = ExtractReadableLines(body);
        var media = ExtractMediaUrls(body, mediaBaseUri);

        // 正文容器未提取到足够内容时，用 meta 摘要或 JSON-LD 内容兜底。
        if (lines.Count < 2)
        {
            var fallback = Clean(
                doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", "") ??
                doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "") ??
                doc.DocumentNode.SelectSingleNode("//meta[@name='twitter:description']")?.GetAttributeValue("content", "") ??
                jsonLd.Content);
            if (fallback.Length >= 10) lines.Add(fallback);
        }

        var content = string.Join(Environment.NewLine + Environment.NewLine, lines);
        if (content.Length > 100_000) content = content[..100_000];
        return new ArticleParseResult(
            title.Length > 300 ? title[..300] : title,
            content,
            rule,
            media);
    }

    /// <summary>
    /// 常见新闻/博客正文容器的 XPath 列表，按优先级从高到低尝试。
    /// 同时匹配 id 与 class，覆盖新浪、网易、腾讯、CSDN、WordPress 等主流结构。
    /// </summary>
    private static readonly string[] ArticleContainerSelectors =
    [
        "//article",
        "//*[@id='article-content']", "//*[@id='article_content']", "//*[@id='content']",
        "//*[@id='article']", "//*[@id='detail']", "//*[@id='post-content']",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article-content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article_content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article-body ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article_body ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article-detail ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article-detail-content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' post-content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' post_content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' entry-content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' entry_content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' detail-content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' news-content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' news_content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' content-article ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article-main ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' postBody ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' post_body ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' rich_media_content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' rich_media_area_primary ')]",
        "//main",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' content ')]",
        "//*[contains(concat(' ', normalize-space(@class), ' '), ' article ')]",
        "//body"
    ];

    private static HtmlNode? SelectArticleBody(HtmlDocument doc, string? contentSelector, IReadOnlyCollection<string>? excludeSelectors, out string rule)
    {
        rule = "";
        // 用户显式配置的选择器优先；支持 CSS 选择器（#id/.class/tag）与 XPath。
        if (!string.IsNullOrWhiteSpace(contentSelector))
        {
            var custom = contentSelector.Trim();
            try
            {
                var node = custom.StartsWith("//", StringComparison.Ordinal)
                    ? doc.DocumentNode.SelectSingleNode(custom)
                    : doc.DocumentNode.SelectSingleNode(ToXPath(custom));
                if (node is not null)
                {
                    rule = custom;
                    return node;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                AppLogger.Instance.Warning($"自定义正文选择器无效：{CliDiagnostics.Sanitize(ex.Message)}");
            }
        }

        foreach (var selector in ArticleContainerSelectors)
        {
            // “换一种规则”场景跳过已命中过的选择器，让下一条规则生效。
            if (excludeSelectors is not null && excludeSelectors.Contains(selector, StringComparer.Ordinal)) continue;
            try
            {
                var node = doc.DocumentNode.SelectSingleNode(selector);
                if (node is not null && HasReadableContent(node))
                {
                    rule = selector;
                    return node;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 单个选择器失败不阻断后续尝试。
            }
        }

        // 最终兜底退回 body；“换一种规则”已排除 body 时视为规则用尽，返回 null 交给调用方兜底。
        const string bodySelector = "//body";
        if (excludeSelectors is not null && excludeSelectors.Contains(bodySelector, StringComparer.Ordinal)) return null;
        var fallback = doc.DocumentNode.SelectSingleNode(bodySelector);
        if (fallback is not null) rule = bodySelector;
        return fallback;
    }

    private static bool HasReadableContent(HtmlNode node)
    {
        var text = Clean(node.InnerText);
        return text.Length >= 20;
    }

    private static Uri? ResolveMediaBaseUri(HtmlDocument doc, string? baseUrl)
    {
        var candidate = baseUrl?.Trim() ?? "";
        var baseHref = doc.DocumentNode
            .SelectSingleNode("//base[@href]")
            ?.GetAttributeValue("href", "")
            .Trim() ?? "";
        if (baseHref.Length > 0)
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var pageUri) &&
                Uri.TryCreate(pageUri, HtmlEntity.DeEntitize(baseHref), out var combined))
            {
                candidate = combined.AbsoluteUri;
            }
            else if (Uri.TryCreate(HtmlEntity.DeEntitize(baseHref), UriKind.Absolute, out var absoluteBase))
            {
                candidate = absoluteBase.AbsoluteUri;
            }
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    private static IReadOnlyList<string> ExtractMediaUrls(HtmlNode body, Uri? baseUri)
    {
        const int maximumMediaCount = 50;
        var media = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = body.SelectNodes(".//img|.//video|.//source");
        if (nodes is null) return media;

        foreach (var node in nodes)
        {
            if (media.Count >= maximumMediaCount) break;
            switch (node.Name.ToLowerInvariant())
            {
                case "img":
                    AddImageMedia(node, baseUri, media, seen, maximumMediaCount);
                    break;
                case "video":
                    AddFirstMedia(
                        node,
                        baseUri,
                        media,
                        seen,
                        maximumMediaCount,
                        ["data-src", "src"]);
                    break;
                case "source":
                    AddFirstMedia(
                        node,
                        baseUri,
                        media,
                        seen,
                        maximumMediaCount,
                        ["data-src", "src"]);
                    AddSrcset(node, "data-srcset", baseUri, media, seen, maximumMediaCount);
                    AddSrcset(node, "srcset", baseUri, media, seen, maximumMediaCount);
                    break;
            }
        }

        return media;
    }

    private static void AddImageMedia(
        HtmlNode node,
        Uri? baseUri,
        List<string> media,
        HashSet<string> seen,
        int maximumMediaCount)
    {
        // 懒加载站点通常把真实地址放在 data-*，而 src 只是占位图。
        if (AddFirstMedia(
                node,
                baseUri,
                media,
                seen,
                maximumMediaCount,
                ["data-src", "data-original", "data-lazy-src", "data-actualsrc"]))
        {
            return;
        }

        if (AddSrcset(node, "data-srcset", baseUri, media, seen, maximumMediaCount) ||
            AddSrcset(node, "srcset", baseUri, media, seen, maximumMediaCount))
        {
            return;
        }

        AddFirstMedia(node, baseUri, media, seen, maximumMediaCount, ["src"]);
    }

    private static bool AddFirstMedia(
        HtmlNode node,
        Uri? baseUri,
        List<string> media,
        HashSet<string> seen,
        int maximumMediaCount,
        IReadOnlyList<string> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (AddMediaCandidate(
                    node.GetAttributeValue(attribute, ""),
                    baseUri,
                    media,
                    seen,
                    maximumMediaCount))
            {
                return true;
            }
        }
        return false;
    }

    private static bool AddSrcset(
        HtmlNode node,
        string attribute,
        Uri? baseUri,
        List<string> media,
        HashSet<string> seen,
        int maximumMediaCount)
    {
        var srcset = HtmlEntity.DeEntitize(node.GetAttributeValue(attribute, "")).Trim();
        if (srcset.Length == 0) return false;
        foreach (var entry in srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var source = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (AddMediaCandidate(source, baseUri, media, seen, maximumMediaCount)) return true;
        }
        return false;
    }

    private static bool AddMediaCandidate(
        string? candidate,
        Uri? baseUri,
        List<string> media,
        HashSet<string> seen,
        int maximumMediaCount)
    {
        if (media.Count >= maximumMediaCount || string.IsNullOrWhiteSpace(candidate)) return false;
        var value = HtmlEntity.DeEntitize(candidate).Trim();
        if (value.Length == 0 ||
            value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Uri? resolved = null;
        if (baseUri is not null && Uri.TryCreate(baseUri, value, out var combined))
            resolved = combined;
        else if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            resolved = absolute;
        if (resolved is null ||
            !TryNormalizePublicUrl(resolved.AbsoluteUri, out var normalized, out _))
        {
            return false;
        }

        var normalizedUrl = normalized.AbsoluteUri;
        if (!MaterialMediaValidator.IsValidSource(normalizedUrl, "image") &&
            !MaterialMediaValidator.IsValidSource(normalizedUrl, "video"))
        {
            return false;
        }
        if (!seen.Add(normalizedUrl)) return false;

        media.Add(normalizedUrl);
        return true;
    }

    private static List<string> ExtractReadableLines(HtmlNode? body)
    {
        if (body is null) return new List<string>();
        var paragraphs = body.SelectNodes(".//p|.//h1|.//h2|.//h3|.//h4|.//h5|.//h6|.//li|.//blockquote|.//pre|.//td")
            ?? body.SelectNodes(".//text()[normalize-space()]");
        if (paragraphs is null) return [];

        var lines = new List<string>();
        foreach (var node in paragraphs)
        {
            // 跳过被移除节点后的空文本和明显噪音。
            var text = Clean(node.InnerText);
            if (text.Length < 2) continue;
            if (IsNoiseLine(text)) continue;
            lines.Add(text);
        }

        // 去重且保留顺序，但不去掉重复引用的同类段落。
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (seen.Add(line)) distinct.Add(line);
        }

        return distinct;
    }

    private static bool IsNoiseLine(string text)
    {
        // 常见页脚/版权/分享/广告提示，避免污染正文。
        string[] markers =
        [
            "版权声明", "版权所有", "免责声明", "责任编辑", "来源：", "扫码", "分享到",
            "点击分享", "相关阅读", "延伸阅读", "上一篇", "下一篇", "广告", "更多精彩"
        ];
        return text.Length < 80 && markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveNoiseNodes(HtmlDocument doc)
    {
        IEnumerable<HtmlNode> removableNodes = doc.DocumentNode.SelectNodes(
            "//script|//style|//noscript|//svg|//nav|//header|//footer|//aside|//form|//button|//iframe") ??
            Enumerable.Empty<HtmlNode>();
        foreach (var node in removableNodes)
            node.Remove();
    }

    private static (string Title, string Content) TryParseJsonLd(HtmlDocument doc)
    {
        var nodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (nodes is null) return ("", "");
        foreach (var node in nodes)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(node.InnerText);
                var root = document.RootElement;
                var candidates = root.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? root.EnumerateArray().ToArray()
                    : [root];
                foreach (var candidate in candidates)
                {
                    if (candidate.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var type = candidate.TryGetProperty("@type", out var typeElement)
                        ? typeElement.ValueKind == System.Text.Json.JsonValueKind.String
                            ? typeElement.GetString() ?? ""
                            : typeElement.ValueKind == System.Text.Json.JsonValueKind.Array
                                ? string.Join(",", typeElement.EnumerateArray().Select(item => item.GetString() ?? ""))
                                : ""
                        : "";
                    if (type.Length > 0 &&
                        !type.Contains("Article", StringComparison.OrdinalIgnoreCase) &&
                        !type.Contains("News", StringComparison.OrdinalIgnoreCase) &&
                        !type.Contains("Blog", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var title = ReadJsonString(candidate, "headline") ?? ReadJsonString(candidate, "name") ?? "";
                    var content = ReadJsonString(candidate, "articleBody") ??
                                  ReadJsonString(candidate, "description") ??
                                  "";
                    if (title.Length > 0 || content.Length > 0) return (title, content);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 单个 JSON-LD 块无效时继续尝试下一个。
            }
        }
        return ("", "");
    }

    private static string? ReadJsonString(System.Text.Json.JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
            System.Text.Json.JsonValueKind.Object => ReadJsonString(value, "name") ?? "",
            _ => ""
        };
    }

    /// <summary>把简单 CSS 选择器转成 XPath，支持 #id、.class、tag、tag.class、tag#id。</summary>
    private static string ToXPath(string selector)
    {
        var trimmed = selector.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return trimmed;
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "//body";
        var xpath = string.Join("/", parts.Select(ToXPathSegment));
        return xpath.StartsWith("/", StringComparison.Ordinal) ? xpath : "//" + xpath;
    }

    private static string ToXPathSegment(string segment)
    {
        var tag = "div";
        var id = "";
        var classes = new List<string>();
        var index = 0;
        while (index < segment.Length)
        {
            var ch = segment[index];
            if (ch is '#')
            {
                var start = ++index;
                while (index < segment.Length && IsCssNameChar(segment[index])) index++;
                id = segment[start..index];
            }
            else if (ch is '.')
            {
                var start = ++index;
                while (index < segment.Length && IsCssNameChar(segment[index])) index++;
                classes.Add(segment[start..index]);
            }
            else if (char.IsLetter(ch))
            {
                var start = index;
                while (index < segment.Length && IsCssNameChar(segment[index])) index++;
                tag = segment[start..index];
            }
            else
            {
                index++;
            }
        }

        var conditions = new List<string>();
        if (id.Length > 0) conditions.Add($"@id='{id}'");
        foreach (var cls in classes)
            conditions.Add($"contains(concat(' ', normalize-space(@class), ' '), ' {cls} ')");
        return conditions.Count == 0 ? tag : $"{tag}[{string.Join(" and ", conditions)}]";
    }

    private static bool IsCssNameChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '-' or '_';

    internal static Uri? GetConfiguredProxyUri(Uri target, IWebProxy proxy)
    {
        if (proxy.IsBypassed(target)) return null;
        var candidate = proxy.GetProxy(target);
        return candidate is null || IsSameEndpoint(candidate, target) ? null : candidate;
    }

    internal static bool IsConfiguredProxyEndpoint(DnsEndPoint endpoint, Uri proxy)
    {
        if (endpoint.Port != proxy.Port) return false;
        var endpointHost = endpoint.Host.Trim('[', ']');
        var proxyHost = proxy.Host.Trim('[', ']');
        if (IPAddress.TryParse(endpointHost, out var endpointAddress) &&
            IPAddress.TryParse(proxyHost, out var proxyAddress))
            return endpointAddress.Equals(proxyAddress);
        return string.Equals(endpointHost, proxyHost, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(endpointHost, proxy.DnsSafeHost, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsConfiguredProxyEndpoint(Uri target, DnsEndPoint endpoint, IWebProxy proxy) =>
        GetConfiguredProxyUri(target, proxy) is { } proxyUri &&
        IsConfiguredProxyEndpoint(endpoint, proxyUri);

    internal static SocketsHttpHandler CreateHandler(Uri target, PublicArticleRequestContext? requestContext)
    {
        var proxy = requestContext is null ? HttpClient.DefaultProxy : requestContext.ResolveProxy(target);
        var proxyUri = proxy is null ? null : GetConfiguredProxyUri(target, proxy);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            UseProxy = proxyUri is not null,
            Proxy = proxy
        };

        // 每跳重新解析代理配置：请求上下文为 null 时沿用系统代理，否则按通道显式决定直连或代理。
        handler.ConnectCallback = (context, cancellationToken) =>
        {
            // 使用代理时，SocketsHttpHandler 会在连接层先连接代理；只对已配置的代理端点
            // 放宽本机/私有地址限制，真正直连目标时仍强制公网地址。
            if (proxy is not null && proxyUri is not null &&
                IsConfiguredProxyEndpoint(target, context.DnsEndPoint, proxy))
                return ConnectEndpointAsync(context.DnsEndPoint, requirePublicAddress: false, cancellationToken);
            return ConnectPublicAsync(context, cancellationToken);
        };

        if (proxyUri is not null)
            handler.SslOptions = BuildProxyCertificateOptions(proxyUri);

        return handler;
    }

    private static Uri? ResolveEffectiveProxyUri(Uri target, PublicArticleRequestContext? requestContext)
    {
        var proxy = requestContext is null ? HttpClient.DefaultProxy : requestContext.ResolveProxy(target);
        return proxy is null ? null : GetConfiguredProxyUri(target, proxy);
    }

    /// <summary>
    /// HTTPS 代理端点自身允许证书链/域名错误（公共代理常见），目标站点的 TLS 校验保持默认严格模式：
    /// 只有握手目标就是代理端点时才放宽，t.me 等目标始终验证链和主机名。
    /// </summary>
    private static SslClientAuthenticationOptions BuildProxyCertificateOptions(Uri proxyUri)
    {
        return new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                errors == SslPolicyErrors.None ||
                (sender is SslStream sslStream && IsProxyCertificateEndpoint(sslStream.TargetHostName, proxyUri, errors))
        };
    }

    internal static bool IsProxyCertificateEndpoint(string? targetHostName, Uri proxyUri, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if ((errors & ~RelaxableProxyCertificateErrors) != SslPolicyErrors.None) return false;
        var expected = NormalizeHost(proxyUri.DnsSafeHost);
        var actual = NormalizeHost(targetHostName);
        return expected.Length > 0 &&
               actual.Length > 0 &&
               string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private const SslPolicyErrors RelaxableProxyCertificateErrors =
        SslPolicyErrors.RemoteCertificateChainErrors |
        SslPolicyErrors.RemoteCertificateNameMismatch;

    private static string NormalizeHost(string? host) =>
        host?.Trim().Trim('[', ']').TrimEnd('.') ?? "";

    private static ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        return ConnectEndpointAsync(context.DnsEndPoint, requirePublicAddress: true, cancellationToken);
    }

    private static async ValueTask<Stream> ConnectEndpointAsync(
        DnsEndPoint endpoint,
        bool requirePublicAddress,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);
        if (requirePublicAddress) EnsurePublicAddress(addresses);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException("无法连接到公开网页服务器。", lastError);
    }

    internal static void EnsurePublicAddress(IEnumerable<IPAddress> addresses)
    {
        var resolved = addresses.ToArray();
        if (resolved.Length == 0 || resolved.Any(address => !IsPublic(address)))
            throw new HttpRequestException("网页域名解析到本机、私有或链路本地地址，已阻止访问。");
    }

    private static bool IsSameEndpoint(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                     b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                     b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                     b[0] == 192 && b[1] == 0 || b[0] == 198 && b[1] is 18 or 19 ||
                     b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            var globalUnicast = (b[0] & 0xE0) == 0x20;
            var documentation = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8;
            return globalUnicast && !documentation;
        }
        return false;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static Encoding TryEncoding(string? charset) { try { return string.IsNullOrWhiteSpace(charset) ? new UTF8Encoding(false, true) : Encoding.GetEncoding(charset); } catch (ArgumentException) { return Encoding.UTF8; } }
    private static string Clean(string value) => HtmlEntity.DeEntitize(value).Replace('\u00a0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? string.Join(' ', parts).Trim() : "";
    private static ArticleFetchResult Failed(string message, string rawHtml = "") => new(false, "", "", "", "", message, rawHtml);
}

public sealed record ArticleFetchResult(
    bool Succeeded,
    string Url,
    string SourceHost,
    string Title,
    string Content,
    string Error,
    string RawHtml = "",
    string Rule = "",
    IReadOnlyList<string>? MediaList = null)
{
    public IReadOnlyList<string> Media => MediaList ?? [];
}

/// <summary>正文解析结果：Rule 是命中的正文选择器（内置 XPath 或自定义选择器），规则用尽时为空。</summary>
public sealed record ArticleParseResult(
    string Title,
    string Content,
    string Rule,
    IReadOnlyList<string>? MediaList = null)
{
    public IReadOnlyList<string> Media => MediaList ?? [];
}
