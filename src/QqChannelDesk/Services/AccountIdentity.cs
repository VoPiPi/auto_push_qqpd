using System.Security.Cryptography;
using System.Text;

namespace QqChannelDesk.Services;

/// <summary>
/// The CLI does not currently expose a documented immutable account id.  This
/// local fingerprint keeps the single active CLI session's data separated
/// without ever inspecting or persisting credentials.
/// </summary>
public sealed record CurrentAccountIdentity(string Nickname, string GlobalNickname, string AccountKey)
{
    public static CurrentAccountIdentity Create(string globalNickname, string nickname)
    {
        var normalizedGlobal = Normalize(globalNickname);
        var normalizedNickname = Normalize(nickname);
        if (normalizedGlobal.Length == 0)
            throw new ArgumentException("频道工具未返回账号标识，请重新登录。", nameof(globalNickname));
        if (normalizedNickname.Length == 0)
            throw new ArgumentException("频道工具未返回账号名称，请重新登录。", nameof(nickname));

        var input = Encoding.UTF8.GetBytes(normalizedGlobal + "\n" + normalizedNickname);
        var key = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return new CurrentAccountIdentity(normalizedNickname, normalizedGlobal, key);
    }

    private static string Normalize(string value) =>
        string.Join(' ', (value ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// In-memory account scope for the current CLI login.  Clearing it immediately
/// makes every production store query return an empty account-scoped result.
/// </summary>
public sealed class AccountContext
{
    // Swapped atomically so background scheduler threads never observe a
    // half-updated identity while the UI thread switches accounts. Callers that
    // need several fields consistently must read Identity once.
    private volatile CurrentAccountIdentity? _current;

    public CurrentAccountIdentity? Identity => _current;
    public string? CurrentAccountKey => _current?.AccountKey;
    public string? Nickname => _current?.Nickname;
    public string? GlobalNickname => _current?.GlobalNickname;
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(CurrentAccountKey);

    public string RequireAccountKey()
    {
        return CurrentAccountKey ?? throw new InvalidOperationException("尚未确认登录账号，请登录后重试。");
    }

    public void Set(CurrentAccountIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _current = identity;
    }

    public void Clear()
    {
        _current = null;
    }
}
