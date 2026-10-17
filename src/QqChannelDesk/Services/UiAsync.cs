using System.Windows.Threading;

namespace QqChannelDesk.Services;

public static class UiAsync
{
    public static bool Post(
        Dispatcher dispatcher,
        Action action,
        DispatcherPriority priority = DispatcherPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return false;

        try
        {
            _ = dispatcher.BeginInvoke(priority, action);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            // The dispatcher can begin shutting down between the check and the queue operation.
            return false;
        }
    }

    public static void Observe(string context, Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = GuardAsync(context, () => task);
    }

    public static void Run(string context, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = GuardAsync(context, action);
    }

    public static async Task GuardAsync(string context, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Instance.WriteException($"UI 异步操作未处理：{context}", ex);
        }
    }
}
