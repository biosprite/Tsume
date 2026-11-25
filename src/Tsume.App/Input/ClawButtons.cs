using System.Management;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

/// <summary>The Claw's two dedicated buttons, as the firmware reports them.</summary>
public enum ClawButton
{
    /// <summary>The "M" button, short press.</summary>
    MsiCenter = 0x29,

    /// <summary>The "M" button, held.</summary>
    MsiCenterHeld = 0x31,

    /// <summary>The Quick Settings button, short press.</summary>
    QuickSettings = 0x58,

    /// <summary>The Quick Settings button, held.</summary>
    QuickSettingsHeld = 0x2A,
}

/// <summary>
/// Listens for the Claw's dedicated buttons. They are not part of the gamepad: the firmware
/// raises them as the WMI event <c>MSI_Event</c>, which needs no elevation to subscribe to.
/// </summary>
public sealed class ClawButtons : IDisposable
{
    private const int StartAttempts = 30;
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResumeSettle = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Every event code is recorded, so unknown buttons can be identified later.</summary>
    private readonly Journal _journal = new("buttons");

    private ManagementEventWatcher? _watcher;
    private int _generation;
    private bool _suspended;
    private bool _disposed;

    /// <summary>Raised on a background thread. Only presses are reported — never releases.</summary>
    public event Action<ClawButton>? Pressed;

    /// <summary>Raised on a background thread with the raw low-byte WMI event code for every press.</summary>
    public event Action<int>? CodePressed;

    public ClawButtons()
    {
        // Subscribing can take a moment (especially right after boot); retry if WMI is not ready yet.
        StartOrResubscribe(initialDelay: TimeSpan.Zero);
    }

    /// <summary>Stops and disposes the WMI watcher before sleep/hibernate so no COM sink survives across S4.</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            if (_disposed || _suspended) return;
            _suspended = true;
            _generation++;
            DisposeWatcherLocked();
        }
    }

    /// <summary>Re-subscribes with a fresh WMI watcher after waking from sleep/hibernate.</summary>
    public void Resume()
    {
        int generation;
        lock (_gate)
        {
            if (_disposed) return;
            _suspended = false;
            _generation++;
            DisposeWatcherLocked();
            generation = _generation;
        }

        _ = Task.Run(() => StartWithRetryAsync(generation, ResumeSettle, _stop.Token));
    }

    private void StartOrResubscribe(TimeSpan initialDelay)
    {
        int generation;
        lock (_gate)
        {
            if (_disposed || _suspended) return;
            _generation++;
            DisposeWatcherLocked();
            generation = _generation;
        }

        _ = Task.Run(() => StartWithRetryAsync(generation, initialDelay, _stop.Token));
    }

    private async Task StartWithRetryAsync(int generation, TimeSpan initialDelay, CancellationToken stop)
    {
        if (initialDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(initialDelay, stop); }
            catch (OperationCanceledException) { return; }
        }

        for (var attempt = 1; attempt <= StartAttempts && !stop.IsCancellationRequested; attempt++)
        {
            ManagementEventWatcher? candidate = null;
            try
            {
                lock (_gate)
                {
                    if (_disposed || _suspended || _generation != generation) return;

                    candidate = new ManagementEventWatcher(@"root\WMI", "SELECT * FROM MSI_Event");
                    candidate.EventArrived += OnEvent;
                    candidate.Stopped += OnStopped;
                    candidate.Start();

                    _watcher = candidate;
                    candidate = null;
                    return;
                }
            }
            catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
            {
                if (candidate is not null) DisposeInstance(candidate);
                if (stop.IsCancellationRequested) return;
                if (attempt == 1 || attempt == StartAttempts)
                    _journal.Write($"Could not listen for buttons (attempt {attempt}/{StartAttempts}): {error.Message}");
            }

            try { await Task.Delay(RetryPause, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnStopped(object sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || _suspended || !ReferenceEquals(sender, _watcher)) return;
        }

        _journal.Write($"Button listener stopped ({e.Status}); re-subscribing.");
        StartOrResubscribe(RetryPause);
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        var raw = Convert.ToInt32(e.NewEvent["MSIEvt"]);
        var code = raw & 0xFF;

        var known = Enum.IsDefined(typeof(ClawButton), code);

        // Act before logging: a press should not wait on the disk.
        CodePressed?.Invoke(code);
        if (known) Pressed?.Invoke((ClawButton)code);

        _journal.Write($"event 0x{raw:X4}  code 0x{code:X2}  {(known ? ((ClawButton)code).ToString() : "unknown")}");
    }

    private void DisposeWatcherLocked()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null) DisposeInstance(watcher);
    }

    private void DisposeInstance(ManagementEventWatcher watcher)
    {
        watcher.Stopped -= OnStopped;
        watcher.EventArrived -= OnEvent;

        try { watcher.Stop(); }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { }

        try { watcher.Dispose(); }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            DisposeWatcherLocked();
        }

        _stop.Cancel();
        _stop.Dispose();
    }
}
