using System.Net;
using System.Net.Http;
using System.Text;
using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class SinglePageCollectorTests
{
    [Fact]
    public async Task ReturnsRuleParsedItemOnSuccess()
    {
        var collector = new SinglePageCollector((_, _) => Task.FromResult(new ArticleFetchResult(
            true, "https://example.test/article?b=2&a=1#top", "example.test", "标题", "正文", "")));
        var source = new CollectionSource(1, "Source", "single-page", "https://example.test/article", true, true, null, "rule-first", 60, "", "{}", false, 10, "", "", null, null, null, "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);

        var result = await collector.CollectAsync(source);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Items);
        Assert.Equal("标题", item.Title);
        Assert.Equal("正文", item.Content);
        Assert.Equal("https://example.test/article?a=1&b=2", item.Url);
        Assert.Equal(item.Url, item.SourceItemId);
    }

    [Fact]
    public async Task ReturnsClassifiedFailureOnError()
    {
        var collector = new SinglePageCollector((_, _) => Task.FromResult(new ArticleFetchResult(
            false, "", "", "", "", "网页无法打开（HTTP 429），请稍后重试。")));
        var source = new CollectionSource(1, "Source", "single-page", "https://example.test/article", true, true, null, "rule-first", 60, "", "{}", false, 10, "", "", null, null, null, "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);

        var result = await collector.CollectAsync(source);

        Assert.False(result.Succeeded);
        Assert.Equal("rate-limit", result.ErrorCode);
    }

    [Fact]
    public async Task UsesCustomContentSelectorFromRuleConfig()
    {
        string? capturedSelector = null;
        var collector = new SinglePageCollector(
            (_, _) => Task.FromResult(new ArticleFetchResult(
                true, "https://example.test/article", "example.test", "标题", "正文", "")),
            fetchPageWithSelector: (_, selector, _) =>
            {
                capturedSelector = selector;
                return Task.FromResult(new ArticleFetchResult(
                    true, "https://example.test/article", "example.test", "自定义标题", "自定义正文", ""));
            });
        var source = new CollectionSource(
            1, "Source", "single-page", "https://example.test/article", true, true, null,
            "rule-first", 60, "", """{"contentSelector":"#article-body"}""", false, 10, "", "",
            null, null, null, "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);

        var result = await collector.CollectAsync(source);

        Assert.True(result.Succeeded);
        Assert.Equal("#article-body", capturedSelector);
        var item = Assert.Single(result.Items);
        Assert.Equal("自定义标题", item.Title);
    }

    [Fact]
    public void ReadContentSelector_ParsesValidJson()
    {
        Assert.Equal("#article", SinglePageCollector.ReadContentSelector("""{"contentSelector":"#article"}"""));
        Assert.Equal("//div[@class='content']", SinglePageCollector.ReadContentSelector("""{"contentSelector":"//div[@class='content']"}"""));
        Assert.Null(SinglePageCollector.ReadContentSelector("{}"));
        Assert.Null(SinglePageCollector.ReadContentSelector("not-json"));
        Assert.Null(SinglePageCollector.ReadContentSelector(null));
    }
}

public sealed class RssCollectorTests
{
    [Fact]
    public async Task ParsesRssItemsAndCanonicalizesIds()
    {
        var xml = """
            <rss version="2.0"><channel>
              <item><guid>guid-a</guid><title>First</title><link>https://example.test/a</link><description>A body</description><pubDate>Thu, 23 Oct 2026 10:00:00 GMT</pubDate></item>
              <item><title>Second</title><link>https://example.test/b</link><description>B body</description></item>
            </channel></rss>
            """;
        var collector = new RssCollector(FakeHttp.Ok(xml, "application/rss+xml"));
        var result = await collector.CollectAsync(Source("https://example.test/rss"));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("guid-a", result.Items[0].SourceItemId);
        Assert.Equal("https://example.test/a", result.Items[0].Url);
    }

    [Fact]
    public async Task ParsesAtomEntries()
    {
        var xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><id>atom-1</id><title>Atom title</title><link href="https://example.test/atom/1"/><summary>Atom body</summary><updated>2026-10-23T10:00:00Z</updated></entry>
            </feed>
            """;
        var collector = new RssCollector(FakeHttp.Ok(xml, "application/atom+xml"));
        var result = await collector.CollectAsync(Source("https://example.test/atom"));

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Items);
        Assert.Equal("atom-1", item.SourceItemId);
        Assert.Equal("https://example.test/atom/1", item.Url);
    }

    [Fact]
    public async Task AppliesMaxItemsAndKeywordFilters()
    {
        var xml = """
            <rss version="2.0"><channel>
              <item><guid>1</guid><title>包含 发布</title><link>https://example.test/1</link><description>正文</description></item>
              <item><guid>2</guid><title>忽略</title><link>https://example.test/2</link><description>正文</description></item>
              <item><guid>3</guid><title>包含 其他</title><link>https://example.test/3</link><description>正文</description></item>
            </channel></rss>
            """;
        var source = new CollectionSource(1, "Source", "rss", "https://example.test/rss", true, true, null, "rule-first", 60, "", "{}", false, 2, "包含", "忽略", null, null, null, "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);
        var collector = new RssCollector(FakeHttp.Ok(xml, "text/xml"));
        var result = await collector.CollectAsync(source);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task AppliesCategoryFilterFromRssCategoryTags()
    {
        // 分类过滤应匹配 RSS <category> 标签，而不是标题包含分类名。
        var xml = """
            <rss version="2.0"><channel>
              <item><guid>1</guid><title>科技新闻</title><category>科技</category><link>https://example.test/1</link><description>科技正文内容足够长。</description></item>
              <item><guid>2</guid><title>体育新闻</title><category>体育</category><link>https://example.test/2</link><description>体育正文内容足够长。</description></item>
            </channel></rss>
            """;
        var source = new CollectionSource(
            1, "RSS", "rss", "https://example.test/rss", true, true, null, "rule-first", 60,
            "", """{"categoryFilter":["科技"]}""", false, 10, "", "", null, null, null,
            "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);
        var collector = new RssCollector(FakeHttp.Ok(xml, "text/xml"));
        var result = await collector.CollectAsync(source);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Items);
        Assert.Equal("科技新闻", item.Title);
    }

    [Fact]
    public async Task ReturnsParseErrorForInvalidXml()
    {
        var collector = new RssCollector(FakeHttp.Ok("<rss", "text/xml"));
        var result = await collector.CollectAsync(Source("https://example.test/rss"));
        Assert.False(result.Succeeded);
        Assert.Equal("parse-error", result.ErrorCode);
    }

    private static CollectionSource Source(string url) => new(
        1, "RSS", "rss", url, true, true, null, "rule-first", 60, "", "{}", false, 10, "", "",
        null, null, null, "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);
}

public sealed class WebColumnCollectorTests
{
    [Fact]
    public async Task FetchesColumnLinksAndCountsDetailFailuresAsPartial()
    {
        var html = """
            <html><body>
              <a href="/a">Article A</a>
              <a href="https://example.test/b">Article B</a>
            </body></html>
            """;
        var fetcher = new FakeColumnFetcher(
            new WebPageResult(true, html, "https://example.test/column", ""),
            new Dictionary<string, ArticleFetchResult>
            {
                ["https://example.test/a"] = new(true, "https://example.test/a", "example.test", "A", "A body", ""),
                ["https://example.test/b"] = new(false, "", "", "", "", "详情失败")
            });
        var collector = new WebColumnCollector(fetcher);
        var result = await collector.CollectAsync(new CollectionSource(
            1, "Column", "web-column", "https://example.test/column", true, true, null,
            "rule-first", 60, "", "{}", false, 10, "", "", null, null, null, "healthy", "", "", "",
            null, DateTimeOffset.Now, DateTimeOffset.Now));

        Assert.True(result.Succeeded);
        Assert.Single(result.Items);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(2, fetcher.ArticleRequests.Count);
    }

    [Fact]
    public async Task AppliesMaxItemsPerRun()
    {
        var html = """
            <html><body>
              <a href="/a">A</a><a href="/b">B</a><a href="/c">C</a>
            </body></html>
            """;
        var fetcher = new FakeColumnFetcher(
            new WebPageResult(true, html, "https://example.test/column", ""),
            new Dictionary<string, ArticleFetchResult>());
        var collector = new WebColumnCollector(fetcher);
        var result = await collector.CollectAsync(new CollectionSource(
            1, "Column", "web-column", "https://example.test/column", true, true, null,
            "rule-first", 60, "", "{}", false, 2, "", "", null, null, null, "healthy", "", "", "",
            null, DateTimeOffset.Now, DateTimeOffset.Now));

        Assert.True(result.Succeeded);
        Assert.Equal(2, fetcher.ArticleRequests.Count);
    }
}

internal static class FakeHttp
{
    public static HttpMessageHandler Ok(string content, string mediaType) =>
        new FixedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        });
}

internal sealed class FixedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(responder(request));
    }
}

internal sealed class FakeColumnFetcher(
    WebPageResult columnResult,
    IReadOnlyDictionary<string, ArticleFetchResult> articles) : IWebColumnPageFetcher
{
    public List<string> ArticleRequests { get; } = [];

    public Task<WebPageResult> FetchHtmlAsync(string url, CancellationToken cancellationToken = default) =>
        Task.FromResult(columnResult);

    public Task<ArticleFetchResult> FetchArticleAsync(string url, CancellationToken cancellationToken = default)
    {
        ArticleRequests.Add(url);
        return Task.FromResult(articles.TryGetValue(url, out var result)
            ? result
            : new ArticleFetchResult(true, url, new Uri(url).DnsSafeHost, $"标题 {url}", "正文", ""));
    }
}
