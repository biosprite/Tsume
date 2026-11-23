using System.Windows.Threading;
using Tsume.App.Input;
using Tsume.App.Platform.Rtss;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

/// <summary>
/// Everything the panel shows, grouped the same way the panel is laid out.
/// Nothing here runs while the panel is closed, except the optional Steam-style RTSS overlay when turned on.
/// </summary>
public sealed class PanelViewModel : IDisposable
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RtssOverlayService _rtssService;

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    /// <param name="quit">Exits Tsume.</param>
    public PanelViewModel(SavedState state, Func<Task> dismiss, Action quit, PadIsolation? padIsolation = null)
    {
        var helper = new HelperClient();

        Status = new StatusViewModel();
        Performance = new PerformanceViewModel(helper, state);
        Levels = new LevelsViewModel();
        Actions = new ActionsViewModel(dismiss);
        Power = new PowerViewModel(dismiss);

        _rtssService = new RtssOverlayService(helper, state, () =>
        {
            var limits = Performance.ActiveLimits;
            return (PerformanceViewModel.Label(Performance.Mode), limits?.Sustained, limits?.Boost);
        });

        Settings = new SettingsViewModel(helper, state, Performance, dismiss, _rtssService.SetFeatureEnabled);
        Lighting = new LightingViewModel();
        PadMode = new PadModeViewModel(padIsolation);
        Upscaling = new UpscalingViewModel(dismiss);
        Rtss = new RtssViewModel(_rtssService, dismiss);
        Quit = new QuitViewModel(quit);

        Layers = [Power, Settings, Settings.PerformancePresets, Settings.Customization, Settings.Controls, Settings.Developer, Lighting, Upscaling, .. Upscaling.Pages, Upscaling.RestoreAll, Rtss, Rtss.Download, Quit];

        _tick.Tick += async (_, _) =>
        {
            Status.Refresh();
            if (Settings.IsOpen) Settings.ChargeLimit.RefreshStatus();
            if (Rtss.IsOpen) Rtss.RefreshRunningState();
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
    public RtssViewModel Rtss { get; }

    public QuitViewModel Quit { get; }

    /// <summary>Everything that can cover the panel, lowest first; a layer later in the list opens over earlier ones.</summary>
    public IReadOnlyList<LayerViewModel> Layers { get; }

    public bool IsCovered => Layers.Any(layer => layer.IsOpen);

    /// <summary>Starts background features that keep running while the panel is closed (e.g. Tsume's RTSS overlay).</summary>
    public void StartBackgroundServices() => _rtssService.StartIfEnabled();

    /// <summary>Steps back inside the top layer if it handles Back internally, otherwise closes it.</summary>
    public void CloseTopLayer()
    {
        if (Layers.LastOrDefault(layer => layer.IsOpen) is not { } top) return;
        if (!top.GoBack()) top.IsOpen = false;
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
        _ = Upscaling.PrepareAsync();
        Rtss.RefreshTile();
        _tick.Start();

        await Task.WhenAll(Levels.RefreshAsync(), Performance.RefreshAsync(), Lighting.RefreshAsync(), PadMode.RefreshAsync());
    }

    /// <summary>Called once the panel has gone: stops all polling.</summary>
    public void OnClosed() => _tick.Stop();

    public void Dispose()
    {
        _tick.Stop();
        _rtssService.Dispose();
        Settings.Controls.Dispose();
    }
}
