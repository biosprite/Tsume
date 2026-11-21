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
        if (TryToggleTouchKeyboard()) return;

        // The keyboard's host process starts on demand; wake it and ask again.
        Launch(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            @"microsoft shared\ink\TabTip.exe"));

        Thread.Sleep(400);
        TryToggleTouchKeyboard();
    }

    private static bool TryToggleTouchKeyboard()
    {
        try
        {
            var host = (ITipInvocation)new UIHostNoLaunch();
            host.Toggle(User32.GetDesktopWindow());
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
}
