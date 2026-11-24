using Tsume.App.Controls;
using Tsume.App.Platform.Interop;
using Tsume.Core.Controller;

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

    private sealed record PadActionBinding(
        DedicatedAction Action,
        DedicatedActionKeys.GamepadInput GamepadMask,
        byte[] KeyboardCodes);

    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly HashSet<DedicatedAction> _latchedActions = [];
    private volatile PadActionBinding[] _actionBindings = [];

    /// <summary>Raised on a background thread when the summon chord is pressed.</summary>
    public event Action? Summoned;

    /// <summary>Raised on a background thread for each press (and repeat) while <see cref="IsNavigating"/>.</summary>
    public event Action<PadButton>? Pressed;

    /// <summary>Raised on a background thread when a Dedicated Action gamepad chord is pressed.</summary>
    public event Action<DedicatedAction>? ActionTriggered;

    /// <summary>True while the panel is open and wants button presses.</summary>
    public volatile bool IsNavigating;

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

        _actionBindings = [.. list];
    }

    public Gamepad()
    {
        _thread = new Thread(Watch) { IsBackground = true, Name = "Tsume gamepad" };
        _thread.Start();
    }

    private void Watch()
    {
        var presses = new PadPresses();

        while (!_stop.IsCancellationRequested)
        {
            var (summoned, pressed) = presses.Step(ReadButtons(), IsNavigating, DateTime.UtcNow);

            if (summoned) Summoned?.Invoke();
            foreach (var button in Each(pressed)) Pressed?.Invoke(button);

            PollDedicatedActions();

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
                if (_latchedActions.Add(binding.Action) && !KeyDropdown.IsAnyOpen)
                    ActionTriggered?.Invoke(binding.Action);
            }
            else
            {
                _latchedActions.Remove(binding.Action);
            }
        }
    }

    /// <summary>Buttons held on any connected controller, with the left stick folded into the d-pad.</summary>
    private static PadButton ReadButtons()
    {
        var buttons = ReadXInput();
        return buttons != PadButton.None ? buttons : ReadWinMm();
    }

    private static PadButton ReadXInput()
    {
        var buttons = PadButton.None;

        for (uint user = 0; user < XInput.MaxControllers; user++)
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
    private static PadButton ReadWinMm()
    {
        const uint center = 32_767;
        const uint threshold = 16_000;

        var buttons = PadButton.None;
        var info = WinMm.JOYINFOEX.Create();

        for (uint id = 0; id < WinMm.MaxControllers; id++)
        {
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
        _stop.Dispose();
    }
}
