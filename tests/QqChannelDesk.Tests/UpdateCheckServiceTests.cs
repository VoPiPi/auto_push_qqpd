using System.Net;
using System.Net.Http;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class UpdateCheckServiceTests
{
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
    public void ParseReleaseKeepsOnlyTrustedReleaseUrl()
    {
        var release = UpdateCheckService.ParseRelease("""
            {
              "tag_name": "v1.2.3-release",
              "name": "稳定版",
              "body": "修复发布问题",
              "html_url": "https://example.com/redirect",
              "published_at": "2026-10-05T10:00:00+08:00",
              "prerelease": false
            }
            """);

        Assert.NotNull(release);
        Assert.Equal("v1.2.3-release", release!.Version);
        Assert.Equal(UpdateCheckService.RepositoryReleaseUrl, release.ReleaseUrl);
        Assert.Equal("修复发布问题", release.Notes);
        Assert.False(release.IsPrerelease);
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
    public void ReleaseUrlRejectsOtherDestinations(string url)
    {
        Assert.False(UpdateCheckService.IsAllowedReleaseUrl(url));
    }

    [Fact]
    public async Task CheckAsyncReportsAvailableUpdateWithoutDownloadingAsset()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "tag_name": "v9.8.7-release",
                  "name": "测试版本",
                  "body": "请到 Gitee 下载",
                  "html_url": "https://gitee.com/vopipi/auto_push_qqpd/releases/tag/v9.8.7-release",
                  "published_at": "2026-10-05T10:00:00+08:00",
                  "prerelease": false
                }
                """)
        });
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);

        var result = await service.CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.True(result.HasUpdate);
        Assert.Equal("v9.8.7-release", result.LatestRelease?.Version);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(UpdateCheckService.LatestReleaseApiUrl, handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task CheckAsyncReportsCurrentVersionWhenReleaseIsNotNewer()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tag_name\":\"v1.0.0-release\",\"html_url\":\"https://gitee.com/vopipi/auto_push_qqpd/releases\"}")
        });
        using var client = new HttpClient(handler);
        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.True(result.Succeeded);
        Assert.False(result.HasUpdate);
        Assert.Contains("最新版本", result.Message);
    }

    [Fact]
    public void BuildReleasePromptIncludesReleaseNotesWhenAlreadyUpToDate()
    {
        var result = new UpdateCheckResult(
            true,
            false,
            "当前已是最新版本（1.0.0）。",
            new ReleaseUpdateInfo(
                "v1.0.0-release",
                "当前版本说明",
                "修复了发布记录展示问题。",
                UpdateCheckService.RepositoryReleaseUrl,
                new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(8)),
                false));

        var prompt = UpdateCheckService.BuildReleasePrompt("1.0.0", result);

        Assert.Contains("当前已是最新版本", prompt);
        Assert.Contains("Release 标题：当前版本说明", prompt);
        Assert.Contains("发布时间：2026-10-05 10:00", prompt);
        Assert.Contains("修复了发布记录展示问题。", prompt);
        Assert.Contains("现在打开 Gitee Releases 页面吗？", prompt);
    }

    [Fact]
    public void BuildReleasePromptTruncatesOverlongReleaseNotes()
    {
        var result = new UpdateCheckResult(
            true,
            true,
            "发现新版本 v2.0.0，当前版本为 1.0.0。",
            new ReleaseUpdateInfo(
                "v2.0.0",
                "新版本",
                new string('x', 2000),
                UpdateCheckService.RepositoryReleaseUrl,
                null,
                true));

        var prompt = UpdateCheckService.BuildReleasePrompt("1.0.0", result);

        Assert.Contains("发现新版本，可以从 Gitee 下载更新包。", prompt);
        Assert.Contains("版本类型：预发布版本", prompt);
        Assert.Contains("…（完整说明请查看 Gitee Release 页面）", prompt);
        Assert.DoesNotContain(new string('x', 1801), prompt);
    }

    [Fact]
    public async Task CheckAsyncRejectsOversizedMetadata()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"tag_name\":\"v2.0.0\",\"body\":\"{new string('x', 600_000)}\"}}")
        });
        using var client = new HttpClient(handler);

        var result = await new UpdateCheckService(client).CheckAsync("1.0.0");

        Assert.False(result.Succeeded);
        Assert.Contains("过大", result.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            return Task.FromResult(responseFactory(request));
        }
    }
}
