using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Tsume.App.Input;
using Tsume.App.Platform;
using Tsume.App.Shell;
using Tsume.App.ViewModels;
using Tsume.Core.Ipc;

namespace Tsume.App;

/// <summary>
/// Start-up wiring. Tsume runs in the background with no window until summoned: the M button (while
/// MSI Center M is paused), View + Menu on the pad, Win + Alt + Q, or launching Tsume again.
/// </summary>
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

    private SingleInstance? _instance;
    private GlobalHotkey? _hotkey;
    private Gamepad? _gamepad;
    private ClawButtons? _buttons;
    private PanelViewModel? _panel;
    private OverlayWindow? _window;
    private DateTime _lastButtonPress;

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

        // The panel has to sit in the UIAccess window band to show over the taskbar, Start, Search, and elevated
        // windows. Only the helper, which runs as the system, can hand that to an unsigned program. A copy that starts
        // without it — the Start-menu shortcut, or the sign-in entry when the helper was not yet granting it — asks the
        // helper to start Tsume again with UIAccess and steps aside. This comes before the single-instance check so it
        // does not hold the lock while the UIAccess copy takes over.
        if (!UiAccess.IsGranted && !e.Args.Contains(RelaunchedSwitch) && StartAgainWithUiAccess(e.Args))
        {
            Shutdown();
            return;
        }

        var quietly = e.Args.Contains(BackgroundSwitch);

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Already running: a manual second launch toggles the panel.
            if (!quietly) _instance.AskFirstInstanceToToggle();
            Shutdown();
            return;
        }

        var panel = _panel = new PanelViewModel(
            SavedState.Load(),
            dismiss: () => _window!.CloseAsync(),
            quit: Shutdown);
        panel.StartBackgroundServices();

        _window = new OverlayWindow(panel, stayOpen: e.Args.Contains(StayOpenSwitch));

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

        _gamepad = new Gamepad();
        _gamepad.Summoned += () => Dispatcher.BeginInvoke(window.Toggle);
        _gamepad.Pressed += button => Dispatcher.BeginInvoke(() => window.Press(button));

        // The controller is only read closely while the panel is open.
        window.OpenChanged += open => _gamepad.IsNavigating = open;

        _buttons = new ClawButtons();
        // The M button is Tsume's only while MSI Center M is paused (otherwise both would open).
        // Checking means walking the process list, too slow per press: act on the last answer, then re-check.
        _msiCenterRuns = !panel.Settings.IsMsiCenterPaused;
        panel.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsMsiCenterPaused))
                _msiCenterRuns = !panel.Settings.IsMsiCenterPaused;
        };

        _buttons.Pressed += button =>
        {
            if (button != ClawButton.MsiCenter) return;

            if (!_msiCenterRuns)
            {
                OpenTrace.Summoned();
                Dispatcher.BeginInvoke(DispatcherPriority.Send, () => OnMButton(window));
            }

            _msiCenterRuns = MsiCenter.IsRunning;
        };

        _instance!.ToggleRequested += () => Dispatcher.BeginInvoke(window.Toggle);
    }

    private void OnMButton(OverlayWindow window)
    {
        var now = DateTime.UtcNow;
        if (now - _lastButtonPress < ButtonEcho) return;
        _lastButtonPress = now;

        window.Toggle();
    }

    /// <summary>Trims memory once the panel has been closed for the idle interval.</summary>
    private void SlimDownWhenIdle(OverlayWindow window)
    {
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (!window.IsOpen) MemoryDiet.Trim();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _panel?.Dispose();
        _buttons?.Dispose();
        _gamepad?.Dispose();
        _hotkey?.Dispose();
        _instance?.Dispose();

        base.OnExit(e);
    }
}
