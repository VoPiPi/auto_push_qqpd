namespace QqChannelDesk.Services;

/// <summary>
/// Keeps repeated scheduled failures for the same channel from producing one tray
/// notification per automatic retry. Claims are cleared after a successful run or
/// when the notification itself could not be shown.
/// </summary>
public sealed class TelegramFailureNotificationGate
{
    private readonly object _lock = new();
    private readonly HashSet<long> _claimedScheduleIds = [];

    public bool TryClaim(long scheduleId)
    {
        lock (_lock) return _claimedScheduleIds.Add(scheduleId);
    }

    public void Release(long scheduleId)
    {
        lock (_lock) _claimedScheduleIds.Remove(scheduleId);
    }

    public void Reset(long scheduleId) => Release(scheduleId);
}
