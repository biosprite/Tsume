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

    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan ActivePoll = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(1);
    private const long ResumeSettleMs = 2500;

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

    /// <summary>True while the panel is open and wants button presses.</summary>
    public volatile bool IsNavigating;

    /// <summary>
    /// True during the brief Claw Gamepad -> Desktop capture transition so pre-switch gamepad motion
    /// is not double-fed into the panel alongside the incoming Desktop-mode hooks.
    /// </summary>
    public volatile bool SuppressNavigation;

    /// <summary>
    /// True while <see cref="PadIsolation"/> holds the built-in Claw pad in Desktop mode: skips slot 0
    /// (the built-in pad) so any latched XInput/HID state cannot phantom-scroll Tsume, while leaving
    /// external controllers on slots 1–3 active.
    /// </summary>
    public volatile bool SuppressBuiltInSlot;

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
        var presses = new PadPresses();
        WaitHandle[] pauseOrStop = [_active.WaitHandle, _stop.Token.WaitHandle];

        while (!_stop.IsCancellationRequested)
        {
            if (!_active.IsSet)
            {
                if (WaitHandle.WaitAny(pauseOrStop) != 0) break;
                continue;
            }

            var remainingMs = Volatile.Read(ref _resumeAfterTick) - Environment.TickCount64;
            if (remainingMs > 0)
            {
                _stop.Token.WaitHandle.WaitOne((int)Math.Min(remainingMs, 250));
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
                var (summoned, pressed) = presses.Step(ReadButtons(skipSlotZero), navigating, DateTime.UtcNow);

                if (summoned) Summoned?.Invoke();
                foreach (var button in Each(pressed)) Pressed?.Invoke(button);

                PollDedicatedActions();
            }
            catch (Exception error)
            {
                if (!_loggedWatchError)
                {
                    _loggedWatchError = true;
                    Errors.Write($"Gamepad watch failed: {error}");
                }

                _stop.Token.WaitHandle.WaitOne(ErrorBackoff);
                continue;
            }

            _stop.Token.WaitHandle.WaitOne(IsNavigating ? ActivePoll : IdlePoll);
        }
    }

    private void PollDedicatedActions()
    {
        var bindings = _actionBindings;
        if (bindings.Length == 0)
        {
            _latchedActions.Clear();
            return;
        }

        var wantsBack = false;
        foreach (var binding in bindings)
        {
            if ((binding.GamepadMask & (DedicatedActionKeys.GamepadInput.BackLeft | DedicatedActionKeys.GamepadInput.BackRight)) != 0)
            {
                wantsBack = true;
                break;
            }
        }

        var pad = DedicatedActionKeys.ReadGamepadInputs(includeBackButtons: wantsBack);
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
                else if (_latchedActions.Add(binding.Action) && !KeyDropdown.IsAnyOpen)
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

    /// <summary>Buttons held on any connected controller, with the left stick folded into the d-pad.</summary>
    private static PadButton ReadButtons(bool skipSlotZero)
    {
        var buttons = ReadXInput(skipSlotZero);
        return buttons != PadButton.None || !FullScreenExperience.IsActive
            ? buttons
            : ReadWinMm(skipSlotZero);
    }

    private static PadButton ReadXInput(bool skipSlotZero)
    {
        var buttons = PadButton.None;

        for (uint user = skipSlotZero ? 1u : 0u; user < XInput.MaxControllers; user++)
        {
            if (XInput.XInputGetState(user, out var state) != XInput.ERROR_SUCCESS) continue;

            buttons |= (PadButton)state.Buttons;

            if (state.ThumbLY > StickThreshold) buttons |= PadButton.Up;
            if (state.ThumbLY < -StickThreshold) buttons |= PadButton.Down;
            if (state.ThumbLX < -StickThreshold) buttons |= PadButton.Left;
            if (state.ThumbLX > StickThreshold) buttons |= PadButton.Right;
        }

        return buttons;
    }

    /// <summary>
    /// Fallback when <c>xinput1_4.dll</c> returns empty state (for example under Xbox FSE's <c>InputHost</c>
    /// gamepad interception). Reads the controller's standard HID gamepad interface via <c>winmm.dll</c>.
    /// </summary>
    private static PadButton ReadWinMm(bool skipSlotZero)
    {
        const uint center = 32_767;
        const uint threshold = 16_000;

        var buttons = PadButton.None;

        for (uint id = skipSlotZero ? 1u : 0u; id < WinMm.MaxControllers; id++)
        {
            var info = WinMm.JOYINFOEX.Create();
            if (WinMm.joyGetPosEx(id, ref info) != WinMm.JOYERR_NOERROR) continue;

            if ((info.Buttons & WinMm.ButtonA) != 0) buttons |= PadButton.A;
            if ((info.Buttons & WinMm.ButtonB) != 0) buttons |= PadButton.B;
            if ((info.Buttons & WinMm.ButtonView) != 0) buttons |= PadButton.View;
            if ((info.Buttons & WinMm.ButtonMenu) != 0) buttons |= PadButton.Menu;

            if (info.Pov < WinMm.JOY_POVCENTERED)
            {
                if (info.Pov <= 4500 || info.Pov >= 31500) buttons |= PadButton.Up;
                if (info.Pov is >= 4500 and <= 13500) buttons |= PadButton.Right;
                if (info.Pov is >= 13500 and <= 22500) buttons |= PadButton.Down;
                if (info.Pov is >= 22500 and <= 31500) buttons |= PadButton.Left;
            }

            // HID Y axis has 0 at top and 65535 at bottom.
            if (info.Y < center - threshold) buttons |= PadButton.Up;
            if (info.Y > center + threshold) buttons |= PadButton.Down;
            if (info.X < center - threshold) buttons |= PadButton.Left;
            if (info.X > center + threshold) buttons |= PadButton.Right;
        }

        return buttons;
    }

    private static IEnumerable<PadButton> Each(PadButton buttons)
    {
        foreach (var button in Enum.GetValues<PadButton>())
            if (button != PadButton.None && buttons.HasFlag(button))
                yield return button;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromMilliseconds(250));
        _active.Dispose();
        _stop.Dispose();
    }
}
