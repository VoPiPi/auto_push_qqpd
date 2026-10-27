namespace QqChannelDesk.Services.Collection;

public static class UrlNormalizer
{
    public static string Canonicalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = "",
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
        };
        if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80) ||
            (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
        {
            builder.Port = -1;
        }

        builder.Query = NormalizeQuery(uri.Query);
        if (uri.AbsolutePath.Length > 1 && uri.AbsolutePath.EndsWith('/'))
        {
            builder.Path = uri.AbsolutePath.TrimEnd('/');
        }

        var canonical = builder.Uri;
        return canonical.AbsoluteUri;
    }

    public static string CanonicalItemId(string? url, string? sourceItemId = null)
    {
        var value = sourceItemId?.Trim();
        return value is { Length: > 0 } ? value : Canonicalize(url);
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return "";
        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var index = pair.IndexOf('=');
                var key = index < 0 ? Uri.UnescapeDataString(pair) : Uri.UnescapeDataString(pair[..index]);
                var value = index < 0 ? "" : Uri.UnescapeDataString(pair[(index + 1)..]);
                return (Key: key, Value: value);
            })
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => pair.Value.Length == 0 ? Uri.EscapeDataString(pair.Key) : $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        return string.Join("&", pairs);
    }
}
