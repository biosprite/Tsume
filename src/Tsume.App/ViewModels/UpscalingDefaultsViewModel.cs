namespace Tsume.App.ViewModels;

/// <summary>
/// The defaults page: the settings a game uses until it has its own.
/// Changing them does not affect games already installed.
/// </summary>
public sealed class UpscalingDefaultsViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;

    public UpscalingDefaultsViewModel(UpscalingViewModel hub)
    {
        _hub = hub;

        Tuning.Changed += () =>
        {
            _hub.List.Defaults = Tuning.Settings;
            _hub.SaveList();
        };
    }

    public UpscalerTuningViewModel Tuning { get; } = new();

    protected override void OnOpened() => Tuning.Load(
        _hub.List.Defaults,
        hasXmx: _hub.Xmx.Current() is not null,
        hasOptiPatcher: _hub.OptiPatcher.Current() is not null);
}
