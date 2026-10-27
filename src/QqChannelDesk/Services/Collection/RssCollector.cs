using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using System.Globalization;
using System.Xml;
using System.IO;
using QqChannelDesk.Services;

namespace QqChannelDesk.Services.Collection;

public sealed class RssCollector : ISourceCollector
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly HttpClient _client;
    private readonly bool _disposeClient;

    public RssCollector(HttpMessageHandler? handler = null)
    {
        _client = handler is null ? PublicUrlGuard.CreateHttpClient() : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _disposeClient = handler is null;
    }

    public string SourceType => "rss";

    public async Task<CollectionResult> CollectAsync(CollectionSource source, CancellationToken cancellationToken = default)
    {
        if (!PublicUrlGuard.TryNormalize(source.EntryUrl, out var url, out var error))
            return CollectionResult.Failure("data-error", error);
        try
        {
            var xml = await FetchXmlAsync(url, cancellationToken);
            var items = await Task.Run(() => ParseFeed(source, xml), cancellationToken);
            return CollectionResult.Success(items);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CollectionResult.Failure("timeout", "读取订阅源超时，请稍后重试。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return CollectionResult.Failure("http-error", CliDiagnostics.Sanitize(exception.Message));
        }
        catch (XmlException exception)
        {
            return CollectionResult.Failure("parse-error", $"订阅源格式无效：{CliDiagnostics.Sanitize(exception.Message)}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return CollectionResult.Failure("http-error", CliDiagnostics.Sanitize(exception.Message));
        }
    }

    private async Task<string> FetchXmlAsync(Uri initialUrl, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var current = initialUrl;
        string error;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            linked.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("QqChannelDesk/1.0 (+rss-reader)");
            request.Headers.Accept.ParseAdd("application/rss+xml,application/atom+xml,application/xml,text/xml;q=0.9,*/*;q=0.8");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects || response.Headers.Location is null)
                    throw new HttpRequestException("订阅源跳转无效或次数过多。");
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                if (!PublicUrlGuard.TryNormalize(next.AbsoluteUri, out current, out error))
                    throw new HttpRequestException(error);
                continue;
            }

            if ((int)response.StatusCode == 429) throw new HttpRequestException("订阅源已限流（HTTP 429）。");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"订阅源无法打开（HTTP {(int)response.StatusCode}）。");
            if (response.Content.Headers.ContentLength is { } length && length > MaximumBytes)
                throw new InvalidOperationException("订阅源内容超过 2 MB。");
            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token);
            if (bytes.Length > MaximumBytes) throw new InvalidOperationException("订阅源内容超过 2 MB。");
            return Encoding.UTF8.GetString(bytes);
        }

        throw new HttpRequestException("订阅源跳转次数过多。");
    }

    private static IReadOnlyList<CollectionItemDraft> ParseFeed(CollectionSource source, string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var options = CollectionRuleOptions.Parse(source);
        var rssItems = document.Root is null ? Array.Empty<XElement>() : document.Root.Descendants().Where(node => node.Name.LocalName == "item").ToArray();
        var atomEntries = document.Root is null ? Array.Empty<XElement>() : document.Root.Elements(Atom + "entry").ToArray();
        var rawItems = rssItems.Length > 0 ? ParseRss(source, rssItems, options) : ParseAtom(source, atomEntries, options);
        return rawItems
            .Take(source.MaxItemsPerRun)
            .ToArray();
    }

    private static IReadOnlyList<CollectionItemDraft> ParseRss(
        CollectionSource source,
        XElement[] items,
        CollectionRuleOptions options)
    {
        var result = new List<CollectionItemDraft>();
        foreach (var item in items)
        {
            var title = Text(item.Element("title"));
            var encodedContent = Text(item.Elements().FirstOrDefault(node => node.Name.LocalName == "encoded"));
            var content = encodedContent.Length > 0 ? encodedContent : Text(item.Element("description"));
            var url = Text(item.Element("link"));
            var itemId = Text(item.Element("guid")) is { Length: > 0 } guid ? guid : UrlNormalizer.Canonicalize(url);
            var categories = item.Elements("category").Select(Text).Where(value => value.Length > 0).ToArray();
            var published = ParseDate(Text(item.Element("pubDate")));
            if (!MatchesRules(title, content, categories, published, options)) continue;
            result.Add(new(title, content, url, itemId));
        }

        return result;
    }

    private static IReadOnlyList<CollectionItemDraft> ParseAtom(
        CollectionSource source,
        XElement[] entries,
        CollectionRuleOptions options)
    {
        var result = new List<CollectionItemDraft>();
        foreach (var entry in entries)
        {
            var title = Text(entry.Element(Atom + "title"));
            var atomContent = Text(entry.Element(Atom + "content"));
            var content = atomContent.Length > 0 ? atomContent : Text(entry.Element(Atom + "summary"));
            var url = LinkUrl(entry);
            var itemId = Text(entry.Element(Atom + "id")) is { Length: > 0 } id ? id : UrlNormalizer.Canonicalize(url);
            var categories = entry.Elements(Atom + "category")
                .Select(node => node.Attribute("term")?.Value ?? "")
                .Where(value => value.Length > 0)
                .ToArray();
            var published = ParseDate(Text(entry.Element(Atom + "published"))) ?? ParseDate(Text(entry.Element(Atom + "updated")));
            if (!MatchesRules(title, content, categories, published, options)) continue;
            result.Add(new(title, content, url, itemId));
        }

        return result;
    }

    private static bool MatchesRules(
        string title,
        string content,
        IReadOnlyList<string> categories,
        DateTimeOffset? published,
        CollectionRuleOptions options)
    {
        if (options.Categories.Count > 0 && !categories.Any(category => options.Categories.Contains(category, StringComparer.OrdinalIgnoreCase))) return false;
        if (options.SinceHours is { } sinceHours &&
            published is { } time &&
            time < DateTimeOffset.Now.AddHours(-sinceHours)) return false;
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content)) return false;
        // 分类过滤已经在上面通过 RSS/Atom 的 category 标签判断；这里仅应用关键词过滤，
        // 避免 CollectionFilter.Matches 再用标题包含分类名重复判断导致误过滤。
        return CollectionFilter.Matches(new CollectionItemDraft(title, content, "", ""),
            options with { Categories = [] });
    }

    private static string LinkUrl(XElement entry)
    {
        var link = entry.Elements(Atom + "link").FirstOrDefault(node =>
            (node.Attribute("rel")?.Value ?? "alternate").Equals("alternate", StringComparison.OrdinalIgnoreCase)) ??
            entry.Elements(Atom + "link").FirstOrDefault();
        return link?.Attribute("href")?.Value ?? "";
    }

    private static string Text(XElement? element) => element?.Value.Trim() ?? "";

    private static DateTimeOffset? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var result)
            ? result
            : null;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
