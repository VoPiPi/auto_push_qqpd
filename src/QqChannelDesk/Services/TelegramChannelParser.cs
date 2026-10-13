using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace QqChannelDesk.Services;

/// <summary>
/// 解析 t.me 频道预览页（t.me/s/&lt;频道&gt;）与单条消息页，提取消息文本、发布时间和媒体引用。
/// 页面可能来自网页代理，节点链接通过 __pot 参数还原为原始地址。
/// </summary>
public static partial class TelegramChannelParser
{
    private const int MaximumTextLength = 100_000;
    private static readonly Regex BackgroundImageRegex = BackgroundImage();

    public static bool IsTelegramHost(string? host)
    {
        var normalized = host?.Trim().TrimStart('.').ToLowerInvariant() ?? "";
        return normalized is "t.me" or "telegram.me";
    }

    public static bool IsChannelPreviewUrl(Uri uri) =>
        IsTelegramHost(uri.Host) && uri.AbsolutePath.TrimEnd('/').StartsWith("/s/", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<TelegramChannelMessage> ParseChannelPage(string html, string channelName)
    {
        var doc = Load(html);
        var title = Clean(doc.DocumentNode.SelectSingleNode("//div[contains(@class,'tgme_channel_info_header_title')]")?.InnerText ?? "");
        var messages = new List<TelegramChannelMessage>();
        foreach (var node in MessageNodes(doc))
        {
            var message = ReadMessage(node, title.Length > 0 ? title : channelName);
            if (message is not null) messages.Add(message);
        }
        return messages;
    }

    public static TelegramChannelMessage? TryParseSingleMessage(string html, Uri fallbackUrl)
    {
        var doc = Load(html);
        var title = Clean(doc.DocumentNode.SelectSingleNode("//div[contains(@class,'tgme_channel_info_header_title')]")?.InnerText ?? "");
        foreach (var node in MessageNodes(doc))
        {
            var message = ReadMessage(node, title.Length > 0 ? title : fallbackUrl.Host);
            if (message is not null) return message with { Url = message.Url.Length > 0 ? message.Url : fallbackUrl.AbsoluteUri };
        }
        return null;
    }

    public static string? TryRestoreOriginalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        // 直接拿原始 HTML 属性值时可能还带 &amp;，先还原成普通查询分隔符。
        var trimmed = url.Trim().Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        var origin = ReadPotOrigin(uri.Query);
        if (origin is null) return uri.AbsoluteUri;
        return origin.TrimEnd('/') + uri.AbsolutePath;
    }

    private static TelegramChannelMessage? ReadMessage(HtmlNode node, string channelTitle)
    {
        var dataPost = node.GetAttributeValue("data-post", "");
        var url = "";
        if (dataPost.Contains('/'))
        {
            url = "https://t.me/" + dataPost.Trim('/');
        }
        else
        {
            var dateLink = ReadAttribute(FindByClass(node, "a", "tgme_widget_message_date"), "href");
            url = TryRestoreOriginalUrl(dateLink) ?? "";
        }

        var publishedAt = DateTimeOffset.MinValue;
        var timeNode = node.SelectSingleNode(".//time[@datetime]");
        if (timeNode is not null && DateTimeOffset.TryParse(timeNode.GetAttributeValue("datetime", ""), out var parsed)) publishedAt = parsed;

        var textNode = FindByClass(node, "div", "tgme_widget_message_text");
        var text = textNode is null ? "" : NormalizeBlockText(ReadBlockInnerText(textNode));
        if (text.Length > MaximumTextLength) text = text[..MaximumTextLength];

        var media = new List<TelegramMediaRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var photo in node.SelectNodes(".//a") ?? Enumerable.Empty<HtmlNode>())
        {
            if (!HasClass(photo, "tgme_widget_message_photo_wrap")) continue;
            var proxied = ReadBackgroundImage(ReadAttribute(photo, "style"));
            if (proxied is null) continue;
            AddMedia(media, seen, proxied, "image");
        }
        foreach (var video in node.SelectNodes(".//video") ?? Enumerable.Empty<HtmlNode>())
        {
            if (!HasClass(video, "tgme_widget_message_video")) continue;
            var proxied = ReadAttribute(video, "src");
            if (proxied.Length == 0) continue;
            AddMedia(media, seen, proxied, "video");
        }

        if (text.Length == 0 && media.Count == 0) return null;
        var notes = channelTitle.Length > 0 ? $"来自 tg 频道 {channelTitle}" : "";
        // TG 消息统一作为短帖正文处理。Title 字段保留在模型和数据库中，供后续产品迭代使用。
        return new TelegramChannelMessage(url, "t.me", publishedAt == DateTimeOffset.MinValue ? null : publishedAt, "", text, media, notes);
    }

    private static IEnumerable<HtmlNode> MessageNodes(HtmlDocument doc)
    {
        var nodes = doc.DocumentNode.SelectNodes("//div");
        if (nodes is null) yield break;
        foreach (var node in nodes)
        {
            if (HasClass(node, "tgme_widget_message") && !HasClass(node, "tgme_widget_message_service"))
                yield return node;
        }
    }

    /// <summary>
    /// 读取属性值并还原 HTML 实体，代理页面里的 &amp; 会影响 __pot 参数的识别。
    /// </summary>
    private static string ReadAttribute(HtmlNode? node, string name) =>
        node is null ? "" : HtmlEntity.DeEntitize(node.GetAttributeValue(name, "")).Trim();

    private static void AddMedia(List<TelegramMediaRef> media, HashSet<string> seen, string proxiedUrl, string kind)
    {
        if (!seen.Add(proxiedUrl)) return;
        media.Add(new TelegramMediaRef(proxiedUrl, TryRestoreOriginalUrl(proxiedUrl) ?? proxiedUrl, kind));
    }

    private static HtmlNode? FindByClass(HtmlNode scope, string tagName, string className)
    {
        foreach (var node in scope.SelectNodes($".//{tagName}") ?? Enumerable.Empty<HtmlNode>())
            if (HasClass(node, className)) return node;
        return null;
    }

    private static bool HasClass(HtmlNode node, string className)
    {
        var value = node.GetAttributeValue("class", "");
        if (value.Length == 0) return false;
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(token, className, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string? ReadBackgroundImage(string style)
    {
        if (string.IsNullOrWhiteSpace(style)) return null;
        var match = BackgroundImageRegex.Match(style);
        if (!match.Success) return null;
        var url = HtmlEntity.DeEntitize(match.Groups["url"].Value).Trim();
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
    }

    private static string? ReadPotOrigin(string query)
    {
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0) return null;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (!string.Equals(pair[..separator], "__pot", StringComparison.Ordinal)) continue;
            var decoded = DecodeBase64UrlSafe(Uri.UnescapeDataString(pair[(separator + 1)..]));
            if (decoded is not null && (decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || decoded.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                return decoded;
        }
        return null;
    }

    private static string? DecodeBase64UrlSafe(string value)
    {
        try
        {
            var builder = new StringBuilder(value);
            builder.Replace('-', '+').Replace('_', '/');
            while (builder.Length % 4 != 0) builder.Append('=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(builder.ToString()));
        }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);
        return doc;
    }

    /// <summary>
    /// 规范化多行正文：保留换行作为段落分隔，段内连续空白折叠为单个空格，去掉首尾空行，段间最多保留一个空行。
    /// </summary>
    private static string NormalizeBlockText(string value)
    {
        var flattened = HtmlEntity.DeEntitize(value).Replace(' ', ' ').Replace("\r\n", "\n").Replace('\r', '\n');
        var builder = new StringBuilder();
        var pendingBlank = false;
        foreach (var rawLine in flattened.Split('\n'))
        {
            var line = CollapseWhitespace(rawLine);
            if (line.Length == 0)
            {
                if (builder.Length > 0) pendingBlank = true;
                continue;
            }
            if (builder.Length > 0) builder.Append(pendingBlank ? "\n\n" : '\n');
            pendingBlank = false;
            builder.Append(line);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 读取元素文本并把 br 还原成换行：HtmlAgilityPack 的 InnerText 会直接丢弃 br 元素，多行消息会粘连成一行。
    /// </summary>
    private static string ReadBlockInnerText(HtmlNode node)
    {
        var builder = new StringBuilder();
        AppendInnerText(node, builder);
        return builder.ToString();
    }

    private static void AppendInnerText(HtmlNode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                builder.Append(HtmlEntity.DeEntitize(child.InnerText));
                continue;
            }
            if (child.NodeType != HtmlNodeType.Element) continue;
            if (string.Equals(child.Name, "br", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('\n');
                continue;
            }
            AppendInnerText(child, builder);
        }
    }

    private static string CollapseWhitespace(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? string.Join(' ', parts) : "";

    private static string Clean(string value) => HtmlEntity.DeEntitize(value).Replace('\u00a0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
        ? string.Join(' ', parts).Trim()
        : "";

    [GeneratedRegex(@"url\(\s*['""]?(?<url>[^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundImage();
}

public sealed record TelegramChannelMessage(
    string Url,
    string SourceHost,
    DateTimeOffset? PublishedAt,
    string Title,
    string Text,
    IReadOnlyList<TelegramMediaRef> Media,
    string Notes);

public sealed record TelegramMediaRef(string ProxiedUrl, string OriginalUrl, string Kind);
