using System.Globalization;
using System.Text;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramCollectorServiceTests
{
    private static TelegramChannelMessage Message(
        string url,
        string title = "标题",
        string text = "正文",
        DateTimeOffset? publishedAt = null) =>
        new(url, "t.me", publishedAt ?? new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.Zero), title, text, [], "");

    [Theory]
    [InlineData("@TGWenAnGuan")]
    [InlineData("TGWenAnGuan")]
    [InlineData("t.me/TGWenAnGuan")]
    [InlineData("https://t.me/TGWenAnGuan")]
    [InlineData("t.me/s/TGWenAnGuan")]
    [InlineData("https://t.me/s/TGWenAnGuan")]
    [InlineData("  https://telegram.me/s/TGWenAnGuan/  ")]
    public void NormalizeChannelUrl_AlwaysPointsAtPreviewPage(string input)
    {
        Assert.Equal("https://t.me/s/TGWenAnGuan", TelegramCollectorService.NormalizeChannelUrl(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@")]
    [InlineData("https://example.com/channel")]
    [InlineData("t.me/频 道")]
    [InlineData("t.me/c/1234567/89")]
    [InlineData("https://t.me/joinchat/AAAA")]
    public void NormalizeChannelUrl_RejectsUnusableInput(string input)
    {
        Assert.Equal("", TelegramCollectorService.NormalizeChannelUrl(input));
    }

    [Fact]
    public void ApplyPreviewFilters_LimitTruncatesFromTheTop()
    {
        var messages = new[]
        {
            Message("https://t.me/c/1"),
            Message("https://t.me/c/2"),
            Message("https://t.me/c/3"),
            Message("https://t.me/c/4")
        };

        var filtered = TelegramCollectorService.ApplyPreviewFilters(messages, 2, "");

        Assert.Equal(new[] { "https://t.me/c/4", "https://t.me/c/3" }, filtered.Select(item => item.Url));
    }

    [Fact]
    public void ApplyPreviewFilters_KeywordMatchesTitleAndBodyCaseInsensitively()
    {
        var messages = new[]
        {
            Message("https://t.me/c/1", title: "This City Is So Empty"),
            Message("https://t.me/c/2", text: "MEMORIES OF A CITY"),
            Message("https://t.me/c/3", title: "其他消息", text: "没有关键词")
        };

        var filtered = TelegramCollectorService.ApplyPreviewFilters(messages, 0, "city");

        Assert.Equal(new[] { "https://t.me/c/2", "https://t.me/c/1" }, filtered.Select(item => item.Url));
    }

    [Fact]
    public void ApplyPreviewFilters_EmptyArgumentsKeepEverything()
    {
        var messages = new[] { Message("https://t.me/c/1"), Message("https://t.me/c/2") };

        Assert.Equal(2, TelegramCollectorService.ApplyPreviewFilters(messages, 0, "").Count);
        Assert.Equal(2, TelegramCollectorService.ApplyPreviewFilters(messages, -5, "  ").Count);
    }

    [Fact]
    public void ApplyPreviewFilters_SupportsMultipleKeywordsWithOrMatching()
    {
        var messages = new[]
        {
            Message("https://t.me/c/1", title: "AI 进展"),
            Message("https://t.me/c/2", text: "今日新闻摘要"),
            Message("https://t.me/c/3", title: "其他消息", text: "没有命中")
        };

        var filtered = TelegramCollectorService.ApplyPreviewFilters(messages, 0, " AI | 新闻 || ");

        Assert.Equal(new[] { "https://t.me/c/2", "https://t.me/c/1" }, filtered.Select(item => item.Url));
        Assert.Equal(new[] { "AI", "新闻" }, TelegramCollectorService.SplitKeywords(" AI | 新闻 || "));
    }

    [Fact]
    public void ApplyPreviewFilters_ExcludeModeSkipsMatchesAndCollectsOtherMessages()
    {
        var messages = new[]
        {
            Message("https://t.me/c/1", title: "公告：今天暂停更新"),
            Message("https://t.me/c/2", text: "新闻摘要"),
            Message("https://t.me/c/3", title: "其他消息", text: "没有过滤词")
        };

        var filtered = TelegramCollectorService.ApplyPreviewFilters(messages, 0, "公告 | 新闻", excludeMatches: true);

        Assert.Equal(new[] { "https://t.me/c/3" }, filtered.Select(item => item.Url));
    }

    [Fact]
    public void ApplyPreviewFilters_IncludeAndExcludeApplyInOrderAndExclusionWins()
    {
        var messages = new[]
        {
            Message("https://t.me/c/1", title: "新闻：普通更新"),
            Message("https://t.me/c/2", title: "新闻：广告合作"),
            Message("https://t.me/c/3", title: "其他消息", text: "广告")
        };

        var filtered = TelegramCollectorService.ApplyPreviewFilters(
            messages,
            0,
            includeKeywords: "新闻",
            exclusionKeywords: "广告");

        Assert.Equal(new[] { "https://t.me/c/1" }, filtered.Select(item => item.Url));
    }

    [Fact]
    public void ApplyPreviewFilters_CapsLimitAtTemporaryListLimit()
    {
        var messages = Enumerable.Range(1, 25).Select(index => Message($"https://t.me/c/{index}")).ToArray();

        var filtered = TelegramCollectorService.ApplyPreviewFilters(
            messages,
            TelegramCollectorService.TempListLimit + 5,
            "");

        Assert.Equal(20, filtered.Count);
        Assert.Equal("https://t.me/c/25", filtered[0].Url);
        Assert.Equal("https://t.me/c/6", filtered[^1].Url);
    }

    [Fact]
    public void ApplyPreviewFilters_DoesNotBackfillInclusionMatchOutsideNewestLimit()
    {
        var messages = Enumerable.Range(1, 21)
            .Select(index => Message(
                $"https://t.me/c/{index}",
                title: index == 1 ? "命中关键词" : $"消息 {index}"))
            .ToArray();

        var filtered = TelegramCollectorService.ApplyPreviewFilters(
            messages,
            TelegramCollectorService.TempListLimit,
            "命中");

        Assert.Empty(filtered);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 20)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    [InlineData(999, 20)]
    public void NormalizeMessageLimit_UsesAtMostTwentyMessages(int input, int expected)
    {
        Assert.Equal(expected, TelegramCollectorService.NormalizeMessageLimit(input));
    }

    [Fact]
    public void OrderByLatest_UsesPublishedTimeThenMessageNumberAndPutsMissingTimesLast()
    {
        var sameTime = new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.Zero);
        var messages = new[]
        {
            Message("https://t.me/c/2", publishedAt: sameTime),
            Message("https://t.me/channel/10", publishedAt: sameTime.AddMinutes(1)),
            new TelegramChannelMessage("https://t.me/c/9", "t.me", null, "标题", "正文", [], ""),
            Message("https://t.me/c/7", publishedAt: sameTime)
        };

        var ordered = TelegramCollectorService.OrderByLatest(messages, message => message);

        Assert.Equal(
            new[]
            {
                "https://t.me/channel/10",
                "https://t.me/c/7",
                "https://t.me/c/2",
                "https://t.me/c/9"
            },
            ordered.Select(message => message.Url));
    }

    [Fact]
    public void BuildDraftDirectory_UsesDateFolderAndSanitizedSegments()
    {
        var directory = TelegramCollectorService.BuildDraftDirectory(
            "G:\\app",
            new DateTimeOffset(2026, 10, 11, 23, 40, 0, TimeSpan.FromHours(8)),
            "TG/WenAn:Guan");

        var expected = Path.Combine("G:\\app", "temp", "telegram", "20261011", "TG_WenAn_Guan");
        Assert.Equal(expected, directory);
    }

    [Fact]
    public void BuildDraftDirectory_TrimsOverLongSegments()
    {
        var directory = TelegramCollectorService.BuildDraftDirectory(
            "G:\\app",
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            new string('a', 60));

        var folder = Path.GetFileName(directory);
        Assert.True(folder.Length < 60, $"目录名过长：{folder}");
    }

    [Fact]
    public void SelectDraftMedia_KeepsImagesAndVideos()
    {
        var media = new[]
        {
            new TelegramMediaRef("https://example.com/a.jpg", "https://example.com/a.jpg", "image"),
            new TelegramMediaRef("https://example.com/a.mp4", "https://example.com/a.mp4", "video"),
            new TelegramMediaRef("https://example.com/b.MP4", "https://example.com/b.MP4", "VIDEO")
        };

        var selected = TelegramCollectorService.SelectDraftMedia(media);

        Assert.Equal(3, selected.Count);
        Assert.Contains(selected, item => string.Equals(item.Kind, "image", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(selected, item => string.Equals(item.Kind, "video", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildPreviewDirectory_NestsUnderPreviewFolder()
    {
        var directory = TelegramCollectorService.BuildPreviewDirectory("G:\\app", "718");

        Assert.Equal(Path.Combine("G:\\app", "temp", "telegram", "preview", "718"), directory);
    }

    [Theory]
    [InlineData("https://t.me/TGWenAnGuan/718", "718")]
    [InlineData("https://t.me/TGWenAnGuan/718/", "718")]
    [InlineData("https://104.223.85.164/TGWenAnGuan/718?token=x", "718")]
    public void ReadMessageId_TakesTheLastPathSegment(string url, string expected)
    {
        Assert.Equal(expected, TelegramCollectorService.ReadMessageId(url));
    }

    [Fact]
    public void ReadMessageId_FallsBackToHashWhenUrlHasNoSegment()
    {
        var id = TelegramCollectorService.ReadMessageId("https://t.me/");

        Assert.Equal(12, id.Length);
        Assert.Equal(id, TelegramCollectorService.ReadMessageId("https://t.me/"));
    }

    [Theory]
    [InlineData("https://t.me/TGWenAnGuan/718", "TGWenAnGuan")]
    [InlineData("https://t.me/s/TGWenAnGuan", "TGWenAnGuan")]
    public void ReadMessageChannel_SkipsThePreviewPrefix(string url, string expected)
    {
        Assert.Equal(expected, TelegramCollectorService.ReadMessageChannel(url));
    }

    [Fact]
    public void CustomProxyAddressesAreRejectedAfterFeatureRemoval()
    {
        var registry = new WebProxyProviderRegistry(Array.Empty<IWebProxyProvider>());

        var error = Assert.Throws<InvalidOperationException>(
            () => registry.Resolve(new WebProxySelection("https://custom.example/proxy")));

        Assert.Contains("自定义网页代理已下线", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultProxyRegistryExposesAutomaticAndThreeProvidersInOrder()
    {
        var registry = new WebProxyProviderRegistry(new IWebProxyProvider[]
        {
            new FakeProxyProvider("proxyunblocker", "Proxy Unblocker"),
            new FakeProxyProvider("coproxy", "CoProxy"),
            new FakeProxyProvider("lumiproxy", "LumiProxy")
        });

        Assert.Equal(new[] { "auto", "proxyunblocker", "coproxy", "lumiproxy" }, registry.Options.Select(option => option.Value));
        Assert.Equal(new[] { "Proxy Unblocker", "CoProxy", "LumiProxy" }, registry.Providers.Select(provider => provider.DisplayName));
    }

    [Fact]
    public async Task PreviewChannel_AutomaticModeTriesProvidersInOrderAfterFailure()
    {
        var attempts = new List<string>();
        var registry = new WebProxyProviderRegistry(new IWebProxyProvider[]
        {
            new FakeProxyProvider("first", "First", _ => throw new InvalidOperationException("first down"), attempts),
            new FakeProxyProvider("second", "Second", _ => new ProxyPageResult(ChannelHtml("first"), "https://second.example", "", "second", "Second"), attempts),
            new FakeProxyProvider("third", "Third", _ => throw new InvalidOperationException("should not run"), attempts)
        });
        var service = new TelegramCollectorService(registry);

        var result = await service.PreviewChannelAsync(
            "https://t.me/s/channel", proxySelection: new WebProxySelection(WebProxySelection.Automatic));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "first", "second" }, attempts);
        Assert.Equal("Second", result.ProviderName);
        Assert.Contains("First：first down", result.Attempts!);
    }

    [Fact]
    public async Task PreviewChannel_AutomaticModeSummarizesAllFailures()
    {
        var attempts = new List<string>();
        var registry = new WebProxyProviderRegistry(new IWebProxyProvider[]
        {
            new FakeProxyProvider("first", "First", _ => throw new InvalidOperationException("first down"), attempts),
            new FakeProxyProvider("second", "Second", _ => new ProxyPageResult("<html></html>", "https://second.example", "", "second", "Second"), attempts)
        });
        var service = new TelegramCollectorService(registry);

        var result = await service.PreviewChannelAsync("https://t.me/s/channel");

        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "first", "second" }, attempts);
        Assert.Contains("First：first down", result.Error, StringComparison.Ordinal);
        Assert.Contains("Second：页面没有解析到频道消息", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewChannel_SpecifiedProviderDoesNotFallback()
    {
        var attempts = new List<string>();
        var registry = new WebProxyProviderRegistry(new IWebProxyProvider[]
        {
            new FakeProxyProvider("first", "First", _ => throw new InvalidOperationException("first down"), attempts),
            new FakeProxyProvider("second", "Second", _ => new ProxyPageResult(ChannelHtml("second"), "https://second.example", "", "second", "Second"), attempts)
        });
        var service = new TelegramCollectorService(registry);

        var result = await service.PreviewChannelAsync(
            "https://t.me/s/channel", proxySelection: new WebProxySelection("first"));

        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "first" }, attempts);
        Assert.Contains("First 请求失败", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewChannel_CancelledAutomaticModeDoesNotTryNextProvider()
    {
        var attempts = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var registry = new WebProxyProviderRegistry(new IWebProxyProvider[]
        {
            new FakeProxyProvider("first", "First", _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }, attempts),
            new FakeProxyProvider("second", "Second", _ => new ProxyPageResult(ChannelHtml("second"), "https://second.example", "", "second", "Second"), attempts)
        });
        var service = new TelegramCollectorService(registry);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PreviewChannelAsync(
            "https://t.me/s/channel", cancellationToken: cancellation.Token));
        Assert.Equal(new[] { "first" }, attempts);
    }

    [Fact]
    public void LumiProxyBuildProxyUrlUsesEncodedAreaAndTargetPayload()
    {
        var url = LumiProxyWebProxyProvider.BuildProxyUrl("https://t.me/s/channel");
        Assert.StartsWith("https://webproxy.lumiproxy.com/request?p=", url, StringComparison.Ordinal);
        var encoded = Uri.UnescapeDataString(new Uri(url).Query[3..]);
        var bytes = Convert.FromBase64String(encoded);
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 123;
        Assert.Equal("area=&u=https://t.me/s/channel", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void CoProxyUsesExpectedSubmissionEndpointAndFormField()
    {
        Assert.Equal("https://mirror-1.coproxy.io/web-proxy", CoProxyWebProxyProvider.SubmissionUri.AbsoluteUri);
        Assert.Equal("coproxy", new CoProxyWebProxyProvider().Id);
    }

    private static string ChannelHtml(string text) => $"""
        <div class="tgme_widget_message" data-post="channel/1">
          <div class="tgme_widget_message_text">{text}</div>
          <time datetime="2026-10-11T07:30:00+00:00"></time>
        </div>
        """;

    private sealed class FakeProxyProvider : IWebProxyProvider
    {
        private readonly Func<string, ProxyPageResult> _fetch;
        private readonly List<string>? _attempts;

        public FakeProxyProvider(string id, string displayName, Func<string, ProxyPageResult>? fetch = null, List<string>? attempts = null)
        {
            Id = id;
            DisplayName = displayName;
            _fetch = fetch ?? (_ => new ProxyPageResult(ChannelHtml(displayName), "https://fake.example", "", id, displayName));
            _attempts = attempts;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string HomepageUrl => $"https://{Id}.example";
        public bool SupportsSimulation => true;

        public Task<ProxyPageResult> FetchAsync(string targetUrl, string? waitSelector = null, CancellationToken cancellationToken = default)
        {
            _attempts?.Add(Id);
            return Task.FromResult(_fetch(targetUrl));
        }
    }
}
