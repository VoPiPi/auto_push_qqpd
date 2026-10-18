using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace QqChannelDesk.Services;

internal static class WebViewHtmlReader
{
    internal const int MaxHtmlCharacters = 5_000_000;
    private const int MaxSerializedResponseCharacters = MaxHtmlCharacters * 6 + 4096;

    internal static async Task<string> ReadAsync(
        CoreWebView2 core,
        string providerName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var script =
            "(function(){var html=document.documentElement?document.documentElement.outerHTML:'';" +
            "var limit=" + MaxHtmlCharacters + ";" +
            "return {tooLarge:html.length>limit,length:html.length,html:html.length>limit?'':html};})()";

        string raw;
        try
        {
            raw = await core.ExecuteScriptAsync(script).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("页面内容读取超时。");
        }

        if (raw.Length > MaxSerializedResponseCharacters)
            throw new InvalidOperationException($"{providerName} 返回的页面序列化结果超过安全上限。");
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
            throw new InvalidOperationException($"{providerName} 返回空页面。");

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var tooLarge = root.TryGetProperty("tooLarge", out var tooLargeElement) &&
                tooLargeElement.ValueKind == JsonValueKind.True;
            var length = root.TryGetProperty("length", out var lengthElement) &&
                lengthElement.TryGetInt32(out var parsedLength)
                ? parsedLength
                : 0;
            if (tooLarge || length > MaxHtmlCharacters)
                throw new InvalidOperationException(
                    $"{providerName} 返回页面超过 {MaxHtmlCharacters / 1_000_000} MB 限制。");

            var html = root.TryGetProperty("html", out var htmlElement) &&
                htmlElement.ValueKind == JsonValueKind.String
                ? htmlElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(html))
                throw new InvalidOperationException($"{providerName} 返回空页面。");
            return html;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{providerName} 页面内容无法读取。");
        }
    }
}
