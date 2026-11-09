using System.Net;
using System.Net.Http;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Window = System.Windows.Window;

namespace QqChannelDesk.Services;

/// <summary>网页代理的统一行为。每个代理站点只负责自己的提交和跳转协议。</summary>
public interface IWebProxyProvider
{
    string Id { get; }
    string DisplayName { get; }
    string HomepageUrl { get; }
    bool SupportsSimulation { get; }
    Task<ProxyPageResult> FetchAsync(string targetUrl, string? waitSelector = null, CancellationToken cancellationToken = default);
}

public sealed record WebProxySelection(string Value)
{
    public const string Automatic = "auto";
    public bool IsAutomatic => string.Equals(Value, Automatic, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// TG 采集传输通道。与网页代理不同，直连/系统代理/代理池都是标准 HTTP(S)
/// 请求，只改“怎么连”，不改目标主机、端口、路径或查询串；在线解析才走 WebView2 网页代理。
/// </summary>
public sealed record TelegramTransportSelection(string Value)
{
    public const string Auto = "auto";
    public const string Direct = "direct";
    public const string System = "system";
    public const string ProxyPool = "proxy-pool";
    public const string Online = "online";
    public const string OnlineProviderPrefix = "online:";

    private static readonly HashSet<string> LegacyOnlineProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "proxyunblocker",
        "coproxy",
        "lumiproxy"
    };

    public static TelegramTransportSelection Automatic { get; } = new(Auto);

    public bool IsAutomatic => string.Equals(Value, Auto, StringComparison.OrdinalIgnoreCase);

    public bool IsDirect => string.Equals(Value, Direct, StringComparison.OrdinalIgnoreCase);

    public bool IsSystem => string.Equals(Value, System, StringComparison.OrdinalIgnoreCase);

    public bool IsProxyPool => string.Equals(Value, ProxyPool, StringComparison.OrdinalIgnoreCase);

    public bool IsOnline =>
        string.Equals(Value, Online, StringComparison.OrdinalIgnoreCase) ||
        Value.StartsWith(OnlineProviderPrefix, StringComparison.OrdinalIgnoreCase) ||
        IsLegacyOnlineProviderId(Value);

    public static bool IsLegacyOnlineProviderId(string? value) =>
        LegacyOnlineProviderIds.Contains((value ?? "").Trim());

    /// <summary>把在线解析的旧 provider ID 归一化为 online:&lt;providerId&gt;，非在线通道返回 null。</summary>
    public static string? NormalizeOnlineSelection(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Equals(Online, StringComparison.OrdinalIgnoreCase)) return Online;
        if (trimmed.StartsWith(OnlineProviderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var providerId = trimmed[OnlineProviderPrefix.Length..].Trim();
            return providerId.Length == 0 ? Online : OnlineProviderPrefix + providerId;
        }
        return IsLegacyOnlineProviderId(trimmed) ? OnlineProviderPrefix + trimmed : null;
    }

    public static string ForOnlineProvider(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("网页代理标识不能为空。", nameof(providerId));
        return OnlineProviderPrefix + providerId.Trim();
    }
}

/// <summary>提供给 UI 的传输通道选项：自动选择 / 系统代理 / 代理池节点 / 在线解析。</summary>
public static class TelegramTransportSelector
{
    /// <summary>旧版本“自定义代理”通道的存储前缀，仅用于把历史配置安全降级为自动选择。</summary>
    private const string LegacyCustomPrefix = "custom:";

    public const string SystemDisplayName = "系统代理";
    public const string ProxyPoolDisplayName = "代理池节点";
    public const string OnlineDisplayName = "在线解析";

    public static IReadOnlyList<WebProxyOption> Options { get; } = new[]
    {
        new WebProxyOption(TelegramTransportSelection.Auto, "自动选择"),
        new WebProxyOption(TelegramTransportSelection.System, SystemDisplayName),
        new WebProxyOption(TelegramTransportSelection.ProxyPool, ProxyPoolDisplayName),
        new WebProxyOption(TelegramTransportSelection.Online, OnlineDisplayName)
    };

    public static WebProxyOption? FindOption(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (IsLegacyCustomProxyValue(trimmed)) return Options[0];
        if (trimmed.StartsWith(TelegramTransportSelection.OnlineProviderPrefix, StringComparison.OrdinalIgnoreCase) ||
            TelegramTransportSelection.IsLegacyOnlineProviderId(trimmed))
            return Options[3];
        return Options.FirstOrDefault(option => string.Equals(option.Value, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把已保存的通道值还原为下拉框文本：已下线的自定义代理映射到自动选择，旧网页代理值映射到在线解析。</summary>
    public static string DisplayValue(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (IsLegacyCustomProxyValue(trimmed)) return Options[0].DisplayName;
        if (TelegramTransportSelection.IsLegacyOnlineProviderId(trimmed))
            return OnlineDisplayName;
        return FindOption(trimmed)?.DisplayName ?? trimmed;
    }

    /// <summary>
    /// 解析下拉框文本或已保存的通道值：旧网页代理 ID 归一到在线解析，其余按通道处理。
    /// 旧版本的自定义代理（custom: 前缀或裸 HTTP(S) 地址）已下线，统一降级为自动选择；
    /// 无法识别时返回 null，由调用方提示用户。
    /// </summary>
    public static string? Parse(string? text, string? selectedValue = null)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return TelegramTransportSelection.Auto;
        if (IsLegacyCustomProxyValue(trimmed)) return TelegramTransportSelection.Auto;

        var online = TelegramTransportSelection.NormalizeOnlineSelection(trimmed);
        if (online is not null) return online;

        if (string.Equals(trimmed, TelegramTransportSelection.Direct, StringComparison.OrdinalIgnoreCase))
            return TelegramTransportSelection.Direct;

        var selectedOption = Options.FirstOrDefault(option =>
            string.Equals(option.Value, selectedValue, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(option.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (selectedOption is not null) return selectedOption.Value;

        var named = Options.FirstOrDefault(option =>
            string.Equals(option.Value, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        return named?.Value;
    }

    private static bool IsLegacyCustomProxyValue(string value) =>
        value.StartsWith(LegacyCustomPrefix, StringComparison.OrdinalIgnoreCase) ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed record WebProxyOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>系统代理注册表，同时负责把页面输入转换为代理选择。</summary>
public sealed class WebProxyProviderRegistry
{
    public const string AutomaticDisplayName = "自动选择";

    private readonly IReadOnlyList<IWebProxyProvider> _providers;

    public WebProxyProviderRegistry(IEnumerable<IWebProxyProvider>? providers = null)
    {
        _providers = (providers ?? CreateDefaultProviders())
            .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<IWebProxyProvider> Providers => _providers;

    public IReadOnlyList<WebProxyOption> Options =>
        new[] { new WebProxyOption(WebProxySelection.Automatic, AutomaticDisplayName) }
            .Concat(_providers.Select(provider => new WebProxyOption(provider.Id, provider.DisplayName)))
            .ToArray();

    internal static async Task DisposeDefaultProvidersAsync()
    {
        Exception? firstError = null;
        foreach (var dispose in new Func<Task>[]
        {
            WebProxyPageFetcher.Default.DisposeAsync,
            CoProxyWebProxyProvider.DisposeSharedBrowserAsync,
            LumiProxyWebProxyProvider.DisposeSharedBrowserAsync
        })
        {
            try
            {
                await dispose().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }

        if (firstError is not null)
            ExceptionDispatchInfo.Capture(firstError).Throw();
    }

    public WebProxySelection ParseSelection(string text, string? selectedValue = null)
    {
        var trimmed = (text ?? "").Trim();

        if (string.Equals(trimmed, AutomaticDisplayName, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Length == 0)
            return new(WebProxySelection.Automatic);

        var selectedProvider = _providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, selectedValue, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(provider.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (selectedProvider is not null) return new(selectedProvider.Id);

        var namedProvider = _providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (namedProvider is not null) return new(namedProvider.Id);

        return new(trimmed);
    }

    public IReadOnlyList<IWebProxyProvider> Resolve(WebProxySelection selection)
    {
        if (selection.IsAutomatic) return _providers;
        var provider = _providers.FirstOrDefault(item =>
            string.Equals(item.Id, selection.Value, StringComparison.OrdinalIgnoreCase));
        if (provider is not null) return new[] { provider };
        if (Uri.TryCreate(selection.Value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            throw new InvalidOperationException("自定义网页代理已下线，请从下拉框重新选择采集通道。");
        throw new InvalidOperationException($"未知的网页代理：{selection.Value}。");
    }

    /// <summary>
    /// 在线解析专用解析：auto/online 使用全部在线 provider，online:&lt;id&gt; 或旧 provider ID
    /// 只使用指定 provider；未知值或自定义代理明确报错，不能静默改成直连或系统代理。
    /// </summary>
    public IReadOnlyList<IWebProxyProvider> ResolveOnlineProviders(TelegramTransportSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.IsAutomatic || string.Equals(selection.Value, TelegramTransportSelection.Online, StringComparison.OrdinalIgnoreCase))
            return _providers;

        var normalized = TelegramTransportSelection.NormalizeOnlineSelection(selection.Value);
        if (normalized is null)
            throw new InvalidOperationException($"“{selection.Value}”不是可用的在线解析通道，请重新选择。");
        if (string.Equals(normalized, TelegramTransportSelection.Online, StringComparison.OrdinalIgnoreCase))
            return _providers;

        var providerId = normalized[TelegramTransportSelection.OnlineProviderPrefix.Length..];
        var provider = _providers.FirstOrDefault(item =>
            string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw new InvalidOperationException($"在线解析服务“{providerId}”未注册，请重新选择。");
        return new[] { provider };
    }

    private static IEnumerable<IWebProxyProvider> CreateDefaultProviders()
    {
        // 保持当前可用代理优先，其他代理按页面配置顺序作为自动模式兜底。
        yield return WebProxyPageFetcher.Default;
        yield return new CoProxyWebProxyProvider();
        yield return new LumiProxyWebProxyProvider();
    }
}

public abstract class HttpWebProxyProviderBase : IWebProxyProvider
{
    private static readonly HttpClient Client = CreateClient();
    private const int MaxHtmlBytes = 5_000_000;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string HomepageUrl { get; }
    public virtual bool SupportsSimulation => true;

    public abstract Task<ProxyPageResult> FetchAsync(string targetUrl, string? waitSelector = null, CancellationToken cancellationToken = default);

    protected static async Task<ProxyPageResult> GetHtmlAsync(
        HttpRequestMessage request,
        string providerId,
        string providerName,
        CancellationToken cancellationToken)
    {
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{providerName} 返回 HTTP {(int)response.StatusCode}。");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{providerName} 返回的不是 HTML 页面。");
        if (response.Content.Headers.ContentLength is > MaxHtmlBytes)
            throw new InvalidOperationException($"{providerName} 返回页面超过 {MaxHtmlBytes / 1_000_000} MB。");
        var html = await ReadLimitedTextAsync(response.Content, MaxHtmlBytes, cancellationToken);
        if (string.IsNullOrWhiteSpace(html)) throw new InvalidOperationException($"{providerName} 返回了空页面。");
        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? request.RequestUri?.AbsoluteUri ?? "";
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(value => value.Split(';', 2)[0]))
            : "";
        return new ProxyPageResult(html, finalUrl, cookies, providerId, providerName);
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
                throw new InvalidOperationException($"页面内容超过 {maxBytes / 1_000_000} MB 限制。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QqChannelDesk/1.0 (+telegram-preview)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9");
        return client;
    }
}

/// <summary>CoProxy 的协议是独立的标准表单 POST，不复用其他站点的令牌流程。</summary>
public sealed class CoProxyWebProxyProvider : HttpWebProxyProviderBase
{
    public override string Id => "coproxy";
    public override string DisplayName => "CoProxy";
    public override string HomepageUrl => "https://coproxy.io/zh-CN/tools/free-web-proxy/";

    internal static Uri SubmissionUri => new("https://mirror-1.coproxy.io/web-proxy");

    public override Task<ProxyPageResult> FetchAsync(string targetUrl, string? waitSelector = null, CancellationToken cancellationToken = default)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["url"] = targetUrl });
        return Browser.FetchAsync(
            SubmissionUri,
            Id,
            DisplayName,
            waitSelector,
            HttpMethod.Post,
            body.ReadAsStringAsync(cancellationToken),
            cancellationToken,
            bootstrapUri: new Uri(HomepageUrl),
            activateInlineProxyScript: true);
    }

    internal static Task DisposeSharedBrowserAsync() => Browser.DisposeAsync();

    private static readonly WebProxyBrowserHost Browser = new();
}

/// <summary>LumiProxy 使用其页面脚本约定的 area/u 参数编码后跳转到代理页面。</summary>
public sealed class LumiProxyWebProxyProvider : HttpWebProxyProviderBase
{
    public override string Id => "lumiproxy";
    public override string DisplayName => "LumiProxy";
    public override string HomepageUrl => "https://www.lumiproxy.com/zh-hans/online-proxy/proxysite/";

    internal static string BuildProxyUrl(string targetUrl, string area = "")
    {
        var value = $"area={area}&u={EnsureHttps(targetUrl)}";
        var bytes = Encoding.UTF8.GetBytes(value);
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 123;
        var encoded = Convert.ToBase64String(bytes);
        return $"https://webproxy.lumiproxy.com/request?p={Uri.EscapeDataString(encoded)}";
    }

    public override Task<ProxyPageResult> FetchAsync(string targetUrl, string? waitSelector = null, CancellationToken cancellationToken = default)
    {
        return Browser.FetchAsync(
            new Uri(BuildProxyUrl(targetUrl)),
            Id,
            DisplayName,
            waitSelector,
            HttpMethod.Get,
            null,
            cancellationToken,
            bootstrapUri: new Uri(HomepageUrl),
            navigateFromBootstrap: true);
    }

    internal static Task DisposeSharedBrowserAsync() => Browser.DisposeAsync();

    private static readonly WebProxyBrowserHost Browser = new();

    private static string EnsureHttps(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? value
            : "https://" + value;
}

/// <summary>
/// 为需要执行网页脚本、挑战或自动跳转的代理提供统一的隐藏 WebView2 宿主。
/// 代理实现只负责构造自己的入口地址和提交协议，不共享站点私有脚本细节。
/// </summary>
internal sealed class WebProxyBrowserHost : IDisposable
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly OperationLeaseTracker _lifetimeLease = new();
    private Window? _window;
    private WebView2? _webView;
    private bool _lifetimeDisposed;

    public Task<ProxyPageResult> FetchAsync(
        Uri entryUri,
        string providerId,
        string providerName,
        string? waitSelector,
        HttpMethod method,
        Task<string>? body,
        CancellationToken cancellationToken,
        Uri? bootstrapUri = null,
        bool activateInlineProxyScript = false,
        bool navigateFromBootstrap = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => FetchAsync(
                entryUri,
                providerId,
                providerName,
                waitSelector,
                method,
                body,
                cancellationToken,
                bootstrapUri,
                activateInlineProxyScript,
                navigateFromBootstrap)).Task.Unwrap();
        return FetchCoreAsync(
            entryUri,
            providerId,
            providerName,
            waitSelector,
            method,
            body,
            cancellationToken,
            bootstrapUri,
            activateInlineProxyScript,
            navigateFromBootstrap);
    }

    public void Dispose()
    {
        if (_lifetimeLease.IsDisposed && _lifetimeDisposed) return;
        var closeNow = _lifetimeLease.MarkDisposed();

        CancelLifetime();
        if (closeNow) DisposeLifetime();
        CloseHostOnDispatcher();
    }

    /// <summary>
    /// 取消在途抓取并等待 WebView2 宿主空闲后再释放。
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
    /// 在 <c>_gate</c> 内登记一次抓取并取出生命周期令牌；释放会等到最后一个租约归还，
    /// 避免排队中的抓取引用已释放的 CTS。
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
        void Close()
        {
            try { _webView?.Dispose(); } catch { }
            try { _window?.Close(); } catch { }
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.Invoke(Close);
        else Close();
    }

    private Task CloseHostAsync()
    {
        void Close()
        {
            try { _webView?.Dispose(); } catch { }
            try { _window?.Close(); } catch { }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(Close).Task;
        Close();
        return Task.CompletedTask;
    }

    private async Task<ProxyPageResult> FetchCoreAsync(
        Uri entryUri,
        string providerId,
        string providerName,
        string? waitSelector,
        HttpMethod method,
        Task<string>? body,
        CancellationToken cancellationToken,
        Uri? bootstrapUri,
        bool activateInlineProxyScript,
        bool navigateFromBootstrap)
    {
        // 静态宿主只有一个 WebView2，多个采集必须串行使用，避免导航与 cookie 相互覆盖。
        var acquired = TryBeginOperation(out var lifetimeToken);
        ObjectDisposedException.ThrowIf(!acquired, this);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        var operationToken = linkedSource.Token;
        await _fetchGate.WaitAsync(operationToken).ConfigureAwait(true);
        try
        {
            await EnsureHostAsync(operationToken);
            var core = _webView?.CoreWebView2 ?? throw new InvalidOperationException("WebView2 尚未初始化。");
            var requestBody = body is null ? null : await body.WaitAsync(operationToken);
            if (bootstrapUri is not null)
            {
                await NavigateAsync(core, bootstrapUri, HttpMethod.Get, null, operationToken, providerName);
                await Task.Delay(TimeSpan.FromMilliseconds(350), operationToken);
            }
            if (navigateFromBootstrap && method == HttpMethod.Get)
                await NavigateFromCurrentPageAsync(core, entryUri, operationToken, providerName);
            else
                await NavigateAsync(core, entryUri, method, requestBody, operationToken, providerName);
            return await WaitForPageAsync(core, providerId, providerName, waitSelector, operationToken, activateInlineProxyScript);
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
        var window = new Window
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
        window.Content = view;
        window.Show();
        try
        {
            await view.EnsureCoreWebView2Async();
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            try { window.Close(); } catch { }
            throw new InvalidOperationException("未检测到 WebView2 运行时，请安装 Microsoft Edge WebView2 运行时后重试。", ex);
        }
        catch
        {
            try { window.Close(); } catch { }
            throw;
        }
        lock (_gate)
        {
            _window = window;
            _webView = view;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task NavigateAsync(
        CoreWebView2 core,
        Uri entryUri,
        HttpMethod method,
        string? body,
        CancellationToken cancellationToken,
        string providerName)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(CreateNavigationException(providerName, entryUri, args.WebErrorStatus));
        }
        core.NavigationCompleted += OnCompleted;
        try
        {
            if (method == HttpMethod.Post)
            {
                // WebView2 may still read the request stream after NavigateWithWebResourceRequest returns.
                // Keep it alive until navigation completes instead of disposing it at call-site return.
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
                var request = core.Environment.CreateWebResourceRequest(
                    entryUri.AbsoluteUri,
                    "POST",
                    stream,
                    "Content-Type: application/x-www-form-urlencoded\r\n");
                try
                {
                    core.NavigateWithWebResourceRequest(request);
                }
                finally
                {
                    // The WebView2 request has taken ownership of the stream by this point.
                    // Do not dispose it before NavigationCompleted.
                }
                using (stream)
                {
                    using var postTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    postTimeout.CancelAfter(NavigationTimeout);
                    try
                    {
                        await completion.Task.WaitAsync(postTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        try { core.Stop(); } catch { }
                        throw new TimeoutException($"打开代理入口超时（{NavigationTimeout.TotalSeconds:0} 秒）。");
                    }
                }
                return;
            }
            else
            {
                core.Navigate(entryUri.AbsoluteUri);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(NavigationTimeout);
            try
            {
                await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { core.Stop(); } catch { }
                throw new TimeoutException($"打开代理入口超时（{NavigationTimeout.TotalSeconds:0} 秒）。");
            }
        }
        finally { core.NavigationCompleted -= OnCompleted; }
    }

    private static async Task NavigateFromCurrentPageAsync(
        CoreWebView2 core,
        Uri entryUri,
        CancellationToken cancellationToken,
        string providerName)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(CreateNavigationException(providerName, entryUri, args.WebErrorStatus));
        }

        core.NavigationCompleted += OnCompleted;
        try
        {
            var script = $"window.location.assign({JsonSerializer.Serialize(entryUri.AbsoluteUri)});";
            await core.ExecuteScriptAsync(script).WaitAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(NavigationTimeout);
            try
            {
                await completion.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { core.Stop(); } catch { }
                throw new TimeoutException($"打开 {providerName} 代理入口超时（{NavigationTimeout.TotalSeconds:0} 秒）。");
            }
        }
        finally { core.NavigationCompleted -= OnCompleted; }
    }

    private static InvalidOperationException CreateNavigationException(
        string providerName,
        Uri entryUri,
        CoreWebView2WebErrorStatus status)
    {
        var reason = status == CoreWebView2WebErrorStatus.Unknown
            ? "WebView2 未返回具体网络错误，代理入口可能返回了 HTTP 403 或被站点拦截"
            : $"网络状态 {status}";
        return new InvalidOperationException(
            $"{providerName} 代理入口打开失败（{reason}，地址：{entryUri.Host}）。请稍后重试或改用其他网页代理。");
    }

    private static async Task<ProxyPageResult> WaitForPageAsync(
        CoreWebView2 core,
        string providerId,
        string providerName,
        string? waitSelector,
        CancellationToken cancellationToken,
        bool activateInlineProxyScript)
    {
        var selector = JsonSerializer.Serialize(waitSelector ?? "");
        var script = "(() => { const selector = " + selector + "; " +
            "const body = (document.body?.innerText || '').slice(0, 800); " +
            "return JSON.stringify({ ready: !selector || !!document.querySelector(selector), href: location.href, title: document.title || '', body: body }); })()";
        var deadline = DateTime.UtcNow + WaitTimeout;
        var inlineScriptActivated = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var raw = await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                if (!string.IsNullOrWhiteSpace(raw) && raw is not "null")
                {
                    var state = JsonSerializer.Deserialize<string>(raw);
                    if (!string.IsNullOrWhiteSpace(state))
                    {
                        using var document = JsonDocument.Parse(state);
                        var root = document.RootElement;
                        var pageBody = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "";
                        var pageTitle = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                        if (LooksLikeBlockedPage(pageTitle, pageBody))
                            throw new InvalidOperationException($"{providerName} 返回了 Cloudflare 或站点拦截页面。请稍后重试或改用其他网页代理。");

                        if (activateInlineProxyScript && !inlineScriptActivated)
                        {
                            inlineScriptActivated = await ActivateInlineProxyScriptAsync(core, cancellationToken);
                        }

                        if (root.GetProperty("ready").GetBoolean())
                        {
                            var html = await WebViewHtmlReader.ReadAsync(
                                core,
                                providerName,
                                TimeSpan.FromSeconds(30),
                                cancellationToken);
                            var finalUrl = root.GetProperty("href").GetString() ?? "";
                            var cookies = await ReadCookiesAsync(core, finalUrl);
                            return new ProxyPageResult(html, finalUrl, cookies, providerId, providerName);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 页面跳转或挑战期间脚本上下文可能暂时不可用，继续等待。
            }
            catch (JsonException)
            {
                // 页面正在重载，下一轮重新读取。
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"{providerName} 未在 {WaitTimeout.TotalSeconds:0} 秒内返回可解析的 Telegram 页面。");
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static async Task<bool> ActivateInlineProxyScriptAsync(CoreWebView2 core, CancellationToken cancellationToken)
    {
        const string script = """
            (() => {
                const marker = document.querySelector('script#serverSelectorScript');
                if (!marker) return false;
                if (marker.dataset.qcdActivated === '1') return true;
                const replacement = document.createElement('script');
                for (const attribute of marker.attributes) {
                    if (attribute.name === 'type' || attribute.name === 'data-cf-settings' || attribute.name === 'data-cf-hash') continue;
                    replacement.setAttribute(attribute.name, attribute.value);
                }
                replacement.type = 'text/javascript';
                replacement.text = marker.textContent || '';
                marker.dataset.qcdActivated = '1';
                marker.replaceWith(replacement);
                return true;
            })()
            """;
        var raw = await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "true\r\n", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBlockedPage(string title, string body)
    {
        var text = $"{title}\n{body}";
        return text.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("you have been blocked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable to access", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadCookiesAsync(CoreWebView2 core, string finalUrl)
    {
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri)) return "";
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(uri.AbsoluteUri);
            return string.Join("; ", cookies.Where(cookie => !string.IsNullOrEmpty(cookie.Name)).Select(cookie => $"{cookie.Name}={cookie.Value}"));
        }
        catch { return ""; }
    }
}
