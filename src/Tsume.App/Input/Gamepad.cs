using System.Numerics;
using Tsume.App.Controls;
using Tsume.App.Platform;
using Tsume.App.Platform.Interop;
using Tsume.Core.Controller;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

/// <summary>The buttons Tsume cares about. Values match the XInput bit masks.</summary>
[Flags]
public enum PadButton : ushort
{
    None  = 0,
    Up    = 0x0001,
    Down  = 0x0002,
    Left  = 0x0004,
    Right = 0x0008,
    Menu  = 0x0010,   // "Start"
    View  = 0x0020,   // "Back"
    A     = 0x1000,
    B     = 0x2000,
}

/// <summary>
/// Watches the controller on a background thread: only the summon chord while the panel is
/// closed (slow poll), every press with auto-repeat on directions while it is open.
/// </summary>
public sealed class Gamepad : IDisposable
{
    private const short StickThreshold = 16_000;
    private const byte TriggerThreshold = 128;

    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ActivePoll = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(1);
    private const long ResumeSettleMs = 2500;

    private static readonly PadButton[] AllButtons = Array.FindAll(Enum.GetValues<PadButton>(), static b => b != PadButton.None);

    private static readonly Journal Errors = new("errors");

    private sealed record PadActionBinding(
        DedicatedAction Action,
        DedicatedActionKeys.GamepadInput GamepadMask,
        byte[] KeyboardCodes);

    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _active = new(initialState: true);
    private readonly Thread _thread;
    private readonly HashSet<DedicatedAction> _latchedActions = [];
    private volatile PadActionBinding[] _actionBindings = [];
    private volatile bool _resetOnResume;
    private bool _loggedWatchError;
    private long _resumeAfterTick;

    /// <summary>Raised on a background thread when the summon chord is pressed.</summary>
    public event Action? Summoned;

    /// <summary>Raised on a background thread for each press (and repeat) while <see cref="IsNavigating"/>.</summary>
    public event Action<PadButton>? Pressed;

    /// <summary>Raised on a background thread when a Dedicated Action gamepad chord is pressed.</summary>
    public event Action<DedicatedAction>? ActionTriggered;

    private volatile bool _isNavigating;
    private volatile bool _suppressNavigation;
    private volatile bool _suppressBuiltInSlot;

    /// <summary>True while the panel is open and wants button presses.</summary>
    public bool IsNavigating
    {
        get => _isNavigating;
        set => _isNavigating = value;
    }

    /// <summary>
    /// True during the brief Claw Gamepad -> Desktop capture transition so pre-switch gamepad motion
    /// is not double-fed into the panel alongside the incoming Desktop-mode hooks.
    /// </summary>
    public bool SuppressNavigation
    {
        get => _suppressNavigation;
        set => _suppressNavigation = value;
    }

    /// <summary>
    /// True while <see cref="PadIsolation"/> holds the built-in Claw pad in Desktop mode: skips slot 0
    /// (the built-in pad) so any latched XInput/HID state cannot phantom-scroll Tsume, while leaving
    /// external controllers on slots 1–3 active.
    /// </summary>
    public bool SuppressBuiltInSlot
    {
        get => _suppressBuiltInSlot;
        set => _suppressBuiltInSlot = value;
    }

    public void SetActionBindings(IReadOnlyList<DedicatedActionBinding> bindings)
    {
        var list = new List<PadActionBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            var pad = DedicatedActionKeys.GamepadInput.None;
            var kb = new List<byte>(DedicatedActionKeys.MaxKeys);
            var hasOemButton = false;

            foreach (var code in binding.Keys)
            {
                if (code is 0 or BackButtonKey.UnboundCode) continue;

                if (code == DedicatedActionKeys.OemButtonCode)
                    hasOemButton = true;
                else if (DedicatedActionKeys.TryGetGamepad(code, out var input))
                    pad |= input;
                else if (DedicatedActionKeys.TryGetKeyboard(code, out _) && !kb.Contains(code))
                    kb.Add(code);
            }

            if (!hasOemButton && pad != DedicatedActionKeys.GamepadInput.None)
                list.Add(new PadActionBinding(binding.Action, pad, [.. kb]));
        }

        // Match longer, more specific chords before shorter subsets.
        list.Sort((a, b) => ChordLength(b).CompareTo(ChordLength(a)));
        _actionBindings = [.. list];
    }

    private static int ChordLength(PadActionBinding binding) =>
        BitOperations.PopCount((uint)binding.GamepadMask) + binding.KeyboardCodes.Length;

    private static bool IsSubsetOf(PadActionBinding shorter, PadActionBinding longer)
    {
        if ((longer.GamepadMask & shorter.GamepadMask) != shorter.GamepadMask) return false;
        foreach (var k in shorter.KeyboardCodes)
            if (Array.IndexOf(longer.KeyboardCodes, k) < 0) return false;
        return true;
    }

    public Gamepad()
    {
        _thread = new Thread(Watch) { IsBackground = true, Name = "Tsume gamepad" };
        _thread.Start();
    }

    /// <summary>Pauses XInput/WinMM polling across sleep/hibernate so USB/HID teardown is not raced.</summary>
    public void Suspend()
    {
        _resetOnResume = true;
        try { _active.Reset(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Resumes polling after a brief settle period so USB/XInput controllers finish re-enumerating.</summary>
    public void Resume()
    {
        _resetOnResume = true;
        Volatile.Write(ref _resumeAfterTick, Environment.TickCount64 + ResumeSettleMs);
        try { _active.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Watch()
    {
        try
        {
            var presses = new PadPresses();
            var stopToken = _stop.Token;
            WaitHandle[] pauseOrStop = [_active.WaitHandle, stopToken.WaitHandle];

            while (!stopToken.IsCancellationRequested)
            {
                if (!_active.IsSet)
                {
                    if (WaitHandle.WaitAny(pauseOrStop) != 0) break;
                    continue;
                }

                var remainingMs = Volatile.Read(ref _resumeAfterTick) - Environment.TickCount64;
                if (remainingMs > 0)
                {
                    stopToken.WaitHandle.WaitOne((int)Math.Min(remainingMs, 250));
                    continue;
                }

                if (_resetOnResume)
                {
                    _resetOnResume = false;
                    presses = new PadPresses();
                    _latchedActions.Clear();
                }

                try
                {
                    var navigating = IsNavigating && !SuppressNavigation;
                    var skipSlotZero = navigating && SuppressBuiltInSlot;
                    var bindings = _actionBindings;
                    var wantsBack = HasBackButtonBinding(bindings);

                    var (navButtons, actionPad) = PollControllers(skipSlotZero, bindings.Length > 0, wantsBack);
                    var (summoned, pressed) = presses.Step(navButtons, navigating, DateTime.UtcNow);

                    if (summoned) Summoned?.Invoke();
                    if (pressed != PadButton.None)
                    {
                        foreach (var button in AllButtons)
                            if ((pressed & button) != 0) Pressed?.Invoke(button);
                    }

                    ProcessDedicatedActions(bindings, actionPad);
                }
                catch (Exception error)
                {
                    if (!_loggedWatchError)
                    {
                        _loggedWatchError = true;
                        Errors.Write($"Gamepad watch failed: {error}");
                    }

                    stopToken.WaitHandle.WaitOne(ErrorBackoff);
                    continue;
                }

                stopToken.WaitHandle.WaitOne(IsNavigating ? ActivePoll : IdlePoll);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool HasBackButtonBinding(PadActionBinding[] bindings)
    {
        foreach (var binding in bindings)
        {
            if ((binding.GamepadMask & (DedicatedActionKeys.GamepadInput.BackLeft | DedicatedActionKeys.GamepadInput.BackRight)) != 0)
                return true;
        }
        return false;
    }

    private void ProcessDedicatedActions(PadActionBinding[] bindings, DedicatedActionKeys.GamepadInput pad)
    {
        if (bindings.Length == 0)
        {
            _latchedActions.Clear();
            return;
        }

        List<PadActionBinding>? activeChords = null;

        foreach (var binding in bindings)
        {
            var allDown = (pad & binding.GamepadMask) == binding.GamepadMask;
            if (allDown && binding.KeyboardCodes.Length > 0)
            {
                foreach (var k in binding.KeyboardCodes)
                {
                    if (!DedicatedActionKeys.IsKeyboardCodeDown(k))
                    {
                        allDown = false;
                        break;
                    }
                }
            }

            if (allDown)
            {
                activeChords ??= [];
                var suppressedByLonger = false;
                foreach (var longer in activeChords)
                {
                    if (IsSubsetOf(binding, longer))
                    {
                        suppressedByLonger = true;
                        break;
                    }
                }

                activeChords.Add(binding);

                if (suppressedByLonger)
                {
                    // Latch the shorter subset without firing so releasing the extra key does not fire it.
                    _latchedActions.Add(binding.Action);
                }
                else if (_latchedActions.Add(binding.Action) && !KeyDropdown.IsAnyOpen && !TextInputMode.IsActive)
                {
                    ActionTriggered?.Invoke(binding.Action);
                }
            }
            else
            {
                _latchedActions.Remove(binding.Action);
            }
        }
    }

    /// <summary>
    /// Reads XInput (and WinMM fallback/back-buttons when needed) once per tick, producing both navigation
    /// buttons (with left stick folded into the d-pad) and Dedicated Action gamepad inputs without double-polling.
    /// </summary>
    private static (PadButton NavButtons, DedicatedActionKeys.GamepadInput ActionInputs) PollControllers(
        bool skipSlotZero, bool needDedicatedActions, bool wantsBack)
    {
        var navButtons = PadButton.None;
        var actionInputs = DedicatedActionKeys.GamepadInput.None;
        var navStartSlot = skipSlotZero ? 1u : 0u;

        for (uint user = 0; user < XInput.MaxControllers; user++)
        {
            var wantNav = user >= navStartSlot;
            if (!wantNav && !needDedicatedActions) continue;

            if (!XInput.TryGetState(user, out var state)) continue;

            if (wantNav)
            {
                navButtons |= (PadButton)(state.Buttons & ~XInput.GuideButton);

                if (state.ThumbLY > StickThreshold) navButtons |= PadButton.Up;
                if (state.ThumbLY < -StickThreshold) navButtons |= PadButton.Down;
                if (state.ThumbLX < -StickThreshold) navButtons |= PadButton.Left;
                if (state.ThumbLX > StickThreshold) navButtons |= PadButton.Right;
            }

            if (needDedicatedActions)
            {
                actionInputs |= (DedicatedActionKeys.GamepadInput)(state.Buttons & 0xF7FF);
                if (state.LeftTrigger >= TriggerThreshold) actionInputs |= DedicatedActionKeys.GamepadInput.Lt;
                if (state.RightTrigger >= TriggerThreshold) actionInputs |= DedicatedActionKeys.GamepadInput.Rt;
            }
        }

        // Fall back to WinMM HID polling when XInput reports nothing under Xbox FSE InputHost,
        // and read WinMM when Dedicated Action back buttons (bits 10/11) are needed.
        var fseActive = FullScreenExperience.IsActive;
        var needNavWinMm = navButtons == PadButton.None && fseActive;
        var needActionWinMmFallback = needDedicatedActions && actionInputs == DedicatedActionKeys.GamepadInput.None && fseActive;
        var needActionWinMmBack = needDedicatedActions && wantsBack;

        if (needNavWinMm || needActionWinMmFallback || needActionWinMmBack)
        {
            const uint center = 32_767;
            const uint threshold = 16_000;

            for (uint id = 0; id < WinMm.MaxControllers; id++)
            {
                var wantNav = needNavWinMm && id >= navStartSlot;
                if (!wantNav && !needActionWinMmFallback && !needActionWinMmBack) continue;

                var info = WinMm.JOYINFOEX.Create();
                if (WinMm.joyGetPosEx(id, ref info) != WinMm.JOYERR_NOERROR) continue;

                if (wantNav)
                {
                    if ((info.Buttons & WinMm.ButtonA) != 0) navButtons |= PadButton.A;
                    if ((info.Buttons & WinMm.ButtonB) != 0) navButtons |= PadButton.B;
                    if ((info.Buttons & WinMm.ButtonView) != 0) navButtons |= PadButton.View;
                    if ((info.Buttons & WinMm.ButtonMenu) != 0) navButtons |= PadButton.Menu;

                    if (info.Pov < WinMm.JOY_POVCENTERED)
                    {
                        if (info.Pov <= 4500 || info.Pov >= 31500) navButtons |= PadButton.Up;
                        if (info.Pov is >= 4500 and <= 13500) navButtons |= PadButton.Right;
                        if (info.Pov is >= 13500 and <= 22500) navButtons |= PadButton.Down;
                        if (info.Pov is >= 22500 and <= 31500) navButtons |= PadButton.Left;
                    }

                    // HID Y axis has 0 at top and 65535 at bottom.
                    if (info.Y < center - threshold) navButtons |= PadButton.Up;
                    if (info.Y > center + threshold) navButtons |= PadButton.Down;
                    if (info.X < center - threshold) navButtons |= PadButton.Left;
                    if (info.X > center + threshold) navButtons |= PadButton.Right;
                }

                if (needActionWinMmBack)
                {
                    if ((info.Buttons & (1u << 10)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.BackLeft;
                    if ((info.Buttons & (1u << 11)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.BackRight;
                }

                if (needActionWinMmFallback)
                {
                    if ((info.Buttons & (1u << 0)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.A;
                    if ((info.Buttons & (1u << 1)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.B;
                    if ((info.Buttons & (1u << 2)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.X;
                    if ((info.Buttons & (1u << 3)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.Y;
                    if ((info.Buttons & (1u << 4)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.Lb;
                    if ((info.Buttons & (1u << 5)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.Rb;
                    if ((info.Buttons & (1u << 6)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.View;
                    if ((info.Buttons & (1u << 7)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.Menu;
                    if ((info.Buttons & (1u << 8)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.LsClick;
                    if ((info.Buttons & (1u << 9)) != 0) actionInputs |= DedicatedActionKeys.GamepadInput.RsClick;

                    if (info.Pov < WinMm.JOY_POVCENTERED)
                    {
                        if (info.Pov <= 4500 || info.Pov >= 31500) actionInputs |= DedicatedActionKeys.GamepadInput.DPadUp;
                        if (info.Pov is >= 4500 and <= 13500) actionInputs |= DedicatedActionKeys.GamepadInput.DPadRight;
                        if (info.Pov is >= 13500 and <= 22500) actionInputs |= DedicatedActionKeys.GamepadInput.DPadDown;
                        if (info.Pov is >= 22500 and <= 31500) actionInputs |= DedicatedActionKeys.GamepadInput.DPadLeft;
                    }
                }
            }
        }

        return (navButtons, actionInputs);
    }

    public void Dispose()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { return; }

        if (_thread.Join(TimeSpan.FromMilliseconds(250)))
        {
            _active.Dispose();
            _stop.Dispose();
        }
    }
}
