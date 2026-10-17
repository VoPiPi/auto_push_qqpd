namespace QqChannelDesk.Services;

/// <summary>
/// 统计在途操作数量，用于把“开始释放”和“真正释放”分离：
/// 释放方调用 <see cref="MarkDisposed"/> 后不会立刻释放共享资源，
/// 只有最后一个操作调用 <see cref="TryReleaseLease"/> 时才会拿到释放许可。
/// 所有成员都可在多线程下安全调用。
/// </summary>
internal sealed class OperationLeaseTracker
{
    private readonly object _gate = new();
    private int _activeCount;
    private bool _disposeRequested;

    public bool IsDisposed
    {
        get { lock (_gate) return _disposeRequested; }
    }

    public int ActiveCount
    {
        get { lock (_gate) return _activeCount; }
    }

    /// <summary>
    /// 登记一次操作。已经请求释放时返回 false，调用方必须放弃该操作。
    /// </summary>
    public bool TryAcquireLease()
    {
        lock (_gate)
        {
            if (_disposeRequested) return false;
            _activeCount++;
            return true;
        }
    }

    /// <summary>
    /// 归还一次操作；返回 true 表示调用方是最后一个操作，需要执行真正的资源释放。
    /// </summary>
    public bool TryReleaseLease()
    {
        lock (_gate)
        {
            if (_activeCount > 0) _activeCount--;
            return _disposeRequested && _activeCount == 0;
        }
    }

    /// <summary>
    /// 请求释放；返回 true 表示当前没有在途操作，调用方应立即释放共享资源。
    /// </summary>
    public bool MarkDisposed()
    {
        lock (_gate)
        {
            _disposeRequested = true;
            return _activeCount == 0;
        }
    }
}
