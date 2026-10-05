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

    public FfmpegManager(string? installDirectory = null, bool includePath = true, string? baseDirectory = null)
    {
        _applicationDirectory = ResolveApplicationDirectory(baseDirectory ?? AppContext.BaseDirectory);
        _installDirectory = installDirectory ?? Path.Combine(_applicationDirectory, "tools", "ffmpeg", "bin");
        _includePath = includePath;
    }

    public string InstallDirectory => _installDirectory;
    public string ArchiveDirectory => Path.Combine(_applicationDirectory, "tools");

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
        var localExecutable = Path.Combine(InstallDirectory, "ffmpeg.exe");
        if (File.Exists(localExecutable)) return InstallDirectory;

        if (!_includePath) return null;
        foreach (var directory in GetPathDirectories())
        {
            if (File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")))
                return directory;
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
        var directory = FindExecutableDirectory();
        if (directory is null) return new(false, "未检测到", "未找到 ffmpeg，可点击部署", null);

        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
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
            if (!process.Start()) return new(false, "异常", "ffmpeg 无法启动", directory);
            var output = await process.StandardOutput.ReadLineAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new(true, "可用", FormatVersionOutput(output), directory)
                : new(false, "异常", "ffmpeg 版本检查失败", directory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, "异常", $"ffmpeg 启动失败：{ex.Message}", directory);
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

            var status = await CheckAsync(cancellationToken);
            return status.Available
                ? new CliInstallResult(true, status.Detail, null)
                : new CliInstallResult(false, status.Detail, null);
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

public sealed record FfmpegStatus(bool Available, string StateLabel, string Detail, string? ExecutableDirectory);
