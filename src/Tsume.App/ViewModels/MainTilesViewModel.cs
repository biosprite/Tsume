using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Theme;

namespace Tsume.App.ViewModels;

/// <summary>
/// Interactive visual editor for the main screen's quick-action tiles: live preview grid,
/// drag-and-drop or button reordering, column density, and an available-tiles tray.
/// Opened from Settings → Controls &amp; Layout.
/// </summary>
public sealed class MainTilesViewModel : LayerViewModel
{
    public const int MinColumns = 2;
    public const int MaxColumns = 4;
    public const int DefaultColumns = 4;

    private static readonly MainTileId[] DefaultActiveOrder =
    [
        MainTileId.RefreshRate,
        MainTileId.Fans,
        MainTileId.Lighting,
        MainTileId.Controller,
        MainTileId.Keyboard,
        MainTileId.Screenshot,
        MainTileId.TaskManager,
        MainTileId.Desktop,
        MainTileId.OptiScaler,
        MainTileId.Rtss,
    ];

    private static readonly MainTileId[] CatalogOrder =
    [
        MainTileId.RefreshRate,
        MainTileId.Fans,
        MainTileId.Lighting,
        MainTileId.Controller,
        MainTileId.Keyboard,
        MainTileId.Screenshot,
        MainTileId.TaskManager,
        MainTileId.Desktop,
        MainTileId.OptiScaler,
        MainTileId.Rtss,
        MainTileId.Mute,
        MainTileId.SteamBigPicture,
        MainTileId.XboxGameBar,
    ];

    private readonly SavedState _state;
    private readonly Dictionary<MainTileId, MainTileSlotViewModel> _slots = [];
    private bool _absorbing;

    public MainTilesViewModel(SavedState state)
    {
        _state = state;

        ColumnChoices =
        [
            Strings.MainTilesColumnsChoice(2),
            Strings.MainTilesColumnsChoice(3),
            Strings.MainTilesColumnsChoice(4),
        ];

        MoveSelectedLeft = new Command(() => MoveSelectedBy(-1));
        MoveSelectedRight = new Command(() => MoveSelectedBy(+1));
        RemoveSelected = new Command(HideSelectedTile);
        ResetToDefaults = new Command(Reset);

        BuildCatalog();

        _absorbing = true;
        ColumnsIndex = Math.Clamp(state.MainTileColumns, MinColumns, MaxColumns) - MinColumns;
        LoadInitialTiles();
        _absorbing = false;

        SyncMetadata();
    }

    public ObservableCollection<MainTileSlotViewModel> ActiveTiles { get; } = [];
    public ObservableCollection<MainTileSlotViewModel> AvailableTiles { get; } = [];

    public IReadOnlyList<string> ColumnChoices { get; }

    public int ColumnsIndex
    {
        get;
        set
        {
            if (!Set(ref field, Math.Clamp(value, 0, ColumnChoices.Count - 1))) return;
            Notify(nameof(Columns));
            if (_absorbing) return;

            SyncMetadata();
            PersistAndNotify();
        }
    }

    public int Columns => ColumnsIndex + MinColumns;

    public MainTileSlotViewModel? SelectedTile
    {
        get;
        private set
        {
            if (ReferenceEquals(field, value)) return;
            if (field is not null) field.IsSelected = false;
            field = value;
            if (field is not null) field.IsSelected = true;

            Notify();
            NotifySelectionMeta();
        }
    }

    public bool HasSelectedTile => SelectedTile is not null;

    public string SelectedPositionText
    {
        get
        {
            if (SelectedTile is null) return "";
            var index = ActiveTiles.IndexOf(SelectedTile);
            return index >= 0 ? Strings.MainTilesSelectedPosition(index + 1, ActiveTiles.Count) : "";
        }
    }

    public bool CanMoveLeft => SelectedTile is not null && ActiveTiles.IndexOf(SelectedTile) > 0;
    public bool CanMoveRight => SelectedTile is not null && ActiveTiles.IndexOf(SelectedTile) is var idx && idx >= 0 && idx < ActiveTiles.Count - 1;

    public ICommand MoveSelectedLeft { get; }
    public ICommand MoveSelectedRight { get; }
    public ICommand RemoveSelected { get; }
    public ICommand ResetToDefaults { get; }

    public string ActiveCountBadge => Strings.MainTilesCountBadge(ActiveTiles.Count, _slots.Count);

    public bool IsCustomized
    {
        get
        {
            if (Columns != DefaultColumns) return true;
            if (ActiveTiles.Count != DefaultActiveOrder.Length) return true;
            for (var i = 0; i < DefaultActiveOrder.Length; i++)
            {
                if (ActiveTiles[i].Id != DefaultActiveOrder[i]) return true;
            }
            return false;
        }
    }

    public string SummaryDetail => ActiveTiles.Count == 0
        ? Strings.MainTilesSummaryNone
        : Strings.MainTilesSummary(ActiveTiles.Count, _slots.Count, Columns);

    public Visibility ActiveGridVisibility => ActiveTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyActiveVisibility => ActiveTiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AvailableGridVisibility => AvailableTiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyAvailableVisibility => AvailableTiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public IReadOnlyList<MainTileId> ActiveIds => [.. ActiveTiles.Select(slot => slot.Id)];

    /// <summary>Raised whenever the active tile list or column count changes.</summary>
    public event Action<IReadOnlyList<MainTileId>, int>? LayoutChanged;

    /// <summary>Raised when the RTSS tile is added or removed so the overlay service updates.</summary>
    public event Action<bool>? RtssTileChanged;

    /// <summary>Raised when Back clears the selected tile so the view can restore focus to that tile.</summary>
    public event Action<MainTileSlotViewModel>? SelectionClearedByBack;

    public override bool GoBack()
    {
        if (SelectedTile is not { } selected) return false;
        SelectedTile = null;
        SelectionClearedByBack?.Invoke(selected);
        return true;
    }

    protected override void OnClosed() => SelectedTile = null;

    /// <summary>Moves an active tile directly to <paramref name="targetIndex"/> (used by drag-and-drop).</summary>
    public bool MoveTile(MainTileSlotViewModel tile, int targetIndex)
    {
        var currentIndex = ActiveTiles.IndexOf(tile);
        if (currentIndex < 0) return false;

        targetIndex = Math.Clamp(targetIndex, 0, ActiveTiles.Count - 1);
        if (currentIndex == targetIndex) return false;

        ActiveTiles.Move(currentIndex, targetIndex);
        SyncMetadata();
        PersistAndNotify();
        return true;
    }

    private void BuildCatalog()
    {
        Register(MainTileId.RefreshRate, Glyphs.RefreshRate, Strings.RefreshRate);
        Register(MainTileId.Fans, Glyphs.Fan, Strings.Fans);
        Register(MainTileId.Lighting, Glyphs.Lighting, Strings.Lighting);
        Register(MainTileId.Controller, Glyphs.Gamepad, Strings.Controller);
        Register(MainTileId.Keyboard, Glyphs.Keyboard, Strings.Keyboard);
        Register(MainTileId.Screenshot, Glyphs.Screenshot, Strings.Screenshot);
        Register(MainTileId.TaskManager, Glyphs.TaskManager, Strings.TaskManager);
        Register(MainTileId.Desktop, Glyphs.Desktop, Strings.Desktop);
        Register(MainTileId.OptiScaler, Glyphs.Upscaling, Strings.OptiScaler);
        Register(MainTileId.Rtss, Glyphs.Overlay, Strings.Rtss);
        Register(MainTileId.Mute, Glyphs.Muted, Strings.TileMute);
        Register(MainTileId.SteamBigPicture, Glyphs.Game, Strings.TileSteam);
        Register(MainTileId.XboxGameBar, Glyphs.Buttons, Strings.TileGameBar);
    }

    private void Register(MainTileId id, string glyph, string label)
    {
        var slot = new MainTileSlotViewModel(id, glyph, label, OnTilePrimaryAction, OnTileQuickRemove);
        _slots[id] = slot;
    }

    private void LoadInitialTiles()
    {
        ActiveTiles.Clear();
        AvailableTiles.Clear();

        var initialIds = ResolveInitialActiveIds();
        var activeSet = new HashSet<MainTileId>();

        foreach (var id in initialIds)
        {
            if (!activeSet.Add(id) || !_slots.TryGetValue(id, out var slot)) continue;
            slot.IsActive = true;
            ActiveTiles.Add(slot);
        }

        foreach (var id in CatalogOrder)
        {
            if (activeSet.Contains(id) || !_slots.TryGetValue(id, out var slot)) continue;
            slot.IsActive = false;
            AvailableTiles.Add(slot);
        }

        // Keep feature flags aligned with the active layout without triggering a disk write at startup.
        _state.ShowOptiScaler = activeSet.Contains(MainTileId.OptiScaler);
        _state.ShowRtss = activeSet.Contains(MainTileId.Rtss);
    }

    private List<MainTileId> ResolveInitialActiveIds()
    {
        if (_state.MainTiles is { } saved)
        {
            var parsed = new List<MainTileId>(saved.Count);
            foreach (var raw in saved)
            {
                if (Enum.TryParse<MainTileId>(raw, ignoreCase: true, out var id) && _slots.ContainsKey(id))
                    parsed.Add(id);
            }
            return parsed;
        }

        return [.. DefaultActiveOrder];
    }

    private void OnTilePrimaryAction(MainTileSlotViewModel slot)
    {
        if (slot.IsActive)
        {
            SelectedTile = ReferenceEquals(SelectedTile, slot) ? null : slot;
            return;
        }

        // Available tile: add directly to the main screen preview.
        AvailableTiles.Remove(slot);
        slot.IsActive = true;
        ActiveTiles.Add(slot);

        SyncMetadata();
        PersistAndNotify();
    }

    private void OnTileQuickRemove(MainTileSlotViewModel slot)
    {
        if (!slot.IsActive) return;
        RemoveActiveTile(slot);
    }

    private void HideSelectedTile()
    {
        if (SelectedTile is not { } selected) return;
        RemoveActiveTile(selected);
    }

    private void RemoveActiveTile(MainTileSlotViewModel slot)
    {
        if (ReferenceEquals(SelectedTile, slot)) SelectedTile = null;

        if (!ActiveTiles.Remove(slot)) return;
        slot.IsActive = false;
        InsertInCatalogOrder(AvailableTiles, slot);

        SyncMetadata();
        PersistAndNotify();
    }

    private void MoveSelectedBy(int delta)
    {
        if (SelectedTile is not { } selected) return;
        var index = ActiveTiles.IndexOf(selected);
        if (index < 0) return;

        var target = index + delta;
        if (target < 0 || target >= ActiveTiles.Count) return;

        ActiveTiles.Move(index, target);
        SyncMetadata();
        PersistAndNotify();
    }

    private void Reset()
    {
        SelectedTile = null;

        _absorbing = true;
        ColumnsIndex = DefaultColumns - MinColumns;
        _absorbing = false;

        ActiveTiles.Clear();
        AvailableTiles.Clear();

        var activeSet = new HashSet<MainTileId>(DefaultActiveOrder);
        foreach (var id in DefaultActiveOrder)
        {
            var slot = _slots[id];
            slot.IsActive = true;
            ActiveTiles.Add(slot);
        }

        foreach (var id in CatalogOrder)
        {
            if (activeSet.Contains(id)) continue;
            var slot = _slots[id];
            slot.IsActive = false;
            AvailableTiles.Add(slot);
        }

        SyncMetadata();
        PersistAndNotify();
    }

    private static void InsertInCatalogOrder(ObservableCollection<MainTileSlotViewModel> list, MainTileSlotViewModel slot)
    {
        var orderIndex = Array.IndexOf(CatalogOrder, slot.Id);
        var insertAt = 0;
        while (insertAt < list.Count && Array.IndexOf(CatalogOrder, list[insertAt].Id) < orderIndex)
            insertAt++;

        list.Insert(insertAt, slot);
    }

    private void SyncMetadata()
    {
        for (var i = 0; i < ActiveTiles.Count; i++)
            ActiveTiles[i].SlotNumber = i + 1;

        foreach (var tile in AvailableTiles)
            tile.SlotNumber = 0;

        Notify(nameof(ActiveCountBadge));
        Notify(nameof(IsCustomized));
        Notify(nameof(SummaryDetail));
        Notify(nameof(ActiveGridVisibility));
        Notify(nameof(EmptyActiveVisibility));
        Notify(nameof(AvailableGridVisibility));
        Notify(nameof(EmptyAvailableVisibility));
        NotifySelectionMeta();
    }

    private void NotifySelectionMeta()
    {
        Notify(nameof(HasSelectedTile));
        Notify(nameof(SelectedPositionText));
        Notify(nameof(CanMoveLeft));
        Notify(nameof(CanMoveRight));
    }

    private void PersistAndNotify()
    {
        var activeIds = ActiveIds;
        var hasOptiScaler = activeIds.Contains(MainTileId.OptiScaler);
        var hasRtss = activeIds.Contains(MainTileId.Rtss);
        var rtssChanged = _state.ShowRtss != hasRtss;

        _state.MainTiles = [.. activeIds.Select(id => id.ToString())];
        _state.MainTileColumns = Columns;
        _state.ShowOptiScaler = hasOptiScaler;
        _state.ShowRtss = hasRtss;
        _state.Save();

        LayoutChanged?.Invoke(activeIds, Columns);

        if (rtssChanged)
            RtssTileChanged?.Invoke(hasRtss);
    }
}

/// <summary>One interactive tile card inside the Main Screen Tiles customizer.</summary>
public sealed class MainTileSlotViewModel : Observable
{
    public MainTileSlotViewModel(
        MainTileId id,
        string glyph,
        string label,
        Action<MainTileSlotViewModel> onPrimaryAction,
        Action<MainTileSlotViewModel> onQuickRemove)
    {
        Id = id;
        Glyph = glyph;
        Label = label;
        PrimaryAction = new Command(() => onPrimaryAction(this));
        QuickRemove = new Command(() => onQuickRemove(this));
    }

    public MainTileId Id { get; }
    public string Glyph { get; }
    public string Label { get; }

    public ICommand PrimaryAction { get; }
    public ICommand QuickRemove { get; }

    public bool IsActive { get; internal set => Set(ref field, value); }

    public bool IsSelected { get; internal set => Set(ref field, value); }

    public bool IsDragging { get; set => Set(ref field, value); }

    public int SlotNumber
    {
        get;
        internal set
        {
            if (Set(ref field, value)) Notify(nameof(SlotLabel));
        }
    }

    public string SlotLabel => SlotNumber > 0 ? SlotNumber.ToString() : "";
}
