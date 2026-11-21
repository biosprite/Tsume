using System.IO;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core;

namespace Tsume.App.ViewModels;

/// <summary>
/// The settings screen: startup, charge limit, performance presets, colours, the OptiScaler tile,
/// pausing MSI Center M, and troubleshooting (sharing logs and opening the data folder).
/// Performance presets and colours each open a page of their own.
/// </summary>
public sealed class SettingsViewModel : LayerViewModel
{
    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    /// <param name="onRtssVisibilityChanged">Called when the RTSS tile switch changes.</param>
    public SettingsViewModel(
        Tsume.Core.Ipc.HelperClient helper,
        SavedState state,
        PerformanceViewModel performance,
        Func<Task> dismiss,
        Action<bool>? onRtssVisibilityChanged = null)
    {
        _helper = helper;
        _state = state;
        ChargeLimit = new ChargeLimitViewModel(helper, state);
        PerformancePresets = new PerformancePresetsViewModel(performance, state);
        Customization = new CustomizationViewModel(state);
        Controls = new ControlsViewModel(state);

        ToggleOptiScaler = new Command(() =>
        {
            _state.ShowOptiScaler = !_state.ShowOptiScaler;
            _state.Save();

            Notify(nameof(ShowsOptiScaler));
            Notify(nameof(OptiScalerTile));
        });

        ToggleRtss = new Command(() =>
        {
            _state.ShowRtss = !_state.ShowRtss;
            _state.Save();

            Notify(nameof(ShowsRtss));
            Notify(nameof(RtssTile));
            onRtssVisibilityChanged?.Invoke(_state.ShowRtss);
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

        ShareLogs = new Command(async () =>
        {
            if (_sharing) return;
            _sharing = true;
            ShareLogsDetail = Strings.ShareLogsBusy;

            try
            {
                var zip = await LogBundle.ExportAsync(_helper);
                LogBundle.CopyFileToClipboard(zip);

                // Started while the panel is still in front: Windows lets only the foreground program
                // bring a new window forward, so after dismissing Explorer would open behind the game.
                LogBundle.RevealInExplorer(zip);
                await dismiss();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShareLogsDetail = Strings.ShareLogsFailed(error.Message);
            }
            finally
            {
                _sharing = false;
            }
        });

        OpenDataFolder = new Command(async () =>
        {
            LogBundle.OpenFolder(DataFolder.Path);
            await dismiss();
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

    public ControlsViewModel Controls { get; }

    private readonly Tsume.Core.Ipc.HelperClient _helper;
    private readonly SavedState _state;
    private bool _sharing;

    public ICommand ToggleOptiScaler { get; }
    public ICommand ToggleRtss { get; }

    public ICommand ShareLogs { get; }
    public ICommand OpenDataFolder { get; }

    public string ShareLogsDetail { get; private set => Set(ref field, value); } = Strings.ShareLogsDetail;

    /// <summary>Whether the OptiScaler tile is on the main screen.</summary>
    public bool ShowsOptiScaler => _state.ShowOptiScaler;

    public System.Windows.Visibility OptiScalerTile => ShowsOptiScaler
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    /// <summary>Whether the RTSS overlay tile is on the main screen.</summary>
    public bool ShowsRtss => _state.ShowRtss;

    public System.Windows.Visibility RtssTile => ShowsRtss
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
        if (!_sharing) ShareLogsDetail = Strings.ShareLogsDetail;
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
