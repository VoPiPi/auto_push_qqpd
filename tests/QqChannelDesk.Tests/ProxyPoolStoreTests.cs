using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class ProxyPoolStoreTests
{
    [Fact]
    public async Task Initialize_SeedsTheTwoHttpProxySources()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "QqChannelDeskTests", $"proxy-sources-{Guid.NewGuid():N}.db");
        var store = new ProxyPoolStore(databasePath);

        var sources = await store.GetSourcesAsync();

        Assert.Contains(sources, source => source.SourceKey == ProxyPoolStore.CbSourceKey && source.IsBuiltIn);
        Assert.Contains(sources, source => source.SourceKey == ProxyPoolStore.XiaoSourceKey && source.IsBuiltIn);
        Assert.All(sources.Where(source => source.IsBuiltIn), source =>
        {
            Assert.StartsWith("https://", source.SourceUrl, StringComparison.OrdinalIgnoreCase);
            Assert.True(source.IsEnabled);
        });

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }

    [Fact]
    public async Task ApplyFetchedNodes_PreservesOnlyCandidatesPassedByTheHttpParser()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "QqChannelDeskTests", $"proxy-nodes-{Guid.NewGuid():N}.db");
        var store = new ProxyPoolStore(databasePath);
        var source = Assert.Single(await store.GetSourcesAsync(), item => item.SourceKey == ProxyPoolStore.XiaoSourceKey);

        await store.ApplyFetchedNodesAsync(source, [
            new ProxyNodeCandidate("http://192.0.2.40:8080", "http", "192.0.2.40", 8080, ""),
            new ProxyNodeCandidate("https://192.0.2.41:8443", "https", "192.0.2.41", 8443, "US")
        ]);

        var nodes = await store.GetNodesAsync(source.Id);

        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, node => Assert.True(node.IsSupported));
        Assert.Equal("US", nodes.Single(node => node.Host == "192.0.2.41").SourceCountry);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }

    [Fact]
    public async Task UpdateSourceAsync_AllowsChangingBuiltInSourceUrl()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "QqChannelDeskTests", $"proxy-source-edit-{Guid.NewGuid():N}.db");
        var store = new ProxyPoolStore(databasePath);
        var source = Assert.Single(await store.GetSourcesAsync(), item => item.SourceKey == ProxyPoolStore.CbSourceKey);

        await store.UpdateSourceAsync(source, "自定义 CB 源", "https://example.com/proxy-list/", false);

        var updated = Assert.Single(await store.GetSourcesAsync(), item => item.Id == source.Id);
        Assert.Equal("自定义 CB 源", updated.DisplayName);
        Assert.Equal("https://example.com/proxy-list", updated.SourceUrl);
        Assert.False(updated.IsEnabled);
        Assert.True(updated.IsBuiltIn);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }
}
