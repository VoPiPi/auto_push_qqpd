using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class MediaStorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"QqChannelMediaTests-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly string _sourcePath;

    public MediaStorageTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "channels.db");
        _sourcePath = Path.Combine(_directory, "source.jpg");
        File.WriteAllBytes(_sourcePath, [1, 2, 3, 4]);
    }

    [Fact]
    public async Task ImportToLibraryAndCreateJobCopyUseSeparateManagedDirectories()
    {
        var service = await CreateServiceAsync();

        var libraryPath = await service.ImportToLibraryAsync(_sourcePath, "image");
        var jobPath = await service.CreateJobCopyAsync(_sourcePath, "image");
        var secondJobPath = await service.CreateJobCopyAsync(_sourcePath, "image");

        Assert.True(File.Exists(libraryPath));
        Assert.True(File.Exists(jobPath));
        Assert.True(File.Exists(secondJobPath));
        Assert.NotEqual(jobPath, secondJobPath);
        Assert.Equal(Path.Combine(_directory, "media", "library"), Directory.GetParent(libraryPath)!.FullName);
        Assert.Equal(Path.Combine(_directory, "media", "jobs"), Directory.GetParent(jobPath)!.FullName);
        Assert.Equal(File.ReadAllBytes(_sourcePath), File.ReadAllBytes(libraryPath));
        Assert.Equal(File.ReadAllBytes(_sourcePath), File.ReadAllBytes(jobPath));
    }

    [Fact]
    public async Task MissingSourceIsRejectedBeforeCopy()
    {
        var service = await CreateServiceAsync();
        var missingPath = Path.Combine(_directory, "missing.jpg");

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.CreateJobCopyAsync(missingPath, "image"));
        Assert.False(File.Exists(missingPath));
    }

    [Fact]
    public async Task SuccessfulJobIsDeletedWithDefaultPolicyButLibraryOriginalRemains()
    {
        var service = await CreateServiceAsync(MaterialRetentionPolicy.DeleteAfterSuccessfulPublish);
        var libraryPath = await service.ImportToLibraryAsync(_sourcePath, "image");
        var jobPath = await service.CreateJobCopyAsync(_sourcePath, "image");

        await service.CompleteSuccessfulJobAsync([jobPath, libraryPath]);

        Assert.False(File.Exists(jobPath));
        Assert.True(File.Exists(libraryPath));
    }

    [Fact]
    public async Task SuccessfulJobIsMovedToSuccessfulDirectoryForSevenDayPolicy()
    {
        var service = await CreateServiceAsync(MaterialRetentionPolicy.KeepSevenDays);
        var jobPath = await service.CreateJobCopyAsync(_sourcePath, "image");

        await service.CompleteSuccessfulJobAsync([jobPath]);

        Assert.False(File.Exists(jobPath));
        var successfulPath = Path.Combine(_directory, "media", "jobs", "successful", Path.GetFileName(jobPath));
        Assert.True(File.Exists(successfulPath));
    }

    [Fact]
    public async Task SuccessfulJobIsMovedToSuccessfulDirectoryForForeverPolicy()
    {
        var service = await CreateServiceAsync(MaterialRetentionPolicy.KeepForever);
        var jobPath = await service.CreateJobCopyAsync(_sourcePath, "image");

        await service.CompleteSuccessfulJobAsync([jobPath]);

        Assert.False(File.Exists(jobPath));
        var successfulPath = Path.Combine(_directory, "media", "jobs", "successful", Path.GetFileName(jobPath));
        Assert.True(File.Exists(successfulPath));
    }

    [Fact]
    public async Task JobCopyRemainsUntilSuccessfulCompletion()
    {
        var service = await CreateServiceAsync();
        var jobPath = await service.CreateJobCopyAsync(_sourcePath, "image");

        Assert.True(File.Exists(jobPath));
    }

    [Fact]
    public void TelegramStagingPathCoversChannelMediaButNotPreviewOrLibrary()
    {
        var root = Path.Combine(_directory, "app");

        Assert.True(MediaStorageService.IsTelegramStagingPath(
            Path.Combine(root, "temp", "telegram", "20261013", "TGWenAnGuan", "video.mp4"), root));
        Assert.False(MediaStorageService.IsTelegramStagingPath(
            Path.Combine(root, "temp", "telegram", "preview", "725", "video.mp4"), root));
        Assert.False(MediaStorageService.IsTelegramStagingPath(
            Path.Combine(root, "temp", "media", "library", "video.mp4"), root));
        Assert.False(MediaStorageService.IsTelegramStagingPath(
            Path.Combine(root, "temp", "media", "jobs", "video.mp4"), root));
        Assert.False(MediaStorageService.IsTelegramStagingPath("", root));
    }

    [Fact]
    public async Task SuccessfulPublishRemovesTelegramStagingMediaButKeepsLibraryOriginal()
    {
        var service = await CreateServiceAsync();
        var root = Path.Combine(_directory, "app");
        var staging = CreateStagingFile(root, "channel-a");
        var libraryPath = await service.ImportToLibraryAsync(_sourcePath, "image");

        await service.DeleteTelegramStagingAsync([staging], root);

        Assert.False(File.Exists(staging));
        Assert.True(File.Exists(libraryPath));
    }

    [Theory]
    [InlineData(MaterialRetentionPolicy.KeepSevenDays)]
    [InlineData(MaterialRetentionPolicy.KeepForever)]
    public async Task RetentionPoliciesKeepTelegramStagingMedia(MaterialRetentionPolicy policy)
    {
        var service = await CreateServiceAsync(policy);
        var root = Path.Combine(_directory, "app");
        var staging = CreateStagingFile(root, "channel-b");

        await service.DeleteTelegramStagingAsync([staging], root);

        Assert.True(File.Exists(staging));
    }

    [Fact]
    public async Task OrphanStagingMediaIsRemovedEvenWhenMaterialWasNeverCreated()
    {
        var service = await CreateServiceAsync(MaterialRetentionPolicy.KeepForever);
        var root = Path.Combine(_directory, "app");
        var staging = CreateStagingFile(root, "channel-c");
        var libraryPath = await service.ImportToLibraryAsync(_sourcePath, "image");

        await service.DeleteOrphanStagingAsync([staging], root);

        Assert.False(File.Exists(staging));
        Assert.True(File.Exists(libraryPath));
    }

    [Fact]
    public async Task StagingCleanupIgnoresMissingFilesAndForeignPaths()
    {
        var service = await CreateServiceAsync();
        var root = Path.Combine(_directory, "app");
        var missing = Path.Combine(root, "temp", "telegram", "20261013", "channel-d", "missing.mp4");
        var libraryPath = await service.ImportToLibraryAsync(_sourcePath, "image");

        await service.DeleteTelegramStagingAsync([missing, libraryPath], root);

        Assert.True(File.Exists(libraryPath));
    }

    private static string CreateStagingFile(string root, string channel)
    {
        var directory = Path.Combine(root, "temp", "telegram", "20261013", channel);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "video.mp4");
        File.WriteAllBytes(path, [9, 9, 9]);
        return path;
    }
    private async Task<MediaStorageService> CreateServiceAsync(
        MaterialRetentionPolicy policy = MaterialRetentionPolicy.DeleteAfterSuccessfulPublish)
    {
        var settingsStore = new SystemSettingsStore(_databasePath);
        await settingsStore.SaveAsync(new AppSettings(
            MaterialStoragePath: Path.Combine(_directory, "media"),
            MaterialRetentionPolicy: policy));
        return new MediaStorageService(settingsStore);
    }

    public void Dispose()
    {
        DeleteFile(_sourcePath);
        DeleteFile(_databasePath);
        // Media files intentionally remain in the unique test directory. The repository
        // cleanup policy does not permit recursive deletion from tests either.
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
