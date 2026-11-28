using System.Windows.Threading;
using Tsume.App.Input;
using Tsume.App.Mvvm;
using Tsume.App.Platform.Rtss;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

/// <summary>
/// Everything the panel shows, grouped the same way the panel is laid out.
/// Nothing here runs while the panel is closed. The optional RTSS overlay is drawn by RTSS itself.
/// </summary>
public sealed class PanelViewModel : IDisposable
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RtssOverlayService _rtssService;
    private readonly PadIsolation? _padIsolation;
    private bool _hasCpuBoostTile;
    private bool _ticking;

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    /// <param name="quit">Exits Tsume.</param>
    public PanelViewModel(SavedState state, Func<Task> dismiss, Action quit, PadIsolation? padIsolation = null)
    {
        var helper = new HelperClient();
        _padIsolation = padIsolation;

        Status = new StatusViewModel();
        Performance = new PerformanceViewModel(helper, state);
        CpuBoost = new CpuBoostViewModel(helper);
        Levels = new LevelsViewModel();
        Actions = new ActionsViewModel(dismiss);
        Power = new PowerViewModel(dismiss, padIsolation is null ? null : () => padIsolation.EnsureRestoredAsync());

        _rtssService = new RtssOverlayService(helper, state);

        Settings = new SettingsViewModel(helper, state, Performance, dismiss, _rtssService.SetFeatureEnabled);
        Performance.SnapshotReceived += Settings.ChargeLimit.AbsorbSnapshot;
        Performance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceViewModel.IsHelperOnline)
                && Performance.IsHelperOnline
                && _hasCpuBoostTile
                && !CpuBoost.IsAvailable)
            {
                _ = CpuBoost.RefreshAsync();
            }
        };
        Lighting = new LightingViewModel(Performance, state);
        PadMode = new PadModeViewModel(padIsolation);
        Upscaling = new UpscalingViewModel(dismiss);
        Rtss = new RtssViewModel(_rtssService, dismiss);
        Quit = new QuitViewModel(quit);

        Actions.InitializeTiles(Performance, Lighting, PadMode, Upscaling, Rtss, Levels, CpuBoost);
        var mainTiles = Settings.Controls.MainTilesPage;
        _hasCpuBoostTile = mainTiles.ActiveIds.Contains(MainTileId.CpuBoost);
        mainTiles.LayoutChanged += OnMainTilesLayoutChanged;
        Actions.ApplyLayout(mainTiles.ActiveIds, mainTiles.Columns);

        Layers = [Power, Settings, Settings.PerformancePresets, Settings.Fan, Settings.Customization, Settings.Controls, Settings.Controls.MainTilesPage, Settings.Controls.DedicatedActionsPage, Settings.Controls.SystemButtonsPage, Settings.Developer, Lighting, Upscaling, .. Upscaling.Pages, Upscaling.RestoreAll, Rtss, Rtss.Download, Quit];
        Sheets = [Power, Upscaling.RestoreAll, Quit];

        _tick.Tick += async (_, _) =>
        {
            if (_ticking) return;
            _ticking = true;
            try
            {
                Status.Refresh();
                if (Settings.IsOpen) Settings.ChargeLimit.RefreshStatus();
                if (Rtss.IsOpen) Rtss.RefreshRunningState();
                await Performance.RefreshTelemetryAsync();
            }
            catch (Exception error)
            {
                AsyncCommand.LogError(error);
            }
            finally
            {
                _ticking = false;
            }
        };
    }

    public StatusViewModel Status { get; }
    public PerformanceViewModel Performance { get; }
    public CpuBoostViewModel CpuBoost { get; }
    public LevelsViewModel Levels { get; }
    public ActionsViewModel Actions { get; }
    public PowerViewModel Power { get; }
    public SettingsViewModel Settings { get; }
    public FanViewModel Fan => Settings.Fan;
    public LightingViewModel Lighting { get; }
    public PadModeViewModel PadMode { get; }
    public UpscalingViewModel Upscaling { get; }
    public RtssViewModel Rtss { get; }
    public RtssOverlayService RtssService => _rtssService;

    public QuitViewModel Quit { get; }

    /// <summary>Everything that can cover the panel, lowest first; a layer later in the list opens over earlier ones.</summary>
    public IReadOnlyList<LayerViewModel> Layers { get; }

    /// <summary>Transient modal sheets (Power, Restore All, Quit) that always close when the panel hides.</summary>
    public IReadOnlyList<LayerViewModel> Sheets { get; }

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

    /// <summary>Flushes any deferred saves on screens that remain open after the panel hides.</summary>
    public void FlushOpenLayers()
    {
        foreach (var layer in Layers)
            if (layer.IsOpen) layer.FlushPending();
    }

    /// <summary>Called as the panel is about to slide in: refreshes every reading and starts polling.</summary>
    public async void OnOpening(bool keepPages = false)
    {
        try
        {
            if (!keepPages) CloseLayers();
            Status.Refresh();
            Actions.Refresh();
            Upscaling.RefreshTile();
            _ = Upscaling.PrepareAsync();
            Rtss.RefreshTile();
            if (Settings.IsOpen)
            {
                Settings.Refresh();
                _ = Settings.ChargeLimit.RefreshAsync();
            }
            if (Rtss.IsOpen) Rtss.RefreshRunningState();
            _tick.Start();

            await Task.WhenAll(Levels.RefreshAsync(), RefreshHelperTilesAsync(), RefreshControllerTilesAsync());

            // Reopened on the fan page: after the snapshot, so the helper is known to be there and the requests do not overlap.
            if (Fan.IsOpen) Fan.Refresh();
        }
        catch (Exception error)
        {
            AsyncCommand.LogError(error);
        }
    }

    private void OnMainTilesLayoutChanged(IReadOnlyList<MainTileId> activeIds, int columns)
    {
        Actions.ApplyLayout(activeIds, columns);

        var hasCpuBoost = activeIds.Contains(MainTileId.CpuBoost);
        var added = hasCpuBoost && !_hasCpuBoostTile;
        _hasCpuBoostTile = hasCpuBoost;
        if (added) _ = CpuBoost.RefreshAsync();
    }

    /// <summary>
    /// Refreshes the performance snapshot first, then the opt-in CPU Boost tile sequentially so they
    /// never contend on the helper's single-instance pipe.
    /// </summary>
    private async Task RefreshHelperTilesAsync()
    {
        await Performance.RefreshAsync();
        if (_hasCpuBoostTile)
            await CpuBoost.RefreshAsync();
    }

    /// <summary>
    /// Waits for any in-flight open-time pad isolation switch before reading lighting/mode over vendor HID,
    /// so tile refreshes never delay switching the Claw out of Gamepad mode.
    /// </summary>
    private async Task RefreshControllerTilesAsync()
    {
        if (_padIsolation is not null)
            await _padIsolation.WaitForOpenCaptureAsync();

        var tasks = new List<Task> { Lighting.RefreshAsync(), PadMode.RefreshAsync() };
        if (Settings.Controls.SystemButtonsPage.IsOpen)
            tasks.Add(Settings.Controls.SystemButtonsPage.RefreshAsync());
        await Task.WhenAll(tasks);
    }

    /// <summary>Called once the panel has gone: stops all polling and commits any pending tile toggle.</summary>
    public void OnClosed()
    {
        _tick.Stop();
        CpuBoost.FlushPending();
    }

    public void Dispose()
    {
        _tick.Stop();
        Status.Dispose();
        Lighting.Dispose();
        CpuBoost.Dispose();
        _rtssService.Dispose();
        Settings.Controls.Dispose();
    }
}
