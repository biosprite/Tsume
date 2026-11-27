using System.Runtime.InteropServices;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Shell;

/// <summary>
/// Keeps the display at its full refresh rate while the panel is up, by changing one nearly
/// invisible pixel every few milliseconds. The Claw's 30–120 Hz VRR screen idles at 30 Hz over a
/// still desktop, which paces the panel's animation (measured: 28 refreshes in 0.9 s; 109 with this).
/// </summary>
// DCompositionBoostCompositorClock made no difference on this hardware.
internal static partial class DisplayClock
{
    /// <summary>Comfortably more often than a 120 Hz display refreshes.</summary>
    private const int NudgeEveryMilliseconds = 4;

    private sealed class StopSignal
    {
        public volatile bool Requested;
    }

    private static readonly Lock Gate = new();
    private static StopSignal? _stop;

    /// <summary>Hold the display at full rate (<c>true</c>), or let it idle again (<c>false</c>).</summary>
    public static void KeepAwake(bool on)
    {
        lock (Gate)
        {
            if (on == (_stop is not null)) return;

            if (on)
            {
                var stop = _stop = new StopSignal();
                new Thread(() => Nudge(stop)) { IsBackground = true, Name = "Tsume display clock" }.Start();
                return;
            }

            _stop!.Requested = true;
            _stop = null;
        }
    }

    /// <summary>Waits for the display's next refresh. For measuring it; see <see cref="OpenTrace"/>.</summary>
    public static void WaitForTick()
    {
        try { _ = DCompositionWaitForCompositorClock(0, 0, 250); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { Thread.Sleep(8); }
    }

    private static void Nudge(StopSignal stop)
    {
        nint pixel = 0;
        nint timer = 0;

        try
        {
            // One pixel, top-left, never focused, never clicked, at 1/255 opacity.
            pixel = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                "Static", "", WS_POPUP | WS_VISIBLE, 0, 0, 1, 1, 0, 0, 0, 0);
            if (pixel == 0) return;

            // A high-resolution timer keeps time without changing the whole system's timer rate.
            timer = CreateWaitableTimerEx(0, 0, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (timer == 0) timer = CreateWaitableTimerEx(0, 0, 0, TIMER_ALL_ACCESS);
            if (timer == 0) return;

            long now = -1;
            if (!SetWaitableTimer(timer, in now, NudgeEveryMilliseconds, 0, 0, false)) return;

            var faint = false;
            while (!stop.Requested)
            {
                if (MsgWaitForMultipleObjectsEx(1, in timer, 100, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_FAILED)
                    break;

                // The window must answer Windows' messages, or anything broadcasting to all windows waits on it.
                while (PeekMessage(out var message, 0, 0, 0, PM_REMOVE)) DispatchMessage(in message);

                faint = !faint;
                SetLayeredWindowAttributes(pixel, 0, (byte)(faint ? 1 : 2), LWA_ALPHA);
            }
        }
        finally
        {
            if (timer != 0) Kernel32.CloseHandle(timer);
            if (pixel != 0) DestroyWindow(pixel);
            lock (Gate)
            {
                if (ReferenceEquals(_stop, stop)) _stop = null;
            }
        }
    }

    private const uint WAIT_FAILED = 0xFFFF_FFFF;

    private const uint WS_POPUP = 0x8000_0000;
    private const uint WS_VISIBLE = 0x1000_0000;
    private const uint WS_EX_TOPMOST = 0x0000_0008;
    private const uint WS_EX_TRANSPARENT = 0x0000_0020;
    private const uint WS_EX_TOOLWINDOW = 0x0000_0080;
    private const uint WS_EX_LAYERED = 0x0008_0000;
    private const uint WS_EX_NOACTIVATE = 0x0800_0000;
    private const uint LWA_ALPHA = 0x2;
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    private const uint TIMER_ALL_ACCESS = 0x1F_0003;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x4;
    private const uint PM_REMOVE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(
        uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint window, uint colourKey, byte alpha, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out MSG message, nint window, uint first, uint last, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in MSG message);

    [LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, in nint handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW")]
    private static partial nint CreateWaitableTimerEx(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(
        nint timer, in long dueTime, int periodMilliseconds, nint routine, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("dcomp.dll")]
    private static partial uint DCompositionWaitForCompositorClock(uint count, nint handles, uint timeoutMilliseconds);
}
