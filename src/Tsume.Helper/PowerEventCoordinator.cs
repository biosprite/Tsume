using System.Diagnostics;

namespace Tsume.Helper;

/// <summary>
/// Serializes and coalesces Windows power notifications (<c>Suspend</c>, <c>Resume*</c>,
/// <c>GUID_CONSOLE_DISPLAY_STATE</c>, and <c>PowerStatusChange</c>) for one active device session.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Runs at most one power worker at a time so <c>Suspend</c> and <c>Resume</c>/<c>DisplayOn</c> never
///     reorder on the thread pool and leave <c>AcerDevice._standbyPaused</c> inverted.</item>
///   <item>Coalesces multi-event wake bursts (e.g. <c>ResumeAutomatic</c> + <c>ResumeSuspend</c> + <c>DisplayOn</c>)
///     into a single restore pass, preventing back-to-back unconditional <c>SetPerformance</c> writes on devices
///     with <c>ReapplyOnResume = true</c> (Predator Atlas 8).</item>
///   <item>Pauses device background loops on both S3/S4 <c>Suspend</c> and Modern Standby <c>DisplayOff</c>
///     (deduplicated so <c>Suspend</c> + <c>DisplayOff</c> in either order notifies once), and ignores
///     <c>DisplayDimmed</c> and initial <c>DisplayOn</c>. Only real <c>Off -&gt; On</c> transitions trigger a
///     Modern Standby wake restore, and a <c>DisplayOn</c> shortly after an already-completed <c>Resume*</c>
///     wake (with no intervening <c>DisplayOff</c>/<c>Suspend</c>) is deduplicated.</item>
///   <item>Coalesces repeated <c>PowerStatusChange</c> broadcasts into a single serialized check.</item>
/// </list>
/// </remarks>
internal sealed class PowerEventCoordinator
{
    public const uint DisplayOff = 0;
    public const uint DisplayOn = 1;
    public const uint DisplayDimmed = 2;

    public static readonly TimeSpan DefaultRestoreCoalesceDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DefaultScreenOnAfterWakeCooldown = TimeSpan.FromMilliseconds(1500);

    private readonly Action<bool> _onSystemPowerState;
    private readonly Action<string> _onRestore;
    private readonly Action _onPowerStatusChange;
    private readonly Action<Exception>? _onError;
    private readonly CancellationToken _stop;
    private readonly TimeSpan _coalesceDelay;
    private readonly TimeSpan _screenOnAfterWakeCooldown;
    private readonly long _coalesceDelayTicks;

    private readonly Lock _gate = new();
    private Task? _workerTask;
    private CancellationTokenSource? _delayCancel;

    private bool _isSuspended;
    private bool? _pendingSuspend;
    private string? _pendingRestoreOccasion;
    private long _restoreReadyAtTicks;
    private bool _pendingPowerStatus;

    private bool _displayWasOff;
    private int _sleepEpoch;
    private long _lastWakeRestoreTicks;

    public PowerEventCoordinator(
        Action<bool> onSystemPowerState,
        Action<string> onRestore,
        Action onPowerStatusChange,
        CancellationToken stop,
        Action<Exception>? onError = null,
        TimeSpan? restoreCoalesceDelay = null,
        TimeSpan? screenOnAfterWakeCooldown = null)
    {
        _onSystemPowerState = onSystemPowerState;
        _onRestore = onRestore;
        _onPowerStatusChange = onPowerStatusChange;
        _stop = stop;
        _onError = onError;
        _coalesceDelay = restoreCoalesceDelay ?? DefaultRestoreCoalesceDelay;
        _screenOnAfterWakeCooldown = screenOnAfterWakeCooldown ?? DefaultScreenOnAfterWakeCooldown;
        _coalesceDelayTicks = (long)(_coalesceDelay.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>Signals an OS suspend (S3/S4). Cancels any queued wake restore and pauses device background loops.</summary>
    public void OnSuspend()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;

            QueueSuspendLocked();
        }
    }

    /// <summary>Signals an OS resume (<c>ResumeAutomatic</c>, <c>ResumeSuspend</c>, or <c>ResumeCritical</c>).</summary>
    public void OnWake(string occasion = "wake")
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;

            QueueWakeLocked(occasion);
        }
    }

    /// <summary>
    /// Feeds a <c>GUID_CONSOLE_DISPLAY_STATE</c> value (<c>0</c>=Off, <c>1</c>=On, <c>2</c>=Dimmed).
    /// <c>DisplayOff</c> pauses device background loops during Modern Standby (where Windows does not send
    /// <c>Suspend</c>), and only genuine <c>Off -&gt; On</c> transitions trigger a Modern Standby restore.
    /// </summary>
    public void OnDisplayStateChanged(uint displayState)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;

            if (displayState == DisplayOff)
            {
                _displayWasOff = true;
                QueueSuspendLocked();
                return;
            }

            if (displayState != DisplayOn || !_displayWasOff)
                return;

            _displayWasOff = false;

            // A DisplayOn arriving shortly after a Resume* wake that already restored (with no intervening
            // DisplayOff/Suspend) belongs to the same wake cycle.
            if (_lastWakeRestoreTicks != 0
                && Stopwatch.GetElapsedTime(_lastWakeRestoreTicks) < _screenOnAfterWakeCooldown)
            {
                return;
            }

            QueueWakeLocked("screen on");
        }
    }

    /// <summary>
    /// Fallback for legacy <c>ServiceBase.OnPowerEvent(PBT_POWERSETTINGCHANGE)</c> when
    /// <c>PowerSettingRegisterNotification</c> callback registration is unavailable.
    /// </summary>
    public void OnLegacyScreenStateChanged()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;

            // Never override an active or pending Suspend with an ambiguous screen-state notification.
            if (_pendingSuspend == true || _isSuspended)
                return;

            if (_lastWakeRestoreTicks != 0
                && Stopwatch.GetElapsedTime(_lastWakeRestoreTicks) < _screenOnAfterWakeCooldown)
            {
                return;
            }

            QueueWakeLocked("screen on or off");
        }
    }

    /// <summary>Signals a Windows power-source or battery-level notification; coalesces bursts into one check.</summary>
    public void OnPowerStatusChange()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;

            _pendingPowerStatus = true;
            EnsureWorkerLocked();
        }
    }

    /// <summary>Returns the currently running worker task (or <see cref="Task.CompletedTask"/> when idle).</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _workerTask ?? Task.CompletedTask;
        }
    }

    private void QueueSuspendLocked()
    {
        _sleepEpoch++;
        _lastWakeRestoreTicks = 0;
        _pendingRestoreOccasion = null;
        _restoreReadyAtTicks = 0;
        CancelDelayLocked();

        if (_isSuspended)
        {
            _pendingSuspend = null;
            return;
        }

        _pendingSuspend = true;
        EnsureWorkerLocked();
    }

    private void QueueWakeLocked(string occasion)
    {
        _pendingSuspend = false;

        // Prefer "wake" over "screen on" when both arrive in the same coalescing window.
        if (_pendingRestoreOccasion is null || occasion == "wake")
            _pendingRestoreOccasion = occasion;

        if (_restoreReadyAtTicks == 0)
            _restoreReadyAtTicks = _coalesceDelayTicks > 0
                ? Stopwatch.GetTimestamp() + _coalesceDelayTicks
                : 0;

        EnsureWorkerLocked();
    }

    private void EnsureWorkerLocked()
    {
        if (_workerTask is not null) return;
        _workerTask = Task.Run(DrainAsync);
    }

    private void CancelDelayLocked()
    {
        try { _delayCancel?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            bool runSuspend = false;
            string? runRestoreOccasion = null;
            int restoreEpoch = 0;
            bool runPowerStatus = false;
            TimeSpan waitBeforeRestore = TimeSpan.Zero;
            CancellationTokenSource? delayCts = null;

            lock (_gate)
            {
                if (_stop.IsCancellationRequested)
                {
                    _workerTask = null;
                    return;
                }

                if (_pendingSuspend == true)
                {
                    _pendingSuspend = null;
                    _isSuspended = true;
                    _pendingRestoreOccasion = null;
                    _restoreReadyAtTicks = 0;
                    runSuspend = true;
                }
                else if (_pendingRestoreOccasion is not null)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (_restoreReadyAtTicks > now)
                    {
                        var remainingTicks = _restoreReadyAtTicks - now;
                        waitBeforeRestore = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
                        delayCts = _delayCancel = CancellationTokenSource.CreateLinkedTokenSource(_stop);
                    }
                    else
                    {
                        runRestoreOccasion = _pendingRestoreOccasion;
                        restoreEpoch = _sleepEpoch;
                        _pendingRestoreOccasion = null;
                        _restoreReadyAtTicks = 0;
                        _pendingSuspend = null;
                        _isSuspended = false;
                    }
                }
                else if (_pendingPowerStatus)
                {
                    _pendingPowerStatus = false;
                    runPowerStatus = true;
                }
                else
                {
                    _workerTask = null;
                    return;
                }
            }

            if (delayCts is not null)
            {
                try
                {
                    await Task.Delay(waitBeforeRestore, delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    // Superseded by Suspend; loop immediately to process it.
                }
                catch (OperationCanceledException)
                {
                    // Service stopping; loop to exit cleanly.
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_delayCancel, delayCts))
                            _delayCancel = null;
                    }
                    delayCts.Dispose();
                }

                continue;
            }

            try
            {
                if (runSuspend)
                {
                    _onSystemPowerState(true);
                }
                else if (runRestoreOccasion is not null)
                {
                    _onSystemPowerState(false);
                    if (!_stop.IsCancellationRequested)
                    {
                        _onRestore(runRestoreOccasion);

                        if (runRestoreOccasion == "wake")
                        {
                            lock (_gate)
                            {
                                if (_sleepEpoch == restoreEpoch)
                                    _lastWakeRestoreTicks = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                }
                else if (runPowerStatus)
                {
                    _onPowerStatusChange();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                _onError?.Invoke(error);
            }
        }
    }
}
