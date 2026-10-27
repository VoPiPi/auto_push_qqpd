using System.Security.Cryptography;
using System.Text;

namespace QqChannelDesk.Services.Collection;

public static class ContentFingerprint
{
    public static string Compute(string title, string content)
    {
        var normalizedTitle = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var normalizedContent = string.Join(' ', (content ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalizedContent.Length > 5000) normalizedContent = normalizedContent[..5000];
        var input = Encoding.UTF8.GetBytes($"{normalizedTitle}\n{normalizedContent}");
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
}
