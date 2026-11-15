using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;

namespace Tsume.App.ViewModels;

/// <summary>
/// The settings screen: startup, charge limit, performance presets, colours, the OptiScaler tile,
/// and pausing MSI Center M. Performance presets and colours each open a page of their own.
/// </summary>
public sealed class SettingsViewModel : LayerViewModel
{
    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public SettingsViewModel(
        Tsume.Core.Ipc.HelperClient helper, SavedState state, PerformanceViewModel performance, Func<Task> dismiss)
    {
        _state = state;
        ChargeLimit = new ChargeLimitViewModel(helper, state);
        PerformancePresets = new PerformancePresetsViewModel(performance, state);
        Customization = new CustomizationViewModel(state);

        ToggleOptiScaler = new Command(() =>
        {
            _state.ShowOptiScaler = !_state.ShowOptiScaler;
            _state.Save();

            Notify(nameof(ShowsOptiScaler));
            Notify(nameof(OptiScalerTile));
        });

        ToggleStartWithWindows = new Command(() => StartsWithWindows = !StartsWithWindows);
        ToggleStartMenu = new Command(() => IsInStartMenu = !IsInStartMenu);

        ToggleMsiCenter = new Command(async () =>
        {
            // Windows shows a permission prompt, which takes focus, so the panel leaves first.
            var pause = !IsMsiCenterPaused;
            await dismiss();

            if (pause) await MsiCenter.PauseAsync();
            else await MsiCenter.ResumeAsync();

            Refresh();
        });

        Refresh();
    }

    public ICommand ToggleStartWithWindows { get; }
    public ICommand ToggleStartMenu { get; }

    public bool StartsWithWindows
    {
        get;
        set
        {
            if (Set(ref field, value) && !_absorbing) StartWithWindows.IsEnabled = value;
        }
    }

    public bool IsInStartMenu
    {
        get;
        set
        {
            if (Set(ref field, value) && !_absorbing) StartMenuShortcut.Exists = value;
        }
    }

    /// <summary>An installed Tsume already has a Start-menu entry, so the switch for one is not shown.</summary>
    public System.Windows.Visibility StartMenuSwitch => StartMenuShortcut.IsProvidedByInstaller
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    public ChargeLimitViewModel ChargeLimit { get; }

    public PerformancePresetsViewModel PerformancePresets { get; }

    public CustomizationViewModel Customization { get; }

    private readonly SavedState _state;

    public ICommand ToggleOptiScaler { get; }

    /// <summary>Whether the OptiScaler tile is on the main screen.</summary>
    public bool ShowsOptiScaler => _state.ShowOptiScaler;

    public System.Windows.Visibility OptiScalerTile => ShowsOptiScaler
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public ICommand ToggleMsiCenter { get; }

    /// <summary>True when MSI Center M is not running. While paused, the M button belongs to Tsume.</summary>
    public bool IsMsiCenterPaused
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(MsiCenterDetail));
        }
    }

    public string MsiCenterDetail =>
        IsMsiCenterPaused     ? Strings.MsiCenterPaused
        : MsiCenter.CanControl ? Strings.MsiCenterRunning
        : Strings.MsiCenterNoHelper;

    private bool _absorbing;

    protected override void OnOpened()
    {
        Refresh();
        _ = ChargeLimit.RefreshAsync();
    }

    /// <summary>Reads the real state of everything on this screen.</summary>
    public void Refresh()
    {
        _absorbing = true;
        StartsWithWindows = StartWithWindows.IsEnabled;
        IsInStartMenu = StartMenuShortcut.Exists;
        _absorbing = false;

        IsMsiCenterPaused = !MsiCenter.IsRunning;
    }
}
