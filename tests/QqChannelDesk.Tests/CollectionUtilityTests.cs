using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class UrlNormalizerTests
{
    [Theory]
    [InlineData("https://Example.test:443/a/?b=2&a=1#top", "https://example.test/a?a=1&b=2")]
    [InlineData("HTTP://example.test:80", "http://example.test/")]
    [InlineData("", "")]
    public void CanonicalizesCommonUrlVariants(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.Canonicalize(input));
    }

    [Fact]
    public void PrefersSourceItemIdAndFallsBackToUrl()
    {
        Assert.Equal("guid-1", UrlNormalizer.CanonicalItemId("https://example.test/a", "guid-1"));
        Assert.Equal("https://example.test/a", UrlNormalizer.CanonicalItemId("https://example.test/a#fragment"));
    }
}

public sealed class ContentFingerprintTests
{
    [Fact]
    public void IdenticalNormalizedContentProducesSameFingerprint()
    {
        var first = ContentFingerprint.Compute("标题", "第一段。\n第二段。");
        var second = ContentFingerprint.Compute(" 标题 ", "第一段。 第二段。");
        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentContentProducesDifferentFingerprint()
    {
        Assert.NotEqual(
            ContentFingerprint.Compute("标题", "第一版内容"),
            ContentFingerprint.Compute("标题", "第二版内容"));
    }
}

public sealed class PublicUrlGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1/page")]
    [InlineData("http://localhost/page")]
    [InlineData("http://192.168.1.10/page")]
    [InlineData("http://169.254.1.10/page")]
    public void RejectsLoopbackAndPrivateAddresses(string input)
    {
        Assert.False(PublicUrlGuard.TryNormalize(input, out _, out var error));
        Assert.Contains("本机、私有或链路本地", error);
    }

    [Theory]
    [InlineData("https://example.test/page#fragment", "https://example.test/page")]
    [InlineData("http://example.test:80/a", "http://example.test/a")]
    public void AcceptsPublicHttpAddressesAndRemovesFragments(string input, string expected)
    {
        Assert.True(PublicUrlGuard.TryNormalize(input, out var uri, out _));
        Assert.Equal(expected, uri.AbsoluteUri);
    }
}
