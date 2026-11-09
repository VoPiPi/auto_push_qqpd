using System.Text.Json;
using System.Net.Http;
using System.IO;
using HtmlAgilityPack;
using QqChannelDesk.Services;

namespace QqChannelDesk.Services.Collection;

public sealed record WebPageResult(bool Succeeded, string Html, string FinalUrl, string Error)
{
    public static WebPageResult Failure(string error) => new(false, "", "", error);
}

public interface IWebColumnPageFetcher
{
    Task<WebPageResult> FetchHtmlAsync(string url, CancellationToken cancellationToken = default);

    Task<ArticleFetchResult> FetchArticleAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class WebColumnCollector(IWebColumnPageFetcher fetcher) : ISourceCollector
{
    public string SourceType => "web-column";

    public async Task<CollectionResult> CollectAsync(CollectionSource source, CancellationToken cancellationToken = default)
    {
        if (!PublicUrlGuard.TryNormalize(source.EntryUrl, out var entryUrl, out var error))
            return CollectionResult.Failure("data-error", error);
        try
        {
            var page = await fetcher.FetchHtmlAsync(entryUrl.AbsoluteUri, cancellationToken);
            if (!page.Succeeded) return CollectionResult.Failure("http-error", page.Error);
            var urls = ParseItemUrls(page.Html, page.FinalUrl, source.RuleConfigJson);
            if (urls.Count == 0) return CollectionResult.Success([]);
            var items = new List<CollectionItemDraft>();
            var failedCount = 0;
            foreach (var url in urls.Take(source.MaxItemsPerRun))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var article = await fetcher.FetchArticleAsync(url, cancellationToken);
                if (article.Succeeded && (article.Title.Length > 0 || article.Content.Length > 0))
                {
                    items.Add(new CollectionItemDraft(
                        article.Title,
                        article.Content,
                        article.Url.Length > 0 ? article.Url : url,
                        UrlNormalizer.Canonicalize(article.Url.Length > 0 ? article.Url : url),
                        MediaList: article.Media));
                }
                else
                {
                    failedCount++;
                }
            }

            return items.Count > 0
                ? CollectionResult.Success(items, failedCount)
                : CollectionResult.Failure("parse-error", "栏目页可访问，但未解析到可用详情内容。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or JsonException)
        {
            return CollectionResult.Failure("http-error", CliDiagnostics.Sanitize(exception.Message));
        }
    }

    private static IReadOnlyList<string> ParseItemUrls(string html, string baseUrl, string ruleConfigJson)
    {
        var options = ParseOptions(ruleConfigJson);
        var document = new HtmlDocument { OptionFixNestedTags = true };
        document.LoadHtml(html);
            var nodes = document.DocumentNode.SelectNodes(options.ItemXPath);
            if (nodes is null) return Array.Empty<string>();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var linkNode = string.IsNullOrWhiteSpace(options.LinkXPath)
                ? node
                : node.SelectSingleNode(options.LinkXPath);
            var href = linkNode?.GetAttributeValue("href", "") ?? "";
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
                !Uri.TryCreate(baseUri, href, out var resolved) ||
                !PublicUrlGuard.TryNormalize(resolved.AbsoluteUri, out var normalized, out _))
            {
                continue;
            }

            if (seen.Add(normalized.AbsoluteUri)) result.Add(normalized.AbsoluteUri);
        }

        return result;
    }

    private static WebColumnOptions ParseOptions(string ruleConfigJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(ruleConfigJson) ? "{}" : ruleConfigJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object) return new();
            return new(
                document.RootElement.TryGetProperty("itemXPath", out var itemElement) && itemElement.ValueKind is JsonValueKind.String
                    ? itemElement.GetString() ?? "//a[@href]"
                    : "//a[@href]",
                document.RootElement.TryGetProperty("linkXPath", out var linkElement) && linkElement.ValueKind is JsonValueKind.String
                    ? linkElement.GetString()
                    : null);
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private sealed record WebColumnOptions(string ItemXPath = "//a[@href]", string? LinkXPath = null);
}

public sealed class PublicWebColumnPageFetcher : IWebColumnPageFetcher
{
    private readonly PublicArticleFetcher _fetcher = new();

    public async Task<WebPageResult> FetchHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!PublicUrlGuard.TryNormalize(url, out var current, out var error)) return WebPageResult.Failure(error);
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _fetcher.FetchRawHtmlAsync(current, PublicArticleRequestContext.SystemProxyOrDefault, cancellationToken);
                if (result.RedirectUri is not null)
                {
                    if (redirect == 5 || !PublicUrlGuard.TryNormalize(result.RedirectUri.AbsoluteUri, out current, out error))
                        return WebPageResult.Failure(redirect == 5 ? "网页跳转次数过多。" : error);
                    continue;
                }

                return result.Error.Length > 0
                    ? WebPageResult.Failure(result.Error)
                    : new(true, result.Html, result.FinalUrl.Length > 0 ? result.FinalUrl : current.AbsoluteUri, "");
            }

            return WebPageResult.Failure("网页跳转次数过多。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            return WebPageResult.Failure(CliDiagnostics.Sanitize(exception.Message));
        }
    }

    public Task<ArticleFetchResult> FetchArticleAsync(string url, CancellationToken cancellationToken = default) =>
        _fetcher.FetchAsync(url, PublicArticleRequestContext.SystemProxyOrDefault, cancellationToken);
}
