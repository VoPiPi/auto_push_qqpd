using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
    public void Constructor_UsesApplicationDirectoryInsteadOfScanningSourceTree()
    {
        var projectDirectory = Path.Combine(_directory, "src", "QqChannelDesk");
        var buildDirectory = Path.Combine(projectDirectory, "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "QqChannelDesk.csproj"), "");

        var manager = new FfmpegManager(includePath: false, baseDirectory: buildDirectory);

        Assert.Equal(Path.Combine(buildDirectory, "tools", "ffmpeg", "bin"), manager.InstallDirectory);
        Assert.Equal(@".\tools\ffmpeg\bin", manager.RelativeInstallDirectory);
    }

    [Fact]
    public void Constructor_UsesToolsRelativeToRepositoryBuildOutput()
    {
        var projectDirectory = Path.Combine(_directory, "src", "QqChannelDesk");
        var buildDirectory = Path.Combine(_directory, "artifacts", "verify", "app");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(buildDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "QqChannelDesk.csproj"), "");

        var manager = new FfmpegManager(includePath: false, baseDirectory: buildDirectory);

        Assert.Equal(Path.Combine(buildDirectory, "tools"), manager.ArchiveDirectory);
    }

    [Fact]
    public void Constructor_ResolvesRelativeInstallDirectoryFromApplicationDirectory()
    {
        var applicationDirectory = Path.Combine(_directory, "portable-app");
        Directory.CreateDirectory(applicationDirectory);

        var manager = new FfmpegManager(
            Path.Combine("tools", "custom-ffmpeg", "bin"),
            includePath: false,
            baseDirectory: applicationDirectory);

        Assert.Equal(
            Path.Combine(applicationDirectory, "tools", "custom-ffmpeg", "bin"),
            manager.InstallDirectory);
        Assert.Equal(@".\tools\custom-ffmpeg\bin", manager.RelativeInstallDirectory);
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

    [Fact]
    public async Task InstallOnlineAsync_DownloadsReportsProgressAndInstallsExecutable()
    {
        var archive = CreateFfmpegArchive("fake-ffmpeg");
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var handler = new RouteHandler(request =>
            request.RequestUri!.AbsoluteUri == "https://test.local/pinned.7z"
                ? BytesResponse(archive)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var progress = new ProgressCollector();
        var manager = CreateOnlineManager(handler, hash);

        var result = await manager.InstallOnlineAsync(progress);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("fake-ffmpeg", await File.ReadAllTextAsync(Path.Combine(ProjectBinDirectory, "ffmpeg.exe")));
        Assert.Contains(progress.Items, item => item.Message.Contains("正在下载", StringComparison.Ordinal));
        Assert.Contains(progress.Items, item => item.Percent >= 100 && item.SourceUrl == "https://test.local/pinned.7z");
        Assert.DoesNotContain(Directory.EnumerateFiles(ProjectBinDirectory), path =>
            Path.GetFileName(path).StartsWith("ffmpeg.download-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("ffmpeg.backup-", StringComparison.OrdinalIgnoreCase));
        AssertNoTemporaryArchives();
    }

    [Fact]
    public async Task InstallOnlineAsync_FallsBackToStableAndVerifiesPublishedChecksum()
    {
        var badArchive = CreateFfmpegArchive("bad-source");
        var stableArchive = CreateFfmpegArchive("stable-ffmpeg");
        var stableHash = Convert.ToHexString(SHA256.HashData(stableArchive));
        var handler = new RouteHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://test.local/pinned.7z" => BytesResponse(badArchive),
            "https://test.local/stable.sha256" => TextResponse(stableHash.ToLowerInvariant()),
            "https://test.local/stable.7z" => BytesResponse(stableArchive),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var progress = new ProgressCollector();
        var manager = CreateOnlineManager(handler, new string('0', 64));

        var result = await manager.InstallOnlineAsync(progress);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("https://test.local/stable.7z", result.Output);
        Assert.Equal("stable-ffmpeg", await File.ReadAllTextAsync(Path.Combine(ProjectBinDirectory, "ffmpeg.exe")));
        Assert.Contains(progress.Items, item => item.Message.Contains("切换备用源", StringComparison.Ordinal));
        Assert.Equal(
            ["https://test.local/pinned.7z", "https://test.local/stable.sha256", "https://test.local/stable.7z"],
            handler.Requests);
        AssertNoTemporaryArchives();
    }

    [Fact]
    public async Task InstallOnlineAsync_RejectsStableSourceWhenChecksumIsInvalid()
    {
        var archive = CreateFfmpegArchive("stable-ffmpeg");
        var handler = new RouteHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://test.local/pinned.7z" => new HttpResponseMessage(HttpStatusCode.BadGateway),
            "https://test.local/stable.sha256" => TextResponse("not-a-checksum"),
            _ => BytesResponse(archive)
        });
        var manager = CreateOnlineManager(handler, new string('0', 64));

        var result = await manager.InstallOnlineAsync(progress: null);

        Assert.False(result.Succeeded);
        Assert.Contains("校验值", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ProjectBinDirectory, "ffmpeg.exe")));
        Assert.DoesNotContain("https://test.local/stable.7z", handler.Requests);
        AssertNoTemporaryArchives();
    }

    [Fact]
    public async Task InstallOnlineAsync_CancellationStopsWithoutTryingFallback()
    {
        var handler = new RouteHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
            };
            return response;
        });
        var manager = CreateOnlineManager(handler, new string('0', 64));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await manager.InstallOnlineAsync(progress: null, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("取消", result.Message, StringComparison.Ordinal);
        Assert.Equal(["https://test.local/pinned.7z"], handler.Requests);
        Assert.False(File.Exists(Path.Combine(ProjectBinDirectory, "ffmpeg.exe")));
        AssertNoTemporaryArchives();
    }

    [Fact]
    public async Task InstallOnlineAsync_RejectsArchiveWithoutFfmpegExecutable()
    {
        var archive = CreateArchive("ffmpeg-test/bin/ffprobe.exe", "not-ffmpeg");
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var handler = new RouteHandler(_ => BytesResponse(archive));
        var manager = CreateOnlineManager(handler, hash);

        var result = await manager.InstallOnlineAsync(progress: null);

        Assert.False(result.Succeeded);
        Assert.Contains("没有找到 ffmpeg.exe", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ProjectBinDirectory, "ffmpeg.exe")));
        AssertNoTemporaryArchives();
    }

    [Fact]
    public async Task InstallOnlineAsync_RejectsOversizedResponseBeforeReadingBody()
    {
        var handler = new RouteHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://test.local/stable.sha256")
                return TextResponse(new string('0', 64));
            var response = BytesResponse([1, 2, 3]);
            response.Content.Headers.ContentLength = 151L * 1024 * 1024;
            return response;
        });
        var manager = CreateOnlineManager(handler, new string('0', 64));

        var result = await manager.InstallOnlineAsync(progress: null);

        Assert.False(result.Succeeded);
        Assert.Contains("150 MB", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(ProjectBinDirectory, "ffmpeg.exe")));
        AssertNoTemporaryArchives();
    }

    [Fact]
    public async Task InstallOnlineAsync_FailurePreservesExistingExecutable()
    {
        Directory.CreateDirectory(ProjectBinDirectory);
        var installedPath = Path.Combine(ProjectBinDirectory, "ffmpeg.exe");
        await File.WriteAllTextAsync(installedPath, "existing-version");
        var archive = CreateArchive("ffmpeg-test/bin/ffprobe.exe", "not-ffmpeg");
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var handler = new RouteHandler(_ => BytesResponse(archive));
        var manager = CreateOnlineManager(handler, hash);

        var result = await manager.InstallOnlineAsync(progress: null);

        Assert.False(result.Succeeded);
        Assert.Equal("existing-version", await File.ReadAllTextAsync(installedPath));
        AssertNoTemporaryArchives();
    }

    [Fact]
    public async Task InstallOnlineAsync_SystemRouteFailsThenDirectRouteSucceeds()
    {
        var archive = CreateFfmpegArchive("direct-ffmpeg");
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var systemHandler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var directHandler = new RouteHandler(_ => BytesResponse(archive));
        var progress = new ProgressCollector();
        var manager = CreateOnlineManager(systemHandler, directHandler, hash, TimeSpan.FromSeconds(1));

        var result = await manager.InstallOnlineAsync(progress);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["https://test.local/pinned.7z"], systemHandler.Requests);
        Assert.Equal(["https://test.local/pinned.7z"], directHandler.Requests);
        Assert.Equal("direct-ffmpeg", await File.ReadAllTextAsync(Path.Combine(ProjectBinDirectory, "ffmpeg.exe")));
        Assert.Contains(progress.Items, item =>
            item.Detail?.Contains("Windows 系统网络设置", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Items, item =>
            item.Detail?.Contains("即将尝试：直接连接", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Items, item =>
            item.Detail?.Contains("HTTP 200", StringComparison.Ordinal) == true &&
            item.Detail.Contains("直接连接", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallOnlineAsync_HardTimeoutMovesFromHungSystemRouteToDirectRoute()
    {
        var archive = CreateFfmpegArchive("timeout-fallback-ffmpeg");
        var hash = Convert.ToHexString(SHA256.HashData(archive));
        var systemHandler = new AsyncRouteHandler((_, _) =>
            new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var directHandler = new RouteHandler(_ => BytesResponse(archive));
        var progress = new ProgressCollector();
        var manager = CreateOnlineManager(systemHandler, directHandler, hash, TimeSpan.FromMilliseconds(80));

        var result = await manager.InstallOnlineAsync(progress);

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(systemHandler.Requests);
        Assert.Single(directHandler.Requests);
        Assert.Contains(progress.Items, item =>
            item.Detail?.Contains("TimeoutException", StringComparison.Ordinal) == true &&
            item.Detail.Contains("直接连接", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallOnlineAsync_UserCancellationDuringHungHeadersDoesNotTryDirectRoute()
    {
        var systemHandler = new AsyncRouteHandler((_, _) =>
            new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var directHandler = new RouteHandler(_ => throw new InvalidOperationException("不应请求直接连接"));
        var manager = CreateOnlineManager(
            systemHandler,
            directHandler,
            new string('0', 64),
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        var result = await manager.InstallOnlineAsync(progress: null, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("取消", result.Message, StringComparison.Ordinal);
        Assert.Single(systemHandler.Requests);
        Assert.Empty(directHandler.Requests);
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
        RemoveDirectoryIfEmpty(Path.Combine(_directory, "temp"));
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

    private FfmpegManager CreateOnlineManager(
        HttpMessageHandler handler,
        string pinnedHash) =>
        new(
            ProjectBinDirectory,
            includePath: false,
            ApplicationDirectory,
            settingsStore: null,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            logger: null,
            executableValidator: (path, _) => Task.FromResult(new FfmpegStatus(
                File.Exists(path),
                File.Exists(path) ? "可用" : "异常",
                File.Exists(path) ? "ffmpeg version test" : "文件不存在",
                Path.GetDirectoryName(path),
                FfmpegSource.ProjectTools)),
            pinnedDownloadUrl: "https://test.local/pinned.7z",
            stableDownloadUrl: "https://test.local/stable.7z",
            stableChecksumUrl: "https://test.local/stable.sha256",
            pinnedSha256: pinnedHash,
            temporaryDirectory: TemporaryDirectory);

    private FfmpegManager CreateOnlineManager(
        HttpMessageHandler primaryHandler,
        HttpMessageHandler fallbackHandler,
        string pinnedHash,
        TimeSpan requestTimeout) =>
        new(
            ProjectBinDirectory,
            includePath: false,
            ApplicationDirectory,
            settingsStore: null,
            new HttpClient(primaryHandler) { Timeout = Timeout.InfiniteTimeSpan },
            logger: null,
            executableValidator: (path, _) => Task.FromResult(new FfmpegStatus(
                File.Exists(path),
                File.Exists(path) ? "可用" : "异常",
                File.Exists(path) ? "ffmpeg version test" : "文件不存在",
                Path.GetDirectoryName(path),
                FfmpegSource.ProjectTools)),
            pinnedDownloadUrl: "https://test.local/pinned.7z",
            stableDownloadUrl: "https://test.local/stable.7z",
            stableChecksumUrl: "https://test.local/stable.sha256",
            pinnedSha256: pinnedHash,
            temporaryDirectory: TemporaryDirectory,
            fallbackHttpClient: new HttpClient(fallbackHandler) { Timeout = Timeout.InfiniteTimeSpan },
            requestTimeout: requestTimeout,
            primaryConnectionName: "Windows 系统网络设置",
            fallbackConnectionName: "直接连接");

    private string TemporaryDirectory => Path.Combine(_directory, "temp");

    private void AssertNoTemporaryArchives()
    {
        if (!Directory.Exists(TemporaryDirectory)) return;
        Assert.Empty(Directory.EnumerateFiles(TemporaryDirectory, "QqChannelDesk-ffmpeg-*.7z"));
    }

    private static byte[] CreateFfmpegArchive(string content) =>
        CreateArchive("ffmpeg-test/bin/ffmpeg.exe", content);

    private static byte[] CreateArchive(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return stream.ToArray();
    }

    private static HttpResponseMessage BytesResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentLength = bytes.Length;
        return response;
    }

    private static HttpResponseMessage TextResponse(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value)
    };

    private sealed class ProgressCollector : IProgress<FfmpegInstallProgress>
    {
        public List<FfmpegInstallProgress> Items { get; } = [];
        public void Report(FfmpegInstallProgress value) => Items.Add(value);
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(route(request));
        }
    }

    private sealed class AsyncRouteHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return route(request, cancellationToken);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

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
