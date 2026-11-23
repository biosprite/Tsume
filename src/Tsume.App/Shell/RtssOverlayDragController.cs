using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Tsume.App.Platform.Interop;
using Tsume.App.Platform.Rtss;

namespace Tsume.App.Shell;

/// <summary>
/// Coordinates <see cref="RtssOverlayDragWindow"/> lifecycle with RTSS overlay state, foreground hooked
/// 3D window visibility, Tsume's side panel open/close transitions, and display resolution changes.
/// </summary>
public sealed class RtssOverlayDragController : IDisposable
{
    private readonly RtssOverlayService _service;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _syncTimer;
    private readonly User32.WinEventProc _winEventCallback;
    private nint _foregroundHook;
    private nint _minimizeHook;

    private RtssOverlayDragWindow? _window;
    private nint _interactiveTargetHwnd;
    private bool _panelOpen;
    private bool _disposed;

    public RtssOverlayDragController(RtssOverlayService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        _winEventCallback = OnWinEvent;

        // Lightweight periodic check so if RTSS starts, exits, or unhooks an app, the touch handle follows.
        _syncTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);

        _foregroundHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0, _winEventCallback, 0, 0, User32.WINEVENT_OUTOFCONTEXT);
        _minimizeHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_MINIMIZESTART, User32.EVENT_SYSTEM_MINIMIZEEND, 0, _winEventCallback, 0, 0, User32.WINEVENT_OUTOFCONTEXT);

        _service.OverlayStateChanged += OnOverlayStateChanged;
        _service.Restarted += OnOverlayStateChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _syncTimer.Start();
        Refresh();
    }

    /// <summary>Hides the standby touch handle while Tsume's main panel is open so it never overlaps panel controls.</summary>
    public void SetPanelOpen(bool open)
    {
        _panelOpen = open;
        if (open) _window?.ExitInteractiveMode();
        Refresh();
    }

    /// <summary>Opens full-screen interactive "Drag on screen" mode for the active FPS overlay.</summary>
    public void BeginInteractiveMove()
    {
        if (_disposed) return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _panelOpen) return;
            if (_service.GetActiveOverlayRect() is not { } rect) return;

            _interactiveTargetHwnd = User32.GetForegroundWindow();
            var window = EnsureWindow();
            window.EnterInteractiveMode(
                rect.Level, rect.X, rect.Y, rect.Width, rect.Height, rect.ScreenWidth, rect.ScreenHeight);
        });
    }

    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed) return;

        if (_window is { IsInteractive: true })
        {
            if (eventType == User32.EVENT_SYSTEM_MINIMIZESTART)
            {
                if (_interactiveTargetHwnd == 0 || window == _interactiveTargetHwnd)
                    _window.ExitInteractiveMode();
            }
            else if (eventType == User32.EVENT_SYSTEM_FOREGROUND)
            {
                var fg = window != 0 ? window : User32.GetForegroundWindow();
                if (fg == 0 || fg == _interactiveTargetHwnd) return;

                // If interactive mode started while Tsume's panel was still handing focus back, adopt the target window.
                if (_interactiveTargetHwnd == 0 || IsOwnProcessWindow(_interactiveTargetHwnd))
                {
                    if (!IsOwnProcessWindow(fg))
                        _interactiveTargetHwnd = fg;
                    return;
                }

                _window.ExitInteractiveMode();
            }
            return;
        }

        Refresh();
    }

    private void OnInteractiveClosed()
    {
        _interactiveTargetHwnd = 0;
        Refresh();
    }

    private void OnOverlayStateChanged()
    {
        if (!_disposed) _dispatcher.BeginInvoke(Refresh);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            // Republish clamped coordinates for the new screen size and re-sync the touch handle.
            _service.ApplySettings(_service.Settings);
            Refresh();
        });
    }

    private void Refresh()
    {
        if (_disposed) return;

        if (_window is { IsActivelyPositioning: true }) return;

        var wantStandby = !_panelOpen
            && _service.Settings.TouchDragEnabled
            && _service.IsTsumeOverlayActive
            && _service.IsRunning
            && _service.IsOsdVisible() != false
            && IsForegroundWindowHookedByRtss()
            && _service.GetActiveOverlayRect() is not null;

        if (!wantStandby)
        {
            _window?.HideQuietly();
            return;
        }

        if (_service.GetActiveOverlayRect() is not { } rect)
        {
            _window?.HideQuietly();
            return;
        }

        var window = EnsureWindow();
        window.SyncStandby(rect.Level, rect.X, rect.Y, rect.Width, rect.Height, rect.ScreenWidth, rect.ScreenHeight);
    }

    private static bool IsForegroundWindowHookedByRtss()
    {
        var fg = User32.GetForegroundWindow();
        if (fg == 0 || User32.IsIconic(fg) || !User32.IsWindowVisible(fg))
            return false;

        User32.GetWindowThreadProcessId(fg, out var fgPid);
        if (fgPid == 0 || fgPid == (uint)Environment.ProcessId)
            return false;

        var hookedPids = RtssSharedMemory.GetHookedProcessIds();
        if (hookedPids is null || hookedPids.Count == 0)
            return false;

        if (hookedPids.Contains(fgPid))
            return true;

        // UWP games run inside ApplicationFrameHost; check child CoreWindow processes.
        var className = new char[64];
        var len = User32.GetClassName(fg, className, className.Length);
        if (len > 0 && new ReadOnlySpan<char>(className, 0, len).SequenceEqual("ApplicationFrameWindow"))
        {
            var matched = false;
            User32.EnumChildWindows(fg, (child, _) =>
            {
                User32.GetWindowThreadProcessId(child, out var childPid);
                if (childPid != 0 && childPid != fgPid && hookedPids.Contains(childPid))
                {
                    matched = true;
                    return false;
                }
                return true;
            }, 0);
            return matched;
        }

        return false;
    }

    private static bool IsOwnProcessWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private RtssOverlayDragWindow EnsureWindow()
    {
        if (_window is not null) return _window;

        _window = new RtssOverlayDragWindow(_service);
        _window.InteractiveClosed += OnInteractiveClosed;
        _window.DragEnded += Refresh;
        return _window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _syncTimer.Stop();
        if (_foregroundHook != 0) { User32.UnhookWinEvent(_foregroundHook); _foregroundHook = 0; }
        if (_minimizeHook != 0) { User32.UnhookWinEvent(_minimizeHook); _minimizeHook = 0; }

        _service.OverlayStateChanged -= OnOverlayStateChanged;
        _service.Restarted -= OnOverlayStateChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (_window is not null)
        {
            _window.InteractiveClosed -= OnInteractiveClosed;
            _window.DragEnded -= Refresh;
            _window.Close();
            _window = null;
        }
    }
}
