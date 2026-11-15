using System.IO;
using System.Windows;
using System.Windows.Threading;
using Tsume.App.Input;
using Tsume.App.Platform;
using Tsume.App.Shell;
using Tsume.App.ViewModels;

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
    private const string ControllerReadSwitch = "--controller-read";
    private const string LightingCheckSwitch = "--lighting-check";
    private const string PadModeCheckSwitch = "--pad-mode-check";
    private const string OptiScalerCheckSwitch = "--optiscaler-check";
    private const string OpenOptiScalerSwitch = "--optiscaler";
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
    private OverlayWindow? _window;
    private DateTime _lastButtonPress;

    /// <summary>Whether MSI Center M was running when last looked at. Read on the button listener's thread.</summary>
    private volatile bool _msiCenterRuns;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled fault must not kill the panel: log it to %LOCALAPPDATA%\Tsume\errors.log and carry on.
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

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Already running: a manual second launch toggles the panel.
            if (!quietly) _instance.AskFirstInstanceToToggle();
            Shutdown();
            return;
        }

        var panel = new PanelViewModel(
            SavedState.Load(),
            dismiss: () => _window!.CloseAsync(),
            quit: Shutdown);

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
        _buttons?.Dispose();
        _gamepad?.Dispose();
        _hotkey?.Dispose();
        _instance?.Dispose();

        base.OnExit(e);
    }
}
