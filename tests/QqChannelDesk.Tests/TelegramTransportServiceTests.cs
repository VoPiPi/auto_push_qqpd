using System.Net;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramTransportServiceTests
{
    [Fact]
    public async Task DirectSelectionUsesNoSystemProxy()
    {
        var service = CreateService();
        PublicArticleRequestContext? observed = null;
        service.RawFetchOverride = (uri, context, _) =>
        {
            observed = context;
            return Task.FromResult(Success(uri, "直连消息"));
        };

        var result = await service.FetchChannelAsync(
            "https://t.me/s/channel",
            new TelegramTransportSelection(TelegramTransportSelection.Direct));

        Assert.True(result.Succeeded);
        Assert.NotNull(observed);
        Assert.False(observed!.UseSystemProxy);
        Assert.Null(observed.ProxyUri);
        Assert.Equal(TelegramTransportModes.Direct, result.Context?.Mode);
        Assert.False(result.Context?.AllowFallback);
    }

    [Fact]
    public async Task SystemSelectionUsesSystemProxy()
    {
        var service = CreateService();
        PublicArticleRequestContext? observed = null;
        service.RawFetchOverride = (uri, context, _) =>
        {
            observed = context;
            return Task.FromResult(Success(uri, "系统代理消息"));
        };

        var result = await service.FetchChannelAsync(
            "https://t.me/s/channel",
            new TelegramTransportSelection(TelegramTransportSelection.System));

        Assert.True(result.Succeeded);
        Assert.NotNull(observed);
        Assert.True(observed!.UseSystemProxy);
        Assert.Null(observed.ProxyUri);
        Assert.Equal(TelegramTransportModes.System, result.Context?.Mode);
    }

    [Fact]
    public void SelectorNoLongerExposesCustomProxy()
    {
        Assert.Equal(
            new[]
            {
                TelegramTransportSelection.Auto,
                TelegramTransportSelection.System,
                TelegramTransportSelection.ProxyPool,
                TelegramTransportSelection.Online
            },
            TelegramTransportSelector.Options.Select(option => option.Value));
        Assert.DoesNotContain(
            TelegramTransportSelector.Options,
            option => option.Value.Contains("custom", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("custom:https://proxy.example:8443/")]
    [InlineData("https://proxy.example:8443/")]
    public void LegacyCustomProxySelectionFallsBackToAutomatic(string value)
    {
        Assert.Equal(TelegramTransportSelection.Auto, TelegramTransportSelector.Parse(value));
        Assert.Equal(TelegramTransportSelector.Options[0], TelegramTransportSelector.FindOption(value));
        Assert.Equal(TelegramTransportSelector.Options[0].DisplayName, TelegramTransportSelector.DisplayValue(value));
    }

    [Fact]
    public async Task RedirectsAreFetchedAndValidatedOneHopAtATime()
    {
        var service = CreateService();
        var visited = new List<string>();
        service.RawFetchOverride = (uri, _, _) =>
        {
            visited.Add(uri.AbsoluteUri);
            return Task.FromResult(visited.Count == 1
                ? new RawPublicFetchResult("", "", new Uri("https://t.me/s/channel?before=1"), "")
                : Success(uri, "重定向后的消息"));
        };

        var result = await service.FetchChannelAsync(
            "https://t.me/s/channel",
            new TelegramTransportSelection(TelegramTransportSelection.Direct));

        Assert.True(result.Succeeded);
        Assert.Equal(
            new[] { "https://t.me/s/channel", "https://t.me/s/channel?before=1" },
            visited);
    }

    [Fact]
    public async Task RedirectToPrivateAddressIsRejected()
    {
        var service = CreateService();
        var calls = 0;
        service.RawFetchOverride = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new RawPublicFetchResult(
                "",
                "",
                new Uri("http://127.0.0.1/private"),
                ""));
        };

        var result = await service.FetchChannelAsync(
            "https://t.me/s/channel",
            new TelegramTransportSelection(TelegramTransportSelection.Direct));

        Assert.False(result.Succeeded);
        Assert.Equal(1, calls);
        Assert.Contains("本机、私有或链路本地地址", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TelegramTransportSelection.Direct)]
    [InlineData(TelegramTransportSelection.System)]
    public async Task ExplicitTransportFailureDoesNotFallback(string selectionValue)
    {
        var service = CreateService();
        var calls = 0;
        service.RawFetchOverride = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(RawPublicFetchResult.Failure("模拟传输失败"));
        };

        var result = await service.FetchChannelAsync(
            "https://t.me/s/channel",
            new TelegramTransportSelection(selectionValue));

        Assert.False(result.Succeeded);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AutomaticSelectionFallsBackFromDirectToSystemProxy()
    {
        var service = CreateService();
        var contexts = new List<PublicArticleRequestContext>();
        service.RawFetchOverride = (uri, context, _) =>
        {
            contexts.Add(context);
            return Task.FromResult(context.UseSystemProxy
                ? Success(uri, "系统代理成功")
                : RawPublicFetchResult.Failure("直连失败"));
        };

        var result = await service.FetchChannelAsync(
            "https://t.me/s/channel",
            TelegramTransportSelection.Automatic);

        Assert.True(result.Succeeded);
        Assert.Equal(2, contexts.Count);
        Assert.False(contexts[0].UseSystemProxy);
        Assert.True(contexts[1].UseSystemProxy);
        Assert.True(result.Context?.AllowFallback);
    }

    [Fact]
    public async Task SpecificOnlineProviderFailureDoesNotTryAnotherProvider()
    {
        var attempts = new List<string>();
        var registry = new WebProxyProviderRegistry(
        [
            new StubProxyProvider(
                "first",
                "First",
                _ => throw new InvalidOperationException("first failed"),
                attempts),
            new StubProxyProvider(
                "second",
                "Second",
                _ => new ProxyPageResult(ChannelHtml("second"), "https://second.example", "", "second", "Second"),
                attempts)
        ]);
        var service = CreateService(registry);

        var result = await service.FetchChannelAsync(
            "https://t.me/s/channel",
            new TelegramTransportSelection(TelegramTransportSelection.ForOnlineProvider("first")));

        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "first" }, attempts);
        Assert.Contains("first failed", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationTokenStopsTransportRequest()
    {
        var service = CreateService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RawFetchOverride = async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return RawPublicFetchResult.Failure("unreachable");
        };
        using var cancellation = new CancellationTokenSource();

        var request = service.FetchChannelAsync(
            "https://t.me/s/channel",
            new TelegramTransportSelection(TelegramTransportSelection.Direct),
            cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public void OnlineContextSelectsProxiedMediaUrlAndCookie()
    {
        var context = TelegramTransportContext.Online("proxyunblocker") with { CookieHeader = "session=abc" };
        var media = new TelegramMediaRef(
            "https://proxy.example/file/photo.jpg",
            "https://cdn.example/file/photo.jpg",
            "image");

        var source = context.SelectMediaSource(media);

        Assert.Equal(media.ProxiedUrl, source.Url);
        Assert.Equal(context.CookieHeader, source.CookieHeader);
    }

    [Fact]
    public void NonOnlineContextUsesOriginalMediaUrlWithoutCookie()
    {
        var context = TelegramTransportContext.Direct();
        var media = new TelegramMediaRef(
            "https://proxy.example/file/photo.jpg",
            "https://cdn.example/file/photo.jpg",
            "image");

        var source = context.SelectMediaSource(media);

        Assert.Equal(media.OriginalUrl, source.Url);
        Assert.Empty(source.CookieHeader);
    }

    [Fact]
    public async Task MediaFallbackStopsAtFirstSuccessAndOnlyOnlineCandidateGetsCookie()
    {
        var service = CreateService();
        var observed = new List<(string Mode, string Cookie)>();
        service.MediaDownloadOverride = (media, context, _, _) =>
        {
            observed.Add((context.Mode, context.SelectMediaSource(media).CookieHeader));
            if (context.Mode == TelegramTransportModes.System)
                return Task.FromResult("system-ok.jpg");
            return Task.FromException<string>(new IOException($"{context.Mode} failed"));
        };
        var context = TelegramTransportContext.Online("provider")
            with { CookieHeader = "session=abc", AllowFallback = true };

        var result = await service.DownloadMediaWithFallbackAsync(Media(), context);

        Assert.Equal("system-ok.jpg", result);
        Assert.Equal(
            [
                TelegramTransportModes.Online,
                TelegramTransportModes.Direct,
                TelegramTransportModes.System
            ],
            observed.Select(item => item.Mode));
        Assert.Equal("session=abc", observed[0].Cookie);
        Assert.All(observed.Skip(1), item => Assert.Empty(item.Cookie));
    }

    [Fact]
    public async Task MediaFallbackUsesProxyPoolAfterDirectAndSystemProxyFail()
    {
        var service = CreateService();
        var modes = new List<string>();
        service.MediaDownloadOverride = (_, context, _, _) =>
        {
            modes.Add(context.Mode);
            return context.Mode == TelegramTransportModes.ProxyPool
                ? Task.FromResult("proxy-ok.jpg")
                : Task.FromException<string>(new IOException("failed"));
        };
        service.ProxyPoolNodesOverride = _ =>
            Task.FromResult<IReadOnlyList<ProxyNode>>([ProxyNode()]);
        var context = TelegramTransportContext.Direct() with { AllowFallback = true };

        var result = await service.DownloadMediaWithFallbackAsync(Media(), context);

        Assert.Equal("proxy-ok.jpg", result);
        Assert.Equal(
            [
                TelegramTransportModes.Direct,
                TelegramTransportModes.System,
                TelegramTransportModes.ProxyPool
            ],
            modes);
    }

    [Fact]
    public async Task MediaFallbackPropagatesTheCallerCancellationToken()
    {
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken observed = default;
        service.MediaDownloadOverride = (_, _, _, cancellationToken) =>
        {
            observed = cancellationToken;
            return Task.FromCanceled<string>(cancellationToken);
        };
        var context = TelegramTransportContext.Direct() with { AllowFallback = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadMediaWithFallbackAsync(
                Media(),
                context,
                cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, observed);
    }

    private static TelegramTransportService CreateService(WebProxyProviderRegistry? registry = null)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"qq-channel-tg-transport-{Guid.NewGuid():N}.db");
        return new TelegramTransportService(new ProxyPoolStore(databasePath), registry);
    }

    private static RawPublicFetchResult Success(Uri uri, string text) =>
        new(ChannelHtml(text), uri.AbsoluteUri, null, "");

    private static TelegramMediaRef Media() =>
        new(
            "https://proxy.example/file/photo.jpg",
            "https://cdn.example/file/photo.jpg",
            "image");

    private static ProxyNode ProxyNode()
    {
        var now = DateTimeOffset.Now;
        return new ProxyNode(
            1,
            1,
            "http://192.0.2.10:8080",
            "http",
            "192.0.2.10",
            8080,
            "US",
            true,
            "192.0.2.10",
            "US",
            "United States",
            now,
            "",
            now,
            true,
            1,
            "",
            true,
            now,
            now);
    }

    private static string ChannelHtml(string text) => $"""
        <div class="tgme_widget_message" data-post="channel/1">
          <div class="tgme_widget_message_text">{text}</div>
          <time datetime="2026-10-11T07:30:00+00:00"></time>
        </div>
        """;

    private sealed class StubProxyProvider : IWebProxyProvider
    {
        private readonly Func<string, ProxyPageResult> _fetch;
        private readonly List<string> _attempts;

        public StubProxyProvider(
            string id,
            string displayName,
            Func<string, ProxyPageResult> fetch,
            List<string> attempts)
        {
            Id = id;
            DisplayName = displayName;
            _fetch = fetch;
            _attempts = attempts;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string HomepageUrl => $"https://{Id}.example/";
        public bool SupportsSimulation => true;

        public Task<ProxyPageResult> FetchAsync(
            string targetUrl,
            string? waitSelector = null,
            CancellationToken cancellationToken = default)
        {
            _attempts.Add(Id);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_fetch(targetUrl));
        }
    }
}
