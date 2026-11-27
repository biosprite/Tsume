using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.App.Theme;

namespace Tsume.App.ViewModels;

/// <summary>The customizable grid of quick-action tiles on the main screen.</summary>
public sealed class ActionsViewModel : Observable
{
    private readonly Dictionary<MainTileId, MainTileViewModel> _catalog = [];

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public ActionsViewModel(Func<Task> dismiss)
    {
        CycleRefreshRate = new Command(NextRefreshRate);

        // These act on whatever is behind the panel, so the panel leaves first.
        Screenshot      = new AsyncCommand(async () => { await dismiss(); QuickActions.Screenshot(); });
        Keyboard        = new AsyncCommand(async () => { await dismiss(); QuickActions.ToggleTouchKeyboard(); });
        TaskManager     = new AsyncCommand(async () => { await dismiss(); QuickActions.TaskManager(); });
        Desktop         = new AsyncCommand(async () => { await dismiss(); QuickActions.ShowDesktop(); });
        SteamBigPicture = new AsyncCommand(async () => { await dismiss(); QuickActions.OpenSteamBigPicture(); });
        XboxGameBar     = new AsyncCommand(async () => { await dismiss(); QuickActions.OpenGameBar(); });
    }

    public ICommand CycleRefreshRate { get; }
    public ICommand Screenshot { get; }
    public ICommand Keyboard { get; }
    public ICommand TaskManager { get; }
    public ICommand Desktop { get; }
    public ICommand SteamBigPicture { get; }
    public ICommand XboxGameBar { get; }

    /// <summary>The tiles currently displayed on the main screen, in the user's chosen order.</summary>
    public ObservableCollection<MainTileViewModel> VisibleTiles { get; } = [];

    /// <summary>Columns in the main-screen tile grid (2, 3, or 4).</summary>
    public int Columns
    {
        get;
        private set => Set(ref field, Math.Clamp(value, 2, 4));
    } = 4;

    public Visibility GridVisibility => VisibleTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public int RefreshRate
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(RefreshRateDetail));
            Notify(nameof(IsHighRefreshRate));
        }
    }

    public string RefreshRateDetail => RefreshRate > 0 ? Strings.RefreshRateValue(RefreshRate) : "";

    public bool IsHighRefreshRate => RefreshRate > 60;

    public void Refresh() => RefreshRate = DisplayMode.CurrentRefreshRate();

    /// <summary>Builds the catalog of all main-screen tiles and binds their live states.</summary>
    public void InitializeTiles(
        PerformanceViewModel performance,
        LightingViewModel lighting,
        PadModeViewModel padMode,
        UpscalingViewModel upscaling,
        RtssViewModel rtss,
        LevelsViewModel levels,
        CpuBoostViewModel cpuBoost)
    {
        if (_catalog.Count > 0) return;

        Add(MainTileId.RefreshRate, Glyphs.RefreshRate, Strings.RefreshRate, CycleRefreshRate)
            .Watch(this, detail: () => RefreshRateDetail, isOn: () => IsHighRefreshRate,
                propertyNames: [nameof(RefreshRateDetail), nameof(IsHighRefreshRate)]);

        Add(MainTileId.Fans, Glyphs.Fan, Strings.Fans, performance.CycleFanMode)
            .Watch(performance, detail: () => performance.FanDetail, isOn: () => performance.IsFanActive,
                propertyNames: [nameof(PerformanceViewModel.FanDetail), nameof(PerformanceViewModel.IsFanActive)]);

        Add(MainTileId.Lighting, Glyphs.Lighting, Strings.Lighting, lighting.Open)
            .Watch(lighting, detail: () => lighting.TileDetail, isOn: () => lighting.IsLit,
                propertyNames: [nameof(LightingViewModel.TileDetail), nameof(LightingViewModel.IsLit)]);

        Add(MainTileId.Controller, padMode.Glyph, Strings.Controller, padMode.Toggle)
            .Watch(padMode, glyph: () => padMode.Glyph, detail: () => padMode.Detail, isOn: () => padMode.IsCursor,
                propertyNames: [nameof(PadModeViewModel.Glyph), nameof(PadModeViewModel.Detail), nameof(PadModeViewModel.IsCursor)]);

        Add(MainTileId.Keyboard, Glyphs.Keyboard, Strings.Keyboard, Keyboard);
        Add(MainTileId.Screenshot, Glyphs.Screenshot, Strings.Screenshot, Screenshot);
        Add(MainTileId.TaskManager, Glyphs.TaskManager, Strings.TaskManager, TaskManager);
        Add(MainTileId.Desktop, Glyphs.Desktop, Strings.Desktop, Desktop);

        Add(MainTileId.OptiScaler, Glyphs.Upscaling, Strings.OptiScaler, upscaling.Open)
            .Watch(upscaling, detail: () => upscaling.TileDetail, isOn: () => upscaling.IsInUse,
                propertyNames: [nameof(UpscalingViewModel.TileDetail), nameof(UpscalingViewModel.IsInUse)]);

        Add(MainTileId.Rtss, Glyphs.Overlay, Strings.Rtss, rtss.Open)
            .Watch(rtss, detail: () => rtss.TileDetail, isOn: () => rtss.IsActive,
                propertyNames: [nameof(RtssViewModel.TileDetail), nameof(RtssViewModel.IsActive)]);

        Add(MainTileId.Mute, levels.VolumeGlyph, Strings.TileMute, levels.ToggleMute)
            .Watch(levels,
                glyph: () => levels.VolumeGlyph,
                detail: () => levels.IsMuted ? Strings.TileMuted : Strings.TileSoundOn,
                isOn: () => levels.IsMuted,
                propertyNames: [nameof(LevelsViewModel.VolumeGlyph), nameof(LevelsViewModel.IsMuted)]);

        Add(MainTileId.SteamBigPicture, Glyphs.Game, Strings.TileSteam, SteamBigPicture);
        Add(MainTileId.XboxGameBar, Glyphs.Buttons, Strings.TileGameBar, XboxGameBar);

        Add(MainTileId.CpuBoost, Glyphs.Chip, Strings.TileCpuBoost, cpuBoost.Toggle)
            .Watch(cpuBoost,
                detail: () => cpuBoost.Detail,
                isOn: () => cpuBoost.IsOn,
                propertyNames: [nameof(CpuBoostViewModel.Detail), nameof(CpuBoostViewModel.IsOn)]);
    }

    /// <summary>Updates the main screen's visible tiles and column count.</summary>
    public void ApplyLayout(IReadOnlyList<MainTileId> activeIds, int columns)
    {
        Columns = columns;

        var desired = activeIds
            .Where(_catalog.ContainsKey)
            .Select(id => _catalog[id])
            .ToList();

        var unchanged = VisibleTiles.Count == desired.Count;
        if (unchanged)
        {
            for (var i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(VisibleTiles[i], desired[i]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (!unchanged)
        {
            VisibleTiles.Clear();
            foreach (var tile in desired) VisibleTiles.Add(tile);
            Notify(nameof(GridVisibility));
        }
    }

    private MainTileViewModel Add(MainTileId id, string glyph, string label, ICommand command)
    {
        var tile = new MainTileViewModel(id, glyph, label, command);
        _catalog[id] = tile;
        return tile;
    }

    /// <summary>Steps to the next rate the display offers, wrapping back to the lowest.</summary>
    private void NextRefreshRate()
    {
        var rates = DisplayMode.AvailableRefreshRates();
        if (rates.Count < 2) return;

        var next = rates.FirstOrDefault(rate => rate > RefreshRate, rates[0]);
        if (DisplayMode.SetRefreshRate(next)) RefreshRate = next;
    }
}

/// <summary>One live quick-action tile rendered on the main screen.</summary>
public sealed class MainTileViewModel : Observable
{
    public MainTileViewModel(MainTileId id, string glyph, string label, ICommand command)
    {
        Id = id;
        Glyph = glyph;
        Label = label;
        Command = command;
    }

    public MainTileId Id { get; }
    public string Label { get; }
    public ICommand Command { get; }

    public string Glyph { get; private set => Set(ref field, value); } = "";
    public string Detail { get; private set => Set(ref field, value); } = "";
    public bool IsOn { get; private set => Set(ref field, value); }

    /// <summary>Keeps this tile's live Glyph, Detail, and IsOn synchronized with its owning ViewModel.</summary>
    public MainTileViewModel Watch(
        INotifyPropertyChanged source,
        Func<string?>? glyph = null,
        Func<string?>? detail = null,
        Func<bool>? isOn = null,
        string[]? propertyNames = null)
    {
        void Sync()
        {
            if (glyph is not null) Glyph = glyph() ?? "";
            if (detail is not null) Detail = detail() ?? "";
            if (isOn is not null) IsOn = isOn();
        }

        Sync();

        var watched = propertyNames is { Length: > 0 } ? new HashSet<string>(propertyNames, StringComparer.Ordinal) : null;
        source.PropertyChanged += (_, e) =>
        {
            if (watched is null || string.IsNullOrEmpty(e.PropertyName) || watched.Contains(e.PropertyName))
                Sync();
        };

        return this;
    }
}
