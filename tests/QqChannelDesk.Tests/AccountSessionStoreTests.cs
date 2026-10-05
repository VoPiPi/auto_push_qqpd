using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class AccountSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QqChannelDeskAccountTests", Guid.NewGuid().ToString("N"));
    private readonly string _sessionPath;
    private readonly AccountSessionStore _store;

    public AccountSessionStoreTests()
    {
        _sessionPath = Path.Combine(_directory, "account.session.json");
        _store = new AccountSessionStore(_sessionPath);
    }

    [Fact]
    public async Task SaveLogin_PersistsNicknameAndLoginTime()
    {
        var loginAt = new DateTimeOffset(2026, 10, 2, 10, 30, 0, TimeSpan.FromHours(8));

        await _store.SaveLoginAsync("奈奈子", loginAt);

        var profile = Assert.IsType<AccountProfile>(await _store.GetAsync());
        Assert.Equal("奈奈子", profile.Nickname);
        Assert.Equal(loginAt, profile.LoginAt);
    }

    [Fact]
    public async Task SaveDetected_UpdatesNicknameAndClearsPreviousAccountsLoginTime()
    {
        var loginAt = new DateTimeOffset(2026, 10, 2, 10, 30, 0, TimeSpan.FromHours(8));
        await _store.SaveLoginAsync("旧昵称", loginAt);

        var profile = await _store.SaveDetectedAsync("新昵称");

        Assert.Equal("新昵称", profile.Nickname);
        Assert.Null(profile.LoginAt);
    }

    [Fact]
    public async Task Get_CorruptFile_ReturnsNullAndCanBeRecovered()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(_sessionPath, "{not-json");

        Assert.Null(await _store.GetAsync());

        var profile = await _store.SaveDetectedAsync("恢复账号");
        Assert.Equal("恢复账号", profile.Nickname);
        Assert.Null(profile.LoginAt);
        Assert.Equal("恢复账号", (await _store.GetAsync())?.Nickname);
    }

    [Fact]
    public async Task Clear_RemovesStoredAccountSession()
    {
        await _store.SaveLoginAsync("奈奈子", DateTimeOffset.Now);

        await _store.ClearAsync();

        Assert.Null(await _store.GetAsync());
    }

    [Theory]
    [InlineData("未登录")]
    [InlineData("授权可能过期")]
    [InlineData("状态未知")]
    public void ResolveDisplay_NeverShowsCachedNicknameWithoutConfirmedLogin(string state)
    {
        var loginAt = DateTimeOffset.Now;

        var display = AccountSessionStore.ResolveDisplay(state, "旧账号昵称", loginAt);

        Assert.Equal(state, display.Nickname);
        Assert.Null(display.LoginAt);
    }

    [Fact]
    public void ResolveDisplay_UsesOnlyCurrentNicknameWhenLoggedIn()
    {
        var loginAt = DateTimeOffset.Now;

        var display = AccountSessionStore.ResolveDisplay("已登录", "当前账号", loginAt);

        Assert.Equal("当前账号", display.Nickname);
        Assert.Equal(loginAt, display.LoginAt);
    }

    [Fact]
    public void ResolveDisplay_DoesNotReuseCachedNicknameWhenCurrentNicknameUnavailable()
    {
        var display = AccountSessionStore.ResolveDisplay("已登录", null, null);

        Assert.Equal("已登录账号", display.Nickname);
        Assert.Null(display.LoginAt);
    }

    public void Dispose()
    {
        if (File.Exists(_sessionPath)) File.Delete(_sessionPath);
        var temporaryPath = _sessionPath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, false);
    }
}
