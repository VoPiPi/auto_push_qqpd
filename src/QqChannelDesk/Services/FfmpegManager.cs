using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharpCompress.Archives;

namespace QqChannelDesk.Services;

public sealed class FfmpegManager
{
    private static readonly Regex VersionLine = new(@"\bffmpeg version\s+(\d+(?:\.\d+)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal const string PinnedDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.7z";
    internal const string StableDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z";
    internal const string StableChecksumUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z.sha256";
    internal const string PinnedSha256 = "4705843CCAAF54257C16AD90F3E952ECE33C17DF964ECF7BFDBB0F49C7171077";
    private const long MaximumArchiveBytes = 150L * 1024 * 1024;
    private const long MaximumExecutableBytes = 300L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient SharedSystemHttpClient = CreateHttpClient(useProxy: true);
    private static readonly HttpClient SharedDirectHttpClient = CreateHttpClient(useProxy: false);
    private readonly string _installDirectory;
    private readonly string _applicationDirectory;
    private readonly bool _includePath;
    private readonly SystemSettingsStore? _settingsStore;
    private readonly IReadOnlyList<FfmpegDownloadRoute> _downloadRoutes;
    private readonly TimeSpan _requestTimeout;
    private readonly AppLogger? _logger;
    private readonly Func<string, CancellationToken, Task<FfmpegStatus>> _executableValidator;
    private readonly string _pinnedDownloadUrl;
    private readonly string _stableDownloadUrl;
    private readonly string _stableChecksumUrl;
    private readonly string _pinnedSha256;
    private readonly string _temporaryDirectory;

    public FfmpegManager(
        string? installDirectory = null,
        bool includePath = true,
        string? baseDirectory = null,
        SystemSettingsStore? settingsStore = null)
        : this(
            installDirectory,
            includePath,
            baseDirectory,
            settingsStore,
            SharedSystemHttpClient,
            AppLogger.Instance,
            executableValidator: null,
            pinnedDownloadUrl: PinnedDownloadUrl,
            stableDownloadUrl: StableDownloadUrl,
            stableChecksumUrl: StableChecksumUrl,
            pinnedSha256: PinnedSha256,
            temporaryDirectory: Path.GetTempPath(),
            fallbackHttpClient: SharedDirectHttpClient,
            primaryConnectionName: "Windows 系统网络设置",
            fallbackConnectionName: "直接连接")
    {
    }

    internal FfmpegManager(
        string? installDirectory,
        bool includePath,
        string? baseDirectory,
        SystemSettingsStore? settingsStore,
        HttpClient httpClient,
        AppLogger? logger = null,
        Func<string, CancellationToken, Task<FfmpegStatus>>? executableValidator = null,
        string pinnedDownloadUrl = PinnedDownloadUrl,
        string stableDownloadUrl = StableDownloadUrl,
        string stableChecksumUrl = StableChecksumUrl,
        string pinnedSha256 = PinnedSha256,
        string? temporaryDirectory = null,
        HttpClient? fallbackHttpClient = null,
        TimeSpan? requestTimeout = null,
        string primaryConnectionName = "自定义连接",
        string fallbackConnectionName = "直接连接")
    {
        // FFmpeg 始终部署到“当前程序所在目录”的 tools 子目录。
        // 不再向上扫描源码仓库，否则开发运行时会把文件写回 src\QqChannelDesk\tools，
        // 与发布后“相对 EXE 目录安装”的行为不一致。
        _applicationDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var toolsDirectory = Path.Combine(_applicationDirectory, "tools");
        _installDirectory = installDirectory is null
            ? Path.Combine(toolsDirectory, "ffmpeg", "bin")
            : ValidateInstallDirectory(installDirectory, _applicationDirectory, toolsDirectory);
        _includePath = includePath;
        _settingsStore = settingsStore;
        ArgumentNullException.ThrowIfNull(httpClient);
        _downloadRoutes = fallbackHttpClient is null
            ? [new FfmpegDownloadRoute(httpClient, primaryConnectionName)]
            :
            [
                new FfmpegDownloadRoute(httpClient, primaryConnectionName),
                new FfmpegDownloadRoute(fallbackHttpClient, fallbackConnectionName)
            ];
        _requestTimeout = requestTimeout ?? RequestTimeout;
        _logger = logger;
        _executableValidator = executableValidator ?? ((path, token) => CheckExecutableAsync(
            path,
            FfmpegSource.ProjectTools,
            false,
            token));
        _pinnedDownloadUrl = pinnedDownloadUrl;
        _stableDownloadUrl = stableDownloadUrl;
        _stableChecksumUrl = stableChecksumUrl;
        _pinnedSha256 = pinnedSha256;
        _temporaryDirectory = Path.GetFullPath(temporaryDirectory ?? Path.GetTempPath());
    }

    public string InstallDirectory => _installDirectory;
    public string RelativeInstallDirectory =>
        $".{Path.DirectorySeparatorChar}{Path.GetRelativePath(_applicationDirectory, _installDirectory)}";
    public string ArchiveDirectory => Path.Combine(_applicationDirectory, "tools");

    private static string ValidateInstallDirectory(
        string installDirectory,
        string applicationDirectory,
        string toolsDirectory)
    {
        var fullInstallDirectory = Path.GetFullPath(
            Path.IsPathFullyQualified(installDirectory)
                ? installDirectory
                : Path.Combine(applicationDirectory, installDirectory));
        var fullToolsDirectory = Path.GetFullPath(toolsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var toolsPrefix = fullToolsDirectory + Path.DirectorySeparatorChar;
        if (!fullInstallDirectory.StartsWith(toolsPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("FFmpeg 部署目录必须位于应用目录的 tools 文件夹内。", nameof(installDirectory));
        return fullInstallDirectory;
    }

    private const string ExecutableName = "ffmpeg.exe";

    public static string ValidateExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("FFmpeg 路径不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath)) throw new FileNotFoundException("指定的 FFmpeg 文件不存在。", fullPath);
        if (!string.Equals(Path.GetFileName(fullPath), ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 ffmpeg.exe 文件。", nameof(path));
        return fullPath;
    }

    public string? FindLatestArchive()
    {
        if (!Directory.Exists(ArchiveDirectory)) return null;

        return Directory.EnumerateFiles(ArchiveDirectory)
            .Select(path => new { Path = path, Version = GetArchiveVersion(Path.GetFileName(path)) })
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .ThenBy(item => Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static Version? GetArchiveVersion(string fileName)
    {
        var match = Regex.Match(fileName, @"^ffmpeg-(\d+(?:\.\d+){1,3})(?:[-_.].*)?\.(?:7z|zip)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    public string? FindExecutableDirectory()
    {
        var systemExecutable = _includePath ? FindSystemExecutablePath() : null;
        if (systemExecutable is not null) return Path.GetDirectoryName(systemExecutable);

        var localExecutable = Path.Combine(InstallDirectory, ExecutableName);
        return File.Exists(localExecutable) ? InstallDirectory : null;
    }

    public async Task<FfmpegResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var configuredPathInvalid = false;
        if (_settingsStore is not null)
        {
            var settings = await _settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
            {
                var configuredPath = TryGetConfiguredExecutablePath(settings.FfmpegPath);
                if (configuredPath is not null)
                {
                    return new FfmpegResolution(configuredPath, FfmpegSource.ConfiguredPath, false);
                }

                configuredPathInvalid = true;
            }
        }

        var localExecutable = Path.Combine(InstallDirectory, ExecutableName);
        string? systemPath;
        bool localExecutableExists;
        (systemPath, localExecutableExists) = await Task.Run(() =>
        {
            var path = _includePath ? FindSystemExecutablePath(cancellationToken) : null;
            return (path, File.Exists(localExecutable));
        }, cancellationToken).ConfigureAwait(false);

        if (systemPath is not null)
        {
            return new FfmpegResolution(systemPath, FfmpegSource.SystemPath, configuredPathInvalid);
        }

        return new FfmpegResolution(
            localExecutableExists ? localExecutable : null,
            localExecutableExists ? FfmpegSource.ProjectTools : null,
            configuredPathInvalid);
    }

    public async Task<string?> FindExecutableDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return resolution.ExecutableDirectory;
    }

    public async Task<string?> FindExecutablePathAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return resolution.ExecutablePath;
    }

    public async Task<string> BuildCliPathAsync(string? inheritedPath, CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var selectedDirectory = resolution.ExecutableDirectory;
        var entries = (inheritedPath ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim().Trim('"'))
            .Where(entry => entry.Length > 0)
            .ToList();

        if (selectedDirectory is not null)
        {
            entries.RemoveAll(entry => PathsEqual(entry, selectedDirectory));
            entries.Insert(0, selectedDirectory);
        }

        return string.Join(Path.PathSeparator, entries);
    }

    private static bool PathsEqual(string first, string second)
    {
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? TryGetConfiguredExecutablePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;
        try
        {
            var fullPath = Path.GetFullPath(configuredPath.Trim());
            if (Directory.Exists(fullPath)) fullPath = Path.Combine(fullPath, ExecutableName);
            return File.Exists(fullPath) && string.Equals(Path.GetFileName(fullPath), ExecutableName, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? FindSystemExecutablePath(CancellationToken cancellationToken = default)
    {
        foreach (var directory in GetPathDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = Path.Combine(directory, ExecutableName);
            if (File.Exists(executable)) return executable;
        }

        return null;
    }

    private static IEnumerable<string> GetPathDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathValues = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            AddRegistryPath(pathValues, Registry.CurrentUser, @"Environment");
            AddRegistryPath(pathValues, Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        }

        foreach (var pathValue in pathValues)
        {
            if (string.IsNullOrWhiteSpace(pathValue)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(pathValue);
            foreach (var entry in expanded.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = entry.Trim().Trim('"');
                if (directory.Length > 0 && seen.Add(directory)) yield return directory;
            }
        }
    }

    private static void AddRegistryPath(ICollection<string?> values, RegistryKey root, string subKey)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            values.Add(key?.GetValue("Path") as string);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
        }
    }

    public async Task<FfmpegStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (resolution.ExecutablePath is null)
        {
            var detail = resolution.ConfiguredPathInvalid
                ? "已保存的 FFmpeg 路径不可用，且系统 PATH 和项目 tools 目录中均未找到 ffmpeg"
                : "未找到 ffmpeg，可在线安装或在系统设置中指定路径";
            return new(false, "未检测到", detail, null, null, resolution.ConfiguredPathInvalid);
        }

        return await CheckExecutableAsync(
            resolution.ExecutablePath,
            resolution.Source,
            resolution.ConfiguredPathInvalid,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FfmpegStatus> CheckExecutableAsync(
        string executable,
        FfmpegSource? source,
        bool configuredPathInvalid,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
            {
                return new(false, "异常", "ffmpeg 无法启动", Path.GetDirectoryName(executable), source, configuredPathInvalid);
            }
            var output = await process.StandardOutput.ReadLineAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new(true, "可用", FormatVersionOutput(output), Path.GetDirectoryName(executable), source, configuredPathInvalid)
                : new(false, "异常", "ffmpeg 版本检查失败", Path.GetDirectoryName(executable), source, configuredPathInvalid);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, "异常", $"ffmpeg 启动失败：{ex.Message}", Path.GetDirectoryName(executable), source, configuredPathInvalid);
        }
    }

    public static string FormatVersionOutput(string? output)
    {
        var match = VersionLine.Match(output ?? string.Empty);
        return match.Success ? $"ffmpeg version {match.Groups[1].Value}" : "ffmpeg version unknown";
    }

    public async Task<CliInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var archivePath = FindLatestArchive();
        if (archivePath is null)
            return new CliInstallResult(false, $"在目录 {ArchiveDirectory} 中没有找到命名格式为 ffmpeg-版本号-描述.7z 或 .zip 的压缩包。", null);

        try
        {
            Directory.CreateDirectory(InstallDirectory);
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var archive = ArchiveFactory.Open(archivePath))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                {
                    if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                    var normalized = entry.Key.Replace('\\', '/');
                    var name = Path.GetFileName(normalized);
                    if (!normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(InstallDirectory, name);
                    await using var input = entry.OpenEntryStream();
                    await using var output = File.Create(target);
                    await input.CopyToAsync(output, cancellationToken);
                    extracted.Add(name);
                }
            }

            if (!extracted.Contains("ffmpeg.exe"))
                return new CliInstallResult(false, $"压缩包 {Path.GetFileName(archivePath)} 的 bin 目录中未找到 ffmpeg.exe，未完成部署。", null);

            var status = await CheckExecutableAsync(
                Path.Combine(InstallDirectory, ExecutableName),
                FfmpegSource.ProjectTools,
                false,
                cancellationToken);
            return status.Available
                ? new CliInstallResult(true, $"项目 tools 中的 FFmpeg 已部署：{status.Detail}", null)
                : new CliInstallResult(false, $"项目 tools 中的 FFmpeg 部署后检查失败：{status.Detail}", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CliInstallResult(false, "FFmpeg 部署已取消。", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CliInstallResult(false, $"FFmpeg 部署失败：{ex.Message}", null);
        }
    }

    public async Task<CliInstallResult> InstallOnlineAsync(
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var temporaryArchivePath = Path.Combine(
            _temporaryDirectory, $"QqChannelDesk-ffmpeg-{Guid.NewGuid():N}.7z");
        var temporaryExecutablePath = Path.Combine(
            InstallDirectory, $"ffmpeg.download-{Guid.NewGuid():N}.exe");
        var backupExecutablePath = Path.Combine(
            InstallDirectory, $"ffmpeg.backup-{Guid.NewGuid():N}.exe");
        var installedExecutablePath = Path.Combine(InstallDirectory, ExecutableName);
        var sourceUrl = _pinnedDownloadUrl;
        var finalReplacementStarted = false;

        try
        {
            Directory.CreateDirectory(_temporaryDirectory);
            Directory.CreateDirectory(InstallDirectory);
            ReportProgress(progress, 2, "正在准备 FFmpeg 在线安装…",
                $"安装目录：{InstallDirectory}", sourceUrl);

            var sources = new[]
            {
                new FfmpegDownloadSource(_pinnedDownloadUrl, _pinnedSha256),
                new FfmpegDownloadSource(_stableDownloadUrl, null)
            };

            Exception? lastFailure = null;
            for (var index = 0; index < sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                sourceUrl = source.Url;
                try
                {
                    var expectedHash = source.ExpectedSha256;
                    if (expectedHash is null)
                    {
                        ReportProgress(progress, 5, "正在读取备用源校验值…",
                            _stableChecksumUrl, sourceUrl);
                        LogInfo($"FFmpeg 在线安装正在请求校验值：{_stableChecksumUrl}");
                        expectedHash = await DownloadChecksumAsync(progress, cancellationToken).ConfigureAwait(false);
                    }

                    await DownloadArchiveFromAvailableRoutesAsync(
                        source.Url,
                        temporaryArchivePath,
                        progress,
                        index == 0 ? 7 : 9,
                        cancellationToken).ConfigureAwait(false);

                    ReportProgress(progress, 78, "下载完成，正在校验安装包…",
                        $"SHA-256：{expectedHash}", source.Url);
                    var actualHash = await ComputeSha256Async(temporaryArchivePath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"SHA-256 校验失败，期望 {expectedHash}，实际 {actualHash}。");

                    LogInfo($"FFmpeg 下载完成并通过 SHA-256 校验：{source.Url}");
                    lastFailure = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsDownloadFailure(ex))
                {
                    lastFailure = ex;
                    LogWarning($"FFmpeg 下载源失败：{source.Url}；{CliDiagnostics.Sanitize(ex.Message)}");
                    DeleteFileIfExists(temporaryArchivePath);
                    if (index + 1 < sources.Length)
                    {
                        var nextUrl = sources[index + 1].Url;
                        ReportProgress(progress, 8, "当前下载源不可用，正在切换备用源…",
                            $"失败源：{source.Url}\n备用源：{nextUrl}", nextUrl);
                    }
                }
            }

            if (lastFailure is not null)
                return new CliInstallResult(false,
                    $"FFmpeg 在线下载失败：{CliDiagnostics.Sanitize(lastFailure.Message)}", sourceUrl);

            ReportProgress(progress, 84, "校验通过，正在解压 ffmpeg.exe…",
                "仅提取程序实际需要的 ffmpeg.exe。", sourceUrl);
            await ExtractExecutableAsync(
                temporaryArchivePath,
                temporaryExecutablePath,
                progress,
                sourceUrl,
                cancellationToken).ConfigureAwait(false);

            ReportProgress(progress, 92, "正在验证下载的 FFmpeg…",
                temporaryExecutablePath, sourceUrl);
            var downloadedStatus = await _executableValidator(temporaryExecutablePath, cancellationToken).ConfigureAwait(false);
            if (!downloadedStatus.Available)
                return new CliInstallResult(false,
                    $"下载的 FFmpeg 验证失败：{downloadedStatus.Detail}", sourceUrl);

            ReportProgress(progress, 96, "正在完成 FFmpeg 安装…",
                installedExecutablePath, sourceUrl, canCancel: false);
            cancellationToken.ThrowIfCancellationRequested();
            finalReplacementStarted = true;
            try
            {
                if (File.Exists(installedExecutablePath))
                    File.Move(installedExecutablePath, backupExecutablePath);
                File.Move(temporaryExecutablePath, installedExecutablePath);
            }
            catch
            {
                if (!File.Exists(installedExecutablePath) && File.Exists(backupExecutablePath))
                    File.Move(backupExecutablePath, installedExecutablePath);
                throw;
            }

            FfmpegStatus installedStatus;
            try
            {
                installedStatus = await _executableValidator(installedExecutablePath, CancellationToken.None).ConfigureAwait(false);
                if (!installedStatus.Available)
                {
                    RestorePreviousExecutable(installedExecutablePath, backupExecutablePath);
                    return new CliInstallResult(false,
                        $"FFmpeg 安装后验证失败，已恢复原版本：{installedStatus.Detail}", sourceUrl);
                }
            }
            catch
            {
                RestorePreviousExecutable(installedExecutablePath, backupExecutablePath);
                throw;
            }

            DeleteFileIfExists(backupExecutablePath);
            ReportProgress(progress, 100, "FFmpeg 安装完成",
                $"{installedStatus.Detail}\n位置：{installedExecutablePath}", sourceUrl, canCancel: false);
            LogInfo($"FFmpeg 在线安装完成：{installedStatus.Detail}；来源：{sourceUrl}");
            return new CliInstallResult(true,
                $"FFmpeg 已安装：{installedStatus.Detail}", sourceUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogInfo("用户取消了 FFmpeg 在线安装。");
            return new CliInstallResult(false, "FFmpeg 在线安装已取消。", sourceUrl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SecurityException or InvalidDataException)
        {
            var message = CliDiagnostics.Sanitize(ex.Message);
            LogWarning($"FFmpeg 在线安装失败：{message}；来源：{sourceUrl}");
            return new CliInstallResult(false, $"FFmpeg 在线安装失败：{message}", sourceUrl);
        }
        finally
        {
            DeleteFileIfExists(temporaryArchivePath);
            DeleteFileIfExists(temporaryExecutablePath);
            if (!finalReplacementStarted || File.Exists(installedExecutablePath))
                DeleteFileIfExists(backupExecutablePath);
        }
    }

    private async Task DownloadArchiveFromAvailableRoutesAsync(
        string url,
        string targetPath,
        IProgress<FfmpegInstallProgress>? progress,
        double connectingPercent,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var routeIndex = 0; routeIndex < _downloadRoutes.Count; routeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = _downloadRoutes[routeIndex];
            var detail = BuildConnectionDetail(route.ConnectionName, url,
                $"若该连接无响应，将在 {_requestTimeout.TotalSeconds:0} 秒后自动尝试其他连接方式。");
            ReportProgress(progress, connectingPercent, "正在连接 FFmpeg 下载源…", detail, url);
            LogInfo($"FFmpeg 在线安装正在请求：{url}；连接方式：{route.ConnectionName}；连接超时：{_requestTimeout.TotalSeconds:0} 秒");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await DownloadArchiveAsync(route, url, targetPath, progress, cancellationToken).ConfigureAwait(false);
                LogInfo($"FFmpeg 下载连接成功：{url}；连接方式：{route.ConnectionName}；耗时：{stopwatch.Elapsed.TotalSeconds:0.0} 秒");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsDownloadFailure(ex))
            {
                lastFailure = ex;
                DeleteFileIfExists(targetPath);
                var failureDetail = BuildConnectionFailureDetail(route.ConnectionName, url, stopwatch.Elapsed, ex);
                LogWarning($"FFmpeg 下载连接失败：{failureDetail.Replace(Environment.NewLine, "；")}");
                if (routeIndex + 1 < _downloadRoutes.Count)
                {
                    var nextRoute = _downloadRoutes[routeIndex + 1];
                    ReportProgress(progress, connectingPercent, "当前连接方式不可用，正在切换…",
                        $"{failureDetail}\n即将尝试：{nextRoute.ConnectionName}", url);
                }
            }
        }

        throw lastFailure ?? new HttpRequestException($"没有可用的 FFmpeg 下载连接：{url}");
    }

    private async Task DownloadArchiveAsync(
        FfmpegDownloadRoute route,
        string url,
        string targetPath,
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(DownloadTimeout);
        try
        {
            var responseStopwatch = Stopwatch.StartNew();
            using var response = await GetResponseWithHardTimeoutAsync(
                route.Client, url, _requestTimeout, totalTimeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaximumArchiveBytes)
                throw new InvalidDataException($"下载文件大小 {FormatBytes(contentLength.Value)} 超过 150 MB 安全限制。");

            var responseDetail =
                $"已收到下载响应：HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n" +
                $"连接方式：{route.ConnectionName}\n" +
                $"内容大小：{(contentLength is > 0 ? FormatBytes(contentLength.Value) : "服务器未提供")}\n" +
                $"耗时：{responseStopwatch.Elapsed.TotalSeconds:0.0} 秒\n" +
                $"请求地址：{url}";
            ReportProgress(progress, 10, "FFmpeg 下载源已连接", responseDetail, url);
            LogInfo(responseDetail.Replace(Environment.NewLine, "；"));

            await using var input = await response.Content.ReadAsStreamAsync(totalTimeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long downloaded = 0;
            double lastReportedPercent = -1;
            while (true)
            {
                using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token);
                idleTimeout.CancelAfter(DownloadIdleTimeout);
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, idleTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                         !totalTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"下载源连续 {DownloadIdleTimeout.TotalSeconds:0} 秒没有返回数据：{url}");
                }

                if (read == 0) break;
                downloaded += read;
                if (downloaded > MaximumArchiveBytes)
                    throw new InvalidDataException("下载内容超过 150 MB 安全限制。");
                await output.WriteAsync(buffer.AsMemory(0, read), totalTimeout.Token).ConfigureAwait(false);

                var percent = contentLength is > 0
                    ? 10 + downloaded * 64d / contentLength.Value
                    : 42;
                var boundedPercent = Math.Min(percent, 74);
                if (contentLength is not > 0 || boundedPercent >= lastReportedPercent + 1 || downloaded == contentLength)
                {
                    var detail = contentLength is > 0
                        ? $"{FormatBytes(downloaded)} / {FormatBytes(contentLength.Value)}\n连接方式：{route.ConnectionName}\n{url}"
                        : $"已下载 {FormatBytes(downloaded)}（服务器未提供总大小）\n连接方式：{route.ConnectionName}\n{url}";
                    ReportProgress(progress, boundedPercent, "正在下载 FFmpeg…", detail, url);
                    lastReportedPercent = boundedPercent;
                }
            }

            await output.FlushAsync(totalTimeout.Token).ConfigureAwait(false);
            if (downloaded == 0)
                throw new InvalidDataException("下载源返回了空文件。");
            if (contentLength is > 0 && downloaded != contentLength.Value)
                throw new EndOfStreamException($"下载未完成，预期 {FormatBytes(contentLength.Value)}，实际 {FormatBytes(downloaded)}。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"下载 FFmpeg 超时：{url}");
        }
    }

    private async Task<string> DownloadChecksumAsync(
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var routeIndex = 0; routeIndex < _downloadRoutes.Count; routeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = _downloadRoutes[routeIndex];
            var stopwatch = Stopwatch.StartNew();
            ReportProgress(progress, 5, "正在读取备用源校验值…",
                BuildConnectionDetail(route.ConnectionName, _stableChecksumUrl,
                    $"连接超时：{_requestTimeout.TotalSeconds:0} 秒"), _stableChecksumUrl);
            LogInfo($"FFmpeg 校验值请求：{_stableChecksumUrl}；连接方式：{route.ConnectionName}");
            try
            {
                var result = await RunHttpOperationWithHardTimeoutAsync(
                    async token =>
                    {
                        using var response = await route.Client.GetAsync(
                            _stableChecksumUrl,
                            HttpCompletionOption.ResponseContentRead,
                            token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                        return ((int)response.StatusCode, text);
                    },
                    _stableChecksumUrl,
                    _requestTimeout,
                    cancellationToken).ConfigureAwait(false);
                var match = Regex.Match(result.text, @"(?i)\b([a-f0-9]{64})\b");
                if (!match.Success)
                    throw new InvalidDataException("备用源没有返回有效的 SHA-256 校验值。");
                LogInfo($"FFmpeg 校验值响应：HTTP {result.Item1}；连接方式：{route.ConnectionName}；耗时：{stopwatch.Elapsed.TotalSeconds:0.0} 秒");
                return match.Groups[1].Value.ToUpperInvariant();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsDownloadFailure(ex))
            {
                lastFailure = ex;
                var failureDetail = BuildConnectionFailureDetail(
                    route.ConnectionName, _stableChecksumUrl, stopwatch.Elapsed, ex);
                LogWarning($"FFmpeg 校验值连接失败：{failureDetail.Replace(Environment.NewLine, "；")}");
                if (routeIndex + 1 < _downloadRoutes.Count)
                {
                    ReportProgress(progress, 5, "校验值连接失败，正在切换连接方式…",
                        $"{failureDetail}\n即将尝试：{_downloadRoutes[routeIndex + 1].ConnectionName}", _stableChecksumUrl);
                }
            }
        }

        throw lastFailure ?? new HttpRequestException($"无法读取 FFmpeg 校验值：{_stableChecksumUrl}");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task ExtractExecutableAsync(
        string archivePath,
        string targetPath,
        IProgress<FfmpegInstallProgress>? progress,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        await Task.Run(async () =>
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var entry = archive.Entries.FirstOrDefault(item =>
            {
                if (item.IsDirectory || string.IsNullOrWhiteSpace(item.Key)) return false;
                var normalized = item.Key.Replace('\\', '/');
                return normalized.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase);
            });
            if (entry is null)
                throw new InvalidDataException("FFmpeg 压缩包的 bin 目录中没有找到 ffmpeg.exe。");
            if (entry.Size > MaximumExecutableBytes)
                throw new InvalidDataException("压缩包中的 ffmpeg.exe 超过 300 MB 安全限制。");

            await using var input = entry.OpenEntryStream();
            await using var output = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long extracted = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                extracted += read;
                if (extracted > MaximumExecutableBytes)
                    throw new InvalidDataException("解压后的 ffmpeg.exe 超过 300 MB 安全限制。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                var percent = entry.Size > 0 ? 84 + extracted * 7d / entry.Size : 88;
                ReportProgress(progress, Math.Min(percent, 91), "正在解压 ffmpeg.exe…",
                    entry.Size > 0
                        ? $"{FormatBytes(extracted)} / {FormatBytes(entry.Size)}"
                        : $"已解压 {FormatBytes(extracted)}",
                    sourceUrl);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (extracted == 0)
                throw new InvalidDataException("压缩包中的 ffmpeg.exe 是空文件。");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsDownloadFailure(Exception exception) => exception is
        HttpRequestException or IOException or InvalidDataException or TimeoutException or TaskCanceledException;

    private static Task<HttpResponseMessage> GetResponseWithHardTimeoutAsync(
        HttpClient client,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunHttpOperationWithHardTimeoutAsync(
            token => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token),
            url,
            timeout,
            cancellationToken);

    private static async Task<T> RunHttpOperationWithHardTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestTask = Task.Run(() => operation(requestCts.Token), CancellationToken.None);
        try
        {
            return await requestTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            requestCts.Cancel();
            ObserveFault(requestTask);
            throw new TimeoutException($"请求 {url} 超过 {timeout.TotalSeconds:0} 秒未响应。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            requestCts.Cancel();
            ObserveFault(requestTask);
            throw;
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string BuildConnectionDetail(string connectionName, string url, string? note = null)
    {
        var detail = $"连接方式：{connectionName}\n请求地址：{url}";
        return string.IsNullOrWhiteSpace(note) ? detail : $"{detail}\n{note}";
    }

    private static string BuildConnectionFailureDetail(
        string connectionName,
        string url,
        TimeSpan elapsed,
        Exception exception) =>
        $"连接方式：{connectionName}\n" +
        $"请求地址：{url}\n" +
        $"耗时：{elapsed.TotalSeconds:0.0} 秒\n" +
        $"异常类型：{exception.GetType().Name}\n" +
        $"失败原因：{CliDiagnostics.Sanitize(exception.Message)}";

    private static HttpClient CreateHttpClient(bool useProxy)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseProxy = useProxy
        };
        if (useProxy)
            handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QqChannelDesk/1.0");
        return client;
    }

    private static void ReportProgress(
        IProgress<FfmpegInstallProgress>? progress,
        double percent,
        string message,
        string? detail,
        string? sourceUrl,
        bool canCancel = true) =>
        progress?.Report(new FfmpegInstallProgress(
            Math.Clamp(percent, 0, 100), message, detail, sourceUrl, canCancel));

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.00} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RestorePreviousExecutable(string installedPath, string backupPath)
    {
        DeleteFileIfExists(installedPath);
        if (File.Exists(backupPath))
            File.Move(backupPath, installedPath);
    }

    // 下载/校验/连接细节只在调试模式下展示，安装结论由 MainWindow 依据 CliInstallResult 另行输出。
    private void LogInfo(string message) => _logger?.Debug(message);
    private void LogWarning(string message) => _logger?.Debug(message);

    private sealed record FfmpegDownloadSource(string Url, string? ExpectedSha256);
    private sealed record FfmpegDownloadRoute(HttpClient Client, string ConnectionName);
}

public enum FfmpegSource
{
    ConfiguredPath,
    SystemPath,
    ProjectTools
}

public sealed record FfmpegResolution(
    string? ExecutablePath,
    FfmpegSource? Source,
    bool ConfiguredPathInvalid)
{
    public string? ExecutableDirectory => ExecutablePath is null ? null : Path.GetDirectoryName(ExecutablePath);

    public string SourceLabel => Source switch
    {
        FfmpegSource.ConfiguredPath => "手动设置",
        FfmpegSource.SystemPath => "系统 PATH",
        FfmpegSource.ProjectTools => "项目 tools 后备",
        _ => "未找到"
    };
}

public sealed record FfmpegStatus(
    bool Available,
    string StateLabel,
    string Detail,
    string? ExecutableDirectory,
    FfmpegSource? Source = null,
    bool ConfiguredPathInvalid = false);

public sealed record FfmpegInstallProgress(
    double Percent,
    string Message,
    string? Detail = null,
    string? SourceUrl = null,
    bool CanCancel = true);
