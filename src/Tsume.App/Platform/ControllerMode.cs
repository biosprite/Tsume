using Tsume.Core.Controller;

namespace Tsume.App.Platform;

/// <summary>
/// Reads and switches the built-in pad between gamepad and cursor mode. The pad is the only
/// record of the mode; no handle is kept open between calls (finding the pad costs ~4 ms).
/// </summary>
public static class ControllerMode
{
    // Claw 8, controller firmware 4.19: the pad stays on the USB bus and reads back the new mode
    // after ~30 ms. SetAsync still re-finds the pad in case another firmware re-enumerates.

    /// <summary>MSI's own pause between asking for a mode and reading it back.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan LookAgain = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(5);

    /// <summary>The mode the pad is in now; null if it cannot be found or does not answer.</summary>
    public static async Task<PadMode?> ReadAsync()
    {
        using var controller = await Task.Run(ClawController.Find);
        if (controller is null) return null;

        return await controller.ReadModeAsync() is { } now ? Named(now.Mode) : null;
    }

    /// <summary>
    /// Switches the pad and waits until it reads back the new mode; false if it never does.
    /// <paramref name="note"/> receives each step, for the check report.
    /// </summary>
    public static async Task<bool> SetAsync(PadMode wanted, Action<string>? note = null)
    {
        var started = DateTime.UtcNow;
        string At() => $"{(DateTime.UtcNow - started).TotalMilliseconds,5:0} ms";

        var controller = await Task.Run(ClawController.Find);
        try
        {
            if (controller is null)
            {
                note?.Invoke($"{At()}  controller not found");
                return false;
            }

            if (await controller.ReadModeAsync() is not { } before)
            {
                note?.Invoke($"{At()}  mode read: no answer — nothing sent");
                return false;
            }

            note?.Invoke($"{At()}  mode {before.Mode}, M keys {before.MKeys}");
            if (before.Mode == (byte)wanted) return true;

            // Pass the paddles' (M keys) setting back unchanged.
            var sent = await controller.SwitchModeAsync(wanted, Math.Min(before.MKeys, (byte)2));
            note?.Invoke($"{At()}  switch to {(byte)wanted} ({wanted}): {(sent ? "sent" : "NOT sent")}");
            if (!sent) return false;

            await Task.Delay(Settle);

            while (DateTime.UtcNow - started < GiveUp)
            {
                if (controller is not null)
                {
                    var now = await controller.ReadModeAsync();
                    note?.Invoke($"{At()}  read back: {(now is { } n ? $"mode {n.Mode}, M keys {n.MKeys}" : "no answer on this handle")}");
                    if (now?.Mode == (byte)wanted) return true;
                }

                // Not switched yet, or not answering: find the pad again.
                controller?.Dispose();
                await Task.Delay(LookAgain);
                controller = await Task.Run(ClawController.Find);
                note?.Invoke($"{At()}  looked for the pad again: {(controller is null ? "not there" : "found")}");
            }

            return false;
        }
        finally
        {
            controller?.Dispose();
        }
    }

    /// <summary>How long the changed pad may take to report a button that was held through the switch.</summary>
    private static readonly TimeSpan CarriedBy = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a carried press must stay up to count as let go.</summary>
    private static readonly TimeSpan LetGo = TimeSpan.FromMilliseconds(40);

    private static readonly TimeSpan ReleaseGiveUp = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReleasePoll = TimeSpan.FromMilliseconds(10);

    private const ushort XInputA = 0x1000;

    /// <summary>
    /// Completes once the press that asked for a switch is let go. That button is still down as
    /// the pad changes, and the pad then reports it afresh as a click, a key or an A press, which
    /// would switch straight back. Returns quickly if nothing was carried over, so the next
    /// press is not lost.
    /// </summary>
    public static async Task ReleasedAsync()
    {
        var started = DateTime.UtcNow;
        DateTime? lastHeld = null;

        while (true)
        {
            var now = DateTime.UtcNow;
            if (AnyPressHeld()) lastHeld = now;

            if (lastHeld is { } held ? now - held >= LetGo : now - started >= CarriedBy) return;
            if (now - started >= ReleaseGiveUp) return;

            await Task.Delay(ReleasePoll);
        }
    }

    private static bool AnyPressHeld()
    {
        if (Interop.User32.GetAsyncKeyState(Interop.User32.VK_LBUTTON) < 0
            || Interop.User32.GetAsyncKeyState(Interop.User32.VK_RETURN) < 0
            || Interop.User32.GetAsyncKeyState(Interop.User32.VK_SPACE) < 0)
        {
            return true;
        }

        for (uint user = 0; user < Interop.XInput.MaxControllers; user++)
        {
            if (Interop.XInput.XInputGetState(user, out var state) == Interop.XInput.ERROR_SUCCESS
                && (state.Buttons & XInputA) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The pad's DInput and MSI modes are gamepads too; only desktop mode is the cursor.</summary>
    private static PadMode Named(byte mode) => mode == (byte)PadMode.Cursor ? PadMode.Cursor : PadMode.Gamepad;
}
