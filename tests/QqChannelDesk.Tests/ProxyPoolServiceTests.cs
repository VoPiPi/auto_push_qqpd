using System.Net;
using System.Net.Http;
using System.Net.Security;
using QqChannelDesk.Pages;
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
    public void ProxyNodeRow_InvalidForBatchDelete_WhenHealthCheckFailed()
    {
        var node = CreateNode("http", 8080) with
        {
            IsAlive = false,
            LastCheckedAt = DateTimeOffset.UtcNow,
            LastCheckError = "测活失败"
        };

        Assert.True(new ProxyNodeRow(node).IsInvalidForBatchDelete);
    }

    [Fact]
    public void ProxyNodeRow_InvalidForBatchDelete_WhenTelegramCheckFailed()
    {
        var node = CreateNode("http", 8080) with
        {
            IsAlive = true,
            LastCheckedAt = DateTimeOffset.UtcNow,
            TgAccessible = false,
            LastTgCheckedAt = DateTimeOffset.UtcNow,
            LastTgCheckError = "TG 验证失败"
        };

        Assert.True(new ProxyNodeRow(node).IsInvalidForBatchDelete);
    }

    [Fact]
    public void ProxyNodeRow_InvalidForBatchDelete_IgnoresUntestedNodes()
    {
        var node = CreateNode("http", 8080);

        Assert.False(new ProxyNodeRow(node).IsInvalidForBatchDelete);
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

    [Fact]
    public void TelegramProbeUrl_UsesPublicPreviewPage()
    {
        Assert.Equal("https://t.me/s/telegram", ProxyPoolService.TelegramProbeUrl);
    }

    [Fact]
    public async Task CheckTelegramBatchAsync_HonorsConcurrencyAndContinuesAfterNodeFailure()
    {
        var databasePath = CreateDatabasePath("proxy-tg-batch");
        var store = new ProxyPoolStore(databasePath);
        var source = Assert.Single(await store.GetSourcesAsync(), item => item.SourceKey == ProxyPoolStore.CbSourceKey);
        await store.ApplyFetchedNodesAsync(source, Enumerable.Range(1, 4)
            .Select(index => new ProxyNodeCandidate(
                $"http://192.0.2.{100 + index}:8080",
                "http",
                $"192.0.2.{100 + index}",
                8080,
                ""))
            .ToArray());
        var nodes = await store.GetNodesAsync(source.Id);
        var failedNodeId = nodes[0].Id;
        var concurrent = 0;
        var maxConcurrent = 0;
        var firstPairEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ProxyPoolService(
            store,
            sourceClient: null,
            healthProbe: (_, _) => Task.FromResult(new ProxyHealthProbeResult(true, "198.51.100.1", "", "未知", "")),
            telegramProbe: async (node, cancellationToken) =>
            {
                if (node.Id == failedNodeId) throw new InvalidOperationException("fake TG probe failure");

                var current = Interlocked.Increment(ref concurrent);
                UpdateMaximum(ref maxConcurrent, current);
                try
                {
                    if (current >= 2) firstPairEntered.TrySetResult();
                    await firstPairEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                    return new ProxyTelegramCheckResult(true, 0, "");
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            });

        var result = await service.CheckTelegramBatchAsync(nodes, concurrency: 2);

        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.True(maxConcurrent <= 2, $"预期最大并发不超过 2，实际为 {maxConcurrent}。");
        Assert.True(maxConcurrent >= 2, $"预期至少出现 2 个并发任务，实际为 {maxConcurrent}。");

        var refreshed = await store.GetNodesAsync(source.Id);
        var failed = Assert.Single(refreshed, node => node.Id == failedNodeId);
        Assert.False(failed.TgAccessible);
        Assert.Contains("fake TG probe failure", failed.LastTgCheckError);
        Assert.Contains("fake TG probe failure", new ProxyNodeRow(failed).LastResult);
        Assert.All(refreshed.Where(node => node.Id != failedNodeId), node => Assert.True(node.TgAccessible));
        Assert.Contains(
            "TG 验证成功",
            new ProxyNodeRow(refreshed.First(node => node.Id != failedNodeId)).LastResult);

        DisposeStore(store, databasePath);
    }

    [Fact]
    public async Task TestHealthBatchAsync_ContinuesAfterNodeProbeFailure()
    {
        var databasePath = CreateDatabasePath("proxy-health-batch");
        var store = new ProxyPoolStore(databasePath);
        var source = Assert.Single(await store.GetSourcesAsync(), item => item.SourceKey == ProxyPoolStore.XiaoSourceKey);
        await store.ApplyFetchedNodesAsync(source, [
            new ProxyNodeCandidate("http://192.0.2.201:8080", "http", "192.0.2.201", 8080, ""),
            new ProxyNodeCandidate("http://192.0.2.202:8080", "http", "192.0.2.202", 8080, "")
        ]);
        var nodes = await store.GetNodesAsync(source.Id);
        var failedNodeId = nodes[0].Id;
        var service = new ProxyPoolService(
            store,
            sourceClient: null,
            healthProbe: (node, _) => node.Id == failedNodeId
                ? Task.FromException<ProxyHealthProbeResult>(new InvalidOperationException("fake health probe failure"))
                : Task.FromResult(new ProxyHealthProbeResult(true, "198.51.100.2", "US", "United States", "")),
            telegramProbe: (_, _) => Task.FromResult(new ProxyTelegramCheckResult(true, 0, "")));

        var result = await service.TestHealthAsync(nodes, concurrency: 2);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        var refreshed = await store.GetNodesAsync(source.Id);
        Assert.Contains("fake health probe failure", Assert.Single(refreshed, node => node.Id == failedNodeId).LastCheckError);
        Assert.True(Assert.Single(refreshed, node => node.Id != failedNodeId).IsAlive);

        DisposeStore(store, databasePath);
    }

    private static string CreateDatabasePath(string prefix) =>
        Path.Combine(Path.GetTempPath(), "QqChannelDeskTests", $"{prefix}-{Guid.NewGuid():N}.db");

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (value <= observed || Interlocked.CompareExchange(ref maximum, value, observed) == observed) return;
        }
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
