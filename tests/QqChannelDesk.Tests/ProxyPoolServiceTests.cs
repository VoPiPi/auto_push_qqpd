using System.Net;
using System.Net.Http;
using System.Net.Security;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class ProxyPoolServiceTests
{
    [Theory]
    [InlineData("http://192.0.2.10:8080", "http", "192.0.2.10", 8080, "")]
    [InlineData("https://192.0.2.11:8443:US", "https", "192.0.2.11", 8443, "US")]
    [InlineData("192.0.2.12:3128:cn", "http", "192.0.2.12", 3128, "CN")]
    public void ParseHttpCandidate_AcceptsOnlyHttpHttpsAndPlainHostPort(
        string raw,
        string scheme,
        string host,
        int port,
        string country)
    {
        var candidate = ProxyPoolService.ParseHttpCandidate(raw);

        Assert.NotNull(candidate);
        Assert.Equal(scheme, candidate.Scheme);
        Assert.Equal(host, candidate.Host);
        Assert.Equal(port, candidate.Port);
        Assert.Equal(country, candidate.SourceCountry);
    }

    [Theory]
    [InlineData("ss://YWVzLTEyMy5leGFtcGxlOjQ0Mw==")]
    [InlineData("socks4://192.0.2.20:1080")]
    [InlineData("socks5://192.0.2.21:1080")]
    [InlineData("vmess://eyJhZGQiOiIxOTIuMC4yLjIyIn0=")]
    [InlineData("ftp://192.0.2.23:21")]
    [InlineData("http://192.0.2.24:0")]
    [InlineData("https://192.0.2.25:65536")]
    [InlineData("192.0.2.26")]
    public void ParseHttpCandidate_FiltersUnsupportedOrInvalidNodes(string raw)
    {
        Assert.Null(ProxyPoolService.ParseHttpCandidate(raw));
    }

    [Fact]
    public void ShouldRelaxProxyCertificate_AllowsHttpsProxyEndpointCertificateErrors()
    {
        var https = CreateNode("https", 8443);

        Assert.True(ProxyPoolService.ShouldRelaxProxyCertificate(
            https,
            https.Host,
            SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(ProxyPoolService.ShouldRelaxProxyCertificate(
            https,
            https.Host,
            SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void ShouldRelaxProxyCertificate_DoesNotRelaxTargetSiteOrHttpProxyErrors()
    {
        var https = CreateNode("https", 443);
        var http = CreateNode("http", 8080);

        Assert.False(ProxyPoolService.ShouldRelaxProxyCertificate(
            https,
            "api.example.com",
            SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(ProxyPoolService.ShouldRelaxProxyCertificate(
            http,
            http.Host,
            SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void ShouldRelaxProxyCertificate_RejectsUnavailableCertificate()
    {
        var https = CreateNode("https", 8443);

        Assert.False(ProxyPoolService.ShouldRelaxProxyCertificate(
            https,
            https.Host,
            SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void ShouldRelaxProxyCertificate_AcceptsValidCertificateForAnyHost()
    {
        var http = CreateNode("http", 8080);

        Assert.True(ProxyPoolService.ShouldRelaxProxyCertificate(
            http,
            "api.example.com",
            SslPolicyErrors.None));
    }

    [Fact]
    public void ProbeTimeoutIsNotTreatedAsUserCancellation()
    {
        using var timeout = new CancellationTokenSource();

        Assert.False(ProxyPoolService.IsCallerCancellation(new TaskCanceledException(), timeout.Token));
    }

    [Fact]
    public void ProbeCancellationStillHonorsUserCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(ProxyPoolService.IsCallerCancellation(new OperationCanceledException(), cancellation.Token));
    }

    [Fact]
    public async Task FetchSourceAsync_FiltersUnsupportedProtocolsBeforePersistence()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "QqChannelDeskTests", $"proxy-pool-{Guid.NewGuid():N}.db");
        var store = new ProxyPoolStore(databasePath);
        using var sourceClient = new HttpClient(new StaticResponseHandler("""
            http://192.0.2.30:8080
            https://192.0.2.31:8443:DE
            192.0.2.32:3128:cn
            ss://YWVzLTEyMy5leGFtcGxlOjQ0Mw==
            socks5://192.0.2.33:1080
            vmess://eyJhZGQiOiIxOTIuMC4yLjM0In0=
            invalid-node
            """));
        var service = new ProxyPoolService(store, sourceClient);
        var source = Assert.Single(await store.GetSourcesAsync(), item => item.SourceKey == ProxyPoolStore.CbSourceKey);

        var result = await service.FetchSourceAsync(source);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.ReadCount);
        Assert.Equal(3, result.AcceptedCount);
        Assert.Equal(4, result.FilteredCount);
        var nodes = await store.GetNodesAsync(source.Id);
        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, node => Assert.Contains(node.Scheme, new[] { "http", "https" }));
        Assert.DoesNotContain(nodes, node => node.RawValue.StartsWith("ss://", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(nodes, node => node.RawValue.StartsWith("socks", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(nodes, node => node.RawValue.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase));

        DisposeStore(store, databasePath);
    }

    private static void DisposeStore(ProxyPoolStore store, string databasePath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }

    private static ProxyNode CreateNode(string scheme, int port) => new(
        1,
        1,
        $"{scheme}://192.0.2.50:{port}",
        scheme,
        "192.0.2.50",
        port,
        "",
        false,
        "",
        "",
        "",
        null,
        "",
        null,
        null,
        0,
        "",
        true,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
            return Task.FromResult(response);
        }
    }
}
