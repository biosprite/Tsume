using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Controller;
using Tsume.Core.Devices;

namespace Tsume.App.ViewModels;

/// <summary>Controls &amp; layout page: panel side, Quick tiles, Dedicated Actions, and System Buttons.</summary>
public sealed class ControlsViewModel : LayerViewModel, IDisposable
{
    private readonly SavedState _state;
    private bool _absorbing;
    private bool _disposed;

    public ControlsViewModel(
        PerformanceViewModel performance,
        SavedState state,
        Action<bool>? onRtssVisibilityChanged = null)
    {
        _state = state;

        ToggleOpenOnRight = new Command(() => OpenOnRight = !OpenOnRight);

        _absorbing = true;
        OpenOnRight = state.OpenOnRight;
        _absorbing = false;

        MainTilesPage = new MainTilesViewModel(state);
        MainTilesPage.RtssTileChanged += enabled => onRtssVisibilityChanged?.Invoke(enabled);

        DedicatedActionsPage = new DedicatedActionsViewModel(state);
        DedicatedActionsPage.BindingsChanged += () => DedicatedActionsChanged?.Invoke();

        SystemButtonsPage = new SystemButtonsViewModel(performance, state, DedicatedActionsPage);
    }

    public ICommand ToggleOpenOnRight { get; }

    /// <summary>True to pin Tsume to the right screen edge and mirror its slide animation.</summary>
    public bool OpenOnRight
    {
        get;
        set
        {
            if (!Set(ref field, value) || _absorbing) return;
            _state.OpenOnRight = value;
            _state.Save();
        }
    }

    public MainTilesViewModel MainTilesPage { get; }

    public DedicatedActionsViewModel DedicatedActionsPage { get; }

    public SystemButtonsViewModel SystemButtonsPage { get; }

    /// <summary>Raised whenever a Dedicated Action key binding changes so global listeners can refresh.</summary>
    public event Action? DedicatedActionsChanged;

    public IReadOnlyList<DedicatedActionBinding> GetDedicatedActionBindings() =>
        DedicatedActionsPage.GetBindings();

    public string Status => Strings.ControlsSavedPanelOnly;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemButtonsPage.Dispose();
    }
}

public sealed class BackButtonEditor : Observable
{
    private readonly Action<BackButtonDefinition, BackButtonBinding> _onChanged;
    private byte _turboSpeed = 0x0C;
    private bool _isMacro;
    private bool _absorbing;

    public BackButtonEditor(
        BackButtonDefinition definition,
        IReadOnlyList<BackButtonKey> availableKeys,
        int maxKeys,
        Action<BackButtonDefinition, BackButtonBinding> onChanged)
    {
        Definition = definition;
        AvailableKeys = availableKeys;
        MaxKeys = Math.Max(1, maxKeys);
        _onChanged = onChanged;

        AddKey = new Command(AddSlot);

        _absorbing = true;
        Slots.Add(CreateSlot(1, BackButtonKey.Unbound));
        _absorbing = false;
        SyncMeta();
    }

    public BackButtonDefinition Definition { get; }
    public string Title => Definition.Label;
    public string Subtitle => Definition.Subtitle;
    public IReadOnlyList<BackButtonKey> AvailableKeys { get; }
    public int MaxKeys { get; }

    public ObservableCollection<KeySlotViewModel> Slots { get; } = [];

    public ICommand AddKey { get; }

    public string Summary { get; private set => Set(ref field, value); } = Strings.BackButtonUnbound;

    public Visibility AddKeyVisibility { get; private set => Set(ref field, value); } = Visibility.Collapsed;

    public void Load(BackButtonBinding binding)
    {
        _absorbing = true;
        _turboSpeed = binding.TurboSpeed;
        _isMacro = binding.IsMacro;

        Slots.Clear();
        if (binding.Keys.Count == 0)
        {
            Slots.Add(CreateSlot(1, BackButtonKey.Unbound));
        }
        else
        {
            for (var i = 0; i < Math.Min(binding.Keys.Count, MaxKeys); i++)
                Slots.Add(CreateSlot(i + 1, BackButtonKey.FromCode(binding.Keys[i])));
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

    private void RemoveSlot(KeySlotViewModel slot)
    {
        if (Slots.Count <= 1)
        {
            var hadBinding = slot.SelectedKey.Code != BackButtonKey.UnboundCode || _isMacro;
            _isMacro = false;

            _absorbing = true;
            slot.SelectedKey = BackButtonKey.Unbound;
            _absorbing = false;

            SyncMeta();
            if (hadBinding) Emit();
            return;
        }

        _isMacro = false;
        Slots.Remove(slot);
        SyncMeta();
        Emit();
    }

    private void OnSlotChanged()
    {
        if (_absorbing) return;

        _isMacro = false;
        SyncMeta();
        Emit();
    }

    private void SyncMeta()
    {
        for (var i = 0; i < Slots.Count; i++)
            Slots[i].Number = i + 1;

        var binding = CurrentBinding();
        Summary = binding.FormatSummary(Strings.BackButtonUnbound, Strings.BackButtonMacro, MaxKeys);

        var hasAnyKey = Slots.Any(slot => slot.SelectedKey.Code != BackButtonKey.UnboundCode);
        AddKeyVisibility = Slots.Count < MaxKeys && hasAnyKey
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private BackButtonBinding CurrentBinding()
    {
        var keys = Slots
            .Select(slot => slot.SelectedKey.Code)
            .Where(code => code is not (0 or BackButtonKey.UnboundCode))
            .Take(MaxKeys)
            .ToList();

        return new BackButtonBinding(keys, _turboSpeed, IsMacro: _isMacro && keys.Count == 0);
    }

    private void Emit() => _onChanged(Definition, CurrentBinding());
}

public sealed class KeySlotViewModel : Observable
{
    private readonly Action _changed;

    public KeySlotViewModel(
        int number,
        BackButtonKey initial,
        IReadOnlyList<BackButtonKey> availableKeys,
        Action changed,
        Action<KeySlotViewModel> remove)
    {
        _changed = changed;
        AvailableKeys = availableKeys;
        Number = number;
        _selectedKey = initial;
        Remove = new Command(() => remove(this));
    }

    public IReadOnlyList<BackButtonKey> AvailableKeys { get; }

    public int Number
    {
        get;
        set
        {
            if (Set(ref field, value)) Notify(nameof(SlotLabel));
        }
    }

    public string SlotLabel => Strings.BackButtonKeySlot(Number);

    private BackButtonKey _selectedKey;

    public BackButtonKey SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (value is null || !Set(ref _selectedKey, value)) return;
            _changed();
        }
    }

    public ICommand Remove { get; }
}
