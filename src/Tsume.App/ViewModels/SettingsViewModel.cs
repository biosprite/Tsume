using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core;
using Tsume.Core.Devices;

namespace Tsume.App.ViewModels;

/// <summary>
/// The settings screen: startup, charge limit, performance presets, colours, controls &amp; layout,
/// pausing MSI Center M, and troubleshooting (sharing logs and opening the data folder).
/// Performance presets, colours, and controls &amp; layout each open a page of their own.
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
        _performance = performance;
        ChargeLimit = new ChargeLimitViewModel(helper, state);
        PerformancePresets = new PerformancePresetsViewModel(performance, state);
        Fan = new FanViewModel(performance);
        Customization = new CustomizationViewModel(state);
        Controls = new ControlsViewModel(performance, state, onRtssVisibilityChanged);
        Developer = new DeveloperViewModel(helper, state, dismiss);

        ToggleStartWithWindows = new Command(() => StartsWithWindows = !StartsWithWindows);
        ToggleStartMenu = new Command(() => IsInStartMenu = !IsInStartMenu);

        ToggleMsiCenter = new AsyncCommand(async () =>
        {
            // Windows shows a permission prompt, which takes focus, so the panel leaves first.
            var pause = !IsMsiCenterPaused;
            var acer = IsAcerDevice;
            await dismiss();

            if (acer)
            {
                if (pause) await AcerSense.PauseAsync();
                else await AcerSense.ResumeAsync();
            }
            else
            {
                if (pause) await MsiCenter.PauseAsync();
                else await MsiCenter.ResumeAsync();
            }

            Refresh();
        });

        _performance.PropertyChanged += OnPerformancePropertyChanged;

        _shareLogs = CreateShareLogsCommand(_helper, dismiss, detail => ShareLogsDetail = detail);

        OpenDataFolder = new AsyncCommand(async () =>
        {
            LogBundle.OpenFolder(DataFolder.Path);
            await dismiss();
        });

        Refresh();
    }

    private void OnPerformancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PerformanceViewModel.Device)) return;

        Notify(nameof(OemPauseLabel));
        Notify(nameof(MsiCenterDetail));
        IsMsiCenterPaused = IsAcerDevice ? !AcerSense.IsRunning : !MsiCenter.IsRunning;
    }

    internal static AsyncCommand CreateShareLogsCommand(
        Tsume.Core.Ipc.HelperClient helper,
        Func<Task> dismiss,
        Action<string> setDetail) => new(async () =>
    {
        setDetail(Strings.ShareLogsBusy);

        try
        {
            var zip = await LogBundle.ExportAsync(helper);
            LogBundle.CopyFileToClipboard(zip);

            // Started while the panel is still in front: Windows lets only the foreground program
            // bring a new window forward, so after dismissing Explorer would open behind the game.
            LogBundle.RevealInExplorer(zip);
            await dismiss();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (error is not (IOException or UnauthorizedAccessException))
                AsyncCommand.LogError(error);
            setDetail(Strings.ShareLogsFailed(error.Message));
        }
    });

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

    public FanViewModel Fan { get; }

    public CustomizationViewModel Customization { get; }

    public ControlsViewModel Controls { get; }

    public DeveloperViewModel Developer { get; }

    private readonly Tsume.Core.Ipc.HelperClient _helper;
    private readonly PerformanceViewModel _performance;
    private readonly AsyncCommand _shareLogs;

    public ICommand ShareLogs => _shareLogs;
    public ICommand OpenDataFolder { get; }

    public string ShareLogsDetail { get; private set => Set(ref field, value); } = Strings.ShareLogsDetail;

    public ICommand ToggleMsiCenter { get; }

    private bool IsAcerDevice =>
        _performance.Device.Controls?.EffectiveOemSoftware == OemSoftwareKind.AcerPredatorSense;

    /// <summary>Label for the OEM software pause switch: "Pause MSI Center M" on MSI, "Pause PredatorSense" on Acer.</summary>
    public string OemPauseLabel => IsAcerDevice ? Strings.PauseAcerSense : Strings.PauseMsiCenter;

    /// <summary>True when the device's OEM control suite (MSI Center M or PredatorSense) is not running.</summary>
    public bool IsMsiCenterPaused
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(MsiCenterDetail));
        }
    }

    public string MsiCenterDetail => IsAcerDevice
        ? (IsMsiCenterPaused ? Strings.AcerSensePaused : AcerSense.CanControl ? Strings.AcerSenseRunning : Strings.AcerSenseNoHelper)
        : (IsMsiCenterPaused ? Strings.MsiCenterPaused : MsiCenter.CanControl ? Strings.MsiCenterRunning : Strings.MsiCenterNoHelper);

    private bool _absorbing;

    protected override void OnOpened()
    {
        if (!_shareLogs.IsRunning) ShareLogsDetail = Strings.ShareLogsDetail;
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

        IsMsiCenterPaused = IsAcerDevice ? !AcerSense.IsRunning : !MsiCenter.IsRunning;
    }
}
