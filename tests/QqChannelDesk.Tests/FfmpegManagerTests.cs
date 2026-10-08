using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

[Collection("Process environment")]
public sealed class FfmpegManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"QqChannelFfmpegTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CheckAsync_ReportsMissingExecutable()
    {
        var manager = CreateManager(includePath: false);

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
        var binDirectory = ProjectBinDirectory;
        Directory.CreateDirectory(binDirectory);
        var executable = Path.Combine(binDirectory, "ffmpeg.exe");
        File.WriteAllBytes(executable, []);

        var manager = CreateManager(includePath: false);

        Assert.Equal(binDirectory, manager.FindExecutableDirectory());
    }

    [Fact]
    public void FindExecutableDirectory_FindsExecutableFromPath()
    {
        var binDirectory = Path.Combine(_directory, "path-bin");
        Directory.CreateDirectory(binDirectory);
        File.WriteAllBytes(Path.Combine(binDirectory, "ffmpeg.exe"), []);
        Directory.CreateDirectory(ProjectBinDirectory);
        File.WriteAllBytes(Path.Combine(ProjectBinDirectory, "ffmpeg.exe"), []);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", binDirectory);
            var manager = CreateManager();

            Assert.Equal(binDirectory, manager.FindExecutableDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task ResolveAsync_PrefersConfiguredPathOverSystemPath()
    {
        var configuredDirectory = Path.Combine(_directory, "configured");
        Directory.CreateDirectory(configuredDirectory);
        var configuredPath = Path.Combine(configuredDirectory, "ffmpeg.exe");
        File.WriteAllBytes(configuredPath, []);
        var settings = new SystemSettingsStore(Path.Combine(_directory, "configured.db"));
        await settings.SaveAsync(new AppSettings(FfmpegPath: configuredPath));

        var manager = CreateManager(settingsStore: settings);
        var resolution = await manager.ResolveAsync();

        Assert.Equal(Path.GetFullPath(configuredPath), resolution.ExecutablePath);
        Assert.Equal(FfmpegSource.ConfiguredPath, resolution.Source);
        Assert.False(resolution.ConfiguredPathInvalid);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToProjectToolsWhenConfiguredPathIsMissing()
    {
        Directory.CreateDirectory(ProjectBinDirectory);
        var projectPath = Path.Combine(ProjectBinDirectory, "ffmpeg.exe");
        File.WriteAllBytes(projectPath, []);
        var settings = new SystemSettingsStore(Path.Combine(_directory, "invalid-config.db"));
        await settings.SaveAsync(new AppSettings(FfmpegPath: Path.Combine(_directory, "missing", "ffmpeg.exe")));

        var resolution = await CreateManager(includePath: false, settingsStore: settings).ResolveAsync();

        Assert.Equal(Path.GetFullPath(projectPath), resolution.ExecutablePath);
        Assert.Equal(FfmpegSource.ProjectTools, resolution.Source);
        Assert.True(resolution.ConfiguredPathInvalid);
    }

    [Fact]
    public async Task BuildCliPath_PutsSelectedDirectoryFirstAndRemovesDuplicate()
    {
        Directory.CreateDirectory(ProjectBinDirectory);
        File.WriteAllBytes(Path.Combine(ProjectBinDirectory, "ffmpeg.exe"), []);
        var inherited = string.Join(Path.PathSeparator, ["C:\\Windows", ProjectBinDirectory, "C:\\Tools", ProjectBinDirectory]);

        var path = await CreateManager(includePath: false).BuildCliPathAsync(inherited);
        var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Path.GetFullPath(ProjectBinDirectory), Path.GetFullPath(entries[0]));
        Assert.Equal(1, entries.Count(entry => string.Equals(Path.GetFullPath(entry), Path.GetFullPath(ProjectBinDirectory), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Constructor_RejectsInstallDirectoryOutsideTools()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new FfmpegManager(Path.Combine(_directory, "outside"), baseDirectory: ApplicationDirectory));

        Assert.Contains("tools", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        var manager = CreateManager(installDirectory: Path.Combine(ApplicationDirectory, "tools", "ffmpeg", "bin"));

        Assert.Equal(latestArchive, manager.FindLatestArchive());
    }

    public void Dispose()
    {
        var executable = Path.Combine(ProjectBinDirectory, "ffmpeg.exe");
        if (File.Exists(executable)) File.Delete(executable);
        var pathExecutable = Path.Combine(_directory, "path-bin", "ffmpeg.exe");
        if (File.Exists(pathExecutable)) File.Delete(pathExecutable);
        var configuredExecutable = Path.Combine(_directory, "configured", "ffmpeg.exe");
        if (File.Exists(configuredExecutable)) File.Delete(configuredExecutable);

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

        foreach (var database in new[] { "configured.db", "invalid-config.db" })
        {
            var databasePath = Path.Combine(_directory, database);
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }

        RemoveDirectoryIfEmpty(Path.Combine(_directory, "configured"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "missing"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "app", "tools", "ffmpeg", "bin"));
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "app", "tools", "ffmpeg"));

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

    private string ApplicationDirectory => Path.Combine(_directory, "app");
    private string ProjectBinDirectory => Path.Combine(ApplicationDirectory, "tools", "ffmpeg", "bin");

    private FfmpegManager CreateManager(
        bool includePath = true,
        SystemSettingsStore? settingsStore = null,
        string? installDirectory = null) =>
        new(
            installDirectory ?? ProjectBinDirectory,
            includePath,
            ApplicationDirectory,
            settingsStore);

    private static void RemoveDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }
}

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollectionDefinition
{
}
