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

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread when a later launch asks for a toggle.</summary>
    public event Action? ToggleRequested;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        IsFirst = createdNew;

        if (IsFirst)
            new Thread(Listen) { IsBackground = true, Name = "Tsume instance signal" }.Start();
    }

    /// <summary>Called by a second launch, just before it exits.</summary>
    public void AskFirstInstanceToToggle() => _signal.Set();

    private void Listen()
    {
        WaitHandle[] either = [_signal, _stop.Token.WaitHandle];

        while (WaitHandle.WaitAny(either) == 0)
            ToggleRequested?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _signal.Dispose();
        _stop.Dispose();
    }
}
