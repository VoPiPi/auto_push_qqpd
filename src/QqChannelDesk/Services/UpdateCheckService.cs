using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services;

public sealed record ReleaseUpdateInfo(
    string Version,
    string Title,
    string Notes,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease);

public sealed record UpdateCheckResult(
    bool Succeeded,
    bool HasUpdate,
    string Message,
    ReleaseUpdateInfo? LatestRelease = null);

/// <summary>
/// Reads public release metadata from the project's Gitee repository.
/// It never downloads, installs, or replaces application files.
/// </summary>
public sealed class UpdateCheckService
{
    public const string RepositoryReleaseUrl = "https://gitee.com/vopipi/auto_push_qqpd/releases";
    public const string LatestReleaseApiUrl = "https://gitee.com/api/v5/repos/vopipi/auto_push_qqpd/releases/latest";

    private const int MaxResponseBytes = 512 * 1024;
    private readonly HttpClient _httpClient;

    public UpdateCheckService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!ApplicationVersionInfo.TryParseVersion(currentVersion, out var current))
            return new(false, false, "当前程序版本号无法识别，暂时不能比较更新。版本号请使用类似 1.0.0 的格式。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(true, false, "Gitee 目前没有可用的正式发布版本。");

            if (!response.IsSuccessStatusCode)
                return new(false, false, $"Gitee 版本服务返回 HTTP {(int)response.StatusCode}。");

            var payload = await ReadLimitedResponseAsync(response, timeout.Token);
            var release = ParseRelease(payload);
            if (release is null)
                return new(false, false, "Gitee 返回的版本信息格式无法识别。");

            if (!ApplicationVersionInfo.TryParseVersion(release.Version, out var latest))
                return new(false, false, $"Gitee Release 版本号无法识别：{release.Version}");

            var hasUpdate = latest > current;
            var message = hasUpdate
                ? $"发现新版本 {release.Version}，当前版本为 {currentVersion}。"
                : $"当前已是最新版本（{currentVersion}）。";
            return new(true, hasUpdate, message, release);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, "版本检查超时，请稍后重试或直接打开 Gitee Releases 页面。");
        }
        catch (HttpRequestException ex)
        {
            return new(false, false, $"无法连接 Gitee：{CliDiagnostics.Sanitize(ex.Message)}");
        }
        catch (JsonException)
        {
            return new(false, false, "Gitee 返回的版本信息不是有效 JSON。");
        }
        catch (IOException ex)
        {
            return new(false, false, $"读取版本信息失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    public static ReleaseUpdateInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var tag = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var url = GetString(root, "html_url");
        var safeUrl = IsAllowedReleaseUrl(url) ? url! : RepositoryReleaseUrl;
        var publishedAt = GetDateTimeOffset(root, "published_at");
        return new ReleaseUpdateInfo(
            tag.Trim(),
            GetString(root, "name")?.Trim() ?? tag.Trim(),
            GetString(root, "body")?.Trim() ?? string.Empty,
            safeUrl,
            publishedAt,
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True);
    }

    public static string BuildReleasePrompt(string currentVersion, UpdateCheckResult result)
    {
        if (result.LatestRelease is not { } release)
            return result.Message;

        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "暂无更新说明。" : release.Notes.Trim();
        var shortNotes = notes.Length > 1800
            ? notes[..1800] + "\n…（完整说明请查看 Gitee Release 页面）"
            : notes;
        var publishedAt = release.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
        var releaseType = release.IsPrerelease ? "预发布版本" : "正式版本";
        var updateHint = result.HasUpdate
            ? "发现新版本，可以从 Gitee 下载更新包。"
            : "当前已是最新版本，以下是 Gitee 当前 Release 的说明。";

        return $"{updateHint}\n\n" +
            $"当前版本：{currentVersion}\n" +
            $"Gitee 最新版本：{release.Version}\n" +
            $"Release 标题：{release.Title}\n" +
            $"发布时间：{publishedAt}\n" +
            $"版本类型：{releaseType}\n\n" +
            $"更新说明：\n{shortNotes}\n\n" +
            "程序不会自动下载或覆盖文件。请关闭程序后，将 Gitee 更新包手动覆盖到原程序目录；已有数据库、日志、素材和本地工具不会由更新包处理。\n\n" +
            "现在打开 Gitee Releases 页面吗？";
    }

    public static bool IsAllowedReleaseUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && string.Equals(parsed.Host, "gitee.com", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(parsed.UserInfo)
            && (string.Equals(parsed.AbsolutePath, "/vopipi/auto_push_qqpd/releases", StringComparison.OrdinalIgnoreCase)
                || parsed.AbsolutePath.StartsWith("/vopipi/auto_push_qqpd/releases/", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QqChannelDesk", ApplicationVersionInfo.CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<string> ReadLimitedResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new IOException("版本信息响应过大。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new IOException("版本信息响应过大。");
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement root, string propertyName)
    {
        var value = GetString(root, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
