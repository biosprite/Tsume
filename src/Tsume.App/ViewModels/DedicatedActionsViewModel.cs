using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Theme;
using Tsume.Core.Controller;

namespace Tsume.App.ViewModels;

/// <summary>
/// Categorized, compact accordion editor for Dedicated Actions (global keyboard &amp; gamepad shortcuts).
/// Opened from Controls &amp; Layout so ControlsScreen stays short even as more actions are added.
/// </summary>
public sealed class DedicatedActionsViewModel : LayerViewModel
{
    private readonly SavedState _state;
    private readonly List<DedicatedActionEditor> _all = [];

    public DedicatedActionsViewModel(SavedState state)
    {
        _state = state;

        Tabs =
        [
            Strings.DedicatedActionsTabActive,
            Strings.DedicatedActionsTabTsume,
            Strings.DedicatedActionsTabSystem,
        ];

        // Essentials first (always visible on the Active tab), then categorized actions.
        AddAction(DedicatedAction.OpenTsume, DedicatedActionCategory.Tsume, Glyphs.Sliders,
            Strings.DedicatedActionOpenTsume, Strings.DedicatedActionOpenTsumeDetail,
            isEssential: true, defaultKeys: [DedicatedActionKeys.OemButtonCode]);

        AddAction(DedicatedAction.OpenFpsOverlay, DedicatedActionCategory.Tsume, Glyphs.Overlay,
            Strings.DedicatedActionFpsOverlay, Strings.DedicatedActionFpsOverlayDetail,
            isEssential: true);

        AddAction(DedicatedAction.CyclePerformanceMode, DedicatedActionCategory.Tsume, Glyphs.Sustained,
            Strings.DedicatedActionCyclePerformance, Strings.DedicatedActionCyclePerformanceDetail);

        AddAction(DedicatedAction.CycleRefreshRate, DedicatedActionCategory.Tsume, Glyphs.RefreshRate,
            Strings.DedicatedActionCycleRefreshRate, Strings.DedicatedActionCycleRefreshRateDetail);

        AddAction(DedicatedAction.ToggleFanBoost, DedicatedActionCategory.Tsume, Glyphs.Fan,
            Strings.DedicatedActionToggleFanBoost, Strings.DedicatedActionToggleFanBoostDetail);

        AddAction(DedicatedAction.ToggleControllerMode, DedicatedActionCategory.Tsume, Glyphs.Gamepad,
            Strings.DedicatedActionToggleControllerMode, Strings.DedicatedActionToggleControllerModeDetail);

        AddAction(DedicatedAction.Screenshot, DedicatedActionCategory.System, Glyphs.Screenshot,
            Strings.DedicatedActionScreenshot, Strings.DedicatedActionScreenshotDetail);

        AddAction(DedicatedAction.TouchKeyboard, DedicatedActionCategory.System, Glyphs.Keyboard,
            Strings.DedicatedActionTouchKeyboard, Strings.DedicatedActionTouchKeyboardDetail);

        AddAction(DedicatedAction.ShowDesktop, DedicatedActionCategory.System, Glyphs.Desktop,
            Strings.DedicatedActionShowDesktop, Strings.DedicatedActionShowDesktopDetail);

        AddAction(DedicatedAction.TaskManager, DedicatedActionCategory.System, Glyphs.TaskManager,
            Strings.DedicatedActionTaskManager, Strings.DedicatedActionTaskManagerDetail);

        AddAction(DedicatedAction.ToggleMute, DedicatedActionCategory.System, Glyphs.Muted,
            Strings.DedicatedActionToggleMute, Strings.DedicatedActionToggleMuteDetail);

        AddAction(DedicatedAction.SteamBigPicture, DedicatedActionCategory.System, Glyphs.Game,
            Strings.DedicatedActionSteamBigPicture, Strings.DedicatedActionSteamBigPictureDetail);

        AddAction(DedicatedAction.XboxGameBar, DedicatedActionCategory.System, Glyphs.Buttons,
            Strings.DedicatedActionXboxGameBar, Strings.DedicatedActionXboxGameBarDetail);

        RebuildVisible();
    }

    public IReadOnlyList<string> Tabs { get; }

    public int SelectedTabIndex
    {
        get;
        set
        {
            if (!Set(ref field, Math.Clamp(value, 0, Tabs.Count - 1))) return;
            Controls.KeyDropdown.CloseIfOpen();
            CollapseAll();
            RebuildVisible();
            Notify(nameof(BrowseHintVisibility));
        }
    }

    public ObservableCollection<DedicatedActionEditor> VisibleActions { get; } = [];

    public Visibility BrowseHintVisibility =>
        SelectedTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

    public string FooterHint => Strings.DedicatedActionsFooterHint(DedicatedActionKeys.MaxKeys);

    public bool HasActiveBindings => _all.Any(a => a.IsBound);

    public string SummaryDetail
    {
        get
        {
            var active = _all.Where(a => a.IsBound).ToList();
            if (active.Count == 0) return Strings.DedicatedActionsNoneActive;

            var names = active.Count <= 2
                ? string.Join(", ", active.Select(a => a.Title))
                : $"{active[0].Title}, {active[1].Title}, +{active.Count - 2}";

            return Strings.DedicatedActionsActiveSummary(active.Count, names);
        }
    }

    /// <summary>Raised whenever any Dedicated Action binding changes so global hooks can refresh.</summary>
    public event Action? BindingsChanged;

    /// <summary>Raised when Back collapses an expanded action so the view can restore focus to its header.</summary>
    public event Action<DedicatedActionEditor>? CollapsedByBack;

    public IReadOnlyList<DedicatedActionBinding> GetBindings() =>
        _all.Select(a => new DedicatedActionBinding(a.Action, a.CurrentKeys())).ToList();

    /// <summary>
    /// Returns the Dedicated Action currently bound solely to <paramref name="hardwareCode"/>, if any.
    /// Used by System Buttons so hardware button dropdowns always stay in sync with Dedicated Actions.
    /// </summary>
    public DedicatedActionEditor? FindSingleButtonAction(byte hardwareCode) =>
        _all.FirstOrDefault(a =>
        {
            var keys = a.CurrentKeys();
            return keys.Count == 1 && keys[0] == hardwareCode;
        });

    /// <summary>
    /// Reassigns a single hardware button (<see cref="DedicatedActionKeys.OemButtonCode"/> or
    /// <see cref="DedicatedActionKeys.XboxButtonCode"/>) to <paramref name="targetAction"/> (or unbinds it when null).
    /// Automatically clears any previous action holding that single button so Active reflects the override immediately.
    /// </summary>
    public void AssignSingleHardwareButton(byte hardwareCode, DedicatedAction? targetAction)
    {
        var changed = false;

        foreach (var editor in _all)
        {
            var keys = editor.CurrentKeys();
            if (keys.Count == 1 && keys[0] == hardwareCode && editor.Action != targetAction)
            {
                editor.SetKeysSilently([]);
                PersistKeys(editor.Action, []);
                changed = true;
            }
        }

        if (targetAction is { } target && _all.FirstOrDefault(a => a.Action == target) is { } targetEditor)
        {
            var current = targetEditor.CurrentKeys();
            if (current.Count != 1 || current[0] != hardwareCode)
            {
                byte[] next = [hardwareCode];
                targetEditor.SetKeysSilently(next);
                PersistKeys(targetEditor.Action, next);
                changed = true;
            }
        }

        if (!changed) return;

        _state.Save();
        RebuildVisible();
        Notify(nameof(HasActiveBindings));
        Notify(nameof(SummaryDetail));
        BindingsChanged?.Invoke();
    }

    public override bool GoBack()
    {
        var expanded = _all.FirstOrDefault(a => a.IsExpanded);
        if (expanded is null) return false;

        expanded.IsExpanded = false;
        if (SelectedTabIndex == 0 && !expanded.IsEssential && !expanded.IsBound)
            RebuildVisible();

        CollapsedByBack?.Invoke(expanded);
        return true;
    }

    protected override void OnOpened()
    {
        CollapseAll();
        SelectedTabIndex = 0;
        RebuildVisible();
    }

    protected override void OnClosed()
    {
        Controls.KeyDropdown.CloseIfOpen();
        CollapseAll();
    }

    private void AddAction(
        DedicatedAction action,
        DedicatedActionCategory category,
        string glyph,
        string title,
        string subtitle,
        bool isEssential = false,
        IReadOnlyList<byte>? defaultKeys = null)
    {
        var initialKeys = LoadSavedKeys(action, defaultKeys);
        _all.Add(new DedicatedActionEditor(
            action,
            category,
            glyph,
            title,
            subtitle,
            isEssential,
            DedicatedActionKeys.All,
            initialKeys,
            OnActionChanged,
            ToggleExpand));
    }

    private IReadOnlyList<byte>? LoadSavedKeys(DedicatedAction action, IReadOnlyList<byte>? defaultKeys)
    {
        if (_state.DedicatedActionHotkeys is { } map && map.TryGetValue(action.ToString(), out var saved))
            return saved;

        return action switch
        {
            DedicatedAction.OpenFpsOverlay => _state.FpsOverlayHotkey,
            DedicatedAction.OpenTsume      => _state.OpenTsumeHotkey ?? defaultKeys,
            _                              => defaultKeys,
        };
    }

    private void OnActionChanged(DedicatedAction action, IReadOnlyList<byte> keys)
    {
        // If this action just claimed a single hardware button (Quick Menu / OEM or Xbox / Guide),
        // release it from any other action that previously held that same single button.
        if (keys.Count == 1 && keys[0] is DedicatedActionKeys.OemButtonCode or DedicatedActionKeys.XboxButtonCode)
        {
            var hwCode = keys[0];
            foreach (var other in _all)
            {
                if (other.Action == action) continue;
                var otherKeys = other.CurrentKeys();
                if (otherKeys.Count == 1 && otherKeys[0] == hwCode)
                {
                    other.SetKeysSilently([]);
                    PersistKeys(other.Action, []);
                }
            }
        }

        PersistKeys(action, keys);
        _state.Save();
        Notify(nameof(HasActiveBindings));
        Notify(nameof(SummaryDetail));
        BindingsChanged?.Invoke();
    }

    private void PersistKeys(DedicatedAction action, IReadOnlyList<byte> keys)
    {
        _state.DedicatedActionHotkeys ??= [];
        _state.DedicatedActionHotkeys[action.ToString()] = [.. keys];

        // Keep legacy fields in sync for backwards compatibility.
        switch (action)
        {
            case DedicatedAction.OpenFpsOverlay:
                _state.FpsOverlayHotkey = keys.Count > 0 ? [.. keys] : null;
                break;
            case DedicatedAction.OpenTsume:
                _state.OpenTsumeHotkey = [.. keys];
                break;
        }
    }

    private void ToggleExpand(DedicatedActionEditor target)
    {
        Controls.KeyDropdown.CloseIfOpen();

        var wasExpanded = target.IsExpanded;
        CollapseAll();

        if (!wasExpanded)
        {
            target.IsExpanded = true;
        }
        else if (SelectedTabIndex == 0 && !target.IsEssential && !target.IsBound)
        {
            RebuildVisible();
        }
    }

    private void CollapseAll()
    {
        foreach (var item in _all)
            item.IsExpanded = false;
    }

    private void RebuildVisible()
    {
        VisibleActions.Clear();

        IEnumerable<DedicatedActionEditor> filtered = SelectedTabIndex switch
        {
            0 => _all.Where(a => a.IsEssential || a.IsBound || a.IsExpanded),
            1 => _all.Where(a => a.Category == DedicatedActionCategory.Tsume),
            _ => _all.Where(a => a.Category == DedicatedActionCategory.System),
        };

        foreach (var item in filtered)
            VisibleActions.Add(item);
    }
}

public sealed class DedicatedActionEditor : Observable
{
    private readonly Action<DedicatedAction, IReadOnlyList<byte>> _onChanged;
    private bool _absorbing;

    public DedicatedActionEditor(
        DedicatedAction action,
        DedicatedActionCategory category,
        string glyph,
        string title,
        string subtitle,
        bool isEssential,
        IReadOnlyList<BackButtonKey> availableKeys,
        IReadOnlyList<byte>? initialKeys,
        Action<DedicatedAction, IReadOnlyList<byte>> onChanged,
        Action<DedicatedActionEditor> onToggleExpand)
    {
        Action = action;
        Category = category;
        Glyph = glyph;
        Title = title;
        Subtitle = subtitle;
        IsEssential = isEssential;
        AvailableKeys = availableKeys;
        MaxKeys = DedicatedActionKeys.MaxKeys;
        _onChanged = onChanged;

        AddKey = new Command(AddSlot);
        Clear = new Command(ClearAll);
        ToggleExpand = new Command(() => onToggleExpand(this));

        _absorbing = true;
        var valid = initialKeys?
            .Where(code => code is not (0 or BackButtonKey.UnboundCode) && availableKeys.Any(k => k.Code == code))
            .Take(MaxKeys)
            .ToList() ?? [];

        if (valid.Count == 0)
        {
            Slots.Add(CreateSlot(1, BackButtonKey.Unbound));
        }
        else
        {
            for (var i = 0; i < valid.Count; i++)
                Slots.Add(CreateSlot(i + 1, DedicatedActionKeys.FromCode(valid[i])));
        }

        _absorbing = false;
        SyncMeta();
    }

    public DedicatedAction Action { get; }
    public DedicatedActionCategory Category { get; }
    public string Glyph { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsEssential { get; }
    public IReadOnlyList<BackButtonKey> AvailableKeys { get; }
    public int MaxKeys { get; }

    public ObservableCollection<KeySlotViewModel> Slots { get; } = [];

    public ICommand AddKey { get; }
    public ICommand Clear { get; }
    public ICommand ToggleExpand { get; }

    public bool IsExpanded
    {
        get;
        set
        {
            if (Set(ref field, value))
                Notify(nameof(ExpandStateTag));
        }
    }

    /// <summary>Drives the chevron rotation trigger on the row header tile.</summary>
    public string ExpandStateTag => IsExpanded ? "Expanded" : "";

    public bool IsBound { get; private set => Set(ref field, value); }

    public string Summary { get; private set => Set(ref field, value); } = Strings.BackButtonUnbound;

    public Visibility AddKeyVisibility { get; private set => Set(ref field, value); } = Visibility.Collapsed;

    public Visibility ClearVisibility { get; private set => Set(ref field, value); } = Visibility.Collapsed;

    public IReadOnlyList<byte> CurrentKeys() =>
        Slots
            .Select(slot => slot.SelectedKey.Code)
            .Where(code => code is not (0 or BackButtonKey.UnboundCode))
            .Take(MaxKeys)
            .ToList();

    internal void SetKeysSilently(IReadOnlyList<byte> keys)
    {
        _absorbing = true;
        Slots.Clear();

        var valid = keys
            .Where(code => code is not (0 or BackButtonKey.UnboundCode) && AvailableKeys.Any(k => k.Code == code))
            .Take(MaxKeys)
            .ToList();

        if (valid.Count == 0)
        {
            Slots.Add(CreateSlot(1, BackButtonKey.Unbound));
        }
        else
        {
            for (var i = 0; i < valid.Count; i++)
                Slots.Add(CreateSlot(i + 1, DedicatedActionKeys.FromCode(valid[i])));
        }

        _absorbing = false;
        SyncMeta();
    }

    private KeySlotViewModel CreateSlot(int number, BackButtonKey key) =>
        new(number, key, AvailableKeys, OnSlotChanged, RemoveSlot);

    private void AddSlot()
    {
        if (Slots.Count >= MaxKeys) return;

        _absorbing = true;
        Slots.Add(CreateSlot(Slots.Count + 1, BackButtonKey.Unbound));
        _absorbing = false;

        SyncMeta();
    }

    private void ClearAll()
    {
        if (!IsBound && Slots.Count <= 1) return;

        _absorbing = true;
        Slots.Clear();
        Slots.Add(CreateSlot(1, BackButtonKey.Unbound));
        _absorbing = false;

        SyncMeta();
        Emit();
    }

    private void RemoveSlot(KeySlotViewModel slot)
    {
        if (Slots.Count <= 1)
        {
            var hadBinding = slot.SelectedKey.Code != BackButtonKey.UnboundCode;

            _absorbing = true;
            slot.SelectedKey = BackButtonKey.Unbound;
            _absorbing = false;

            SyncMeta();
            if (hadBinding) Emit();
            return;
        }

        Slots.Remove(slot);
        SyncMeta();
        Emit();
    }

    private void OnSlotChanged()
    {
        if (_absorbing) return;
        SyncMeta();
        Emit();
    }

    private void SyncMeta()
    {
        for (var i = 0; i < Slots.Count; i++)
            Slots[i].Number = i + 1;

        var keys = CurrentKeys();
        IsBound = keys.Count > 0;
        Summary = IsBound
            ? string.Join(" + ", keys.Select(code => DedicatedActionKeys.FromCode(code).Name))
            : Strings.BackButtonUnbound;

        AddKeyVisibility = Slots.Count < MaxKeys && IsBound
            ? Visibility.Visible
            : Visibility.Collapsed;

        ClearVisibility = IsBound
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Emit() => _onChanged(Action, CurrentKeys());
}
