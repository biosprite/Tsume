using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Tsume.App.Input;
using Tsume.App.Platform;
using Tsume.App.Shell;
using Tsume.App.ViewModels;
using Tsume.Core.Devices;
using Tsume.Core.Ipc;

namespace Tsume.App;

/// <summary>
/// Start-up wiring. Tsume runs in the background with no window until summoned: the M button (while
/// MSI Center M is paused), View + Menu on the pad, Win + Alt + Q, or launching Tsume again.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application does not implement IDisposable; all owned fields are disposed in OnExit.")]
public partial class App
{
    // --background is how Windows starts Tsume at sign-in; --stay keeps the panel open on focus loss (development).
    // The check/read switches take a report file, write it and exit. --screens <folder> saves a picture of each
    // screen and may be followed by --add-xmx, --main (main screen only, live readings) or --preset <name>.
    public const string BackgroundSwitch = "--background";
    private const string StayOpenSwitch = "--stay";
    private const string OpenSettingsSwitch = "--settings";
    private const string OpenLightingSwitch = "--lighting";

    // Marks a copy the helper started with UIAccess, so a copy that still lacks it (the grant did not take) never
    // turns around and asks the helper again. See the UIAccess gate in OnStartup and PanelStarter in the helper.
    private const string RelaunchedSwitch = "--relaunched";
    private const string ControllerReadSwitch = "--controller-read";
    private const string LightingCheckSwitch = "--lighting-check";
    private const string PadModeCheckSwitch = "--pad-mode-check";
    private const string OptiScalerCheckSwitch = "--optiscaler-check";
    private const string OpenOptiScalerSwitch = "--optiscaler";
    private const string OpenRtssSwitch = "--rtss";
    private const string ScreensSwitch = "--screens";

    // Run by the uninstaller (installer\Package.wxs) before it removes anything.
    private const string UninstallingSwitch = "--uninstalling";

    /// <summary>The firmware sometimes raises two events for one press; a second within this window is ignored.</summary>
    private static readonly TimeSpan ButtonEcho = TimeSpan.FromMilliseconds(350);

    private static readonly Tsume.Core.Diagnostics.Journal Errors = new("errors");

    private readonly DispatcherTimer _idle =new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly CancellationTokenSource _appExit = new();

    private SingleInstance? _instance;
    private GlobalHotkey? _hotkey;
    private Gamepad? _gamepad;
    private PadIsolation? _padIsolation;
    private ClawButtons? _buttons;
    private GameBarButtonHook? _gameBarHook;
    private DedicatedActionHook? _dedicatedActions;
    private PanelViewModel? _panel;
    private OverlayWindow? _window;
    private RtssOverlayDragController? _rtssDrag;
    private DateTime _lastGameBarPress;
    private readonly Dictionary<DedicatedAction, DateTime> _lastDedicatedActions = [];

    /// <summary>Latest controls profile for lock-free reads from the WMI button thread.</summary>
    private ControlsProfile? _activeControls;

    /// <summary>Latest Dedicated Action bindings for lock-free reads from the WMI button thread.</summary>
    private IReadOnlyList<DedicatedActionBinding> _dedicatedActionBindings = [];

    /// <summary>Whether MSI Center M was running when last looked at. Read on the button listener's thread.</summary>
    private volatile bool _msiCenterRuns;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled fault must not kill the panel: log it to %LOCALAPPDATA%\Tsume\errors.log and carry on.
        AppDomain.CurrentDomain.UnhandledException += (_, fault) =>
        {
            if (fault.ExceptionObject is not null) Errors.Write(fault.ExceptionObject.ToString()!);
        };
        DispatcherUnhandledException += (_, fault) =>
        {
            Errors.Write(fault.Exception.ToString());
            fault.Handled = true;
        };

        if (e.Args is [ControllerReadSwitch or LightingCheckSwitch or PadModeCheckSwitch or OptiScalerCheckSwitch, var reportFile])
        {
            // Diagnostics: talk to the hardware, write what it said, and leave.
            Func<Task<string>> check = e.Args[0] switch
            {
                ControllerReadSwitch => ControllerReport.RunAsync,
                LightingCheckSwitch => ControllerReport.WriteCheckAsync,
                PadModeCheckSwitch => ControllerReport.ModeCheckAsync,
                _ => Upscaling.Diagnostics.SelfCheck.RunAsync,
            };
            Task.Run(check).ContinueWith(read =>
            {
                File.WriteAllText(reportFile, read.IsFaulted ? read.Exception!.ToString() : read.Result);
                Dispatcher.BeginInvoke(Shutdown);
            });
            return;
        }

        if (e.Args is [ScreensSwitch, var pictures, ..])
        {
            // Development: draw the screens off to one side, save them as pictures, and leave.
            ScreenShots.Take(pictures, then: Shutdown);
            return;
        }

        if (e.Args is [UninstallingSwitch])
        {
            try { StartWithWindows.RemoveIfMine(); }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

            // Offers to take OptiScaler out of the games; the uninstaller waits until this copy exits.
            // Colours first: a window made before them would fix the accent in place.
            Theme.Colours.Apply(SavedState.Load().Colours ?? Theme.ColourScheme.Default);

            var asking = new UninstallWindow();
            if (!asking.IsNeeded)
            {
                Shutdown();
                return;
            }

            asking.Closed += (_, _) => Shutdown();
            asking.Show();
            return;
        }

        var quietly = e.Args.Contains(BackgroundSwitch);

        // If a live instance already owns the mutex, toggle it (or exit quietly for --background) without
        // asking the helper to spawn a redundant UIAccess copy on every Explorer Run launch or shortcut click.
        if (SingleInstance.TrySignalExisting(toggle: !quietly))
        {
            Shutdown();
            return;
        }

        // The panel has to sit in the UIAccess window band to show over the taskbar, Start, Search, Xbox FSE, and
        // elevated windows. Only the helper, which runs as the system, can hand that to an unsigned program. A copy
        // that starts without it — the Start-menu shortcut, or the sign-in entry when the helper was not yet granting
        // it — asks the helper to start Tsume again with UIAccess and steps aside. This comes before taking the
        // single-instance lock so it does not hold the lock while the UIAccess copy takes over.
        if (!UiAccess.IsGranted && !e.Args.Contains(RelaunchedSwitch) && StartAgainWithUiAccess(e.Args))
        {
            Shutdown();
            return;
        }

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Already running: a manual second launch toggles the panel.
            if (!quietly) _instance.AskFirstInstanceToToggle();
            Shutdown();
            return;
        }

        _padIsolation = new PadIsolation(Dispatcher);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _padIsolation?.RestoreSynchronously();
        SessionEnding += (_, _) => _padIsolation?.RestoreSynchronously();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        var panel = _panel = new PanelViewModel(
            SavedState.Load(),
            dismiss: () => _window!.CloseAsync(),
            quit: Shutdown,
            padIsolation: _padIsolation);
        panel.StartBackgroundServices();

        _window = new OverlayWindow(panel, stayOpen: e.Args.Contains(StayOpenSwitch));
        _rtssDrag = new RtssOverlayDragController(panel.RtssService, Dispatcher);
        _window.OpenChanged += open => _rtssDrag.SetPanelOpen(open);

        // Raise scheduling priority so a busy game does not starve the panel.
        RightOfWay.Settle();

        ListenForSummons(_window, panel);
        SlimDownWhenIdle(_window);
        KeepEntryPointsCurrent();

        if (!quietly) _window.Open();
        else Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, _window.WarmUp);

        if (e.Args.Contains(OpenSettingsSwitch)) panel.Settings.IsOpen = true;
        if (e.Args.Contains(OpenLightingSwitch)) panel.Lighting.IsOpen = true;
        if (e.Args.Contains(OpenOptiScalerSwitch)) panel.Upscaling.IsOpen = true;
        if (e.Args.Contains(OpenRtssSwitch)) panel.Rtss.IsOpen = true;
    }

    /// <summary>
    /// Asks the helper to start Tsume again with UIAccess, passing on only the start-up intentions that matter.
    /// Returns false — and the panel carries on without UIAccess, as before — when the helper is absent or refuses.
    /// Runs before the dispatcher, so a short blocking wait on the pipe is fine, as long as it is not on this thread.
    /// On a background sign-in launch (e.g. Explorer's Run entry during Xbox FSE boot) while the helper service is
    /// installed, waits briefly for the service's pipe to come up so Tsume does not settle for a non-UIAccess copy.
    /// </summary>
    private static bool StartAgainWithUiAccess(string[] args)
    {
        var request = new StartPanel(
            Background: args.Contains(BackgroundSwitch),
            Settings: args.Contains(OpenSettingsSwitch),
            Lighting: args.Contains(OpenLightingSwitch),
            OptiScaler: args.Contains(OpenOptiScalerSwitch),
            Stay: args.Contains(StayOpenSwitch),
            Rtss: args.Contains(OpenRtssSwitch));

        var attempts = request.Background && HelperServiceInstalled() ? 20 : 1;

        try
        {
            // On the thread pool: blocking here while the client resumed on this thread's dispatcher would hang for good.
            return Task.Run(async () =>
            {
                var client = new HelperClient();
                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    var reply = await client.SendAsync(request);
                    if (reply.Ok) return true;
                    if (attempt < attempts) await Task.Delay(500);
                }

                return false;
            }).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Errors.Write(error.ToString());
            return false;
        }
    }

    private static bool HelperServiceInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TsumeHelper");
            return key is not null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ListenForSummons(OverlayWindow window, PanelViewModel panel)
    {
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += window.Toggle;
        if (!_hotkey.IsRegistered)
            _ = RetryHotkeyRegistrationAsync(_appExit.Token);

        _gamepad = new Gamepad();
        _gamepad.Summoned += () => Dispatcher.BeginInvoke(window.Toggle);
        _gamepad.Pressed += button => Dispatcher.BeginInvoke(() => window.Press(button));
        _gamepad.ActionTriggered += action => Dispatcher.BeginInvoke(DispatcherPriority.Send, () => OnDedicatedAction(action, window, panel));

        _dedicatedActions = new DedicatedActionHook(Dispatcher);
        _dedicatedActions.Triggered += action => OnDedicatedAction(action, window, panel);

        void SyncDedicatedActions()
        {
            var bindings = panel.Settings.Controls.GetDedicatedActionBindings();
            Volatile.Write(ref _dedicatedActionBindings, bindings);
            _dedicatedActions.SetBindings(bindings);
            _gamepad.SetActionBindings(bindings);
            SyncHardwareButtons(window, panel);
        }

        panel.Settings.Controls.DedicatedActionsChanged += SyncDedicatedActions;
        SyncDedicatedActions();

        _padIsolation!.Pressed += window.Press;
        _padIsolation.StateChanged += (capturing, captured) =>
        {
            _gamepad.SuppressNavigation = capturing;
            _gamepad.SuppressBuiltInSlot = captured;
        };

        // The controller is only read closely while the panel is open; on the Claw, temporarily
        // isolate it from the game behind the panel so inputs never move both at once.
        window.OpenChanged += open =>
        {
            _gamepad.IsNavigating = open;
            _padIsolation.SetPanelOpen(open);
        };

        _gameBarHook = new GameBarButtonHook(Dispatcher);
        _gameBarHook.Pressed += () => OnGameBarButton(window, panel);

        // The OEM button is Tsume's only while MSI Center M is paused on MSI machines (otherwise both would open).
        // Checking means walking the process list, too slow per press: act on the last answer, then re-check.
        _msiCenterRuns = !panel.Settings.IsMsiCenterPaused;
        panel.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsMsiCenterPaused))
                _msiCenterRuns = !panel.Settings.IsMsiCenterPaused;
        };

        panel.Settings.Controls.SystemButtonsPage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemButtonsViewModel.IsGameBarDisabled))
                SyncHardwareButtons(window, panel);
        };

        panel.Performance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceViewModel.Device))
                SyncHardwareButtons(window, panel);
        };

        SyncHardwareButtons(window, panel);

        // Fetch the profile right away if state.json predates ControlsProfile (or was reset) so hardware buttons
        // work before first open — retrying briefly while the helper finishes early-boot WMI discovery.
        if (panel.Performance.Device.Controls is null)
            _ = EnsureControlsProfileLoadedAsync(panel, _appExit.Token);

        _instance!.ToggleRequested += () => Dispatcher.BeginInvoke(window.Toggle);
    }

    private void SyncHardwareButtons(OverlayWindow window, PanelViewModel panel)
    {
        var controls = panel.Performance.Device.Controls;
        Volatile.Write(ref _activeControls, controls);

        if (_gameBarHook is not null)
        {
            _gameBarHook.SetTriggers(controls?.GameBarTriggers ?? []);

            var xboxBound = DedicatedActionKeys.UsesCode(
                Volatile.Read(ref _dedicatedActionBindings), DedicatedActionKeys.XboxButtonCode);
            var gameBarDisabled = panel.Settings.Controls.SystemButtonsPage.IsGameBarDisabled;
            _gameBarHook.IsEnabled = controls is { HasGameBarButton: true, GameBarTriggers.Count: > 0 }
                && (xboxBound || gameBarDisabled || !controls.GameBarNativeWinG);
        }

        if (_buttons is null && controls is { OemButtonWmiCode: not null } or { GameBarWmiCode: not null })
        {
            _buttons = new ClawButtons();
            _buttons.CodePressed += code => OnWmiButtonCode(code, window, panel);
        }
    }

    private void OnWmiButtonCode(int code, OverlayWindow window, PanelViewModel panel)
    {
        var controls = Volatile.Read(ref _activeControls);
        if (controls is null) return;

        if (controls.HasOemButton && controls.OemButtonWmiCode == code)
        {
            var blockedByMsi = controls.HasMsiCenterConflict && _msiCenterRuns;
            var bindings = Volatile.Read(ref _dedicatedActionBindings);

            if (!blockedByMsi && DedicatedActionKeys.TryMatchHardwareButton(bindings, DedicatedActionKeys.OemButtonCode, out var dedicated))
            {
                if (dedicated == DedicatedAction.OpenTsume) OpenTrace.Summoned();
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () => OnDedicatedAction(dedicated, window, panel));
            }

            if (controls.HasMsiCenterConflict)
                _msiCenterRuns = MsiCenter.IsRunning;
        }
        else if (controls.HasGameBarButton && controls.GameBarWmiCode == code)
        {
            var bindings = Volatile.Read(ref _dedicatedActionBindings);
            if (DedicatedActionKeys.TryMatchHardwareButton(bindings, DedicatedActionKeys.XboxButtonCode, out var dedicated))
            {
                if (dedicated == DedicatedAction.OpenTsume) OpenTrace.Summoned();
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () => OnDedicatedAction(dedicated, window, panel));
                return;
            }

            var handleDefault = !panel.Settings.Controls.SystemButtonsPage.IsGameBarDisabled
                && !controls.GameBarNativeWinG && (!controls.HasMsiCenterConflict || !_msiCenterRuns);
            if (handleDefault)
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () => OnGameBarButton(window, panel));
        }
    }

    private void OnGameBarButton(OverlayWindow window, PanelViewModel panel)
    {
        var now = DateTime.UtcNow;
        if (now - _lastGameBarPress < ButtonEcho) return;
        _lastGameBarPress = now;

        var bindings = Volatile.Read(ref _dedicatedActionBindings);
        if (DedicatedActionKeys.TryMatchHardwareButton(bindings, DedicatedActionKeys.XboxButtonCode, out var dedicated))
        {
            OnDedicatedAction(dedicated, window, panel);
            return;
        }

        if (!panel.Settings.Controls.SystemButtonsPage.IsGameBarDisabled)
            QuickActions.OpenGameBar();
    }

    private void OnDedicatedAction(DedicatedAction action, OverlayWindow window, PanelViewModel panel)
    {
        var now = DateTime.UtcNow;
        if (_lastDedicatedActions.TryGetValue(action, out var last) && now - last < ButtonEcho) return;
        _lastDedicatedActions[action] = now;

        switch (action)
        {
            case DedicatedAction.OpenFpsOverlay:
                panel.Rtss.ToggleOverlay();
                break;

            case DedicatedAction.OpenTsume:
                OpenTrace.Summoned();
                window.Toggle();
                break;

            case DedicatedAction.CyclePerformanceMode:
                panel.Performance.CycleMode();
                break;

            case DedicatedAction.CycleRefreshRate:
                panel.Actions.Refresh();
                panel.Actions.CycleRefreshRate.Execute(null);
                break;

            case DedicatedAction.ToggleFanBoost:
                panel.Performance.CycleFanMode.Execute(null);
                break;

            case DedicatedAction.ToggleControllerMode:
                panel.PadMode.Toggle.Execute(null);
                break;

            case DedicatedAction.Screenshot:
                QuickActions.Screenshot();
                break;

            case DedicatedAction.TouchKeyboard:
                _ = ToggleTouchKeyboardSafeAsync(_appExit.Token);
                break;

            case DedicatedAction.ShowDesktop:
                QuickActions.ShowDesktop();
                break;

            case DedicatedAction.TaskManager:
                QuickActions.TaskManager();
                break;

            case DedicatedAction.ToggleMute:
                Task.Run(() =>
                {
                    if (AudioVolume.Read() is { } reading)
                    {
                        var nextMuted = !reading.Muted;
                        AudioVolume.SetMuted(nextMuted);
                        Dispatcher.BeginInvoke(() => _ = panel.Levels.RefreshAsync());
                    }
                });
                break;

            case DedicatedAction.SteamBigPicture:
                QuickActions.OpenSteamBigPicture();
                break;

            case DedicatedAction.XboxGameBar:
                QuickActions.OpenGameBar();
                break;
        }
    }

    private static async Task ToggleTouchKeyboardSafeAsync(CancellationToken cancel)
    {
        try
        {
            await QuickActions.ToggleTouchKeyboardAsync(cancellationToken: cancel);
        }
        catch (Exception error)
        {
            Errors.Write($"ToggleTouchKeyboardAsync failed: {error}");
        }
    }

    /// <summary>Trims memory once the panel has been closed for the idle interval.</summary>
    private void SlimDownWhenIdle(OverlayWindow window)
    {
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (!window.IsOpen) _ = Task.Run(MemoryDiet.Trim);
        };

        window.OpenChanged += open =>
        {
            _idle.Stop();
            if (!open) _idle.Start();
        };

        _idle.Start();
    }

    /// <summary>If Tsume has been moved or rebuilt elsewhere, its shortcut and start-up entry follow it.</summary>
    private static void KeepEntryPointsCurrent() => Task.Run(() =>
    {
        try
        {
            StartWithWindows.RepairIfMoved();
            StartMenuShortcut.RepairIfMoved();
        }
        catch (Exception)
        {
            // A stale shortcut is not worth failing start-up over.
        }
    });

    private async Task EnsureControlsProfileLoadedAsync(PanelViewModel panel, CancellationToken stop)
    {
        for (var attempt = 1; attempt <= 15 && !stop.IsCancellationRequested; attempt++)
        {
            await panel.Performance.RefreshAsync();
            if (panel.Performance.Device.Controls is not null) return;

            try { await Task.Delay(TimeSpan.FromSeconds(2), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RetryHotkeyRegistrationAsync(CancellationToken stop)
    {
        for (var attempt = 1; attempt <= 5 && !stop.IsCancellationRequested; attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), stop); }
            catch (OperationCanceledException) { return; }

            if (_hotkey is null || _hotkey.IsRegistered) return;
            _hotkey.EnsureRegistered();
            if (_hotkey.IsRegistered) return;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // Pause native WMI/XInput/WinMM listeners cleanly before S3/S4 so nothing races PnP/WMI teardown.
            _gamepad?.Suspend();
            _buttons?.Suspend();
            _gameBarHook?.ResetState();
            _dedicatedActions?.ResetState();
            _padIsolation?.RestoreSynchronously();
            Dispatcher.BeginInvoke(() => _window?.ForceCloseForSuspend());
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _gamepad?.Resume();
            _buttons?.Resume();
            Dispatcher.BeginInvoke(() =>
            {
                _hotkey?.EnsureRegistered();
                _gameBarHook?.ResetAndReinstall();
                _dedicatedActions?.ResetAndReinstall();
            });
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _gameBarHook?.ResetState();
            _dedicatedActions?.ResetState();
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _hotkey?.EnsureRegistered();
                _gameBarHook?.ResetAndReinstall();
                _dedicatedActions?.ResetAndReinstall();
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _appExit.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _rtssDrag?.Dispose();
        _padIsolation?.Dispose();
        _panel?.Dispose();
        _dedicatedActions?.Dispose();
        _gameBarHook?.Dispose();
        _buttons?.Dispose();
        _gamepad?.Dispose();
        _hotkey?.Dispose();
        _instance?.Dispose();
        _appExit.Dispose();

        base.OnExit(e);
    }
}
