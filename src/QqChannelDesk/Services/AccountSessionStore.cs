using System.IO;
using System.Text.Json;

namespace QqChannelDesk.Services;

public sealed class AccountSessionStore
{
    private readonly string _sessionPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AccountSessionStore(string? sessionPath = null)
    {
        _sessionPath = sessionPath ?? Path.Combine(AppContext.BaseDirectory, "account.session.json");
    }

    public string SessionPath => _sessionPath;

    public static AccountProfile ResolveDisplay(string loginState, string? currentNickname, DateTimeOffset? loginAt)
    {
        if (!string.Equals(loginState, "已登录", StringComparison.Ordinal))
            return new AccountProfile(string.IsNullOrWhiteSpace(loginState) ? "状态未知" : loginState, null);
        return new AccountProfile(
            string.IsNullOrWhiteSpace(currentNickname) ? "已登录账号" : currentNickname.Trim(),
            loginAt);
    }

    public async Task<AccountProfile?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_sessionPath)) return null;

        try
        {
            await using var stream = File.OpenRead(_sessionPath);
            var profile = await JsonSerializer.DeserializeAsync<AccountProfile>(
                stream,
                cancellationToken: cancellationToken);
            return string.IsNullOrWhiteSpace(profile?.Nickname) ? null : profile;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<AccountProfile> SaveLoginAsync(
        string nickname,
        DateTimeOffset loginAt,
        CancellationToken cancellationToken = default)
    {
        var profile = new AccountProfile(NormalizeNickname(nickname), loginAt);
        await WriteAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<AccountProfile> SaveLoginAsync(
        CurrentAccountIdentity identity,
        DateTimeOffset loginAt,
        CancellationToken cancellationToken = default)
    {
        var profile = new AccountProfile(identity.Nickname, loginAt, identity.GlobalNickname, identity.AccountKey);
        await WriteAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<AccountProfile> SaveDetectedAsync(
        string nickname,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(cancellationToken);
        var normalizedNickname = NormalizeNickname(nickname);
        var loginAt = string.Equals(existing?.Nickname, normalizedNickname, StringComparison.Ordinal)
            ? existing?.LoginAt
            : null;
        var profile = new AccountProfile(normalizedNickname, loginAt);
        await WriteAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<AccountProfile> SaveDetectedAsync(
        CurrentAccountIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(cancellationToken);
        var loginAt = string.Equals(existing?.AccountKey, identity.AccountKey, StringComparison.Ordinal)
            ? existing?.LoginAt
            : null;
        var profile = new AccountProfile(identity.Nickname, loginAt, identity.GlobalNickname, identity.AccountKey);
        await WriteAsync(profile, cancellationToken);
        return profile;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_sessionPath)) File.Delete(_sessionPath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteAsync(AccountProfile profile, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _sessionPath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, profile, cancellationToken: cancellationToken);
            }

            File.Move(temporaryPath, _sessionPath, true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string NormalizeNickname(string nickname) =>
        string.IsNullOrWhiteSpace(nickname) ? "未知账号" : nickname.Trim();
}

public sealed record AccountProfile(
    string Nickname,
    DateTimeOffset? LoginAt,
    string? GlobalNickname = null,
    string? AccountKey = null);
