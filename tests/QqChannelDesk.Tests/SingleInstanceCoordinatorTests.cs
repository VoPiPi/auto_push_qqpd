using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void SecondaryInstanceSignalsPrimaryWithoutStartingRuntimeTwice()
    {
        var applicationId = CreateApplicationId();
        using var primary = new SingleInstanceCoordinator(applicationId);
        using var activated = new ManualResetEventSlim(false);
        var runtimeStarts = 0;

        Assert.True(primary.IsPrimary);
        primary.StartActivationListener(activated.Set);
        Assert.True(primary.TryStartPrimary(() => runtimeStarts++));
        Assert.False(primary.TryStartPrimary(() => runtimeStarts++));

        using var secondary = new SingleInstanceCoordinator(applicationId);
        Assert.False(secondary.IsPrimary);
        Assert.False(secondary.TryStartPrimary(() => runtimeStarts++));
        Assert.True(secondary.NotifyPrimaryInstance());

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, runtimeStarts);
    }

    [Fact]
    public void ReleasedPrimaryAllowsTheNextInstanceToStart()
    {
        var applicationId = CreateApplicationId();
        var first = new SingleInstanceCoordinator(applicationId);
        Assert.True(first.IsPrimary);

        first.Dispose();

        using var replacement = new SingleInstanceCoordinator(applicationId);
        Assert.True(replacement.IsPrimary);
    }

    [Fact]
    public void PrimaryActivationSignalRemainsPendingUntilListenerStarts()
    {
        var applicationId = CreateApplicationId();
        using var primary = new SingleInstanceCoordinator(applicationId);
        using var secondary = new SingleInstanceCoordinator(applicationId);
        using var activated = new ManualResetEventSlim(false);

        Assert.True(secondary.NotifyPrimaryInstance());
        primary.StartActivationListener(activated.Set);

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
    }

    private static string CreateApplicationId() =>
        $"QqChannelDesk.Tests.{Guid.NewGuid():N}";
}
