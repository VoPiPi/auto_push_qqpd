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
            true, "https://example.test/article?b=2&a=1#top", "example.test", "标题", "正文", "",
            MediaList: ["https://cdn.example.test/cover.png"])));
        var source = new CollectionSource(1, "Source", "single-page", "https://example.test/article", true, true, null, "rule-first", 60, "", "{}", false, 10, "", "", null, null, null, "healthy", "", "", "", null, DateTimeOffset.Now, DateTimeOffset.Now);

        var result = await collector.CollectAsync(source);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Items);
        Assert.Equal("标题", item.Title);
        Assert.Equal("正文", item.Content);
        Assert.Equal("https://example.test/article?a=1&b=2", item.Url);
        Assert.Equal(item.Url, item.SourceItemId);
        Assert.Equal("https://cdn.example.test/cover.png", Assert.Single(item.Media));
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

public sealed class PublicArticleMediaParsingTests
{
    [Fact]
    public void ExtractsLazyRelativeAndDeduplicatedBodyMedia()
    {
        var html = """
            <html>
              <head><base href="https://cdn.example.test/media/"></head>
              <body>
                <nav><img src="https://cdn.example.test/navigation.png"></nav>
                <article>
                  <p>正文第一段内容。</p>
                  <p>正文第二段内容。</p>
                  <img src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP" data-src="cover.png?x=1&amp;y=2">
                  <img data-original="//cdn.example.test/media/cover.png?x=1&amp;y=2#duplicate">
                  <img srcset="small.jpg 1x, large.jpg 2x">
                  <video><source src="clip.mp4" type="video/mp4"></video>
                </article>
              </body>
            </html>
            """;

        var parsed = PublicArticleFetcher.ParseHtmlWithRule(
            html,
            baseUrl: "https://blog.example.test/post/733");

        Assert.Equal(
            [
                "https://cdn.example.test/media/cover.png?x=1&y=2",
                "https://cdn.example.test/media/small.jpg",
                "https://cdn.example.test/media/clip.mp4"
            ],
            parsed.Media);
    }

    [Fact]
    public void ExtractsPublishedArticleImageFromBody()
    {
        var html = """
            <html>
              <body>
                <article>
                  <h1>文章标题</h1>
                  <div class="msg__text post-page__text">
                    <p>这是正文内容，用于验证图片会随正文一起采集。</p>
                    <img src="https://img.cdn.vopipi.cn/local/202611/12ac2b58f9f14d399e6d94d9.png" alt="文章配图" loading="lazy">
                  </div>
                </article>
              </body>
            </html>
            """;

        var parsed = PublicArticleFetcher.ParseHtmlWithRule(
            html,
            baseUrl: "https://blog.vopipi.cn/post/733");

        Assert.Equal(
            "https://img.cdn.vopipi.cn/local/202611/12ac2b58f9f14d399e6d94d9.png",
            Assert.Single(parsed.Media));
    }
}

public sealed class RssCollectorTests
{
    [Fact]
    public async Task FeedRequestUsesTargetAwareHttpClient()
    {
        var requestedUrls = new List<Uri>();
        var collector = new RssCollector(target =>
        {
            requestedUrls.Add(target);
            return new HttpClient(FakeHttp.Ok(
                "<rss version=\"2.0\"><channel><item><title>条目</title><description>正文</description></item></channel></rss>",
                "application/rss+xml"));
        });

        var result = await collector.CollectAsync(Source("https://www.example.test/feed.xml"));

        Assert.True(result.Succeeded);
        Assert.Equal("https://www.example.test/feed.xml", Assert.Single(requestedUrls).AbsoluteUri);
    }

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
    public async Task DecodesGb2312FeedDeclaredInXml()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string xml = """
            <?xml version="1.0" encoding="GBK"?>
            <rss version="2.0"><channel>
              <item><guid>gbk-1</guid><title>岳阳新闻标题</title><link>https://example.test/gbk</link><description>中文正文</description></item>
            </channel></rss>
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.GetEncoding("GB18030").GetBytes(xml))
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml");
        var collector = new RssCollector(new FixedHttpHandler(_ => response));

        var result = await collector.CollectAsync(Source("https://example.test/gbk"));

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Items);
        Assert.Equal("岳阳新闻标题", item.Title);
        Assert.Equal("中文正文", item.Content);
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
                ["https://example.test/a"] = new(
                    true, "https://example.test/a", "example.test", "A", "A body", "",
                    MediaList: ["https://cdn.example.test/a.png"]),
                ["https://example.test/b"] = new(false, "", "", "", "", "详情失败")
            });
        var collector = new WebColumnCollector(fetcher);
        var result = await collector.CollectAsync(new CollectionSource(
            1, "Column", "web-column", "https://example.test/column", true, true, null,
            "rule-first", 60, "", "{}", false, 10, "", "", null, null, null, "healthy", "", "", "",
            null, DateTimeOffset.Now, DateTimeOffset.Now));

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Items);
        Assert.Equal("https://cdn.example.test/a.png", Assert.Single(item.Media));
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
