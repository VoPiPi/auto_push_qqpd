using System.Net;
using System.Net.Http;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class UpdateCheckServiceTests
{
    private const string PackageUrl =
        "https://gitee.com/vopipi/auto_push_qqpd/releases/download/v2.0.0/QqChannelDesk-2.0.0-win-x64.zip";
    private const string ChecksumUrl =
        "https://gitee.com/vopipi/auto_push_qqpd/releases/download/v2.0.0/QqChannelDesk-2.0.0-win-x64.zip.sha256";

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3-release", 1, 2, 3)]
    [InlineData("release-2.4", 2, 4, 0)]
    public void VersionParserAcceptsReleaseTags(string value, int major, int minor, int build)
    {
        Assert.True(ApplicationVersionInfo.TryParseVersion(value, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void VersionParserAcceptsFourComponentVersion()
    {
        Assert.True(ApplicationVersionInfo.TryParseVersion("v1.2.3.4-release", out var version));
        Assert.Equal(new Version(1, 2, 3, 4), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("release")]
    public void VersionParserRejectsUnversionedTags(string value)
    {
        Assert.False(ApplicationVersionInfo.TryParseVersion(value, out _));
    }

    [Fact]
    public void ParseReleaseBuildsTrustedTagPageWhenHtmlUrlIsUntrusted()
    {
        var release = UpdateCheckService.ParseRelease("""
            {
              "tag_name": "v1.2.3-release",
              "name": "稳定版",
              "body": "修复发布问题",
              "html_url": "https://example.com/redirect?token=secret",
              "published_at": "2026-10-05T10:00:00+08:00",
              "prerelease": false
            }
            """);

        Assert.NotNull(release);
        Assert.Equal("v1.2.3-release", release!.Version);
        Assert.Equal(
            "https://gitee.com/vopipi/auto_push_qqpd/releases/tag/v1.2.3-release",
            release.ReleaseUrl);
        Assert.Equal("修复发布问题", release.Notes);
        Assert.False(release.IsPrerelease);
    }

    [Fact]
    public void ParseReleaseFallsBackToCreatedAt()
    {
        var release = UpdateCheckService.ParseRelease("""
            {
              "tag_name": "v1.2.3",
              "created_at": "2026-10-04T02:30:00Z",
              "prerelease": false
            }
            """);

        Assert.NotNull(release);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 4, 2, 30, 0, TimeSpan.Zero),
            release!.PublishedAt);
    }

    [Theory]
    [InlineData("https://gitee.com/vopipi/auto_push_qqpd/releases")]
    [InlineData("https://gitee.com/vopipi/auto_push_qqpd/releases/tag/v1.0.1")]
    public void ReleaseUrlAllowsOnlyThisRepositoriesReleasePages(string url)
    {
        Assert.True(UpdateCheckService.IsAllowedReleaseUrl(url));
    }

    [Theory]
    [InlineData("http://gitee.com/vopipi/auto_push_qqpd/releases")]
    [InlineData("https://gitee.com/other/repo/releases")]
    [InlineData("https://gitee.com.evil.example/vopipi/auto_push_qqpd/releases")]
    [InlineData("https://gitee.com@evil.example/vopipi/auto_push_qqpd/releases")]
    [InlineData("https://gitee.com/vopipi/auto_push_qqpd/releases-evil")]
    [InlineData("https://gitee.com:8443/vopipi/auto_push_qqpd/releases")]
    [InlineData("https://gitee.com/vopipi/auto_push_qqpd/releases?token=secret")]
    [InlineData("https://gitee.com/vopipi/auto_push_qqpd/releases#fragment")]
    public void ReleaseUrlRejectsOtherDestinationsAndTransientParameters(string url)
    {
        Assert.False(UpdateCheckService.IsAllowedReleaseUrl(url));
    }

    [Fact]
    public void AssetUrlAllowsOnlyThisRepositoriesDownloadPages()
    {
        Assert.True(UpdateCheckService.IsAllowedAssetUrl(PackageUrl));
        Assert.False(UpdateCheckService.IsAllowedAssetUrl(
            "https://gitee.com/vopipi/auto_push_qqpd/releases"));
        Assert.False(UpdateCheckService.IsAllowedAssetUrl(
            "https://example.com/vopipi/auto_push_qqpd/releases/download/v2/file.zip"));
        Assert.False(UpdateCheckService.IsAllowedAssetUrl($"{PackageUrl}?token=secret"));
        Assert.False(UpdateCheckService.IsAllowedAssetUrl($"{PackageUrl}#fragment"));
    }

    [Fact]
    public async Task CheckAsyncSelectsHighestStableNumericVersion()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            [
              {
                "tag_name": "v2.0.0",
                "published_at": "2026-10-01T10:00:00Z",
                "prerelease": false
              },
              {
                "tag_name": "v99.0.0-beta",
                "published_at": "2026-10-03T10:00:00Z",
                "prerelease": true
              },
              {
                "tag_name": "v10.0.0",
                "published_at": "2026-10-02T10:00:00Z",
                "prerelease": false
              }
            ]
            """));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal("v10.0.0", result.LatestRelease?.Version);
        Assert.Equal(UpdateCheckService.BuildReleasesPageUrl(1), Assert.Single(handler.RequestUris).ToString());
    }

    [Fact]
    public async Task CheckAsyncIgnoresPrereleasesWhenOnlyPrereleasesExist()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            [
              {
                "tag_name": "v9.0.0-beta",
                "published_at": "2026-10-03T10:00:00Z",
                "prerelease": true
              }
            ]
            """));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.False(result.HasUpdate);
        Assert.Null(result.LatestRelease);
        Assert.Contains("暂无正式发布版本", result.Message);
    }

    [Fact]
    public void ParseReleaseRecognizesWindowsPackageAndMatchingChecksumOnly()
    {
        var release = UpdateCheckService.ParseRelease("""
            {
              "tag_name": "v2.0.0",
              "prerelease": false,
              "assets": [
                {
                  "name": "source.zip",
                  "size": 100,
                  "browser_download_url": "https://gitee.com/vopipi/auto_push_qqpd/releases/download/v2.0.0/source.zip"
                },
                {
                  "name": "QqChannelDesk-2.0.0-win-x64.zip",
                  "size": "12345678",
                  "browser_download_url": "https://gitee.com/vopipi/auto_push_qqpd/releases/download/v2.0.0/QqChannelDesk-2.0.0-win-x64.zip"
                },
                {
                  "name": "QqChannelDesk-2.0.0-win-x64.zip.sha256",
                  "size": 130,
                  "browser_download_url": "https://gitee.com/vopipi/auto_push_qqpd/releases/download/v2.0.0/QqChannelDesk-2.0.0-win-x64.zip.sha256"
                },
                {
                  "name": "source.tar.gz",
                  "size": 99,
                  "browser_download_url": "https://gitee.com/vopipi/auto_push_qqpd/releases/download/v2.0.0/source.tar.gz"
                }
              ]
            }
            """);

        Assert.NotNull(release);
        Assert.Equal("QqChannelDesk-2.0.0-win-x64.zip", release!.WindowsPackage?.Name);
        Assert.Equal(12345678, release.WindowsPackage?.SizeBytes);
        Assert.Equal("QqChannelDesk-2.0.0-win-x64.zip.sha256", release.ChecksumAsset?.Name);
        Assert.Equal(PackageChecksumStatus.ReadFailed, release.ChecksumStatus);
    }

    [Fact]
    public async Task CheckAsyncAllowsReleaseWithoutWindowsPackage()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            [
              {
                "tag_name": "v2.0.0",
                "prerelease": false,
                "assets": [
                  {
                    "name": "source.zip",
                    "size": 100,
                    "browser_download_url": "https://gitee.com/vopipi/auto_push_qqpd/releases/download/v2.0.0/source.zip"
                  }
                ]
              }
            ]
            """));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Null(result.LatestRelease?.WindowsPackage);
        Assert.Null(result.LatestRelease?.ChecksumAsset);
        Assert.Equal(UpdateCheckService.BuildReleasesPageUrl(1), Assert.Single(handler.RequestUris).ToString());
    }

    [Fact]
    public async Task CheckAsyncStopsAtFirstPageWhenStableReleasePresent()
    {
        // direction=desc 时第 1 页就是最新版本；出现正式版本后不应再请求后续页。
        var firstPage = "[" + string.Join(",", Enumerable.Range(0, 20).Select(index =>
            $"{{\"tag_name\":\"v1.0.{index}\",\"prerelease\":false}}")) + "]";
        var handler = new StubHandler(_ => JsonResponse(firstPage));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal("v1.0.19", result.LatestRelease?.Version);
        Assert.Equal(
            UpdateCheckService.BuildReleasesPageUrl(1),
            Assert.Single(handler.RequestUris).ToString());
    }

    [Fact]
    public async Task CheckAsyncReadsNextPageWhenFirstPageHasOnlyPrereleases()
    {
        // 第 1 页全是预发布版本时继续翻页，从更旧的页里找最新的正式版本。
        var firstPage = "[" + string.Join(",", Enumerable.Range(0, 20).Select(index =>
            $"{{\"tag_name\":\"v9.{index}.0-beta\",\"prerelease\":true}}")) + "]";
        var handler = new StubHandler(request =>
            // 用 "page=2&" 整段匹配，避免误命中 per_page=20 里的 "page=2" 子串。
            request.RequestUri?.Query.Contains("page=2&", StringComparison.Ordinal) == true
                ? JsonResponse("""
                    [
                      {
                        "tag_name": "v1.1.1-release",
                        "prerelease": false
                      }
                    ]
                    """)
                : JsonResponse(firstPage));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal("v1.1.1-release", result.LatestRelease?.Version);
        Assert.Equal(
            new[]
            {
                UpdateCheckService.BuildReleasesPageUrl(1),
                UpdateCheckService.BuildReleasesPageUrl(2)
            },
            handler.RequestUris.Select(uri => uri.ToString()).ToArray());
    }

    [Fact]
    public async Task CheckAsyncReportsNoStableReleaseWhenLaterPageFails()
    {
        var firstPage = "[" + string.Join(",", Enumerable.Range(0, 20).Select(index =>
            $"{{\"tag_name\":\"v9.{index}.0-beta\",\"prerelease\":true}}")) + "]";
        var handler = new StubHandler(request =>
            request.RequestUri?.Query.Contains("page=1", StringComparison.Ordinal) == true
                ? JsonResponse(firstPage)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.False(result.HasUpdate);
        Assert.Contains("暂无正式发布版本", result.Message);
    }

    [Fact]
    public async Task CheckAsyncReadsMatchingChecksumWithoutDownloadingApplicationPackage()
    {
        var handler = new StubHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) == true
                ? JsonResponse($"{new string('a', 64)}  QqChannelDesk-2.0.0-win-x64.zip")
                : JsonResponse($$"""
                    [
                      {
                        "tag_name": "v2.0.0",
                        "prerelease": false,
                        "assets": [
                          {
                            "name": "QqChannelDesk-2.0.0-win-x64.zip",
                            "size": 32100000,
                            "browser_download_url": "{{PackageUrl}}"
                          },
                          {
                            "name": "QqChannelDesk-2.0.0-win-x64.zip.sha256",
                            "size": 130,
                            "browser_download_url": "{{ChecksumUrl}}"
                          }
                        ]
                      }
                    ]
                    """));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal(PackageChecksumStatus.Available, result.LatestRelease?.ChecksumStatus);
        Assert.Equal(new string('A', 64), result.LatestRelease?.PackageSha256);
        Assert.Equal(
            new[]
            {
                UpdateCheckService.BuildReleasesPageUrl(1),
                ChecksumUrl
            },
            handler.RequestUris.Select(uri => uri.ToString()).ToArray());
    }

    [Theory]
    [InlineData("not-a-checksum")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  another-file.zip")]
    public async Task CheckAsyncKeepsUpdateWhenChecksumContentCannotBeParsed(string payload)
    {
        var result = await CheckWithChecksumResponseAsync(JsonResponse(payload));

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal(PackageChecksumStatus.ReadFailed, result.LatestRelease?.ChecksumStatus);
        Assert.Null(result.LatestRelease?.PackageSha256);
    }

    [Fact]
    public async Task CheckAsyncKeepsUpdateWhenChecksumDownloadFails()
    {
        var result = await CheckWithChecksumResponseAsync(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal(PackageChecksumStatus.ReadFailed, result.LatestRelease?.ChecksumStatus);
    }

    [Fact]
    public async Task CheckAsyncKeepsUpdateWhenChecksumResponseIsOversized()
    {
        var result = await CheckWithChecksumResponseAsync(JsonResponse(new string('a', 4097)));

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal(PackageChecksumStatus.ReadFailed, result.LatestRelease?.ChecksumStatus);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "package.zip", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  *package.zip", "package.zip", true)]
    [InlineData("\uFEFFaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  package.zip", "package.zip", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  other.zip", "package.zip", false)]
    [InlineData("not-a-checksum", "package.zip", false)]
    public void ParseChecksumValidatesHashAndFilename(string payload, string packageName, bool expected)
    {
        var checksum = UpdateCheckService.ParseChecksum(payload, packageName);

        Assert.Equal(expected, checksum is not null);
        if (expected) Assert.Equal(new string('A', 64), checksum);
    }

    [Fact]
    public async Task CheckAsyncReportsCurrentVersionWhenReleaseIsNotNewer()
    {
        var handler = new StubHandler(_ => JsonResponse("""
            [
              {
                "tag_name": "v1.0.0",
                "html_url": "https://gitee.com/vopipi/auto_push_qqpd/releases/tag/v1.0.0",
                "prerelease": false
              }
            ]
            """));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.False(result.HasUpdate);
        Assert.Contains("最新正式版本", result.Message);
    }

    [Fact]
    public async Task CheckAsyncReportsOversizedMetadataAndDoesNotTrustResponseBody()
    {
        var handler = new StubHandler(_ => JsonResponse($$"""
            [
              {
                "tag_name": "v2.0.0",
                "body": "{{new string('x', 600_000)}}"
              }
            ]
            """));
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.False(result.Succeeded);
        Assert.Contains("过大", result.Message);
    }

    private static async Task<UpdateCheckResult> CheckWithChecksumResponseAsync(
        HttpResponseMessage checksumResponse)
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) == true)
                return checksumResponse;

            return JsonResponse($$"""
                [
                  {
                    "tag_name": "v2.0.0",
                    "prerelease": false,
                    "assets": [
                      {
                        "name": "QqChannelDesk-2.0.0-win-x64.zip",
                        "size": 32100000,
                        "browser_download_url": "{{PackageUrl}}"
                      },
                      {
                        "name": "QqChannelDesk-2.0.0-win-x64.zip.sha256",
                        "size": 130,
                        "browser_download_url": "{{ChecksumUrl}}"
                      }
                    ]
                  }
                ]
                """);
        });
        using var client = new HttpClient(handler);
        return await new UpdateCheckService(client).CheckAsync("1.0.0");
    }

    private static HttpResponseMessage JsonResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri ?? throw new InvalidOperationException("请求缺少 URI。"));
            return Task.FromResult(responseFactory(request));
        }
    }
}
