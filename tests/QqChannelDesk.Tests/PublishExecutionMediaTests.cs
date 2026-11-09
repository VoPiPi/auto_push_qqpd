using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class PublishExecutionMediaTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "QqChannelDeskPublishMediaTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(FeedType.Image, "image", ".jpg")]
    [InlineData(FeedType.Video, "video", ".mp4")]
    public async Task SuccessfulPublishKeepsStagingOriginalAndDeletesJobCopy(
        FeedType feedType,
        string mediaType,
        string extension)
    {
        var (storage, workflow, executor) = await CreateExecutorAsync();
        var source = Path.Combine(_directory, "temp", "telegram", "20261020", "channel", $"media{extension}");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [1, 2, 3]);

        var outcome = await executor.ExecuteAsync(
            new PublishRequest("100", "200", "", "", feedType, [source]),
            "测试频道",
            "公告");

        Assert.True(outcome.Result.Succeeded);
        Assert.NotNull(workflow.LastRequest);
        var publishedPath = Assert.Single(workflow.LastRequest!.MediaPaths);
        Assert.NotEqual(source, publishedPath);
        Assert.Equal(Path.Combine(_directory, "media", "jobs"), Path.GetDirectoryName(publishedPath));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(publishedPath));
        Assert.Equal(mediaType, MaterialMediaValidator.InferType([source]));
    }

    [Theory]
    [InlineData(FeedType.Image, "image", ".jpg")]
    [InlineData(FeedType.Video, "video", ".mp4")]
    public async Task SuccessfulPublishKeepsLibraryOriginal(
        FeedType feedType,
        string mediaType,
        string extension)
    {
        var (storage, workflow, executor) = await CreateExecutorAsync();
        var source = Path.Combine(_directory, $"source{extension}");
        File.WriteAllBytes(source, [4, 5, 6]);
        var libraryPath = await storage.ImportToLibraryAsync(source, mediaType);

        var outcome = await executor.ExecuteAsync(
            new PublishRequest("100", "200", "", "", feedType, [libraryPath]),
            "测试频道",
            "公告");

        Assert.True(outcome.Result.Succeeded);
        Assert.NotNull(workflow.LastRequest);
        Assert.Equal(libraryPath, Assert.Single(workflow.LastRequest!.MediaPaths));
        Assert.True(File.Exists(libraryPath));
    }

    private async Task<(MediaStorageService Storage, RecordingWorkflow Workflow, PublishExecutionService Executor)>
        CreateExecutorAsync()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "channels.db");
        var settings = new SystemSettingsStore(databasePath);
        await settings.SaveAsync(new AppSettings(MaterialStoragePath: Path.Combine(_directory, "media")));
        var storage = new MediaStorageService(settings);
        var workflow = new RecordingWorkflow();
        var executor = new PublishExecutionService(
            workflow,
            new PublishHistoryStore(databasePath),
            storage);
        return (storage, workflow, executor);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private sealed class RecordingWorkflow : IPublishWorkflow
    {
        public PublishRequest? LastRequest { get; private set; }

        public Task<PublishResult> PublishAsync(
            PublishRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new PublishResult(
                true,
                PublishErrorCategory.None,
                "帖子已提交。",
                "https://example.test/post/1",
                "post-1"));
        }
    }
}
