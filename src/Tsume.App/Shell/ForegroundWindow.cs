using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Shell;

/// <summary>
/// Brings a window to the foreground past Windows' foreground lock, trying two methods in order.
/// Neither is certain, so callers check <see cref="Has"/> afterwards and retry.
/// </summary>
internal static class ForegroundWindow
{
    /// <summary>The foreground window; zero while it is changing hands.</summary>
    public static nint Current => User32.GetForegroundWindow();

    /// <summary>The window that was in front when the panel last opened: usually the game.</summary>
    public static nint BehindPanel { get; set; }

    public static bool Has(Window window) => Current == new WindowInteropHelper(window).Handle;

    /// <returns>Whether the window is in front afterwards.</returns>
    public static bool Take(Window window)
    {
        var mine = new WindowInteropHelper(window).Handle;

        // 1. Briefly share an input queue with whoever is in front.
        var theirs = User32.GetForegroundWindow();
        if (theirs != mine && theirs != 0)
        {
            var theirThread = User32.GetWindowThreadProcessId(theirs, 0);
            var myThread = Kernel32.GetCurrentThreadId();

            User32.AttachThreadInput(theirThread, myThread, attach: true);
            User32.SetForegroundWindow(mine);
            User32.AttachThreadInput(theirThread, myThread, attach: false);
        }

        // 2. A tap of Alt lifts the foreground lock for the process that sent it. A UIAccess panel is already free
        //    of the lock, so it skips the tap, which a game in front would otherwise see as a key press.
        if (User32.GetForegroundWindow() != mine)
        {
            if (!Platform.UiAccess.IsGranted) User32.SendChord(User32.VK_MENU);
            User32.SetForegroundWindow(mine);
        }

        window.Activate();
        return User32.GetForegroundWindow() == mine;
    }

    /// <summary>Describes a window for the log: program, window class, and whether it is topmost.</summary>
    public static string Describe(nint window)
    {
        if (window == 0) return "no window";

        var name = new char[128];
        var length = User32.GetClassName(window, name, name.Length);
        User32.GetWindowThreadProcessId(window, out var processId);

        string program;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            program = process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            program = $"process {processId}";
        }

        var topmost = (User32.GetWindowLongPtr(window, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0;
        return $"{program} [{new string(name, 0, length)}]{(topmost ? " topmost" : "")}";
    }
}
