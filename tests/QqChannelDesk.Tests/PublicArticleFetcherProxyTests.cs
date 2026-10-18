using System.Net;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class PublicArticleFetcherProxyTests
{
    [Fact]
    public void GetConfiguredProxyUri_ReturnsProxyForNonBypassedTarget()
    {
        var target = new Uri("https://cdn.example.com/media.jpg");
        var proxyUri = new Uri("http://127.0.0.1:7897");
        var proxy = new StubProxy(proxyUri, isBypassed: false);

        Assert.Equal(proxyUri, PublicArticleFetcher.GetConfiguredProxyUri(target, proxy));
    }

    [Fact]
    public void GetConfiguredProxyUri_ReturnsNullWhenBypassed()
    {
        var target = new Uri("https://cdn.example.com/media.jpg");
        var proxy = new StubProxy(new Uri("http://127.0.0.1:7897"), isBypassed: true);

        Assert.Null(PublicArticleFetcher.GetConfiguredProxyUri(target, proxy));
    }

    [Fact]
    public void GetConfiguredProxyUri_ReturnsNullWhenProxyReportsDirectConnection()
    {
        var target = new Uri("https://cdn.example.com/media.jpg");
        var proxy = new StubProxy(target, isBypassed: false);

        Assert.Null(PublicArticleFetcher.GetConfiguredProxyUri(target, proxy));
    }

    [Fact]
    public void IsConfiguredProxyEndpoint_AllowsOnlyTheConfiguredLocalProxy()
    {
        var proxyUri = new Uri("http://127.0.0.1:7897");

        Assert.True(PublicArticleFetcher.IsConfiguredProxyEndpoint(new DnsEndPoint("127.0.0.1", 7897), proxyUri));
        Assert.False(PublicArticleFetcher.IsConfiguredProxyEndpoint(new DnsEndPoint("127.0.0.1", 8080), proxyUri));
        Assert.False(PublicArticleFetcher.IsConfiguredProxyEndpoint(new DnsEndPoint("127.0.0.2", 7897), proxyUri));
    }

    [Fact]
    public void EnsurePublicAddress_StillRejectsPrivateTargets()
    {
        Assert.Throws<HttpRequestException>(() =>
            PublicArticleFetcher.EnsurePublicAddress([IPAddress.Loopback]));
    }

    private sealed class StubProxy(Uri? proxyUri, bool isBypassed) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => proxyUri;

        public bool IsBypassed(Uri host) => isBypassed;
    }
}
