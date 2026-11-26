using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Platform;

/// <summary>One-shot conveniences behind the tiles.</summary>
public static class QuickActions
{
    /// <summary>Full-screen capture into Pictures\Screenshots (Win + PrtSc).</summary>
    public static void Screenshot() => User32.SendChord(User32.VK_LWIN, User32.VK_SNAPSHOT);

    /// <summary>
    /// Minimise or restore every window (Win + D), or exit Xbox Full Screen Experience to the desktop (Win + F11)
    /// when FSE is active and there is no desktop raised behind the current app.
    /// </summary>
    public static void ShowDesktop()
    {
        if (FullScreenExperience.IsActive) User32.SendChord(User32.VK_LWIN, User32.VK_F11);
        else User32.SendChord(User32.VK_LWIN, User32.VK_D);
    }

    public static void TaskManager() => Launch("taskmgr.exe");

    /// <summary>Opens Steam Big Picture via Steam's registered URI scheme.</summary>
    public static void OpenSteamBigPicture() => Launch("steam://open/bigpicture");

    /// <summary>Toggles Xbox Game Bar via a tagged Win+G chord that passes through Tsume's Game Bar hook.</summary>
    public static void OpenGameBar() => User32.SendChord(Input.GameBarButtonHook.PassThroughExtraInfo, User32.VK_LWIN, User32.VK_G);

    public static void Run(HardwareButtonAction action, Action toggleTsume)
    {
        switch (action)
        {
            case HardwareButtonAction.OpenTsume:
                toggleTsume();
                break;

            case HardwareButtonAction.XboxGameBar:
                OpenGameBar();
                break;

            case HardwareButtonAction.SteamBigPicture:
                OpenSteamBigPicture();
                break;

            case HardwareButtonAction.TaskManager:
                TaskManager();
                break;

            case HardwareButtonAction.TouchKeyboard:
                ToggleTouchKeyboard();
                break;

            case HardwareButtonAction.Screenshot:
                Screenshot();
                break;

            case HardwareButtonAction.ShowDesktop:
                ShowDesktop();
                break;
        }
    }

    /// <summary>
    /// Presses Insert, the key OptiScaler's in-game menu opens on, for the window behind the panel.
    /// Posted to that window rather than typed, so the panel can stay open and in front.
    /// </summary>
    public static void OpenOptiScalerMenu()
    {
        var game = Shell.ForegroundWindow.BehindPanel;
        if (game == 0) return;

        // lParam: repeat count 1, scan code 0x52, extended key; key-up adds "was down" and "being released".
        const nint down = 0x0152_0001;
        User32.PostMessage(game, User32.WM_KEYDOWN, User32.VK_INSERT, down);
        User32.PostMessage(game, User32.WM_KEYUP, User32.VK_INSERT, down | unchecked((nint)0xC000_0000));
    }

    /// <summary>Shows or hides the Windows touch keyboard.</summary>
    public static void ToggleTouchKeyboard()
    {
        if (TryToggleTouchKeyboard(0)) return;

        // The keyboard's host process starts on demand; wake it and ask again.
        WakeTouchKeyboardHost();
        Thread.Sleep(400);
        TryToggleTouchKeyboard(0);
    }

    /// <summary>Shows or hides the Windows touch keyboard without blocking the UI thread if TabTip needs to start.</summary>
    public static async Task ToggleTouchKeyboardAsync(nint window = 0)
    {
        if (TryToggleTouchKeyboard(window)) return;

        WakeTouchKeyboardHost();
        await Task.Delay(400);
        TryToggleTouchKeyboard(window);
    }

    /// <summary>
    /// Ensures the Windows touch keyboard is visible, without toggling it off if it is already shown.
    /// Returns true if this call opened it.
    /// </summary>
    public static async Task<bool> ShowTouchKeyboardAsync(nint window = 0)
    {
        if (IsTouchKeyboardVisible()) return false;

        await ToggleTouchKeyboardAsync(window);
        return true;
    }

    /// <summary>Dismisses the Windows touch keyboard if it is currently visible.</summary>
    public static void HideTouchKeyboard(nint window = 0)
    {
        if (IsTouchKeyboardVisible()) TryToggleTouchKeyboard(window);
    }

    /// <summary>True when Windows' touch keyboard input pane is currently shown on screen.</summary>
    public static bool IsTouchKeyboardVisible()
    {
        try
        {
            var pane = (IFrameworkInputPane)new FrameworkInputPane();
            return pane.Location(out var rect) == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>True if <paramref name="window"/> belongs to Windows' on-screen / touch keyboard host.</summary>
    public static bool IsTouchKeyboardWindow(nint window)
    {
        if (window == 0) return false;

        var buffer = new char[128];
        var length = User32.GetClassName(window, buffer, buffer.Length);
        if (length > 0)
        {
            var className = new string(buffer, 0, length);
            if (className.Equals("IPTip_Main_Window", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        User32.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0) return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("TabTip", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("osk", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void WakeTouchKeyboardHost() => Launch(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        @"microsoft shared\ink\TabTip.exe"));

    private static bool TryToggleTouchKeyboard(nint window)
    {
        try
        {
            var host = (ITipInvocation)new UIHostNoLaunch();
            host.Toggle(window != 0 ? window : User32.GetDesktopWindow());
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static void Launch(string file)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException) { }
    }

    [ComImport, Guid("4CE576FA-83DC-4F88-951C-9D0782B4E376")]
    private class UIHostNoLaunch;

    [ComImport, Guid("37C994E7-432B-4834-A2F7-DCE1F13B834B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(nint window);
    }

    [ComImport, Guid("D5120AA3-46BA-44C5-822D-CA8092C1FC72")]
    private class FrameworkInputPane;

    [ComImport, Guid("5752238B-24F0-495A-82F1-2FD593056796"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFrameworkInputPane
    {
        int Advise(nint pWindow, nint pHandler, out uint pdwCookie);
        int AdviseWithHWND(nint hwnd, nint pHandler, out uint pdwCookie);
        int Unadvise(uint dwCookie);
        int Location(out User32.RECT prcInputPaneScreenLocation);
    }
}
