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
