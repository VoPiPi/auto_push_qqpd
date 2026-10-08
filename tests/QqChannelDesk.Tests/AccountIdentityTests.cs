using System.Security.Cryptography;
using System.Text;
using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class AccountIdentityTests
{
    [Fact]
    public void Create_UsesStableSha256OfNormalizedIdentity()
    {
        var identity = CurrentAccountIdentity.Create("  Global   Name ", "  Nickname ");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Global Name\nNickname"))).ToLowerInvariant();

        Assert.Equal(expected, identity.AccountKey);
        Assert.Equal(identity.AccountKey, CurrentAccountIdentity.Create("Global Name", "Nickname").AccountKey);
        Assert.Equal("Global Name", identity.GlobalNickname);
        Assert.Equal("Nickname", identity.Nickname);
    }

    [Fact]
    public void Create_RejectsMissingIdentityParts()
    {
        Assert.Throws<ArgumentException>(() => CurrentAccountIdentity.Create("", "Nickname"));
        Assert.Throws<ArgumentException>(() => CurrentAccountIdentity.Create("Global", "   "));
        Assert.Throws<ArgumentException>(() => CurrentAccountIdentity.Create(" \t\n", "Nickname"));
    }

    [Fact]
    public void DifferentIdentityParts_ProduceDifferentKeys()
    {
        var first = CurrentAccountIdentity.Create("Global A", "Nickname");
        var second = CurrentAccountIdentity.Create("Global B", "Nickname");
        var third = CurrentAccountIdentity.Create("Global A", "Other");

        Assert.NotEqual(first.AccountKey, second.AccountKey);
        Assert.NotEqual(first.AccountKey, third.AccountKey);
    }

    [Fact]
    public void Context_ClearRemovesAuthentication()
    {
        var context = new AccountContext();
        context.Set(CurrentAccountIdentity.Create("Global", "Nickname"));
        Assert.True(context.IsAuthenticated);

        context.Clear();

        Assert.False(context.IsAuthenticated);
        Assert.Null(context.CurrentAccountKey);
        Assert.Throws<InvalidOperationException>(() => context.RequireAccountKey());
    }
}
