using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharpCompress.Archives;

namespace QqChannelDesk.Services;

public sealed class FfmpegManager
{
    private static readonly Regex VersionLine = new(@"\bffmpeg version\s+(\d+(?:\.\d+)+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string _installDirectory;
    private readonly string _applicationDirectory;
    private readonly bool _includePath;
    private readonly SystemSettingsStore? _settingsStore;

    public FfmpegManager(
        string? installDirectory = null,
        bool includePath = true,
        string? baseDirectory = null,
        SystemSettingsStore? settingsStore = null)
    {
        _applicationDirectory = ResolveApplicationDirectory(baseDirectory ?? AppContext.BaseDirectory);
        var toolsDirectory = Path.Combine(_applicationDirectory, "tools");
        _installDirectory = installDirectory is null
            ? Path.Combine(toolsDirectory, "ffmpeg", "bin")
            : ValidateInstallDirectory(installDirectory, toolsDirectory);
        _includePath = includePath;
        _settingsStore = settingsStore;
    }

    public string InstallDirectory => _installDirectory;
    public string ArchiveDirectory => Path.Combine(_applicationDirectory, "tools");

    private static string ValidateInstallDirectory(string installDirectory, string toolsDirectory)
    {
        var fullInstallDirectory = Path.GetFullPath(installDirectory);
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

    private static string ResolveApplicationDirectory(string baseDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QqChannelDesk.csproj")))
                return current.FullName;

            var projectDirectory = Path.Combine(current.FullName, "src", "QqChannelDesk");
            if (File.Exists(Path.Combine(projectDirectory, "QqChannelDesk.csproj")))
                return projectDirectory;

            current = current.Parent;
        }

        return Path.GetFullPath(baseDirectory);
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
                : "未找到 ffmpeg，可在系统设置中指定路径或点击部署";
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
