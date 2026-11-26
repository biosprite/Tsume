using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Tsume.App.Platform;
using Tsume.App.Platform.Interop;
using Tsume.Core.Controller;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

/// <summary>
/// Keeps controller input inside Tsume while the panel is open over a game.
/// Many borderless-fullscreen games (such as Unreal Engine titles using <c>xinput1_3.dll</c> or
/// Steam Input) keep polling the gamepad even after Tsume takes foreground focus. When the Claw's
/// built-in pad is in Gamepad mode, Tsume temporarily switches it to Desktop mode (same USB device,
/// no controller-disconnect popup, zero XInput reports) and intercepts its mouse and keyboard output
/// via low-level hooks to drive panel navigation cleanly. On close, exit, or sleep, Gamepad mode is
/// restored once the closing button is released.
/// </summary>
public sealed class PadIsolation : IDisposable
{
    /// <summary>Minimum distance from any screen edge while capturing mouse deltas, so Windows never clamps them.</summary>
    private const int CursorMargin = 64;

    /// <summary>How long an external USB/Bluetooth mouse stays passed through after moving.</summary>
    private const long ExternalMouseWindowMs = 2000;

    /// <summary>How long to wait for a closing button to be released before switching back to Gamepad mode.</summary>
    private static readonly TimeSpan ReleaseButtonWaitCap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReleaseButtonSettle = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan NavInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// How long to wait for sticks/buttons to return to neutral before switching Mode 1 -> Mode 4 (and, in
    /// <c>OverlayWindow</c>, before taking the foreground). Long enough to cover a stick held through the open,
    /// short enough that a drifting stick never blocks the panel for good.
    /// </summary>
    public static readonly TimeSpan HeldInputWaitCap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NeutralPoll = TimeSpan.FromMilliseconds(8);

    /// <summary>Idle windows in Mode 4 before flushing any latched XInput/HID report from a mid-input switch.</summary>
    private const long LatchedFlushLeftIdleMs = 60;
    private const long LatchedFlushRightIdleMs = 80;
    private static readonly TimeSpan LatchedFlushPoll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LatchedFlushRetryBackoff = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LatchedNeutralWaitCap = TimeSpan.FromMilliseconds(100);
    private const short LatchedStickDeadzone = 8_000;
    private const byte LatchedTriggerDeadzone = 40;

    private static readonly Journal Errors = new("errors");

    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Message-only window that receives <c>WM_INPUT</c> to distinguish an external mouse from the Claw.</summary>
    private readonly HwndSource _rawSink;
    private readonly Dictionary<nint, bool> _isClawMouse = [];

    // Kept in fields so the GC never collects delegates while Windows holds pointers to them.
    private readonly User32.LowLevelHookProc _mouseProc;
    private readonly User32.LowLevelHookProc _keyboardProc;

    private readonly DispatcherTimer _navTick;
    private readonly StickFromMouse _stick = new();
    private readonly PadPresses _presses = new();

    private nint _mouseHook;
    private nint _keyboardHook;
    private bool _rawRegistered;

    private User32.POINT _savedCursor;
    private bool _nudgedCursor;

    // UI-thread input state while hooks are active.
    private PadButton _heldKeys;
    private PadButton _pendingKeys;
    private bool _leftDown;
    private bool _rightDown;
    private bool _confirmKeyDown;
    private bool _backKeyDown;
    private bool _tabKeyDown;
    private bool _navStepQueued;
    private long _externalMouseUntil;
    private bool _externalButtonDown;

    // Shared coordination state across UI thread and background HID transitions.
    private volatile bool _panelOpen;
    private volatile bool _isCapturing;
    private volatile bool _isCaptured;
    private volatile bool _hooksActive;
    private volatile bool _userChoseCursorWhileOpen;
    private volatile bool _anyButtonHeld;
    private volatile bool _disposed;
    private long _lastClawInputTick;
    private long _lastRightStickTick;
    private TaskCompletionSource _openCaptureDone = CreateCompletedWaiter();
    private CancellationTokenSource? _captureCancel;
    private CancellationTokenSource? _releaseCancel;
    private CancellationTokenSource? _flushCancel;

    /// <summary>Raised on the UI thread when an intercepted Claw input maps to a panel navigation button.</summary>
    public event Action<PadButton>? Pressed;

    /// <summary>Raised whenever capture-in-progress or captured state changes.</summary>
    public event Action<bool, bool>? StateChanged;

    public PadIsolation(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _mouseProc = OnMouseHook;
        _keyboardProc = OnKeyboardHook;

        _rawSink = new HwndSource(new HwndSourceParameters("Tsume pad isolation") { ParentWindow = new nint(-3) });
        _rawSink.AddHook(OnRawInputMessage);

        _navTick = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = NavInterval };
        _navTick.Tick += (_, _) => StepNavigation();
    }

    /// <summary>True while the Claw pad is temporarily held in Desktop mode on behalf of Gamepad mode.</summary>
    public bool IsCaptured => _isCaptured;

    /// <summary>
    /// Completes once the current open-time capture attempt has settled (captured, skipped, or closed),
    /// so panel tile refreshes never contend with the isolation switch over the vendor HID pipe.
    /// </summary>
    public Task WaitForOpenCaptureAsync() => Volatile.Read(ref _openCaptureDone).Task;

    /// <summary>Called when the overlay opens or starts closing.</summary>
    public void SetPanelOpen(bool open)
    {
        if (_disposed) return;

        _panelOpen = open;

        if (open)
        {
            CancelPendingRelease();
            CancelPendingFlush();

            if (!_isCaptured && !_userChoseCursorWhileOpen)
            {
                _isCapturing = true;
                var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _openCaptureDone, next).TrySetResult();
                NotifyStateChanged();
            }
        }
        else
        {
            _userChoseCursorWhileOpen = false;
            _isCapturing = false;
            CancelPendingCapture();
            CancelPendingFlush();
            Volatile.Read(ref _openCaptureDone).TrySetResult();
            NotifyStateChanged();
        }

        _ = Task.Run(ReconcileAsync);
    }

    /// <summary>
    /// Reads the user's logical pad mode: returns <see cref="PadMode.Gamepad"/> while captured,
    /// otherwise reads the pad directly. Serialised with capture/release transitions.
    /// </summary>
    public async Task<PadMode?> ReadLogicalModeAsync()
    {
        if (_disposed) return null;

        await _gate.WaitAsync();
        try
        {
            if (_isCaptured) return PadMode.Gamepad;
            return await ControllerMode.ReadAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Handles a user toggle on the Controller tile, coordinating with temporary overlay capture
    /// so the hardware never fights the tile or leaks input to a game behind the panel.
    /// </summary>
    public async Task<bool> SetLogicalModeAsync(PadMode wanted)
    {
        if (_disposed) return false;

        await _gate.WaitAsync();
        try
        {
            // Captured (hardware is already in Cursor) -> user explicitly wants Cursor mode.
            if (_isCaptured && wanted == PadMode.Cursor)
            {
                CancelPendingFlush();
                await WaitForButtonsReleasedAsync(CancellationToken.None);
                _isCaptured = false;
                _userChoseCursorWhileOpen = true;
                await _dispatcher.InvokeAsync(UninstallHooks);
                NotifyStateChanged();
                return true;
            }

            // Panel open in Cursor mode -> user explicitly wants Gamepad mode again.
            // Keep hardware in Cursor while the panel stays open, re-engage isolation hooks,
            // and restore hardware to Gamepad when the panel closes.
            if (!_isCaptured && _panelOpen && wanted == PadMode.Gamepad)
            {
                var current = await ControllerMode.ReadAsync();
                if (current is null) return false;

                await ControllerMode.ReleasedAsync();
                _userChoseCursorWhileOpen = false;

                await _dispatcher.InvokeAsync(InstallHooks, DispatcherPriority.Send);
                var ok = current == PadMode.Cursor || await ControllerMode.SetAsync(PadMode.Cursor);
                if (!ok)
                {
                    await _dispatcher.InvokeAsync(UninstallHooks, DispatcherPriority.Send);
                    return false;
                }

                _isCaptured = true;
                NotifyStateChanged();
                return true;
            }

            _userChoseCursorWhileOpen = _panelOpen && wanted == PadMode.Cursor;
            return await ControllerMode.SetAsync(wanted);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asynchronously waits for any in-flight capture/release transition to finish and restores Gamepad mode
    /// if still captured. Call before initiating Hibernate/Restart/Shutdown so the controller never wakes in Cursor mode.
    /// </summary>
    public async Task EnsureRestoredAsync(int timeoutMs = 2000)
    {
        if (_disposed) return;

        _panelOpen = false;
        _isCapturing = false;
        CancelPendingCapture();
        CancelPendingRelease();
        CancelPendingFlush();
        Volatile.Read(ref _openCaptureDone).TrySetResult();
        NotifyStateChanged();

        if (!await _gate.WaitAsync(timeoutMs)) return;
        try
        {
            if (!_isCaptured) return;

            await ControllerMode.SetAsync(PadMode.Gamepad);
            _isCaptured = false;
            await _dispatcher.InvokeAsync(UninstallHooks);
            NotifyStateChanged();
        }
        catch (Exception error)
        {
            Errors.Write($"PadIsolation async restore failed: {error}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Synchronously unhooks and restores Gamepad mode if currently captured.
    /// Safe to call on app exit, process exit, logoff, or system suspend.
    /// </summary>
    public void RestoreSynchronously()
    {
        _panelOpen = false;
        _isCapturing = false;
        CancelPendingCapture();
        CancelPendingFlush();
        Volatile.Read(ref _openCaptureDone).TrySetResult();
        NotifyStateChanged();

        if (_dispatcher.CheckAccess())
        {
            UninstallHooks();
        }
        else
        {
            UnhookDirect();
            try { _dispatcher.BeginInvoke(UninstallHooks); }
            catch (Exception) { }
        }

        if (!_isCaptured) return;

        // Avoid deadlocking the UI thread if a background transition is awaiting _dispatcher.InvokeAsync under _gate.
        var gateTimeoutMs = _dispatcher.CheckAccess() ? 0 : 1500;
        var locked = false;
        try
        {
            locked = _gate.Wait(gateTimeoutMs);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!locked)
        {
            // A background ReconcileAsync already holds _gate and will see _panelOpen == false and restore Gamepad mode.
            return;
        }

        try
        {
            if (!_isCaptured) return;
            _isCaptured = false;
            NotifyStateChanged();
            Task.Run(() => ControllerMode.SetAsync(PadMode.Gamepad)).Wait(TimeSpan.FromMilliseconds(1500));
        }
        catch (Exception error)
        {
            Errors.Write($"PadIsolation restore failed: {error}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileAsync()
    {
        await _gate.WaitAsync();
        try
        {
            while (!_disposed)
            {
                if (_panelOpen && !_isCaptured)
                {
                    var waiter = Volatile.Read(ref _openCaptureDone);
                    if (_userChoseCursorWhileOpen)
                    {
                        _isCapturing = false;
                        waiter.TrySetResult();
                        NotifyStateChanged();
                        return;
                    }

                    var captured = await CaptureLockedAsync();
                    _isCapturing = false;
                    waiter.TrySetResult();
                    NotifyStateChanged();

                    if (!captured) return;
                    // Loop once more in case the panel closed while we were switching.
                    continue;
                }

                if (!_panelOpen && _isCaptured)
                {
                    await ReleaseLockedAsync();
                    // Loop once more in case the panel reopened while we were releasing.
                    continue;
                }

                _isCapturing = false;
                Volatile.Read(ref _openCaptureDone).TrySetResult();
                NotifyStateChanged();
                return;
            }
        }
        catch (Exception error)
        {
            _isCapturing = false;
            Volatile.Read(ref _openCaptureDone).TrySetResult();
            NotifyStateChanged();
            Errors.Write($"PadIsolation transition failed: {error}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> CaptureLockedAsync()
    {
        using var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _captureCancel, cancel);

        _isCapturing = true;
        NotifyStateChanged();

        try
        {
            if (!_panelOpen || _disposed) return false;

            var controller = await Task.Run(ClawController.Find, cancel.Token);
            if (controller is null) return false;

            (byte Mode, byte MKeys)? before;
            try
            {
                before = await controller.ReadModeAsync();
                if (before is null || before.Value.Mode == (byte)PadMode.Cursor)
                {
                    controller.Dispose();
                    return false;
                }

                if (!_panelOpen || _disposed || cancel.IsCancellationRequested)
                {
                    controller.Dispose();
                    return false;
                }

                // If a stick, trigger, or button is deflected right as Tsume opens (e.g. fast-scrolling in
                // Xbox FSE or turning a game camera), switching Mode 1 -> Mode 4 immediately makes the Claw
                // firmware stop sending XInput reports without sending a neutral report first, latching that
                // non-zero state in xusb22.sys / InputHost. Wait briefly for the thumb to lift or cross center
                // so the controller itself emits a clean zeroed XInput report before entering Mode 4.
                if (ReadBuiltInGamepadInput() != BuiltInInput.None)
                {
                    var deadline = DateTime.UtcNow + HeldInputWaitCap;
                    while (DateTime.UtcNow < deadline && _panelOpen && !_disposed && !cancel.IsCancellationRequested)
                    {
                        await Task.Delay(NeutralPoll, cancel.Token);
                        if (ReadBuiltInGamepadInput() == BuiltInInput.None) break;
                    }
                }

                if (!_panelOpen || _disposed || cancel.IsCancellationRequested)
                {
                    controller.Dispose();
                    return false;
                }

                await _dispatcher.InvokeAsync(InstallHooks, DispatcherPriority.Send);
                if (!_hooksActive)
                {
                    controller.Dispose();
                    return false;
                }
            }
            catch
            {
                controller.Dispose();
                throw;
            }

            // Ownership of controller transfers to SwitchOnHandleAsync (which always disposes it).
            var switched = await ControllerMode.SwitchOnHandleAsync(
                controller, before.Value.MKeys, PadMode.Cursor, cancel: cancel.Token);
            if (!switched)
            {
                await _dispatcher.InvokeAsync(UninstallHooks, DispatcherPriority.Send);
                return false;
            }

            _isCaptured = true;

            if (_panelOpen && ReadBuiltInGamepadInput() != BuiltInInput.None)
                StartLatchedInputFlush();

            return true;
        }
        catch (OperationCanceledException)
        {
            if (_hooksActive && !_isCaptured)
                await _dispatcher.InvokeAsync(UninstallHooks, DispatcherPriority.Send);
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _captureCancel, null, cancel);
        }
    }

    private void StartLatchedInputFlush()
    {
        CancelPendingFlush();
        var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _flushCancel, cancel);
        _ = Task.Run(() => FlushLatchedInputAsync(cancel));
    }

    /// <summary>
    /// If switching Mode 1 -> Mode 4 while a stick was still held (past <see cref="HeldInputWaitCap"/>
    /// or re-deflected during the ~30 ms USB switch) left the Claw's persistent XInput/HID gamepad endpoint
    /// reporting a non-zero stick or button state, keeps retrying on idle pauses until a brief
    /// Gamepad -> neutral -> Cursor bounce clears it through <c>xusb22.sys</c> / <c>InputHost</c>.
    /// </summary>
    private async Task FlushLatchedInputAsync(CancellationTokenSource cancel)
    {
        var token = cancel.Token;

        try
        {
            while (!token.IsCancellationRequested && _panelOpen && _isCaptured)
            {
                var input = ReadBuiltInGamepadInput();
                if (input == BuiltInInput.None) return;

                var nowTick = Environment.TickCount64;
                var readyToTry = input == BuiltInInput.LeftOrButtons
                    ? !_anyButtonHeld && nowTick - Volatile.Read(ref _lastClawInputTick) >= LatchedFlushLeftIdleMs
                    : nowTick - Volatile.Read(ref _lastRightStickTick) >= LatchedFlushRightIdleMs;

                if (readyToTry)
                {
                    await _gate.WaitAsync(token);
                    var shouldBackoff = false;
                    try
                    {
                        if (token.IsCancellationRequested || !_panelOpen || !_isCaptured) return;
                        if (ReadBuiltInGamepadInput() == BuiltInInput.None) return;

                        if (!await ControllerMode.SetAsync(PadMode.Gamepad, cancel: token))
                        {
                            shouldBackoff = true;
                        }
                        else
                        {
                            // Give the controller a brief moment in Mode 1 to emit a fresh neutral XInput/HID report.
                            var deadline = DateTime.UtcNow + LatchedNeutralWaitCap;
                            while (DateTime.UtcNow < deadline && ReadBuiltInGamepadInput() != BuiltInInput.None)
                                await Task.Delay(NeutralPoll);

                            var cleared = ReadBuiltInGamepadInput() == BuiltInInput.None;

                            if (!_panelOpen || _disposed)
                            {
                                // Panel closed during the brief bounce: stay cleanly in Gamepad mode.
                                _isCaptured = false;
                                await _dispatcher.InvokeAsync(UninstallHooks, DispatcherPriority.Send);
                                NotifyStateChanged();
                                return;
                            }

                            if (!await ControllerMode.SetAsync(PadMode.Cursor))
                            {
                                _isCaptured = false;
                                await _dispatcher.InvokeAsync(UninstallHooks, DispatcherPriority.Send);
                                NotifyStateChanged();
                                return;
                            }

                            if (cleared) return;
                            shouldBackoff = true;
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    if (shouldBackoff)
                    {
                        await Task.Delay(LatchedFlushRetryBackoff, token);
                        continue;
                    }
                }

                await Task.Delay(LatchedFlushPoll, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Panel closed, user chose Cursor mode, or a new open cancelled the flush.
        }
        catch (Exception error)
        {
            Errors.Write($"PadIsolation latched input flush failed: {error}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _flushCancel, null, cancel);
            cancel.Dispose();
        }
    }

    /// <summary>True while the built-in pad reports any pressed button, trigger, or deflected stick.</summary>
    public static bool IsBuiltInPadHeld() => ReadBuiltInGamepadInput() != BuiltInInput.None;

    private enum BuiltInInput
    {
        None,
        RightStick,
        LeftOrButtons,
    }

    /// <summary>
    /// Checks whether the built-in Claw gamepad interface (slot 0) is reporting any pressed button,
    /// trigger, or deflected stick across XInput or WinMM HID.
    /// </summary>
    private static BuiltInInput ReadBuiltInGamepadInput()
    {
        if (XInput.XInputGetState(0, out var x) == XInput.ERROR_SUCCESS)
        {
            if (x.Buttons != 0
                || x.LeftTrigger >= LatchedTriggerDeadzone
                || x.RightTrigger >= LatchedTriggerDeadzone
                || Math.Abs((int)x.ThumbLX) > LatchedStickDeadzone
                || Math.Abs((int)x.ThumbLY) > LatchedStickDeadzone)
            {
                return BuiltInInput.LeftOrButtons;
            }

            if (Math.Abs((int)x.ThumbRX) > LatchedStickDeadzone
                || Math.Abs((int)x.ThumbRY) > LatchedStickDeadzone)
            {
                return BuiltInInput.RightStick;
            }
        }

        if (FullScreenExperience.IsActive)
        {
            var joy = WinMm.JOYINFOEX.Create();
            if (WinMm.joyGetPosEx(0, ref joy) == WinMm.JOYERR_NOERROR)
            {
                const int center = 32_767;
                if (joy.Buttons != 0
                    || joy.Pov < WinMm.JOY_POVCENTERED
                    || Math.Abs((int)joy.X - center) > LatchedStickDeadzone
                    || Math.Abs((int)joy.Y - center) > LatchedStickDeadzone)
                {
                    return BuiltInInput.LeftOrButtons;
                }
            }
        }

        return BuiltInInput.None;
    }

    private async Task ReleaseLockedAsync()
    {
        CancelPendingFlush();
        using var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _releaseCancel, cancel);

        try
        {
            if (_panelOpen) return;

            // Wait until the button that closed the panel (e.g. B) is let go before XInput turns back on,
            // so the game behind the panel never sees a trailing press.
            await WaitForButtonsReleasedAsync(cancel.Token);
            if (_panelOpen || cancel.IsCancellationRequested) return;

            await ControllerMode.SetAsync(PadMode.Gamepad);
            _isCaptured = false;
            await _dispatcher.InvokeAsync(UninstallHooks, DispatcherPriority.Send);
            NotifyStateChanged();
        }
        catch (OperationCanceledException)
        {
            // Panel reopened while waiting for release: stay captured.
        }
        finally
        {
            Interlocked.CompareExchange(ref _releaseCancel, null, cancel);
        }
    }

    private async Task WaitForButtonsReleasedAsync(CancellationToken cancel)
    {
        var started = DateTime.UtcNow;
        DateTime? releasedAt = null;

        while (!cancel.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (_anyButtonHeld)
            {
                releasedAt = null;
            }
            else
            {
                releasedAt ??= now;
                if (now - releasedAt.Value >= ReleaseButtonSettle) return;
            }

            if (now - started >= ReleaseButtonWaitCap) return;
            await Task.Delay(ReleasePoll, cancel);
        }
    }

    private void CancelPendingCapture()
    {
        try { Volatile.Read(ref _captureCancel)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void CancelPendingRelease()
    {
        try { Volatile.Read(ref _releaseCancel)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void CancelPendingFlush()
    {
        try { Volatile.Read(ref _flushCancel)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(_isCapturing, _isCaptured);

    private static TaskCompletionSource CreateCompletedWaiter()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private void InstallHooks()
    {
        if (_disposed || _hooksActive) return;

        ResetInputState();
        NudgeCursorAwayFromEdges();
        RegisterRawMouse(register: true);

        var module = Kernel32.GetModuleHandle(null);
        _mouseHook = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _keyboardProc, module, 0);

        if (_mouseHook == 0 || _keyboardHook == 0)
        {
            Errors.Write($"PadIsolation could not install input hooks (mouse={_mouseHook}, kbd={_keyboardHook}, err={Marshal.GetLastWin32Error()}).");
            UninstallHooks();
            return;
        }

        _hooksActive = true;
        _navTick.Start();
    }

    private void UninstallHooks()
    {
        _hooksActive = false;
        _navTick.Stop();

        UnhookDirect();
        RegisterRawMouse(register: false);
        RestoreCursorIfNudged();
        ResetInputState();
    }

    private void UnhookDirect()
    {
        _hooksActive = false;

        var mouse = Interlocked.Exchange(ref _mouseHook, 0);
        if (mouse != 0) User32.UnhookWindowsHookEx(mouse);

        var keyboard = Interlocked.Exchange(ref _keyboardHook, 0);
        if (keyboard != 0) User32.UnhookWindowsHookEx(keyboard);
    }

    private void ResetInputState()
    {
        _stick.Reset();
        _presses.Step(PadButton.None, navigating: false, DateTime.UtcNow);
        _heldKeys = PadButton.None;
        _pendingKeys = PadButton.None;
        _leftDown = false;
        _rightDown = false;
        _confirmKeyDown = false;
        _backKeyDown = false;
        _tabKeyDown = false;
        _navStepQueued = false;
        _externalMouseUntil = 0;
        _externalButtonDown = false;
        _anyButtonHeld = false;
        var nowTick = Environment.TickCount64;
        Volatile.Write(ref _lastClawInputTick, nowTick);
        Volatile.Write(ref _lastRightStickTick, nowTick);
    }

    private void NudgeCursorAwayFromEdges()
    {
        _nudgedCursor = false;
        if (!User32.GetCursorPos(out var cur)) return;

        var left = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        var top = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        var width = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN);
        var height = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);
        if (width <= CursorMargin * 2 || height <= CursorMargin * 2) return;

        var clampedX = Math.Clamp(cur.X, left + CursorMargin, left + width - CursorMargin);
        var clampedY = Math.Clamp(cur.Y, top + CursorMargin, top + height - CursorMargin);
        if (clampedX == cur.X && clampedY == cur.Y) return;

        _savedCursor = cur;
        if (User32.SetCursorPos(clampedX, clampedY)) _nudgedCursor = true;
    }

    private void RestoreCursorIfNudged()
    {
        if (!_nudgedCursor) return;
        _nudgedCursor = false;

        // If the person moved a real external mouse while the panel was open, leave it where they put it.
        if (_externalMouseUntil == 0) User32.SetCursorPos(_savedCursor.X, _savedCursor.Y);
    }

    private void RegisterRawMouse(bool register)
    {
        if (register == _rawRegistered) return;

        var devices = new User32.RAWINPUTDEVICE[]
        {
            new()
            {
                UsagePage = User32.HID_USAGE_PAGE_GENERIC,
                Usage = User32.HID_USAGE_GENERIC_MOUSE,
                Flags = register ? User32.RIDEV_INPUTSINK : User32.RIDEV_REMOVE,
                Target = register ? _rawSink.Handle : 0,
            },
        };

        if (User32.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>()))
            _rawRegistered = register;
    }

    private nint OnRawInputMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != User32.WM_INPUT || !_hooksActive) return 0;

        var headerSize = (uint)Marshal.SizeOf<User32.RAWINPUTHEADER>();
        var size = headerSize;
        if (User32.GetRawInputData(lParam, User32.RID_HEADER, out var header, ref size, headerSize) == unchecked((uint)-1))
            return 0;

        if (header.Type != User32.RIM_TYPEMOUSE || header.Device == 0) return 0;

        if (IsClawMouseDevice(header.Device))
        {
            _externalMouseUntil = 0;
        }
        else
        {
            _externalMouseUntil = Environment.TickCount64 + ExternalMouseWindowMs;
            _stick.Reset();
        }

        return 0;
    }

    private bool IsClawMouseDevice(nint device)
    {
        if (_isClawMouse.TryGetValue(device, out var known)) return known;

        uint chars = 0;
        if (User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, null, ref chars) != 0 || chars == 0)
            return false;

        var buffer = new char[chars];
        if (User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, buffer, ref chars) == unchecked((uint)-1))
            return false;

        var name = new string(buffer).TrimEnd('\0');
        var isClaw = ClawController.IsClaw(name);
        _isClawMouse[device] = isClaw;
        return isClaw;
    }

    private nint OnMouseHook(int code, nint wParam, nint lParam)
    {
        if (code < User32.HC_ACTION || !_hooksActive)
            return User32.CallNextHookEx(_mouseHook, code, wParam, lParam);

        var info = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);

        // Always leave touchscreen, pen, and injected mouse input untouched.
        if ((info.Flags & (User32.LLMHF_INJECTED | User32.LLMHF_LOWER_IL_INJECTED)) != 0
            || ((ulong)info.ExtraInfo & User32.MI_WP_SIGNATURE_MASK) == User32.MI_WP_SIGNATURE)
        {
            return User32.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        var message = (uint)wParam;

        // Pass a real external mouse through whenever Raw Input reports activity from one.
        if (_externalButtonDown || Environment.TickCount64 < _externalMouseUntil)
        {
            if (message is User32.WM_LBUTTONDOWN or User32.WM_RBUTTONDOWN or User32.WM_MBUTTONDOWN)
                _externalButtonDown = true;
            else if (message is User32.WM_LBUTTONUP or User32.WM_RBUTTONUP or User32.WM_MBUTTONUP)
                _externalButtonDown = false;

            return User32.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        switch (message)
        {
            case User32.WM_MOUSEMOVE:
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                if (User32.GetCursorPos(out var cur))
                {
                    var now = DateTime.UtcNow;
                    var previous = _stick.Current(now);
                    var dir = _stick.Feed(info.Pt.X - cur.X, info.Pt.Y - cur.Y, now);
                    if (dir != PadButton.None && dir != previous)
                    {
                        _pendingKeys |= dir;
                        QueueNavigationStep();
                    }
                }
                return 1;

            case User32.WM_LBUTTONDOWN:
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                if (!_leftDown)
                {
                    _leftDown = true;
                    UpdateHeldFlag();
                    if (_panelOpen) QueuePress(PadButton.A);
                }
                return 1;

            case User32.WM_LBUTTONUP:
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                _leftDown = false;
                UpdateHeldFlag();
                return 1;

            case User32.WM_RBUTTONDOWN:
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                if (!_rightDown)
                {
                    _rightDown = true;
                    UpdateHeldFlag();
                    if (_panelOpen) QueuePress(PadButton.B);
                }
                return 1;

            case User32.WM_RBUTTONUP:
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                _rightDown = false;
                UpdateHeldFlag();
                return 1;

            // Swallow right-stick scroll, back/forward, and middle click so nothing leaks to the game behind.
            case User32.WM_MOUSEWHEEL:
            case User32.WM_MOUSEHWHEEL:
                var wheelTick = Environment.TickCount64;
                Volatile.Write(ref _lastClawInputTick, wheelTick);
                Volatile.Write(ref _lastRightStickTick, wheelTick);
                return 1;

            case User32.WM_MBUTTONDOWN:
            case User32.WM_MBUTTONUP:
            case User32.WM_XBUTTONDOWN:
            case User32.WM_XBUTTONUP:
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                return 1;

            default:
                return User32.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }
    }

    private nint OnKeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code < User32.HC_ACTION || !_hooksActive)
            return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        // Let physical keyboards type Space, Enter, Escape, Tab, and arrows freely into focused text boxes.
        if (TextInputMode.IsActive)
        {
            if (_heldKeys != PadButton.None || _confirmKeyDown || _backKeyDown || _tabKeyDown)
            {
                _heldKeys = PadButton.None;
                _pendingKeys = PadButton.None;
                _confirmKeyDown = false;
                _backKeyDown = false;
                _tabKeyDown = false;
                UpdateHeldFlag();
            }
            return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);

        // Never interfere with injected keys (e.g. QuickActions SendChord) or modified system chords (Win+Alt+Q, Alt+Tab, Alt+F4).
        if ((info.Flags & (User32.LLKHF_INJECTED | User32.LLKHF_LOWER_IL_INJECTED)) != 0)
            return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var message = (uint)wParam;
        var isDown = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        var isUp = message is User32.WM_KEYUP or User32.WM_SYSKEYUP;
        if (!isDown && !isUp)
            return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var modified = (info.Flags & User32.LLKHF_ALTDOWN) != 0
            || User32.GetAsyncKeyState(User32.VK_CONTROL) < 0
            || User32.GetAsyncKeyState(User32.VK_LWIN) < 0
            || User32.GetAsyncKeyState(User32.VK_RWIN) < 0;

        var direction = info.VkCode switch
        {
            User32.VK_UP    => PadButton.Up,
            User32.VK_DOWN  => PadButton.Down,
            User32.VK_LEFT  => PadButton.Left,
            User32.VK_RIGHT => PadButton.Right,
            _               => PadButton.None,
        };

        if (direction != PadButton.None)
        {
            if (isDown && !modified)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                if ((_heldKeys & direction) == 0)
                {
                    _heldKeys |= direction;
                    _pendingKeys |= direction;
                    UpdateHeldFlag();
                    QueueNavigationStep();
                }
                return 1;
            }

            if (isUp && (_heldKeys & direction) != 0)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                _heldKeys &= ~direction;
                UpdateHeldFlag();
                return 1;
            }

            return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (info.VkCode is User32.VK_SPACE or User32.VK_RETURN)
        {
            if (isDown && !modified)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                if (!_confirmKeyDown)
                {
                    _confirmKeyDown = true;
                    UpdateHeldFlag();
                    if (_panelOpen) QueuePress(PadButton.A);
                }
                return 1;
            }

            if (isUp && _confirmKeyDown)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                _confirmKeyDown = false;
                UpdateHeldFlag();
                return 1;
            }

            return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        if (info.VkCode == User32.VK_ESCAPE)
        {
            if (isDown && !modified)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                if (!_backKeyDown)
                {
                    _backKeyDown = true;
                    UpdateHeldFlag();
                    if (_panelOpen) QueuePress(PadButton.B);
                }
                return 1;
            }

            if (isUp && _backKeyDown)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                _backKeyDown = false;
                UpdateHeldFlag();
                return 1;
            }

            return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        // Claw Desktop mode maps View to Tab: swallow plain Tab so focus stays under D-pad/stick control.
        if (info.VkCode == User32.VK_TAB)
        {
            if (isDown && !modified)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                _tabKeyDown = true;
                UpdateHeldFlag();
                return 1;
            }

            if (isUp && _tabKeyDown)
            {
                Volatile.Write(ref _lastClawInputTick, Environment.TickCount64);
                _tabKeyDown = false;
                UpdateHeldFlag();
                return 1;
            }
        }

        return User32.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void UpdateHeldFlag() =>
        _anyButtonHeld = _leftDown || _rightDown || _confirmKeyDown || _backKeyDown || _tabKeyDown || _heldKeys != PadButton.None;

    private void QueuePress(PadButton button) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_panelOpen && _hooksActive) Pressed?.Invoke(button);
        });

    private void QueueNavigationStep()
    {
        if (_navStepQueued) return;
        _navStepQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, StepNavigation);
    }

    private void StepNavigation()
    {
        _navStepQueued = false;
        if (!_hooksActive) return;

        var now = DateTime.UtcNow;
        var held = _heldKeys | _pendingKeys | _stick.Current(now);
        _pendingKeys = PadButton.None;

        var (_, pressed) = _presses.Step(held, navigating: _panelOpen, now);
        if (pressed == PadButton.None) return;

        if (pressed.HasFlag(PadButton.Up)) Pressed?.Invoke(PadButton.Up);
        if (pressed.HasFlag(PadButton.Down)) Pressed?.Invoke(PadButton.Down);
        if (pressed.HasFlag(PadButton.Left)) Pressed?.Invoke(PadButton.Left);
        if (pressed.HasFlag(PadButton.Right)) Pressed?.Invoke(PadButton.Right);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RestoreSynchronously();
        _rawSink.Dispose();
    }
}
