namespace QqChannelDesk.Services;

/// <summary>
/// Process lifetime information for the current UI session. It is kept in
/// memory only and is intentionally not persisted with account data.
/// </summary>
public sealed class RuntimeSessionState
{
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public TimeSpan Elapsed => DateTimeOffset.Now - StartedAt;

    public string ElapsedDisplay => Elapsed.ToString(@"hh\:mm\:ss");
}
