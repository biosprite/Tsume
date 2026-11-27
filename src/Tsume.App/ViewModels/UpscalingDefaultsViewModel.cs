using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;

namespace Tsume.App.ViewModels;

/// <summary>
/// The defaults page: the settings a game uses until it has its own, plus the option to auto-install OptiScaler
/// into every supported game. Changing tuning defaults does not affect games already installed.
/// </summary>
public sealed class UpscalingDefaultsViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;

    public UpscalingDefaultsViewModel(UpscalingViewModel hub)
    {
        _hub = hub;
        _hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpscalingViewModel.IsBusy)) Notify(nameof(CanAct));
        };

        Tuning.Changed += () =>
        {
            if (_hub.IsBusy) return;

            _hub.List.Defaults = Tuning.Settings.PreservingUnavailable(
                _hub.List.Defaults,
                Tuning.HasXmx,
                hasOptiPatcher: true) with { OptiPatcher = false };
            _hub.SaveList();
        };

        ToggleAutoInstall = new Command(() => _ = ToggleAutoInstallAsync());
    }

    public UpscalerTuningViewModel Tuning { get; } = new();

    public ICommand ToggleAutoInstall { get; }

    public bool AutoInstall => _hub.List.AutoInstall;

    /// <summary>False while files are being downloaded, installed, or removed.</summary>
    public bool CanAct => !_hub.IsBusy;

    public string Status
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(StatusShown));
        }
    } = "";

    public Visibility StatusShown => Status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnOpened()
    {
        if (!_hub.IsBusy) Status = "";
        Tuning.Load(
            _hub.List.Defaults,
            hasXmx: Tsume.Upscaling.GpuVendor.HasIntelXmxGpu() && _hub.Xmx.Current() is not null,
            hasOptiPatcher: false,
            showOptiPatcher: false);
        Notify(nameof(AutoInstall));
        Notify(nameof(CanAct));
    }

    protected override void OnClosed() => Tuning.FlushPendingCap();

    public override void FlushPending() => Tuning.FlushPendingCap();

    private async Task ToggleAutoInstallAsync()
    {
        if (_hub.IsBusy) return;

        Tuning.FlushPendingCap();

        var enable = !_hub.List.AutoInstall;
        _hub.List.AutoInstall = enable;
        _hub.SaveList();
        Notify(nameof(AutoInstall));

        if (!enable)
        {
            Status = "";
            return;
        }

        try
        {
            var (installed, failures) = await _hub.AutoInstallAllAsync(text => Status = text);
            Status = (installed, failures.Count) switch
            {
                (0, 0) => Strings.AutoInstallNoneNeeded,
                (0, _) => string.Join(Environment.NewLine, failures),
                _      => string.Join(Environment.NewLine, failures.Prepend(Strings.AutoInstalledGames(installed))),
            };
        }
        catch (Exception error)
        {
            _hub.List.AutoInstall = false;
            _hub.SaveList();
            Notify(nameof(AutoInstall));
            Status = Strings.Explain(error);
        }
    }
}
