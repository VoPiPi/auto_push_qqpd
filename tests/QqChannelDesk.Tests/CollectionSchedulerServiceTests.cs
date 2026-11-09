using QqChannelDesk.Services;
using QqChannelDesk.Services.Collection;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class CollectionSchedulerServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "QqChannelDeskCollectionSchedulerTests",
        Guid.NewGuid().ToString("N"),
        "channels.db");
    private readonly AccountContext _context = new();

    public CollectionSchedulerServiceTests()
    {
        _context.Set(CurrentAccountIdentity.Create("global-a", "nickname-a"));
    }

    [Fact]
    public async Task RunsSourceWritesBatchAndDeduplicatesItems()
    {
        var sourceStore = new CollectionSourceStore(_databasePath, _context);
        var runStore = new CollectionRunStore(_databasePath, _context);
        var library = new ContentLibraryStore(_databasePath, _context);
        var sourceId = await sourceStore.CreateSourceAsync(new CollectionSourceDraft(
            "来源", "rss", "https://example.test/rss", AuthConfirmed: true));
        var collector = new ListSourceCollector([
            new("标题", "正文", "https://example.test/a", "same"),
            new("重复标题", "重复正文", "https://example.test/a-copy", "same")
        ]);
        var scheduler = new CollectionSchedulerService(
            sourceStore, runStore, library,
            new Dictionary<string, ISourceCollector> { ["rss"] = collector });

        await scheduler.RunSourceAsync(sourceId);

        var run = Assert.Single(await runStore.GetRunsAsync(sourceId));
        Assert.Equal("succeeded", run.Result);
        Assert.Equal(2, run.FoundCount);
        Assert.Equal(1, run.NewItemCount);
        Assert.Equal(1, run.DuplicateCount);
        var item = Assert.Single(await library.GetItemsAsync());
        Assert.Equal(sourceId, item.SourceId);
        Assert.Equal("same", item.SourceItemId);
        Assert.Equal("正文", item.OriginalContent);
        Assert.Equal("rule", item.ParsedBy);
    }

    [Fact]
    public async Task SameSourceDoesNotRunConcurrently()
    {
        var sourceStore = new CollectionSourceStore(_databasePath, _context);
        var runStore = new CollectionRunStore(_databasePath, _context);
        var library = new ContentLibraryStore(_databasePath, _context);
        var sourceId = await sourceStore.CreateSourceAsync(new CollectionSourceDraft(
            "串行来源", "rss", "https://example.test/rss", AuthConfirmed: true));
        var collector = new BlockingSourceCollector();
        var scheduler = new CollectionSchedulerService(
            sourceStore, runStore, library,
            new Dictionary<string, ISourceCollector> { ["rss"] = collector });

        var first = scheduler.RunSourceAsync(sourceId);
        await collector.Started.Task;
        await scheduler.RunSourceAsync(sourceId);
        Assert.Equal(1, collector.CallCount);
        collector.Release.SetResult();
        await first;
    }

    [Fact]
    public async Task RecollectionRepairsPreviouslyGarbledSourceItem()
    {
        var sourceStore = new CollectionSourceStore(_databasePath, _context);
        var runStore = new CollectionRunStore(_databasePath, _context);
        var library = new ContentLibraryStore(_databasePath, _context);
        var sourceId = await sourceStore.CreateSourceAsync(new CollectionSourceDraft(
            "编码修复来源", "rss", "https://example.test/rss", AuthConfirmed: true));
        var brokenScheduler = new CollectionSchedulerService(
            sourceStore, runStore, library,
            new Dictionary<string, ISourceCollector>
            {
                ["rss"] = new ListSourceCollector([
                    new("\uFFFD\uFFFD\uFFFD", "\uFFFD\uFFFD", "https://example.test/fix", "same")
                ])
            });
        await brokenScheduler.RunSourceAsync(sourceId);

        var fixedScheduler = new CollectionSchedulerService(
            sourceStore, runStore, library,
            new Dictionary<string, ISourceCollector>
            {
                ["rss"] = new ListSourceCollector([
                    new("正确标题", "正确正文", "https://example.test/fix", "same")
                ])
            });
        await fixedScheduler.RunSourceAsync(sourceId);

        var item = Assert.Single(await library.GetItemsAsync());
        Assert.Equal("正确标题", item.Title);
        Assert.Equal("正确正文", item.Content);
        Assert.Equal("正确正文", item.OriginalContent);
    }

    [Fact]
    public async Task ThreeConsecutiveFailuresPauseAutomaticScheduling()
    {
        var sourceStore = new CollectionSourceStore(_databasePath, _context);
        var runStore = new CollectionRunStore(_databasePath, _context);
        var library = new ContentLibraryStore(_databasePath, _context);
        var sourceId = await sourceStore.CreateSourceAsync(new CollectionSourceDraft(
            "失败来源", "rss", "https://example.test/rss", AuthConfirmed: true));
        var scheduler = new CollectionSchedulerService(
            sourceStore, runStore, library,
            new Dictionary<string, ISourceCollector>
            {
                ["rss"] = new ListSourceCollector([], "network-error", "读取失败")
            });

        await scheduler.RunSourceAsync(sourceId);
        await scheduler.RunSourceAsync(sourceId);
        await scheduler.RunSourceAsync(sourceId);

        var source = await sourceStore.GetSourceAsync(sourceId);
        Assert.NotNull(source);
        Assert.Equal("failed", source!.HealthStatus);
        Assert.Equal(3, source.ConsecutiveFailures);
        Assert.Null(source.NextRunAt);
        var runs = await runStore.GetRunsAsync(sourceId);
        Assert.Equal(3, runs.Count);
        Assert.All(runs, run =>
        {
            Assert.Equal("failed", run.Result);
            Assert.Equal("network-error", run.ErrorCode);
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
    }
}

internal sealed class ListSourceCollector(
    IReadOnlyList<CollectionItemDraft> items,
    string errorCode = "",
    string errorSummary = "") : ISourceCollector
{
    public string SourceType => "rss";

    public Task<CollectionResult> CollectAsync(CollectionSource source, CancellationToken cancellationToken = default) =>
        Task.FromResult(errorCode.Length == 0
            ? CollectionResult.Success(items)
            : CollectionResult.Failure(errorCode, errorSummary));
}

internal sealed class BlockingSourceCollector : ISourceCollector
{
    public int CallCount { get; private set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string SourceType => "rss";

    public async Task<CollectionResult> CollectAsync(CollectionSource source, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Started.TrySetResult();
        await Release.Task;
        return CollectionResult.Success([new CollectionItemDraft("标题", "正文", "https://example.test/a", "same")]);
    }
}
