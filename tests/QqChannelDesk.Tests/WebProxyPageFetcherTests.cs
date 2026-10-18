using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

/// <summary>
/// 覆盖代理接口响应中 meta[http-equiv=x-refresh] 节点地址的提取：实测裸 URL、0;url= 变体、相对地址补全与异常输入。
/// </summary>
public sealed class WebProxyPageFetcherTests
{
    private const string Node = "205.185.122.126";

    [Fact]
    public void ExtractProxyDestination_ReadsBareUrlFromMeta()
    {
        var html = $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8"><meta http-equiv="x-refresh"
            content="https://{{Node}}/s/TGWenAnGuan?__pot=aHR0cHM6Ly90Lm1l&__poss=ANtczdYZ"></head></html>
            """;
        Assert.Equal(
            $"https://{Node}/s/TGWenAnGuan?__pot=aHR0cHM6Ly90Lm1l&__poss=ANtczdYZ",
            WebProxyPageFetcher.ExtractProxyDestination(html));
    }

    [Fact]
    public void ExtractProxyDestination_StripsDelayPrefixAndUrlKeyword()
    {
        var html = """
            <html><head><meta http-equiv="x-refresh" content="0;url=https://23.134.76.53/s/TGWenAnGuan?__pot=abc"></head></html>
            """;
        Assert.Equal("https://23.134.76.53/s/TGWenAnGuan?__pot=abc", WebProxyPageFetcher.ExtractProxyDestination(html));
    }

    [Fact]
    public void ExtractProxyDestination_AcceptsMixedCaseHeaderAndKeyword()
    {
        var html = """
            <html><head><meta http-equiv="X-Refresh" content="0;URL=https://1.2.3.4/s/x"></head></html>
            """;
        Assert.Equal("https://1.2.3.4/s/x", WebProxyPageFetcher.ExtractProxyDestination(html));
    }

    [Fact]
    public void ExtractProxyDestination_ResolvesRelativeUrlAgainstHomepage()
    {
        var html = """
            <html><head><meta http-equiv="x-refresh" content="url=/s/TGWenAnGuan?__pot=abc"></head></html>
            """;
        Assert.Equal(
            "https://proxyunblocker.net/s/TGWenAnGuan?__pot=abc",
            WebProxyPageFetcher.ExtractProxyDestination(html));
    }

    [Fact]
    public void ExtractProxyDestination_KeepsUrlContainingSemicolon()
    {
        var html = """
            <html><head><meta http-equiv="x-refresh" content="https://1.2.3.4/a;b"></head></html>
            """;
        Assert.Equal("https://1.2.3.4/a;b", WebProxyPageFetcher.ExtractProxyDestination(html));
    }

    [Fact]
    public void ExtractProxyDestination_TrimsQuotesAroundTarget()
    {
        var html = """
            <html><head><meta http-equiv="x-refresh" content='0; url="https://1.2.3.4/s/x"'></head></html>
            """;
        Assert.Equal("https://1.2.3.4/s/x", WebProxyPageFetcher.ExtractProxyDestination(html));
    }

    [Fact]
    public void ExtractProxyDestination_IgnoresIrrelevantMetasAndDelaysOnly()
    {
        var html = """
            <html><head><meta charset="UTF-8"><meta name="robots" content="noindex">
            <meta http-equiv="x-refresh" content="5"></head></html>
            """;
        Assert.Equal("", WebProxyPageFetcher.ExtractProxyDestination(html));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><head></head></html>")]
    [InlineData("<html><head><meta http-equiv=\"x-refresh\" content=\"\"></head></html>")]
    public void ExtractProxyDestination_ReturnsEmptyForUnusableResponses(string html)
    {
        Assert.Equal("", WebProxyPageFetcher.ExtractProxyDestination(html));
    }
}
