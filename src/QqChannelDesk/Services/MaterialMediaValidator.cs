using System.IO;

namespace QqChannelDesk.Services;

public static class MaterialMediaValidator
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".webm", ".avi", ".mkv"];

    public static string Validate(
        string type,
        string title,
        IReadOnlyList<string> media,
        bool allowUnknownSources = false)
    {
        if (type == "text") return media.Count == 0 ? "" : "文本素材不能包含媒体文件或链接。";
        if (type == "image")
        {
            if (media.Count == 0) return "图片素材至少需要一个有效的图片链接或本地文件。";
            if (IsMixedMedia(media)) return "图片和视频媒体不能混合，请拆分为不同素材。";
            var limit = string.IsNullOrWhiteSpace(title) ? 18 : 50;
            if (media.Count > limit) return $"图片数量不能超过 {limit} 个。";
            if (media.Any(item => !IsValidSource(item, "image", allowUnknownSources)))
                return "图片素材包含无效链接或文件；请使用图片文件，链接须为公开 HTTP/HTTPS 地址。";
        }
        else if (type == "video")
        {
            if (media.Count != 1)
                return "视频素材需要一个视频链接或本地文件。";
            if (IsMixedMedia(media)) return "图片和视频媒体不能混合，请拆分为不同素材。";
            if (media.Any(item => !IsValidSource(item, "video", allowUnknownSources)))
                return "视频素材包含无效链接或文件；请使用视频文件，链接须为公开 HTTP/HTTPS 地址。";
        }
        else return "素材类型无效。";

        return "";
    }

    public static bool IsValidSource(string value, string expectedType, bool allowUnknownExtension = false)
    {
        var extensions = expectedType switch
        {
            "image" => ImageExtensions,
            "video" => VideoExtensions,
            _ => []
        };
        if (extensions.Length == 0 || string.IsNullOrWhiteSpace(value)) return false;

        if (IsLocalPath(value))
        {
            if (!File.Exists(value)) return false;
            var extension = Path.GetExtension(value);
            return extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                || allowUnknownExtension && IsUnknownExtension(extension);
        }

        if (!PublicArticleFetcher.TryNormalizePublicUrl(value, out var uri, out _)) return false;
        var urlExtension = Path.GetExtension(uri.AbsolutePath);
        return extensions.Contains(urlExtension, StringComparer.OrdinalIgnoreCase)
            || allowUnknownExtension && IsUnknownExtension(urlExtension);
    }

    public static bool IsLocalPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return false;
        return Path.IsPathFullyQualified(value);
    }

    public static string? InferType(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceTypes = sources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(GetSourceType)
            .ToArray();
        if (sourceTypes.Length == 0) return "text";
        if (sourceTypes.Any(type => type is null)) return null;

        var distinctTypes = sourceTypes.Distinct(StringComparer.Ordinal).ToArray();
        return distinctTypes.Length == 1 ? distinctTypes[0] : null;
    }

    public static bool IsMixedMedia(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var types = sources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(GetSourceType)
            .Where(type => type is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return types.Contains("image") && types.Contains("video");
    }

    private static string? GetSourceType(string source)
    {
        var extension = IsLocalPath(source)
            ? Path.GetExtension(source)
            : Uri.TryCreate(source, UriKind.Absolute, out var uri)
                ? Path.GetExtension(uri.AbsolutePath)
                : "";
        if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return "video";
        if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return "image";
        return null;
    }

    private static bool IsUnknownExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension)
        || string.Equals(extension, ".img", StringComparison.OrdinalIgnoreCase)
        || string.Equals(extension, ".video", StringComparison.OrdinalIgnoreCase);
}
