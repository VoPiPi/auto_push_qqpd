using HtmlAgilityPack;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace QqChannelDesk.Services;

public sealed class PublicArticleFetcher
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public async Task<ArticleFetchResult> FetchAsync(string input, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePublicUrl(input, out var current, out var error)) return new(false, "", "", "", "", error);
        try
        {
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsurePublicAddress(await Dns.GetHostAddressesAsync(current.DnsSafeHost, cancellationToken));
                using var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(8),
                    ConnectCallback = ConnectPublicAsync
                };
                using var client = new HttpClient(handler) { Timeout = RequestTimeout };
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("QqChannelDesk/1.0 (+public-page-reader)");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == MaximumRedirects) return Failed("网页重定向次数过多。");
                    var location = response.Headers.Location;
                    if (location is null) return Failed("网页返回了无效的重定向地址。");
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!TryNormalizePublicUrl(next.AbsoluteUri, out current, out error)) return Failed(error);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return Failed($"网页返回 HTTP {(int)response.StatusCode}，无法读取内容。");
                if (response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                    !(mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
                    return Failed("链接不是 HTML 网页，无法提取文章内容。");
                if (response.Content.Headers.ContentLength > MaximumBytes) return Failed("网页内容超过 2 MB，已停止读取。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, cancellationToken);
                    if (read == 0) break;
                    if (buffer.Length + read > MaximumBytes) return Failed("网页内容超过 2 MB，已停止读取。");
                    buffer.Write(chunk, 0, read);
                }
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
                var encoding = TryEncoding(charset);
                var html = encoding.GetString(buffer.ToArray());
                var parsed = await Task.Run(() => ParseHtml(html), cancellationToken);
                if (parsed.Title.Length == 0 && parsed.Content.Length == 0) return Failed("网页没有识别到可用标题或正文。");
                return new(true, current.AbsoluteUri, current.DnsSafeHost, parsed.Title, parsed.Content, "");
            }
            return Failed("无法读取该网页。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed("读取网页超时（最长 20 秒）。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or InvalidOperationException)
        {
            return Failed($"读取网页失败：{ex.Message}");
        }
    }

    public Task<string> DownloadMediaAsync(string input, string expectedType, CancellationToken cancellationToken = default) =>
        DownloadMediaAsync(input, expectedType, Path.Combine(AppContext.BaseDirectory, "tools", "media-cache"), cancellationToken);

    public async Task<string> DownloadMediaAsync(
        string input,
        string expectedType,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePublicUrl(input, out var current, out var error)) throw new InvalidOperationException(error);
        if (expectedType is not ("image" or "video")) throw new ArgumentOutOfRangeException(nameof(expectedType));
        if (string.IsNullOrWhiteSpace(targetDirectory)) throw new ArgumentException("媒体保存目录不能为空。", nameof(targetDirectory));
        const long maximumMediaBytes = 100L * 1024 * 1024;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePublicAddress(await Dns.GetHostAddressesAsync(current.DnsSafeHost, cancellationToken));
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8), ConnectCallback = ConnectPublicAsync };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("QqChannelDesk/1.0 (+public-media-reader)");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects || response.Headers.Location is null) throw new InvalidOperationException("媒体链接重定向无效或次数过多。");
                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                if (!TryNormalizePublicUrl(next.AbsoluteUri, out current, out error)) throw new InvalidOperationException(error);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"媒体服务器返回 HTTP {(int)response.StatusCode}。");
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (expectedType == "image" && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("图片链接没有返回 image/* 媒体类型。");
            if (expectedType == "video" && !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("视频链接没有返回 video/* 媒体类型。");
            if (response.Content.Headers.ContentLength is > maximumMediaBytes) throw new InvalidOperationException("媒体文件超过 100 MB 限制。");
            var extension = Path.GetExtension(current.AbsolutePath);
            if (extension.Length is < 2 or > 8 || extension.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.')) extension = expectedType == "image" ? ".img" : ".video";
            var directory = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
            try
            {
                await using var inputStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var outputStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await inputStream.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > maximumMediaBytes) throw new InvalidOperationException("媒体文件超过 100 MB 限制。");
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
        throw new InvalidOperationException("无法下载媒体链接。");
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

    public static (string Title, string Content) ParseHtml(string html)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);
        IEnumerable<HtmlNode> removableNodes = doc.DocumentNode.SelectNodes("//script|//style|//noscript|//svg|//nav|//header|//footer|//aside|//form|//button|//iframe") ?? Enumerable.Empty<HtmlNode>();
        foreach (var node in removableNodes)
            node.Remove();
        var title = Clean(doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "") ??
                          doc.DocumentNode.SelectSingleNode("//title")?.InnerText ??
                          doc.DocumentNode.SelectSingleNode("//h1")?.InnerText ?? "");
        var body = doc.DocumentNode.SelectSingleNode("//article") ?? doc.DocumentNode.SelectSingleNode("//main") ?? doc.DocumentNode.SelectSingleNode("//body");
        var paragraphs = body?.SelectNodes(".//p|.//h1|.//h2|.//h3|.//li|.//blockquote")
            ?? body?.SelectNodes(".//text()[normalize-space()]");
        var lines = paragraphs is null
            ? Array.Empty<string>()
            : paragraphs.Select(node => Clean(node.InnerText)).Where(text => text.Length >= 2).Distinct().ToArray();
        var content = string.Join(Environment.NewLine + Environment.NewLine, lines);
        if (content.Length > 100_000) content = content[..100_000];
        return (title.Length > 300 ? title[..300] : title, content);
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        EnsurePublicAddress(addresses);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
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

    private static void EnsurePublicAddress(IEnumerable<IPAddress> addresses)
    {
        var resolved = addresses.ToArray();
        if (resolved.Length == 0 || resolved.Any(address => !IsPublic(address)))
            throw new HttpRequestException("网页域名解析到本机、私有或链路本地地址，已阻止访问。");
    }

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
    private static ArticleFetchResult Failed(string message) => new(false, "", "", "", "", message);
}

public sealed record ArticleFetchResult(bool Succeeded, string Url, string SourceHost, string Title, string Content, string Error);
