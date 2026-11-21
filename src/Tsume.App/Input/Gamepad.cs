using Tsume.App.Platform.Interop;

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

    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    /// <summary>Raised on a background thread when the summon chord is pressed.</summary>
    public event Action? Summoned;

    /// <summary>Raised on a background thread for each press (and repeat) while <see cref="IsNavigating"/>.</summary>
    public event Action<PadButton>? Pressed;

    /// <summary>True while the panel is open and wants button presses.</summary>
    public volatile bool IsNavigating;

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

            _stop.Token.WaitHandle.WaitOne(IsNavigating ? ActivePoll : IdlePoll);
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
