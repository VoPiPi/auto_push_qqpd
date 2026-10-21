using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqChannelDesk.Services;

public sealed record ReleaseAssetInfo(
    string Name,
    long? SizeBytes,
    string DownloadUrl);

public enum PackageChecksumStatus
{
    NotProvided,
    Available,
    ReadFailed
}

public sealed record ReleaseUpdateInfo(
    string Version,
    string Title,
    string Notes,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    ReleaseAssetInfo? WindowsPackage = null,
    ReleaseAssetInfo? ChecksumAsset = null,
    string? PackageSha256 = null,
    PackageChecksumStatus ChecksumStatus = PackageChecksumStatus.NotProvided);

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
    public const string ReleasesApiUrl = "https://gitee.com/api/v5/repos/vopipi/auto_push_qqpd/releases?page=1&per_page=10";

    private const int MaxResponseBytes = 512 * 1024;
    private const int MaxChecksumBytes = 4 * 1024;
    private static readonly Regex WindowsPackagePattern = new(
        @"^QqChannelDesk-\d+\.\d+\.\d+(?:\.\d+)?-win-x64\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
            using var response = await _httpClient.GetAsync(
                ReleasesApiUrl,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(true, false, "Gitee 暂无正式发布版本。");

            if (!response.IsSuccessStatusCode)
                return new(false, false, $"Gitee 版本检查未完成，请稍后重试或打开 Gitee Releases 查看。HTTP {(int)response.StatusCode}");

            var payload = await ReadLimitedResponseAsync(response, MaxResponseBytes, timeout.Token);
            var release = SelectLatestStableRelease(ParseReleases(payload));
            if (release is null)
                return new(true, false, "Gitee 暂无正式发布版本。");

            if (!ApplicationVersionInfo.TryParseVersion(release.Version, out var latest))
                return new(false, false, $"无法识别 Gitee 新版本号，请打开 Gitee Releases 查看：{release.Version}");

            var hasUpdate = latest > current;
            if (hasUpdate && release.WindowsPackage is not null && release.ChecksumAsset is not null)
                release = await ReadReleaseChecksumAsync(release, cancellationToken);

            var message = hasUpdate
                ? $"发现新版本 {release.Version}，当前版本为 {currentVersion}。"
                : $"当前已是最新正式版本（{currentVersion}）。";
            return new(true, hasUpdate, message, release);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, "版本检查超时，请稍后重试或直接打开 Gitee Releases 页面。");
        }
        catch (HttpRequestException ex)
        {
            return new(false, false, $"无法连接 Gitee，请检查网络后重试：{CliDiagnostics.Sanitize(ex.Message)}");
        }
        catch (JsonException)
        {
            return new(false, false, "Gitee 版本信息无法读取，请稍后重试或打开 Gitee Releases 查看。");
        }
        catch (IOException ex)
        {
            return new(false, false, $"读取版本信息失败，请稍后重试或打开 Gitee Releases 查看：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    public static IReadOnlyList<ReleaseUpdateInfo> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<ReleaseUpdateInfo>();

        var releases = new List<ReleaseUpdateInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var release = ParseReleaseElement(item);
            if (release is not null) releases.Add(release);
        }

        return releases;
    }

    public static ReleaseUpdateInfo? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? ParseReleaseElement(document.RootElement)
            : null;
    }

    public static bool IsAllowedReleaseUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && string.Equals(parsed.Host, "gitee.com", StringComparison.OrdinalIgnoreCase)
            && parsed.IsDefaultPort
            && string.IsNullOrEmpty(parsed.UserInfo)
            && string.IsNullOrEmpty(parsed.Query)
            && string.IsNullOrEmpty(parsed.Fragment)
            && (string.Equals(parsed.AbsolutePath, "/vopipi/auto_push_qqpd/releases", StringComparison.OrdinalIgnoreCase)
                || parsed.AbsolutePath.StartsWith("/vopipi/auto_push_qqpd/releases/", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAllowedAssetUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && string.Equals(parsed.Host, "gitee.com", StringComparison.OrdinalIgnoreCase)
            && parsed.IsDefaultPort
            && string.IsNullOrEmpty(parsed.UserInfo)
            && string.IsNullOrEmpty(parsed.Query)
            && string.IsNullOrEmpty(parsed.Fragment)
            && parsed.AbsolutePath.StartsWith(
                "/vopipi/auto_push_qqpd/releases/download/",
                StringComparison.OrdinalIgnoreCase);
    }

    private static ReleaseUpdateInfo? ParseReleaseElement(JsonElement root)
    {
        var tag = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) return null;

        tag = tag.Trim();
        var htmlUrl = GetString(root, "html_url");
        var releaseUrl = IsAllowedReleaseUrl(htmlUrl) ? htmlUrl! : BuildTagReleaseUrl(tag);
        var publishedAt = GetDateTimeOffset(root, "published_at")
            ?? GetDateTimeOffset(root, "created_at");
        var assets = ParseAssets(root);
        var windowsPackage = assets.FirstOrDefault(asset => WindowsPackagePattern.IsMatch(asset.Name));
        var checksumAsset = windowsPackage is null
            ? null
            : assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, windowsPackage.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

        return new ReleaseUpdateInfo(
            tag,
            GetString(root, "name")?.Trim() ?? tag,
            GetString(root, "body")?.Trim() ?? string.Empty,
            releaseUrl,
            publishedAt,
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True,
            windowsPackage,
            checksumAsset,
            null,
            checksumAsset is null ? PackageChecksumStatus.NotProvided : PackageChecksumStatus.ReadFailed);
    }

    private static IReadOnlyList<ReleaseAssetInfo> ParseAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return Array.Empty<ReleaseAssetInfo>();

        var result = new List<ReleaseAssetInfo>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(asset, "name")?.Trim();
            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || !IsAllowedAssetUrl(url)) continue;
            result.Add(new ReleaseAssetInfo(name, GetInt64(asset, "size"), url!));
        }

        return result;
    }

    private async Task<ReleaseUpdateInfo> ReadReleaseChecksumAsync(
        ReleaseUpdateInfo release,
        CancellationToken cancellationToken)
    {
        if (release.WindowsPackage is not { } package || release.ChecksumAsset is not { } checksumAsset)
            return release;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _httpClient.GetAsync(
                checksumAsset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode) return release;

            var payload = await ReadLimitedResponseAsync(response, MaxChecksumBytes, timeout.Token);
            var sha256 = ParseChecksum(payload, package.Name);
            return string.IsNullOrWhiteSpace(sha256)
                ? release
                : release with
                {
                    PackageSha256 = sha256,
                    ChecksumStatus = PackageChecksumStatus.Available
                };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return release;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            return release;
        }
    }

    internal static string? ParseChecksum(string payload, string packageName)
    {
        var match = Regex.Match(
            payload.Trim().TrimStart('\uFEFF'),
            @"(?m)^\s*(?<hash>[A-Fa-f0-9]{64})(?:\s+\*?(?<name>[^\r\n]+))?\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var fileName = match.Groups["name"].Value.Trim();
        if (!string.IsNullOrWhiteSpace(fileName)
            && !string.Equals(Path.GetFileName(fileName), packageName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return match.Groups["hash"].Value.ToUpperInvariant();
    }

    private static ReleaseUpdateInfo? SelectLatestStableRelease(IReadOnlyList<ReleaseUpdateInfo> releases)
    {
        ReleaseUpdateInfo? selected = null;
        Version? selectedVersion = null;
        foreach (var release in releases)
        {
            if (release.IsPrerelease) continue;
            if (!ApplicationVersionInfo.TryParseVersion(release.Version, out var version)) continue;
            if (selectedVersion is null || version > selectedVersion)
            {
                selected = release;
                selectedVersion = version;
            }
        }

        return selected;
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

    private static async Task<string> ReadLimitedResponseAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new IOException("版本信息响应过大。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new IOException("版本信息响应过大。");
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildTagReleaseUrl(string tag) =>
        $"{RepositoryReleaseUrl}/tag/{Uri.EscapeDataString(tag)}";

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? GetInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number)) return number;
        return property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement root, string propertyName)
    {
        var value = GetString(root, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
                ? parsed
                : null;
    }
}
