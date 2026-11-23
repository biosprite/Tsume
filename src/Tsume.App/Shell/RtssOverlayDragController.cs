using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Tsume.App.Platform.Rtss;

namespace Tsume.App.Shell;

/// <summary>
/// Coordinates <see cref="RtssOverlayDragWindow"/> lifecycle with RTSS overlay state, Tsume's side panel
/// open/close transitions, and display resolution changes.
/// </summary>
public sealed class RtssOverlayDragController : IDisposable
{
    private readonly RtssOverlayService _service;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _syncTimer;

    private RtssOverlayDragWindow? _window;
    private bool _panelOpen;
    private bool _disposed;

    public RtssOverlayDragController(RtssOverlayService service, Dispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;

        // Lightweight periodic check so if RTSS starts or exits externally, the touch handle follows.
        _syncTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);

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

            var window = EnsureWindow();
            window.EnterInteractiveMode(
                rect.Level, rect.X, rect.Y, rect.Width, rect.Height, rect.ScreenWidth, rect.ScreenHeight);
        });
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

    private RtssOverlayDragWindow EnsureWindow()
    {
        if (_window is not null) return _window;

        _window = new RtssOverlayDragWindow(_service);
        _window.InteractiveClosed += Refresh;
        return _window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _syncTimer.Stop();
        _service.OverlayStateChanged -= OnOverlayStateChanged;
        _service.Restarted -= OnOverlayStateChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (_window is not null)
        {
            _window.InteractiveClosed -= Refresh;
            _window.Close();
            _window = null;
        }
    }
}
