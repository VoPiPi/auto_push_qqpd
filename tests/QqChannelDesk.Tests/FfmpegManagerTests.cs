using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class FfmpegManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"QqChannelFfmpegTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CheckAsync_ReportsMissingExecutable()
    {
        var manager = new FfmpegManager(Path.Combine(_directory, "bin"), includePath: false);

        var result = await manager.CheckAsync();

        Assert.False(result.Available);
        Assert.Contains("未找到 ffmpeg", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatVersionOutput_KeepsOnlyNameAndNumericVersion()
    {
        var detail = FfmpegManager.FormatVersionOutput(
            "ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers");

        Assert.Equal("ffmpeg version 9.0.2", detail);
    }

    [Fact]
    public void FormatVersionOutput_UsesFallbackForUnrecognizedOutput()
    {
        var detail = FfmpegManager.FormatVersionOutput("unexpected output");

        Assert.Equal("ffmpeg version unknown", detail);
    }

    [Fact]
    public void FindExecutableDirectory_PrefersBundledExecutable()
    {
        var binDirectory = Path.Combine(_directory, "bin");
        Directory.CreateDirectory(binDirectory);
        var executable = Path.Combine(binDirectory, "ffmpeg.exe");
        File.WriteAllBytes(executable, []);

        var manager = new FfmpegManager(binDirectory, includePath: false);

        Assert.Equal(binDirectory, manager.FindExecutableDirectory());
    }

    [Fact]
    public void FindExecutableDirectory_FindsExecutableFromPath()
    {
        var binDirectory = Path.Combine(_directory, "path-bin");
        Directory.CreateDirectory(binDirectory);
        File.WriteAllBytes(Path.Combine(binDirectory, "ffmpeg.exe"), []);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", binDirectory);
            var manager = new FfmpegManager(Path.Combine(_directory, "app-bin"));

            Assert.Equal(binDirectory, manager.FindExecutableDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void Constructor_UsesProjectDirectoryInsteadOfBuildOutputDirectory()
    {
        var projectDirectory = Path.Combine(_directory, "src", "QqChannelDesk");
        var buildDirectory = Path.Combine(projectDirectory, "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "QqChannelDesk.csproj"), "");

        var manager = new FfmpegManager(includePath: false, baseDirectory: buildDirectory);

        Assert.Equal(Path.Combine(projectDirectory, "tools", "ffmpeg", "bin"), manager.InstallDirectory);
    }

    [Fact]
    public void Constructor_UsesProjectToolsFromRepositoryRootBuildOutput()
    {
        var projectDirectory = Path.Combine(_directory, "src", "QqChannelDesk");
        var buildDirectory = Path.Combine(_directory, "artifacts", "verify", "app");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "QqChannelDesk.csproj"), "");

        var manager = new FfmpegManager(includePath: false, baseDirectory: buildDirectory);

        Assert.Equal(Path.Combine(projectDirectory, "tools"), manager.ArchiveDirectory);
    }

    [Fact]
    public void FindLatestArchive_SelectsHighestVersionAndIgnoresGitBuilds()
    {
        var toolsDirectory = Path.Combine(_directory, "app", "tools");
        Directory.CreateDirectory(toolsDirectory);
        var oldArchive = Path.Combine(toolsDirectory, "ffmpeg-8.1.2-essentials_build.7z");
        var latestArchive = Path.Combine(toolsDirectory, "ffmpeg-9.0.2-essentials_build.7z");
        var gitArchive = Path.Combine(toolsDirectory, "ffmpeg-git-essentials.7z");
        File.WriteAllBytes(oldArchive, []);
        File.WriteAllBytes(latestArchive, []);
        File.WriteAllBytes(gitArchive, []);

        var manager = new FfmpegManager(installDirectory: Path.Combine(_directory, "install"), baseDirectory: Path.Combine(_directory, "app"));

        Assert.Equal(latestArchive, manager.FindLatestArchive());
    }

    public void Dispose()
    {
        var executable = Path.Combine(_directory, "bin", "ffmpeg.exe");
        if (File.Exists(executable)) File.Delete(executable);
        var pathExecutable = Path.Combine(_directory, "path-bin", "ffmpeg.exe");
        if (File.Exists(pathExecutable)) File.Delete(pathExecutable);

        var toolsDirectory = Path.Combine(_directory, "app", "tools");
        foreach (var archive in new[] { "ffmpeg-8.1.2-essentials_build.7z", "ffmpeg-9.0.2-essentials_build.7z", "ffmpeg-git-essentials.7z" })
        {
            var archivePath = Path.Combine(toolsDirectory, archive);
            if (File.Exists(archivePath)) File.Delete(archivePath);
        }

        var projectDirectory = Path.Combine(_directory, "src", "QqChannelDesk");
        var projectFile = Path.Combine(projectDirectory, "QqChannelDesk.csproj");
        if (File.Exists(projectFile)) File.Delete(projectFile);
        RemoveDirectoryIfEmpty(projectDirectory);

        RemoveDirectoryIfEmpty(Path.Combine(projectDirectory, "bin", "Debug", "net8.0-windows"));
        RemoveDirectoryIfEmpty(Path.Combine(projectDirectory, "bin", "Debug"));
        RemoveDirectoryIfEmpty(Path.Combine(projectDirectory, "bin"));
        RemoveDirectoryIfEmpty(projectDirectory);
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "src"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "bin"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "path-bin"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "src"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "artifacts", "verify", "app"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "artifacts", "verify"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "artifacts"));
        RemoveDirectoryIfEmpty(toolsDirectory);
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "app"));
        RemoveDirectoryIfEmpty(_directory);
    }

    private static void RemoveDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }
}
