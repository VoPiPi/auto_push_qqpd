using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Application = System.Windows.Application;
using Window = System.Windows.Window;

namespace QqChannelDesk.Services;

/// <summary>
/// 借助 proxyunblocker 网页代理抓取页面：首页取 initialToken，页内 POST /api/servers 换取代理节点地址，
/// 再用屏幕外的隐藏 WebView2 打开节点页面（JS 挑战由浏览器自动完成），最后取回真实 HTML 与节点 cookie。
/// 所有 WebView2 调用都需要在 UI 线程执行，非 UI 线程调用会自动转派到主界面线程。
/// </summary>
public sealed partial class WebProxyPageFetcher : IWebProxyProvider, IDisposable
{
    public const string ProxyHomeUrl = "https://proxyunblocker.net/zh-Hans";
    private const string ProxyHost = "proxyunblocker.net";
    private const int HomepageAttempts = 3;
    private const int ApiAttempts = 3;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly Regex InitialTokenRegex = InitialTokenPattern();

    private static readonly Lazy<WebProxyPageFetcher> DefaultFetcher = new(() => new WebProxyPageFetcher());
    public static WebProxyPageFetcher Default => DefaultFetcher.Value;

    public string Id => "proxyunblocker";
    public string DisplayName => "Proxy Unblocker";
    public string HomepageUrl => ProxyHomeUrl;
    public bool SupportsSimulation => true;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly OperationLeaseTracker _lifetimeLease = new();
    private Window? _hostWindow;
    private WebView2? _webView;
    private bool _lifetimeDisposed;

    /// <summary>
    /// 通过网页代理打开目标地址，等待页面内容出现后返回 HTML、最终地址和节点 cookie。
    /// </summary>
    public Task<ProxyPageResult> FetchAsync(string url, string? waitSelector = null, CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => FetchAsync(url, waitSelector, cancellationToken)).Task.Unwrap();
        return FetchCoreAsync(url, waitSelector, cancellationToken);
    }

    public void Dispose()
    {
        if (_lifetimeLease.IsDisposed && _lifetimeDisposed) return;
        // 有在途抓取时由最后一个操作完成释放；否则当前调用直接收尾。
        var closeNow = _lifetimeLease.MarkDisposed();

        CancelLifetime();
        if (closeNow) DisposeLifetime();
        CloseHostOnDispatcher();
    }

    /// <summary>
    /// 取消在途抓取并等待 WebView2 宿主空闲后再释放，避免关闭窗口与脚本执行并发。
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_lifetimeLease.IsDisposed && _lifetimeDisposed) return;
        var disposeNow = _lifetimeLease.MarkDisposed();
        CancelLifetime();

        if (!disposeNow)
            await _fetchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseHostAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!disposeNow) _fetchGate.Release();
            DisposeLifetime();
        }
    }

    private void CancelLifetime()
    {
        try { _lifetimeSource.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void DisposeLifetime()
    {
        lock (_gate)
        {
            if (_lifetimeDisposed) return;
            _lifetimeDisposed = true;
        }
        try { _lifetimeSource.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 在 <c>_gate</c> 内登记一次抓取并取出生命周期令牌。
    /// 取消/释放会一直等到最后一个租约归还后才关闭宿主和释放 CTS，
    /// 因此排队中的抓取不会引用到已释放的令牌。
    /// </summary>
    private bool TryBeginOperation(out CancellationToken lifetimeToken)
    {
        if (!_lifetimeLease.TryAcquireLease())
        {
            lifetimeToken = default;
            return false;
        }
        lifetimeToken = _lifetimeSource.Token;
        return true;
    }

    private void CloseHostOnDispatcher()
    {
        void CloseHost()
        {
            try { _webView?.Dispose(); }
            catch { }
            try { _hostWindow?.Close(); }
            catch { }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.Invoke(CloseHost);
        else CloseHost();
    }

    private Task CloseHostAsync()
    {
        void CloseHost()
        {
            try { _webView?.Dispose(); }
            catch { }
            try { _hostWindow?.Close(); }
            catch { }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(CloseHost).Task;
        CloseHost();
        return Task.CompletedTask;
    }

    private async Task<ProxyPageResult> FetchCoreAsync(string url, string? waitSelector, CancellationToken cancellationToken)
    {
        // 单例宿主只有一个 WebView2，多个采集（定时 + 手动）必须串行使用，
        // 否则导航、cookie 与读取到的 HTML 会相互覆盖。
        var acquired = TryBeginOperation(out var lifetimeToken);
        ObjectDisposedException.ThrowIf(!acquired, this);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        var operationToken = linkedSource.Token;
        await _fetchGate.WaitAsync(operationToken).ConfigureAwait(true);
        try
        {
            if (!PublicArticleFetcher.TryNormalizePublicUrl(url, out var target, out var error))
                throw new InvalidOperationException(error);
            await EnsureHostAsync(operationToken);
            var core = RequireCore();

            var homepageHtml = await LoadHomepageAsync(operationToken);
            var token = ReadInitialToken(homepageHtml) ?? throw new InvalidOperationException("代理首页没有返回初始令牌，请稍后重试。");
            var destination = await RequestProxyDestinationAsync(token, target.AbsoluteUri, operationToken);
            var page = await WaitForProxyPageAsync(core, destination, waitSelector, operationToken);
            return page with
            {
                CookieHeader = await ReadCookiesAsync(core, page.FinalUrl),
                ProviderId = Id,
                ProviderName = DisplayName
            };
        }
        finally
        {
            _fetchGate.Release();
            if (_lifetimeLease.TryReleaseLease()) DisposeLifetime();
        }
    }

    private async Task EnsureHostAsync(CancellationToken cancellationToken)
    {
        if (_webView?.CoreWebView2 is not null) return;
        var host = new Window
        {
            Title = "QqChannelDesk 代理浏览器",
            Width = 1280,
            Height = 720,
            Left = -4000,
            Top = -4000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        var view = new WebView2();
        host.Content = view;
       host.Show();
       try
       {
            await view.EnsureCoreWebView2Async();
       }
        catch (WebView2RuntimeNotFoundException ex)
        {
            try { host.Close(); }
            catch { }
            throw new InvalidOperationException("未检测到 WebView2 运行时，请安装 Microsoft Edge WebView2 运行时后重试。", ex);
        }
        catch
        {
            try { host.Close(); }
            catch { }
            throw;
        }
        lock (_gate)
        {
            _hostWindow = host;
            _webView = view;
        }
    }

    private async Task<string> LoadHomepageAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= HomepageAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var html = await NavigateAndReadHtmlAsync(ProxyHomeUrl, NavigationTimeout, cancellationToken);
                if (ReadInitialToken(html) is not null) return html;
                lastError = new InvalidOperationException("代理首页没有返回初始令牌。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
            if (attempt < HomepageAttempts) await Task.Delay(RetryDelay, cancellationToken);
        }
        throw new InvalidOperationException($"无法打开代理首页（已重试 {HomepageAttempts} 次）：{lastError?.Message}");
    }

    /// <summary>
    /// 复刻代理站点表单提交：页内 POST /api/servers，从响应的 meta[http-equiv=x-refresh] 取出节点地址。
    /// ExecuteScriptAsync 只序列化"同步完成"的返回值，异步 Promise 会变成空对象，因此这里用同步 XHR 等响应回来。
    /// </summary>
    private async Task<string> RequestProxyDestinationAsync(string token, string targetUrl, CancellationToken cancellationToken)
    {
        var script = $$"""
            (() => {
                const payload = (status, body) => JSON.stringify({ status: status, body: body });
                try {
                    const form = new FormData();
                    form.append('input', {{JsonSerializer.Serialize(targetUrl)}});
                    const xhr = new XMLHttpRequest();
                    xhr.open('POST', '/api/servers', false);
                    xhr.setRequestHeader('x-stats-id', {{JsonSerializer.Serialize(token)}});
                    xhr.send(form);
                    return payload(xhr.status, (xhr.responseText || '').slice(0, 8000));
                } catch (e) {
                    return payload(0, String((e && e.message) || e));
                }
            })()
            """;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= ApiAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var raw = await ExecuteStringAsync(script, ScriptTimeout, cancellationToken);
                var (status, body) = ParseProxyServerResponse(raw);
                var destination = ExtractProxyDestination(body);
                if (destination.Length > 0 && PublicArticleFetcher.TryNormalizePublicUrl(destination, out var nodeUri, out _))
                    return nodeUri.AbsoluteUri;
                lastError = new InvalidOperationException(
                    $"代理服务没有返回有效的节点地址（HTTP {status}，响应：{DescribeResponse(body)}）。");
                AppLogger.Instance.Warning(
                    $"proxyunblocker /api/servers 第 {attempt} 次未拿到节点：HTTP {status}，响应：{ScrubProxyToken(body)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                AppLogger.Instance.Warning($"proxyunblocker /api/servers 第 {attempt} 次调用异常：{ex.Message}");
            }
            if (attempt < ApiAttempts) await Task.Delay(RetryDelay, cancellationToken);
        }
        throw new InvalidOperationException($"获取代理节点失败（已重试 {ApiAttempts} 次）：{lastError?.Message}");
    }

    private static (int Status, string Body) ParseProxyServerResponse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (0, "");
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetInt32() : 0;
            var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "";
            return (status, body);
        }
        catch (JsonException)
        {
            return (0, raw);
        }
    }

    /// <summary>
    /// 从 /api/servers 响应中提取 meta[http-equiv=x-refresh] 指向的代理节点地址，相对地址基于代理首页补全。
    /// </summary>
    internal static string ExtractProxyDestination(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var meta in doc.DocumentNode.SelectNodes("//meta") ?? Enumerable.Empty<HtmlNode>())
        {
            if (!meta.GetAttributeValue("http-equiv", "").Trim().Equals("x-refresh", StringComparison.OrdinalIgnoreCase)) continue;
            var url = ReadRefreshTarget(meta.GetAttributeValue("content", ""));
            if (url.Length == 0) continue;
            if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
            if (Uri.TryCreate(new Uri(ProxyHomeUrl), url, out var resolved)) return resolved.AbsoluteUri;
        }
        return "";
    }

    private static string ReadRefreshTarget(string content)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0 || text.All(char.IsAsciiDigit)) return "";
        var marker = text.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) return text[(marker + 4)..].Trim().Trim('"', '\'');
        var semicolon = text.IndexOf(';');
        if (semicolon > 0 && text[..semicolon].All(char.IsAsciiDigit)) return text[(semicolon + 1)..].Trim().Trim('"', '\'');
        return text;
    }

    private static string DescribeResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "（空响应）";
        var text = body.ReplaceLineEndings(" ").Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    /// <summary>日志里抹掉代理会话令牌（__pot/__poss），只留结构信息。</summary>
    private static string ScrubProxyToken(string body) => ProxyTokenPattern().Replace(body ?? "", "$1***");

    [GeneratedRegex(@"([?&])(?:__pot|__poss)=[^&""\s]+")]
    private static partial Regex ProxyTokenPattern();

    /// <summary>
    /// 打开代理节点页面并轮询等待内容：节点会先做 JS 挑战并自动重载，因此只认"已离开代理源站且目标元素出现"。
    /// </summary>
    private async Task<ProxyPageResult> WaitForProxyPageAsync(CoreWebView2 core, string destination, string? waitSelector, CancellationToken cancellationToken)
    {
        var probe = $$"""
            (() => {
                const selector = {{JsonSerializer.Serialize(waitSelector ?? "")}};
                const ready = !selector || !!document.querySelector(selector);
                return JSON.stringify({ host: location.host, href: location.href, ready: ready });
            })()
            """;
        string? navigationError = null;
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess) navigationError = args.WebErrorStatus.ToString();
        }
        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            core.Navigate(destination);
            var deadline = DateTime.UtcNow + SettleTimeout;
            var lastHost = "";
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? state = null;
                try
                {
                    state = await ExecuteStringAsync(probe, ScriptTimeout, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 页面可能正在挑战重载，脚本上下文暂时不可用，继续轮询即可。
                }
                if (state is not null)
                {
                    using var document = JsonDocument.Parse(state);
                    var root = document.RootElement;
                    var host = root.GetProperty("host").GetString() ?? "";
                    var href = root.GetProperty("href").GetString() ?? "";
                    var ready = root.GetProperty("ready").GetBoolean();
                    lastHost = host;
                   if (ready && !string.Equals(host, ProxyHost, StringComparison.OrdinalIgnoreCase))
                   {
                       var html = await ReadHtmlAsync(cancellationToken);
                        return new ProxyPageResult(html, href, "");
                   }
                    if (navigationError is not null && string.Equals(host, ProxyHost, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"代理节点页面打开失败（{navigationError}），请重试。");
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException($"代理页面未在 {SettleTimeout.TotalSeconds:0} 秒内打开（当前域名：{(lastHost.Length > 0 ? lastHost : "未知")}），请重试。");
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally { core.NavigationCompleted -= OnNavigationCompleted; }
    }

    private async Task<string> NavigateAndReadHtmlAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var core = RequireCore();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            core.NavigationCompleted -= OnNavigationCompleted;
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException($"页面打开失败（{args.WebErrorStatus}）。"));
        }
        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            core.Navigate(url);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await completion.Task.WaitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { core.Stop(); }
                catch { }
                throw new TimeoutException($"打开 {url} 超时（{timeout.TotalSeconds:0} 秒）。");
            }
        }
        finally { core.NavigationCompleted -= OnNavigationCompleted; }
        return await ReadHtmlAsync(cancellationToken);
    }

    private async Task<string> ReadHtmlAsync(CancellationToken cancellationToken)
    {
        return await WebViewHtmlReader.ReadAsync(
            RequireCore(),
            DisplayName,
            ScriptTimeout,
            cancellationToken);
    }

    private async Task<string?> ExecuteStringAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var core = RequireCore();
        string raw;
        try
        {
            raw = await core.ExecuteScriptAsync(script).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("页面脚本执行超时。");
        }
        if (string.IsNullOrWhiteSpace(raw) || raw is "null" or "\"\"") return null;
        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadCookiesAsync(CoreWebView2 core, string finalUrl)
    {
        try
        {
            if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri)) return "";
            var cookies = await core.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
            return string.Join("; ", cookies.Where(cookie => !string.IsNullOrEmpty(cookie.Name)).Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }
        catch (Exception)
        {
            return "";
        }
    }

    private CoreWebView2 RequireCore() =>
        _webView?.CoreWebView2 ?? throw new InvalidOperationException("WebView2 尚未初始化。");

    private static string? ReadInitialToken(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var match = InitialTokenRegex.Match(html);
        return match.Success && match.Groups["token"].Value.Length > 0 ? match.Groups["token"].Value : null;
    }

    [GeneratedRegex(@"initialToken\\?"":\\?""(?<token>[^""\\]+)")]
    private static partial Regex InitialTokenPattern();
}

public sealed record ProxyPageResult(
    string Html,
    string FinalUrl,
    string CookieHeader,
    string ProviderId = "",
    string ProviderName = "");
