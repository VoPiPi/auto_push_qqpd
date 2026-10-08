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
            throw new ArgumentException("CLI 未返回 global_nickname，无法确认当前账号。", nameof(globalNickname));
        if (normalizedNickname.Length == 0)
            throw new ArgumentException("CLI 未返回 nickname，无法确认当前账号。", nameof(nickname));

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
    public string? CurrentAccountKey { get; private set; }
    public string? Nickname { get; private set; }
    public string? GlobalNickname { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(CurrentAccountKey);

    public string RequireAccountKey()
    {
        return CurrentAccountKey ?? throw new InvalidOperationException("当前未确认登录账号，不能访问账号数据。");
    }

    public void Set(CurrentAccountIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        CurrentAccountKey = identity.AccountKey;
        Nickname = identity.Nickname;
        GlobalNickname = identity.GlobalNickname;
    }

    public void Clear()
    {
        CurrentAccountKey = null;
        Nickname = null;
        GlobalNickname = null;
    }
}
