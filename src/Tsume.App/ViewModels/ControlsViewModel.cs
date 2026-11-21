using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Controller;
using Tsume.Core.Devices;

namespace Tsume.App.ViewModels;

/// <summary>Controls &amp; layout page; capabilities come from <see cref="DeviceProfile.Controls"/>.</summary>
public sealed class ControlsViewModel : LayerViewModel, IDisposable
{
    private readonly PerformanceViewModel _performance;
    private readonly SavedState _state;
    private readonly Dictionary<string, (BackButtonDefinition Button, BackButtonBinding Binding)> _pending = [];

    private ControlsProfile? _controls;
    private IBackButtonController? _controller;

    private bool _absorbing;
    private bool _msiCenterRuns;
    private int _changes;
    private Task? _reading;

    private bool _writing;
    private Task _writer = Task.CompletedTask;
    private bool _unsaved;
    private bool _disposed;

    public ControlsViewModel(PerformanceViewModel performance, SavedState state)
    {
        _performance = performance;
        _state = state;

        GameBarOptions =
        [
            new(HardwareButtonAction.XboxGameBar, Strings.GameBarDefault),
            new(HardwareButtonAction.OpenTsume, Strings.GameBarOpenTsume),
            new(HardwareButtonAction.SteamBigPicture, Strings.GameBarSteam),
            new(HardwareButtonAction.TaskManager, Strings.GameBarTaskManager),
            new(HardwareButtonAction.TouchKeyboard, Strings.GameBarTouchKeyboard),
            new(HardwareButtonAction.Screenshot, Strings.GameBarScreenshot),
            new(HardwareButtonAction.ShowDesktop, Strings.GameBarShowDesktop),
            new(HardwareButtonAction.Disabled, Strings.GameBarDisabled),
        ];

        OemButtonOptions =
        [
            new(HardwareButtonAction.OpenTsume, Strings.MButtonDefault),
            new(HardwareButtonAction.XboxGameBar, Strings.MButtonXboxGameBar),
            new(HardwareButtonAction.SteamBigPicture, Strings.GameBarSteam),
            new(HardwareButtonAction.TaskManager, Strings.GameBarTaskManager),
            new(HardwareButtonAction.TouchKeyboard, Strings.GameBarTouchKeyboard),
            new(HardwareButtonAction.Screenshot, Strings.GameBarScreenshot),
            new(HardwareButtonAction.ShowDesktop, Strings.GameBarShowDesktop),
            new(HardwareButtonAction.Disabled, Strings.GameBarDisabled),
        ];

        ToggleOpenOnRight = new Command(() => OpenOnRight = !OpenOnRight);

        var initialOem = state.ResolveOemButton();

        _absorbing = true;
        OpenOnRight = state.OpenOnRight;
        SelectedGameBarOption = GameBarOptions.FirstOrDefault(o => o.Action == state.GameBarButton) ?? GameBarOptions[0];
        SelectedOemButtonOption = OemButtonOptions.FirstOrDefault(o => o.Action == initialOem) ?? OemButtonOptions[0];
        _absorbing = false;

        performance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceViewModel.Device)) AdoptDevice(performance.Device);
        };

        AdoptDevice(performance.Device);
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

    public Visibility DedicatedButtonsVisibility =>
        _controls is { HasGameBarButton: true } or { HasOemButton: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility GameBarButtonVisibility =>
        _controls?.HasGameBarButton == true ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OemButtonVisibility =>
        _controls?.HasOemButton == true ? Visibility.Visible : Visibility.Collapsed;

    public string GameBarButtonLabel => _controls?.GameBarButtonLabel ?? Strings.GameBarButton;

    public string OemButtonLabel => _controls?.OemButtonLabel ?? Strings.MButton;

    public IReadOnlyList<HardwareButtonOption> GameBarOptions { get; }

    public HardwareButtonOption SelectedGameBarOption
    {
        get;
        set
        {
            if (value is null || !Set(ref field, value)) return;

            Notify(nameof(GameBarAction));
            Notify(nameof(GameBarDetail));

            if (_absorbing) return;

            var previous = _state.GameBarButton;
            _state.GameBarButton = value.Action;

            // On two-button devices, moving Tsume onto the Game Bar button swaps the OEM button
            // to Xbox Game Bar by default; restoring Game Bar restores the OEM button to Tsume.
            if (_controls?.HasOemButton == true)
            {
                if (value.Action == HardwareButtonAction.OpenTsume
                    && previous != HardwareButtonAction.OpenTsume
                    && OemButtonAction == HardwareButtonAction.OpenTsume)
                {
                    SetOemButtonSilently(HardwareButtonAction.XboxGameBar);
                }
                else if (value.Action == HardwareButtonAction.XboxGameBar
                    && previous == HardwareButtonAction.OpenTsume
                    && OemButtonAction is HardwareButtonAction.XboxGameBar or HardwareButtonAction.Disabled)
                {
                    SetOemButtonSilently(HardwareButtonAction.OpenTsume);
                }
            }

            _state.Save();
        }
    } = null!;

    public HardwareButtonAction GameBarAction => SelectedGameBarOption.Action;

    public string GameBarDetail =>
        GameBarAction == HardwareButtonAction.XboxGameBar
            ? (_controls?.GameBarNativeWinG != false ? Strings.GameBarDefaultDetail : Strings.GameBarNonNativeDefaultDetail)
            : _msiCenterRuns && _controls?.HasMsiCenterConflict == true ? Strings.GameBarMsiCenterWarning
            : Strings.GameBarCustomDetail;

    public IReadOnlyList<HardwareButtonOption> OemButtonOptions { get; }

    public HardwareButtonOption SelectedOemButtonOption
    {
        get;
        set
        {
            if (value is null || !Set(ref field, value)) return;

            Notify(nameof(OemButtonAction));
            Notify(nameof(OemButtonDetail));

            if (_absorbing) return;

            SaveOemButton(value.Action);
            _state.Save();
        }
    } = null!;

    public HardwareButtonAction OemButtonAction => SelectedOemButtonOption.Action;

    public string OemButtonDetail =>
        _msiCenterRuns && _controls?.HasMsiCenterConflict == true ? Strings.MButtonMsiCenterWarning
        : OemButtonAction switch
        {
            HardwareButtonAction.OpenTsume => Strings.MButtonDetailTsume,
            HardwareButtonAction.Disabled  => Strings.MButtonDetailDisabled,
            _                              => Strings.MButtonDetailCustom,
        };

    private void SetOemButtonSilently(HardwareButtonAction action)
    {
        _absorbing = true;
        SelectedOemButtonOption = OemButtonOptions.FirstOrDefault(o => o.Action == action) ?? OemButtonOptions[0];
        _absorbing = false;
        SaveOemButton(action);
    }

    private void SaveOemButton(HardwareButtonAction action)
    {
        _state.OemButton = action;
        _state.MButton = null;
        _state.MButtonOpensTsume = action == HardwareButtonAction.OpenTsume;
    }

    public ObservableCollection<BackButtonEditor> BackButtons { get; } = [];

    public Visibility BackButtonsVisibility =>
        _controls?.BackButtons is { Buttons.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public string BackButtonMultiHint =>
        Strings.BackButtonMultiHint(_controls?.BackButtons?.MaxSimultaneousKeys ?? BackButtonBinding.MaxKeys);

    public bool IsControllerAvailable { get; private set => Set(ref field, value); } = true;

    public string Status { get; private set => Set(ref field, value); } = Strings.ControlsSavedPanelOnly;

    private void AdoptDevice(DeviceProfile device)
    {
        var next = device.Controls;
        var sameControls = _controls is null ? next is null : next is not null && _controls.Matches(next);
        if (sameControls) return;

        var previousBack = _controls?.BackButtons;
        var nextBack = next?.BackButtons;
        var backChanged = previousBack is null ? nextBack is not null : nextBack is null || !previousBack.Matches(nextBack);

        _controls = next;

        if (backChanged)
        {
            _controller?.Dispose();
            _controller = null;
            _pending.Clear();
            _unsaved = false;

            RebuildBackButtons(nextBack);
            IsControllerAvailable = nextBack is { Buttons.Count: > 0 };
            Status = nextBack is { Buttons.Count: > 0 } ? Strings.ControlsSaved : Strings.ControlsSavedPanelOnly;
        }

        Notify(nameof(DedicatedButtonsVisibility));
        Notify(nameof(GameBarButtonVisibility));
        Notify(nameof(OemButtonVisibility));
        Notify(nameof(GameBarButtonLabel));
        Notify(nameof(OemButtonLabel));
        Notify(nameof(GameBarDetail));
        Notify(nameof(OemButtonDetail));
        Notify(nameof(BackButtonsVisibility));
        Notify(nameof(BackButtonMultiHint));

        if (IsOpen) _ = RefreshAsync();
    }

    private void RebuildBackButtons(BackButtonsProfile? profile)
    {
        BackButtons.Clear();
        if (profile is null || profile.Buttons.Count == 0) return;

        var keys = BackButtonControllers.KeysFor(profile);
        var maxKeys = Math.Max(1, profile.MaxSimultaneousKeys);

        foreach (var button in profile.Buttons)
            BackButtons.Add(new BackButtonEditor(button, keys, maxKeys, OnBackButtonChanged));
    }

    protected override void OnOpened() => _ = RefreshAsync();

    public Task RefreshAsync() => _reading is { IsCompleted: false } ? _reading : _reading = ReadAsync();

    private async Task ReadAsync()
    {
        var changesBefore = _changes;
        var controls = _controls;

        var msiRuns = controls?.HasMsiCenterConflict == true && await Task.Run(() => MsiCenter.IsRunning);
        _msiCenterRuns = msiRuns;
        Notify(nameof(GameBarDetail));
        Notify(nameof(OemButtonDetail));

        var backProfile = controls?.BackButtons;
        if (backProfile is null || backProfile.Buttons.Count == 0)
        {
            Status = Strings.ControlsSavedPanelOnly;
            return;
        }

        var controller = await ConnectAsync(backProfile);
        if (controller is null) return;

        var readings = new List<(BackButtonEditor Editor, BackButtonBinding Binding)>(BackButtons.Count);
        foreach (var editor in BackButtons)
        {
            var binding = await controller.ReadAsync(editor.Definition);
            if (binding is null)
            {
                Lost();
                return;
            }
            readings.Add((editor, binding));
        }

        if (_changes != changesBefore || _writing) return;

        foreach (var (editor, binding) in readings)
            editor.Load(binding);

        Status = msiRuns ? Strings.ControlsMsiCenterRuns : Strings.ControlsSaved;
    }

    private async Task<IBackButtonController?> ConnectAsync(BackButtonsProfile profile)
    {
        _controller ??= await Task.Run(() => BackButtonControllers.Open(profile));

        IsControllerAvailable = _controller is not null;
        if (_controller is null) Status = Strings.ControllerNotFound;

        return _controller;
    }

    private void Lost()
    {
        _controller?.Dispose();
        _controller = null;
        Status = Strings.ControllerNotAnswering;
    }

    private void OnBackButtonChanged(BackButtonDefinition button, BackButtonBinding binding)
    {
        if (_controls?.BackButtons is null) return;

        _changes++;
        _pending[button.Id] = (button, binding);

        if (_writing) return;
        _writing = true;
        _writer = WriteQueuedAsync();
    }

    private async Task WriteQueuedAsync()
    {
        try
        {
            while (_pending.Count > 0)
            {
                var profile = _controls?.BackButtons;
                if (profile is null)
                {
                    _pending.Clear();
                    return;
                }

                var batch = _pending.Values.ToList();
                _pending.Clear();

                var controller = await ConnectAsync(profile);
                if (controller is null) return;

                var ok = true;
                var anyBound = false;
                foreach (var (button, binding) in batch)
                {
                    ok = await controller.WriteAsync(button, binding);
                    if (!ok) break;
                    if (binding.Keys.Count > 0) anyBound = true;
                }

                if (ok && anyBound)
                    await controller.EnsureActiveAsync();

                if (ok) _unsaved = true;
                else Lost();
            }
        }
        finally
        {
            _writing = false;
        }
    }

    protected override async void OnClosed()
    {
        Controls.KeyDropdown.CloseIfOpen();

        while (_writing) await _writer;

        if (!_unsaved || _controller is null) return;

        if (await _controller.SaveAsync()) _unsaved = false;
        else Lost();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller?.Dispose();
        _controller = null;
    }
}

public sealed record HardwareButtonOption(HardwareButtonAction Action, string Label)
{
    public override string ToString() => Label;
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
        if (Slots.Count <= 1) return;

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
        {
            Slots[i].Number = i + 1;
            Slots[i].CanRemove = Slots.Count > 1;
        }

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

    public bool CanRemove
    {
        get;
        set
        {
            if (Set(ref field, value)) Notify(nameof(RemoveVisibility));
        }
    }

    public Visibility RemoveVisibility => CanRemove ? Visibility.Visible : Visibility.Collapsed;

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
