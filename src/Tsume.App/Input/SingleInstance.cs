namespace Tsume.App.Input;

/// <summary>
/// Makes sure only one Tsume runs. A second launch asks the first to toggle the panel and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Tsume.App.Instance";
    private const string SignalName = @"Local\Tsume.App.Toggle";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread? _listener;

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread when a later launch asks for a toggle.</summary>
    public event Action? ToggleRequested;

    /// <summary>
    /// Checks whether a live instance already owns the single-instance mutex (ignoring abandoned mutexes left
    /// behind by a crashed process). When one is running and <paramref name="toggle"/> is true, signals it to toggle.
    /// </summary>
    public static bool TrySignalExisting(bool toggle)
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;

        using (existing)
        {
            var freeOrAbandoned = false;
            try
            {
                freeOrAbandoned = existing.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                freeOrAbandoned = true;
            }

            if (freeOrAbandoned)
            {
                try { existing.ReleaseMutex(); }
                catch (ApplicationException) { }
                return false;
            }
        }

        if (toggle && EventWaitHandle.TryOpenExisting(SignalName, out var signal))
        {
            using (signal) signal.Set();
        }

        return true;
    }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);

        var acquired = false;
        try
        {
            acquired = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed while holding the mutex: take ownership cleanly.
            acquired = true;
        }

        IsFirst = acquired;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);

        if (IsFirst)
        {
            _listener = new Thread(Listen) { IsBackground = true, Name = "Tsume instance signal" };
            _listener.Start();
        }
    }

    /// <summary>Called by a second launch, just before it exits.</summary>
    public void AskFirstInstanceToToggle() => _signal.Set();

    private void Listen()
    {
        WaitHandle[] either = [_signal, _stop.Token.WaitHandle];

        try
        {
            while (WaitHandle.WaitAny(either) == 0)
                ToggleRequested?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Join(TimeSpan.FromMilliseconds(250));

        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }

        _mutex.Dispose();
        _signal.Dispose();
        _stop.Dispose();
    }
}
