namespace QqChannelDesk.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexSuffix = "InstanceMutex";
    private const string ActivationSuffix = "Activate";
    private static readonly TimeSpan ListenerStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly object ProcessRegistryLock = new();
    private static readonly HashSet<string> ActiveApplicationIds =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _applicationId;
    private readonly Mutex _instanceMutex;
    private readonly EventWaitHandle _activationSignal;
    private readonly ManualResetEvent _stopSignal = new(false);
    private readonly object _listenerLock = new();
    private Thread? _listenerThread;
    private int _primaryRuntimeStarted;
    private bool _registeredInProcess;
    private bool _disposed;

    public SingleInstanceCoordinator(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            throw new ArgumentException("单实例标识不能为空。", nameof(applicationId));
        if (applicationId.Contains('\\', StringComparison.Ordinal))
            throw new ArgumentException("单实例标识不能包含命名空间分隔符。", nameof(applicationId));

        _applicationId = applicationId;
        var prefix = $"Local\\{applicationId}";
        _activationSignal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            $"{prefix}.{ActivationSuffix}");
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: false, $"{prefix}.{MutexSuffix}");
        }
        catch
        {
            _activationSignal.Dispose();
            throw;
        }

        lock (ProcessRegistryLock)
        {
            _registeredInProcess = ActiveApplicationIds.Add(_applicationId);
        }

        if (!_registeredInProcess)
        {
            IsPrimary = false;
            return;
        }

        try
        {
            IsPrimary = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            IsPrimary = true;
        }
        catch
        {
            RemoveProcessRegistration();
            _instanceMutex.Dispose();
            _activationSignal.Dispose();
            throw;
        }
    }

    public bool IsPrimary { get; }

    public bool TryStartPrimary(Action startPrimaryRuntime)
    {
        ArgumentNullException.ThrowIfNull(startPrimaryRuntime);
        ThrowIfDisposed();
        if (!IsPrimary ||
            Interlocked.CompareExchange(ref _primaryRuntimeStarted, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            startPrimaryRuntime();
            return true;
        }
        catch
        {
            Volatile.Write(ref _primaryRuntimeStarted, 0);
            throw;
        }
    }

    public bool NotifyPrimaryInstance()
    {
        ThrowIfDisposed();
        return _activationSignal.Set();
    }

    public void StartActivationListener(Action onActivated)
    {
        ArgumentNullException.ThrowIfNull(onActivated);
        ThrowIfDisposed();
        if (!IsPrimary)
            throw new InvalidOperationException("只有主实例可以监听唤醒信号。");

        lock (_listenerLock)
        {
            ThrowIfDisposed();
            if (_listenerThread is not null)
                throw new InvalidOperationException("单实例唤醒监听已经启动。");

            _listenerThread = new Thread(() => ListenForActivation(onActivated))
            {
                IsBackground = true,
                Name = "QqChannelDesk.SingleInstanceActivation"
            };
            _listenerThread.Start();
        }
    }

    private void ListenForActivation(Action onActivated)
    {
        WaitHandle[] handles = [_activationSignal, _stopSignal];
        while (true)
        {
            int signalled;
            try
            {
                signalled = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (signalled != 0)
                return;

            try
            {
                onActivated();
            }
            catch (Exception ex)
            {
                AppLogger.Instance.WriteException("单实例唤醒回调失败", ex);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        Thread? listener;
        lock (_listenerLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _stopSignal.Set();
            listener = _listenerThread;
        }

        if (listener is not null && listener != Thread.CurrentThread)
            listener.Join(ListenerStopTimeout);

        if (IsPrimary)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex can only be released by its owning thread. Normal app
                // shutdown disposes on the UI thread that acquired it.
            }
        }

        RemoveProcessRegistration();
        _instanceMutex.Dispose();
        _activationSignal.Dispose();
        _stopSignal.Dispose();
    }

    private void RemoveProcessRegistration()
    {
        if (!_registeredInProcess)
            return;

        lock (ProcessRegistryLock)
        {
            if (!_registeredInProcess)
                return;
            ActiveApplicationIds.Remove(_applicationId);
            _registeredInProcess = false;
        }
    }
}
