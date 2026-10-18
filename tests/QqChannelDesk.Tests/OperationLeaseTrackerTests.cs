using QqChannelDesk.Services;
using Xunit;

namespace QqChannelDesk.Tests;

public sealed class OperationLeaseTrackerTests
{
    [Fact]
    public void MarkDisposedWithoutLeasesAllowsImmediateRelease()
    {
        var tracker = new OperationLeaseTracker();

        Assert.True(tracker.MarkDisposed());
        Assert.True(tracker.IsDisposed);
        Assert.False(tracker.TryAcquireLease());
    }

    [Fact]
    public void ReleaseIsDeferredUntilLastLeaseReturns()
    {
        var tracker = new OperationLeaseTracker();
        Assert.True(tracker.TryAcquireLease());
        Assert.True(tracker.TryAcquireLease());
        Assert.Equal(2, tracker.ActiveCount);

        Assert.False(tracker.MarkDisposed());
        Assert.False(tracker.TryAcquireLease());
        Assert.False(tracker.TryReleaseLease());
        Assert.True(tracker.TryReleaseLease());
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void ConcurrentReleasesOnlyGrantOneReleasePermit()
    {
        var tracker = new OperationLeaseTracker();
        const int leaseCount = 64;
        for (var i = 0; i < leaseCount; i++)
        {
            Assert.True(tracker.TryAcquireLease());
        }

        Assert.Equal(leaseCount, tracker.ActiveCount);
        Assert.False(tracker.MarkDisposed());

        var releaseWinners = 0;
        Parallel.For(0, leaseCount, _ =>
        {
            if (tracker.TryReleaseLease())
            {
                Interlocked.Increment(ref releaseWinners);
            }
        });

        Assert.Equal(1, releaseWinners);
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void RepeatedMarkDisposedKeepsLeaseAccountingStable()
    {
        var tracker = new OperationLeaseTracker();
        Assert.True(tracker.TryAcquireLease());

        Assert.False(tracker.MarkDisposed());
        Assert.False(tracker.MarkDisposed());
        Assert.True(tracker.TryReleaseLease());
        Assert.True(tracker.MarkDisposed());
    }
}
