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

    [Fact]
    public async Task UpdateHealthAsync_PreservesExistingCountryWhenProbeReturnsUnknown()
    {
        var databasePath = CreateDatabasePath("proxy-country");
        var store = new ProxyPoolStore(databasePath);
        var source = Assert.Single(await store.GetSourcesAsync(), item => item.SourceKey == ProxyPoolStore.XiaoSourceKey);
        await store.ApplyFetchedNodesAsync(source, [
            new ProxyNodeCandidate("http://192.0.2.60:8080:DE", "http", "192.0.2.60", 8080, "DE")
        ]);
        var node = Assert.Single(await store.GetNodesAsync(source.Id));

        await store.UpdateHealthAsync(node.Id, true, "198.51.100.60", "", "未知", "");

        var updated = Assert.Single(await store.GetNodesAsync(source.Id));
        Assert.Equal("DE", updated.SourceCountry);
        Assert.Equal("", updated.CountryCode);
        Assert.Equal("", updated.CountryName);
        DisposeDatabase(databasePath);
    }

    [Fact]
    public async Task DeleteNodeAsync_IsolatedByNodeAndSource_AndFetchCanReAddIt()
    {
        var databasePath = CreateDatabasePath("proxy-delete-node");
        var store = new ProxyPoolStore(databasePath);
        var sources = await store.GetSourcesAsync();
        var first = Assert.Single(sources, item => item.SourceKey == ProxyPoolStore.CbSourceKey);
        var second = Assert.Single(sources, item => item.SourceKey == ProxyPoolStore.XiaoSourceKey);
        var candidate = new ProxyNodeCandidate("http://192.0.2.61:8080", "http", "192.0.2.61", 8080, "");

        await store.ApplyFetchedNodesAsync(first, [candidate]);
        await store.ApplyFetchedNodesAsync(second, [candidate]);
        var firstNode = Assert.Single(await store.GetNodesAsync(first.Id));
        var secondNode = Assert.Single(await store.GetNodesAsync(second.Id));

        await store.DeleteNodeAsync(firstNode);

        Assert.Empty(await store.GetNodesAsync(first.Id));
        Assert.Single(await store.GetNodesAsync(second.Id));
        await store.ApplyFetchedNodesAsync(first, [candidate]);
        Assert.Single(await store.GetNodesAsync(first.Id));

        DisposeDatabase(databasePath);
    }

    [Fact]
    public async Task DeleteCustomSourceAsync_DeletesCustomSource_ButProtectsBuiltInSource()
    {
        var databasePath = CreateDatabasePath("proxy-delete-source");
        var store = new ProxyPoolStore(databasePath);
        var builtIn = Assert.Single(await store.GetSourcesAsync(), item => item.SourceKey == ProxyPoolStore.CbSourceKey);
        var customId = await store.CreateCustomSourceAsync("测试源", "https://example.com/proxy");
        var custom = Assert.Single(await store.GetSourcesAsync(), item => item.Id == customId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteCustomSourceAsync(builtIn));
        await store.DeleteCustomSourceAsync(custom);

        Assert.DoesNotContain(await store.GetSourcesAsync(), item => item.Id == customId);
        Assert.Contains(await store.GetSourcesAsync(), item => item.Id == builtIn.Id);
        DisposeDatabase(databasePath);
    }

    private static string CreateDatabasePath(string prefix) => Path.Combine(Path.GetTempPath(), "QqChannelDeskTests", $"{prefix}-{Guid.NewGuid():N}.db");

    private static void DisposeDatabase(string databasePath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
    }
}
