using System.Windows.Threading;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

/// <summary>
/// Everything the panel shows, grouped the same way the panel is laid out.
/// Nothing here runs while the panel is closed.
/// </summary>
public sealed class PanelViewModel
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    /// <param name="quit">Exits Tsume.</param>
    public PanelViewModel(SavedState state, Func<Task> dismiss, Action quit)
    {
        var helper = new HelperClient();

        Status = new StatusViewModel();
        Performance = new PerformanceViewModel(helper, state);
        Levels = new LevelsViewModel();
        Actions = new ActionsViewModel(dismiss);
        Power = new PowerViewModel(dismiss);
        Settings = new SettingsViewModel(helper, state, Performance, dismiss);
        Lighting = new LightingViewModel();
        PadMode = new PadModeViewModel();
        Upscaling = new UpscalingViewModel(dismiss);
        Quit = new QuitViewModel(quit);

        Layers = [Power, Settings, Settings.PerformancePresets, Settings.Customization, Lighting, Upscaling, .. Upscaling.Pages, Upscaling.RestoreAll, Quit];

        _tick.Tick += async (_, _) =>
        {
            Status.Refresh();
            if (Settings.IsOpen) Settings.ChargeLimit.RefreshStatus();
            await Performance.RefreshTelemetryAsync();
        };
    }

    public StatusViewModel Status { get; }
    public PerformanceViewModel Performance { get; }
    public LevelsViewModel Levels { get; }
    public ActionsViewModel Actions { get; }
    public PowerViewModel Power { get; }
    public SettingsViewModel Settings { get; }
    public LightingViewModel Lighting { get; }
    public PadModeViewModel PadMode { get; }
    public UpscalingViewModel Upscaling { get; }

    public QuitViewModel Quit { get; }

    /// <summary>Everything that can cover the panel, lowest first; a layer later in the list opens over earlier ones.</summary>
    public IReadOnlyList<LayerViewModel> Layers { get; }

    public bool IsCovered => Layers.Any(layer => layer.IsOpen);

    /// <summary>Closes whichever layer is on top.</summary>
    public void CloseTopLayer()
    {
        if (Layers.LastOrDefault(layer => layer.IsOpen) is { } top) top.IsOpen = false;
    }

    public void CloseLayers()
    {
        foreach (var layer in Layers.Reverse()) layer.IsOpen = false;
    }

    /// <summary>Called as the panel is about to slide in: refreshes every reading and starts polling.</summary>
    public async void OnOpening()
    {
        CloseLayers();
        Status.Refresh();
        Actions.Refresh();
        Upscaling.RefreshTile();
        _tick.Start();

        await Task.WhenAll(Levels.RefreshAsync(), Performance.RefreshAsync(), Lighting.RefreshAsync(), PadMode.RefreshAsync());
    }

    /// <summary>Called once the panel has gone: stops all polling.</summary>
    public void OnClosed() => _tick.Stop();
}
