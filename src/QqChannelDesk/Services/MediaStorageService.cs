using System.IO;

namespace QqChannelDesk.Services;

public sealed class MediaStorageService
{
    private readonly SystemSettingsStore _settingsStore;

    public MediaStorageService(SystemSettingsStore? settingsStore = null)
    {
        _settingsStore = settingsStore ?? new SystemSettingsStore();
    }

    public async Task<string> GetStorageRootAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.GetAsync(cancellationToken);
        return settings.EffectiveMaterialStoragePath;
    }

    public async Task<bool> ValidateAndPrepareDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ValidateDirectoryAsync(path, cancellationToken);
        return result.Succeeded;
    }

    public static async Task<StorageDirectoryResult> ValidateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(false, "请选择素材文件存储路径。", "");
        string fullPath;
        try { fullPath = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(false, $"素材存储路径无效，请重新选择：{ex.Message}", "");
        }

        try
        {
            Directory.CreateDirectory(fullPath);
            var probePath = Path.Combine(fullPath, $".qcd-write-test-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probePath, "QqChannelDesk", cancellationToken);
            File.Delete(probePath);
            return new(true, "素材目录可用。", fullPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(false, $"素材目录不可写，请检查权限或更换目录：{ex.Message}", fullPath);
        }
    }

    public async Task<string> ImportToLibraryAsync(
        string sourcePath,
        string expectedType,
        CancellationToken cancellationToken = default,
        bool allowUnknownSources = false)
    {
        EnsureLocalSource(sourcePath, expectedType, allowUnknownSources);
        var root = await GetStorageRootAsync(cancellationToken);
        var libraryDirectory = Path.Combine(root, "library");
        var directoryResult = await ValidateDirectoryAsync(libraryDirectory, cancellationToken);
        if (!directoryResult.Succeeded) throw new IOException(directoryResult.Message);
        if (IsManagedLibraryPath(sourcePath, root)) return Path.GetFullPath(sourcePath);
        return await CopyToUniquePathAsync(sourcePath, directoryResult.FullPath, cancellationToken);
    }

    public async Task<string> CreateJobCopyAsync(
        string sourcePath,
        string expectedType,
        CancellationToken cancellationToken = default,
        bool allowUnknownSources = false)
    {
        EnsureLocalSource(sourcePath, expectedType, allowUnknownSources);
        var jobsDirectory = await GetJobsDirectoryAsync(cancellationToken);
        return await CopyToUniquePathAsync(sourcePath, jobsDirectory, cancellationToken);
    }

    public async Task<string> DownloadToJobAsync(string sourceUrl, string expectedType, PublicArticleFetcher fetcher, CancellationToken cancellationToken = default)
    {
        var jobsDirectory = await GetJobsDirectoryAsync(cancellationToken);
        return await fetcher.DownloadMediaAsync(sourceUrl, expectedType, jobsDirectory, cancellationToken);
    }

    public async Task CompleteSuccessfulJobAsync(IReadOnlyList<string> jobPaths, CancellationToken cancellationToken = default)
    {
        if (jobPaths.Count == 0) return;
        try
        {
            var settings = await _settingsStore.GetAsync(cancellationToken);
            var jobsDirectory = await GetJobsDirectoryAsync(cancellationToken);
            var successDirectory = settings.MaterialRetentionPolicy == MaterialRetentionPolicy.DeleteAfterSuccessfulPublish
                ? null
                : await GetSuccessfulDirectoryAsync(cancellationToken);
            await Task.Run(() =>
            {
                switch (settings.MaterialRetentionPolicy)
                {
                    case MaterialRetentionPolicy.DeleteAfterSuccessfulPublish:
                        foreach (var path in jobPaths)
                            DeleteOneFile(path, jobsDirectory);
                        break;
                    case MaterialRetentionPolicy.KeepSevenDays:
                        foreach (var path in jobPaths)
                            MoveOneFile(path, jobsDirectory, successDirectory!);
                        CleanupExpiredSuccessfulFiles(successDirectory!, DateTime.UtcNow.AddDays(-7));
                        break;
                    case MaterialRetentionPolicy.KeepForever:
                        foreach (var path in jobPaths)
                            MoveOneFile(path, jobsDirectory, successDirectory!);
                        break;
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLogger.Instance.Warning($"发布成功后的媒体文件收尾未完成：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }


    /// <summary>
    /// 判断是否为 TG 采集草稿的临时媒体（temp/telegram/日期/频道）。这类文件属于发布成功即清理的
    /// 暂存数据，与素材库原件（library/）和任务副本（jobs/）分开；预览缓存 preview/ 不在清理范围。
    /// </summary>
    public static bool IsTelegramStagingPath(string path, string? root = null)
    {
        try
        {
            var baseDir = root ?? AppContext.BaseDirectory;
            var staging = Path.GetFullPath(Path.Combine(baseDir, "temp", "telegram")) + Path.DirectorySeparatorChar;
            var preview = Path.GetFullPath(Path.Combine(baseDir, "temp", "telegram", "preview")) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(staging, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(preview, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 发布成功后清理 TG 采集暂存媒体。仅“完成任务删除文件”策略下删除，保留策略下不动，
    /// 与任务副本保持同一口径。发布失败时不要调用，暂存文件需保留以便重试。
    /// </summary>
    public async Task DeleteTelegramStagingAsync(IReadOnlyList<string> paths, string? root = null, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        var settings = await _settingsStore.GetAsync(cancellationToken);
        if (settings.MaterialRetentionPolicy != MaterialRetentionPolicy.DeleteAfterSuccessfulPublish) return;
        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    if (IsTelegramStagingPath(path, root) && File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    AppLogger.Instance.Warning($"TG 暂存媒体清理失败：{Path.GetFileName(path)}，{CliDiagnostics.Sanitize(ex.Message)}");
                }
            }
        }, cancellationToken);
    }
    /// <summary>
    /// 清理没有对应素材的 TG 暂存媒体，用于媒体部分下载失败、类型无法识别等未入库场景。
    /// 与“发布成功后清理”共用同一路径判断，只清理 temp/telegram 下的暂存文件，不碰素材库原件。
    /// </summary>
    public async Task DeleteOrphanStagingAsync(IReadOnlyList<string> paths, string? root = null, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    if (IsTelegramStagingPath(path, root) && File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    AppLogger.Instance.Warning($"TG 暂存媒体清理失败：{Path.GetFileName(path)}，{CliDiagnostics.Sanitize(ex.Message)}");
                }
            }
        }, cancellationToken);
    }
    public async Task<bool> IsManagedLibraryPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var root = await GetStorageRootAsync(cancellationToken);
        return IsManagedLibraryPath(path, root);
    }

    private async Task<string> GetJobsDirectoryAsync(CancellationToken cancellationToken)
    {
        var root = await GetStorageRootAsync(cancellationToken);
        var result = await ValidateDirectoryAsync(Path.Combine(root, "jobs"), cancellationToken);
        if (!result.Succeeded) throw new IOException(result.Message);
        return result.FullPath;
    }

    private async Task<string> GetSuccessfulDirectoryAsync(CancellationToken cancellationToken)
    {
        var root = await GetStorageRootAsync(cancellationToken);
        var result = await ValidateDirectoryAsync(Path.Combine(root, "jobs", "successful"), cancellationToken);
        if (!result.Succeeded) throw new IOException(result.Message);
        return result.FullPath;
    }

    private static async Task<string> CopyToUniquePathAsync(string sourcePath, string directory, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath);
        var destination = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
        return destination;
    }

    private static void EnsureLocalSource(string sourcePath, string expectedType, bool allowUnknownSources)
    {
        if (!MaterialMediaValidator.IsLocalPath(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("本地媒体文件不存在。", sourcePath);
        if (!MaterialMediaValidator.IsValidSource(sourcePath, expectedType, allowUnknownSources))
            throw new InvalidDataException($"媒体文件类型与{(expectedType == "image" ? "图片" : "视频")}不匹配。");
    }

    private static bool IsManagedLibraryPath(string path, string root)
    {
        var library = Path.GetFullPath(Path.Combine(root, "library")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(library, StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveOneFile(string sourcePath, string jobsDirectory, string destinationDirectory)
    {
        try
        {
            if (!IsWithinDirectory(sourcePath, jobsDirectory) || !File.Exists(sourcePath)) return;
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(destination)) destination = Path.Combine(destinationDirectory, $"{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}{Path.GetExtension(destination)}");
            File.Move(sourcePath, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLogger.Instance.Warning($"成功任务媒体文件移动失败：{Path.GetFileName(sourcePath)}，{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private static void DeleteOneFile(string path, string jobsDirectory)
    {
        try
        {
            if (IsWithinDirectory(path, jobsDirectory) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLogger.Instance.Warning($"成功任务媒体文件删除失败：{Path.GetFileName(path)}，{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private static void CleanupExpiredSuccessfulFiles(string directory, DateTime cutoffUtc)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoffUtc) DeleteOneFile(file, directory);
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public sealed record StorageDirectoryResult(bool Succeeded, string Message, string FullPath);
