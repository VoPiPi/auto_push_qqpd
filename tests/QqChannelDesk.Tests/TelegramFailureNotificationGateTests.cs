using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class TelegramFailureNotificationGateTests
{
    [Fact]
    public void ConsecutiveFailuresForSameScheduleOnlyNotifyOnce()
    {
        var gate = new TelegramFailureNotificationGate();

        Assert.True(gate.TryClaim(11));
        Assert.False(gate.TryClaim(11));
        Assert.True(gate.TryClaim(12));
    }

    [Fact]
    public void ResetAllowsNotificationAfterSuccessAndReleaseAllowsRetry()
    {
        var gate = new TelegramFailureNotificationGate();
        Assert.True(gate.TryClaim(11));

        gate.Reset(11);
        Assert.True(gate.TryClaim(11));

        gate.Release(11);
        Assert.True(gate.TryClaim(11));
    }
}
