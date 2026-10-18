using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QqChannelDesk.Services;

public sealed record ProxyFetchResult(int ReadCount, int AcceptedCount, int FilteredCount, int UpdatedCount, string Error = "")
{
    public bool Succeeded => Error.Length == 0;
}

public sealed record ProxyHealthResult(int SuccessCount, int FailedCount, int SkippedCount);
public sealed record ProxyTelegramCheckResult(bool Succeeded, int MessageCount, string Error);

public sealed class ProxyPoolService
{
    public const int DefaultConcurrency = 10;
    public const int MaxConcurrency = 50;
    public const string TelegramProbeUrl = "https://t.me/s/TGWenAnGuan";
    private const int MaxSourceBytes = 5_000_000;
    private const string CountryProbeUrl = "https://ipwho.is/";
    private const string IpProbeFallbackUrl = "https://api.ipify.org?format=json";
    private const SslPolicyErrors RelaxableProxyCertificateErrors =
        SslPolicyErrors.RemoteCertificateChainErrors |
        SslPolicyErrors.RemoteCertificateNameMismatch;
    private static readonly Regex NodePattern = new(@"^(?:(?<scheme>https?)://)?(?<host>\[[^\]]+\]|[^:/\s]+):(?<port>\d{1,5})(?::(?<country>[A-Za-z]{2,3}))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ProxyPoolStore _store;
    private readonly HttpClient _sourceClient;

    public ProxyPoolService(ProxyPoolStore store, HttpClient? sourceClient = null)
    {
        _store = store;
        _sourceClient = sourceClient ?? CreateSourceClient();
    }

    public static string NormalizeSourceUrl(string input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("代理源地址必须是公开的 HTTP/HTTPS 地址。", nameof(input));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public async Task<ProxyFetchResult> FetchSourceAsync(ProxySource source, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Invoke($"正在获取 {source.DisplayName}…");
            using var response = await _sourceClient.GetAsync(source.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxSourceBytes) throw new InvalidOperationException("代理源文件超过 5 MB。");
            var text = await ReadLimitedTextAsync(response.Content, MaxSourceBytes, cancellationToken);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var candidates = lines.Select(ParseHttpCandidate).Where(item => item is not null).Cast<ProxyNodeCandidate>().DistinctBy(item => $"{item.Scheme}|{item.Host}|{item.Port}", StringComparer.OrdinalIgnoreCase).ToArray();
            await _store.ApplyFetchedNodesAsync(source, candidates, cancellationToken);
            var filtered = Math.Max(0, lines.Length - candidates.Length);
            AppLogger.Instance.Info($"代理源 {source.DisplayName} 获取完成：读取 {lines.Length} 条，保留 HTTP/HTTPS {candidates.Length} 条，过滤 {filtered} 条。");
            return new(lines.Length, candidates.Length, filtered, candidates.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var error = CliDiagnostics.Sanitize(ex.Message);
            await _store.MarkFetchFailedAsync(source.Id, error, cancellationToken);
            return new(0, 0, 0, 0, error);
        }
    }

    public async Task<ProxyHealthResult> TestHealthAsync(IReadOnlyList<ProxyNode> nodes, int concurrency, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        concurrency = Math.Clamp(concurrency, 1, MaxConcurrency);
        var success = 0;
        var failed = 0;
        var skipped = 0;
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = nodes.Select(async node =>
        {
            if (!node.IsSupported) { Interlocked.Increment(ref skipped); return; }
            await gate.WaitAsync(cancellationToken);
            try
            {
                var result = await ProbeAsync(node, cancellationToken);
                try
                {
                    await _store.UpdateHealthAsync(node.Id, result.Success, result.Ip, result.Code, result.Country, result.Error, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One persistence failure must not stop the remaining public proxies from being tested.
                    AppLogger.Instance.WriteException($"保存代理测活结果失败：{node.Address}", ex);
                }
                if (result.Success) Interlocked.Increment(ref success); else Interlocked.Increment(ref failed);
                try
                {
                    progress?.Invoke($"{node.Address}：{(result.Success ? "可用" : result.Error)}");
                }
                catch (Exception ex)
                {
                    // Progress is UI feedback and must not abort the worker batch.
                    AppLogger.Instance.WriteException($"更新代理测活进度失败：{node.Address}", ex);
                }
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        return new(success, failed, skipped);
    }

    public async Task<ProxyTelegramCheckResult> CheckTelegramAsync(ProxyNode node, CancellationToken cancellationToken = default)
    {
        if (!node.IsSupported) return new(false, 0, "仅支持 HTTP/HTTPS 代理。");
        try
        {
            using var client = CreateProxyClient(node, 20);
            using var response = await client.GetAsync(TelegramProbeUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var messages = TelegramChannelParser.ParseChannelPage(html, "TGWenAnGuan");
            var result = messages.Count > 0
                ? new ProxyTelegramCheckResult(true, messages.Count, "")
                : new ProxyTelegramCheckResult(false, 0, "页面打开成功，但没有解析到频道消息。");
            await TryUpdateTelegramResultAsync(node, result, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var result = new ProxyTelegramCheckResult(false, 0, CliDiagnostics.Sanitize(ex.Message));
            AppLogger.Instance.Warning($"代理节点 TG 验证失败 {node.Address}：{result.Error}");
            await TryUpdateTelegramResultAsync(node, result, cancellationToken);
            return result;
        }
    }

    private async Task TryUpdateTelegramResultAsync(ProxyNode node, ProxyTelegramCheckResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _store.UpdateTelegramAsync(node.Id, result.Succeeded, result.MessageCount, result.Error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Instance.WriteException($"保存代理节点 TG 验证结果失败：{node.Address}", ex);
        }
    }

    internal static ProxyNodeCandidate? ParseHttpCandidate(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0 || value.StartsWith('#') || value.StartsWith("//")) return null;
        var match = NodePattern.Match(value);
        if (!match.Success) return null;
        var scheme = (match.Groups["scheme"].Value.Length == 0 ? "http" : match.Groups["scheme"].Value).ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;
        if (!int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535) return null;
        var host = match.Groups["host"].Value.Trim('[', ']').ToLowerInvariant();
        if (host.Length == 0) return null;
        return new ProxyNodeCandidate(value, scheme, host, port, match.Groups["country"].Value.ToUpperInvariant());
    }

    internal static bool ShouldRelaxProxyCertificate(ProxyNode node, string? targetHostName, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (!string.Equals(node.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;

        var expectedProxyHost = NormalizeHost(GetProxyHost(node));
        var actualTargetHost = NormalizeHost(targetHostName);
        if (expectedProxyHost.Length == 0 ||
            actualTargetHost.Length == 0 ||
            !string.Equals(expectedProxyHost, actualTargetHost, StringComparison.OrdinalIgnoreCase))
            return false;

        return (errors & ~RelaxableProxyCertificateErrors) == SslPolicyErrors.None;
    }

    internal static bool IsCallerCancellation(OperationCanceledException exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested;

    private async Task<ProxyProbeResult> ProbeAsync(ProxyNode node, CancellationToken cancellationToken)
    {
        string primaryError;
        try
        {
            using var client = CreateProxyClient(node, 12);
            using var response = await client.GetAsync(CountryProbeUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<IpWhoResponse>(cancellationToken: cancellationToken);
            if (payload is null || payload.Success == false || string.IsNullOrWhiteSpace(payload.Ip))
                primaryError = "国家探针未返回出口 IP。";
            else
                return new(true, payload.Ip, payload.CountryCode ?? "", payload.Country ?? "未知", "");
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken)) { throw; }
        catch (TaskCanceledException) { primaryError = "国家探针请求超时（12 秒）。"; }
        catch (OperationCanceledException) { primaryError = "国家探针请求超时（12 秒）。"; }
        catch (Exception ex) { primaryError = CliDiagnostics.Sanitize(ex.Message); }

        // Some public proxies block geo-IP services while still being usable. A second, smaller
        // endpoint keeps availability separate from optional country detection.
        try
        {
            using var client = CreateProxyClient(node, 12);
            using var response = await client.GetAsync(IpProbeFallbackUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<IpifyResponse>(cancellationToken: cancellationToken);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.Ip))
                return new(true, payload.Ip, "", "未知", "国家信息获取失败，已使用出口 IP 兜底探针。");
            return new(false, "", "", "", $"{primaryError}；备用 IP 探针未返回出口 IP。 ");
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken)) { throw; }
        catch (TaskCanceledException) { return new(false, "", "", "", $"{primaryError}；备用 IP 探针请求超时（12 秒）。"); }
        catch (OperationCanceledException) { return new(false, "", "", "", $"{primaryError}；备用 IP 探针请求超时（12 秒）。"); }
        catch (Exception ex)
        {
            return new(false, "", "", "", $"{primaryError}；备用 IP 探针失败：{CliDiagnostics.Sanitize(ex.Message)}");
        }
    }

    private static HttpClient CreateProxyClient(ProxyNode node, int timeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(node.Address),
            UseProxy = true,
            AllowAutoRedirect = true
        };

        // Keep target-site certificate validation intact. Some public HTTPS proxies use
        // expired or self-signed certificates, so only relax validation when TLS is
        // connecting to the proxy endpoint itself.
        if (string.Equals(node.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    (sender is SslStream sslStream &&
                     ShouldRelaxProxyCertificate(node, sslStream.TargetHostName, errors))
            };
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QqChannelDesk/1.0 (+proxy-pool)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/html;q=0.9");
        return client;
    }

    private static string GetProxyHost(ProxyNode node) =>
        Uri.TryCreate(node.Address, UriKind.Absolute, out var proxyUri)
            ? proxyUri.IdnHost
            : node.Host;

    private static string NormalizeHost(string? host) =>
        host?.Trim().Trim('[', ']').TrimEnd('.') ?? "";

    private static HttpClient CreateSourceClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QqChannelDesk/1.0 (+proxy-pool-source)");
        return client;
    }

    private static async Task<string> ReadLimitedTextAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException("代理源文件超过 5 MB。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record IpWhoResponse(bool Success, string? Ip, string? Country, string? CountryCode);
    private sealed record IpifyResponse(string? Ip);
    private sealed record ProxyProbeResult(bool Success, string Ip, string Code, string Country, string Error);
}
