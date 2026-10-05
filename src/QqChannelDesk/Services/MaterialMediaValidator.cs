using System.IO;

namespace QqChannelDesk.Services;

public static class MaterialMediaValidator
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".webm", ".avi", ".mkv"];

    public static string Validate(string type, string title, IReadOnlyList<string> media)
    {
        if (type == "text") return media.Count == 0 ? "" : "文本素材不能包含媒体文件或链接。";
        if (type == "image")
        {
            if (media.Count == 0) return "图片素材至少需要一个有效的图片链接或本地文件。";
            var limit = string.IsNullOrWhiteSpace(title) ? 18 : 50;
            if (media.Count > limit) return $"图片数量不能超过 {limit} 个。";
            if (media.Any(item => !IsValidSource(item, "image")))
                return "图片素材包含无效链接或文件；请使用图片文件，链接须为公开 HTTP/HTTPS 地址。";
        }
        else if (type == "video")
        {
            if (media.Count != 1)
                return "视频素材需要一个视频链接或本地文件。";
            if (media.Any(item => !IsValidSource(item, "video")))
                return "视频素材包含无效链接或文件；请使用视频文件，链接须为公开 HTTP/HTTPS 地址。";
        }
        else return "素材类型无效。";

        return "";
    }

    public static bool IsValidSource(string value, string expectedType)
    {
        var extensions = expectedType switch
        {
            "image" => ImageExtensions,
            "video" => VideoExtensions,
            _ => []
        };
        if (extensions.Length == 0 || string.IsNullOrWhiteSpace(value)) return false;

        if (IsLocalPath(value))
            return File.Exists(value) && extensions.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase);

        return PublicArticleFetcher.TryNormalizePublicUrl(value, out var uri, out _) &&
               extensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);
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
        foreach (var source in sources)
        {
            var extension = IsLocalPath(source) ? Path.GetExtension(source) :
                Uri.TryCreate(source, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath) : "";
            if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return "video";
            if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return "image";
        }
        return null;
    }
}
