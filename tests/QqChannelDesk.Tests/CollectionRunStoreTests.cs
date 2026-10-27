using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class CollectionRunStoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "QqChannelDeskCollectionRunTests",
        Guid.NewGuid().ToString("N"),
        "channels.db");

    [Fact]
    public async Task CreateCompleteAndFilterRun()
    {
        var sourceId = await CreateSourceAsync();
        var runStore = NewRunStore();
        var runId = await runStore.CreateRunAsync(new CollectionRunDraft(sourceId, "scheduled"));

        var running = await runStore.GetRunAsync(runId);
        Assert.NotNull(running);
        Assert.Equal("running", running!.Result);

        await runStore.CompleteRunAsync(new CollectionRunUpdate(
            runId, "partial", FoundCount: 5, FetchedCount: 4, DuplicateCount: 1,
            AiFallbackCount: 1, NewItemCount: 2, ErrorCode: "parse-error", ErrorSummary: "脱敏错误"));

        var allRuns = await runStore.GetRunsAsync();
        var run = Assert.Single(allRuns);
        Assert.Equal("partial", run.Result);
        Assert.Equal(4, run.FetchedCount);
        Assert.Equal(2, run.NewItemCount);
        Assert.NotNull(run.EndedAt);
        Assert.Single(await runStore.GetRunsAsync(sourceId: sourceId));
        Assert.Empty(await runStore.GetRunsAsync(sourceId: sourceId + 9999));
        Assert.Single(await runStore.GetRunsAsync(result: "partial"));
        Assert.Empty(await runStore.GetRunsAsync(result: "succeeded"));
    }

    [Fact]
    public async Task RunRequiresOwnedSource()
    {
        var runStore = NewRunStore();
        await NewSourceStore().InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runStore.CreateRunAsync(
            new CollectionRunDraft(999, "manual")));
    }

    [Fact]
    public async Task GetTodaySucceededCount_OnlyCountsTodaySucceededRuns()
    {
        var sourceId = await CreateSourceAsync();
        var runStore = NewRunStore();
        var succeededId = await runStore.CreateRunAsync(new CollectionRunDraft(sourceId, "scheduled"));
        await runStore.CompleteRunAsync(new CollectionRunUpdate(succeededId, "succeeded", NewItemCount: 3));
        var failedId = await runStore.CreateRunAsync(new CollectionRunDraft(sourceId, "scheduled"));
        await runStore.CompleteRunAsync(new CollectionRunUpdate(failedId, "failed", ErrorCode: "http-error", ErrorSummary: "失败"));
        var partialId = await runStore.CreateRunAsync(new CollectionRunDraft(sourceId, "scheduled"));
        await runStore.CompleteRunAsync(new CollectionRunUpdate(partialId, "partial", NewItemCount: 1));

        var todayStart = new DateTimeOffset(DateTime.Now.Date);
        Assert.Equal(1, await runStore.GetTodaySucceededCountAsync(todayStart));
        Assert.Equal(0, await runStore.GetTodaySucceededCountAsync(DateTimeOffset.Now.AddMinutes(5)));
    }

    private async Task<long> CreateSourceAsync()
    {
        var sourceStore = NewSourceStore();
        return await sourceStore.CreateSourceAsync(new CollectionSourceDraft("来源", "rss", "https://example.test/rss"));
    }

    private CollectionSourceStore NewSourceStore()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        return new CollectionSourceStore(_databasePath, context);
    }

    private CollectionRunStore NewRunStore()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
        return new CollectionRunStore(_databasePath, context);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}
