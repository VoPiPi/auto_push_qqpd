using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class CollectionSourceStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "QqChannelDeskCollectionSourceTests",
        Guid.NewGuid().ToString("N"),
        "channels.db");

    [Fact]
    public async Task CreateUpdateHealthAndSoftDeleteSource()
    {
        var store = NewStore();
        var id = await store.CreateSourceAsync(new CollectionSourceDraft(
            "官方博客", "rss", "https://example.test/rss", FrequencyMinutes: 30));

        var created = await store.GetSourceAsync(id);
        Assert.NotNull(created);
        Assert.Equal("官方博客", created!.Name);
        Assert.Equal("rss", created.SourceType);
        Assert.True(created.Enabled);
        Assert.Equal("healthy", created.HealthStatus);

        await store.UpdateSourceAsync(new CollectionSourceDraft(
            "厂商更新", "rss", "https://example.test/rss", AuthConfirmed: true,
            AuthConfirmedAt: DateTimeOffset.Now, MaxItemsPerRun: 10, Id: id));
        await store.UpdateHealthAsync(new SourceHealthUpdate(
            id, "degraded", DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(-5), null,
            "解析降级", "parse-error", "未识别到条目"));

        var updated = await store.GetSourceAsync(id);
        Assert.NotNull(updated);
        Assert.Equal("厂商更新", updated!.Name);
        Assert.True(updated.AuthConfirmed);
        Assert.Equal(10, updated.MaxItemsPerRun);
        Assert.Equal("degraded", updated.HealthStatus);
        Assert.Equal("parse-error", updated.LastErrorCode);

        Assert.True(await store.SoftDeleteSourceAsync(id));
        Assert.Empty(await store.GetSourcesAsync());
        Assert.Null(await store.GetSourceAsync(id));
    }

    [Fact]
    public async Task SourceValidationRejectsInvalidFields()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateSourceAsync(
            new CollectionSourceDraft("无效", "rss", "https://example.test/rss", RuleConfigJson: "[]")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateSourceAsync(
            new CollectionSourceDraft("无效", "wechat", "https://example.test/rss")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateSourceAsync(
            new CollectionSourceDraft("无效", "rss", "https://example.test/rss", FrequencyMinutes: 0)));
    }

    private CollectionSourceStore NewStore()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        return new CollectionSourceStore(_databasePath, context);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
