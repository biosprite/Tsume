using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Controller;
using Tsume.Core.Lighting;

namespace Tsume.App.ViewModels;

/// <summary>
/// Controls &amp; layout page, opened from Settings: panel side (left/right), Game Bar &amp; M button
/// assignments, and M1 / M2 rear-paddle key rebinding (up to 3 simultaneous keys per button).
/// </summary>
public sealed class ControlsViewModel : LayerViewModel
{
    private readonly SavedState _state;
    private ClawController? _controller;

    private bool _absorbing;
    private bool _msiCenterRuns;
    private int _changes;
    private Task? _reading;

    private BackButtonBinding? _pendingM1;
    private BackButtonBinding? _pendingM2;
    private bool _writing;
    private Task _writer = Task.CompletedTask;
    private bool _unsaved;

    public ControlsViewModel(SavedState state)
    {
        _state = state;

        GameBarOptions =
        [
            new(GameBarAction.Default, Strings.GameBarDefault),
            new(GameBarAction.OpenTsume, Strings.GameBarOpenTsume),
            new(GameBarAction.SteamBigPicture, Strings.GameBarSteam),
            new(GameBarAction.TaskManager, Strings.GameBarTaskManager),
            new(GameBarAction.TouchKeyboard, Strings.GameBarTouchKeyboard),
            new(GameBarAction.Screenshot, Strings.GameBarScreenshot),
            new(GameBarAction.ShowDesktop, Strings.GameBarShowDesktop),
            new(GameBarAction.Disabled, Strings.GameBarDisabled),
        ];

        M1 = new BackButtonEditor(BackButton.M1, Strings.BackButtonM1, Strings.BackButtonM1Sub, OnBackButtonChanged);
        M2 = new BackButtonEditor(BackButton.M2, Strings.BackButtonM2, Strings.BackButtonM2Sub, OnBackButtonChanged);

        ToggleOpenOnRight = new Command(() => OpenOnRight = !OpenOnRight);
        ToggleMButtonOpensTsume = new Command(() => MButtonOpensTsume = !MButtonOpensTsume);

        _absorbing = true;
        OpenOnRight = state.OpenOnRight;
        MButtonOpensTsume = state.MButtonOpensTsume;
        SelectedGameBarOption = GameBarOptions.FirstOrDefault(o => o.Action == state.GameBarButton) ?? GameBarOptions[0];
        _absorbing = false;
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

    public IReadOnlyList<GameBarOption> GameBarOptions { get; }

    public GameBarOption SelectedGameBarOption
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

            // Switching the Game Bar button to open Tsume replaces the M button by default;
            // switching away restores the M button so Tsume always keeps a hardware trigger.
            if (value.Action == GameBarAction.OpenTsume && previous != GameBarAction.OpenTsume && MButtonOpensTsume)
            {
                _absorbing = true;
                MButtonOpensTsume = false;
                _absorbing = false;
                _state.MButtonOpensTsume = false;
            }
            else if (value.Action != GameBarAction.OpenTsume && previous == GameBarAction.OpenTsume && !MButtonOpensTsume)
            {
                _absorbing = true;
                MButtonOpensTsume = true;
                _absorbing = false;
                _state.MButtonOpensTsume = true;
            }

            _state.Save();
        }
    } = null!;

    public GameBarAction GameBarAction => SelectedGameBarOption.Action;

    public string GameBarDetail =>
        GameBarAction == GameBarAction.Default ? Strings.GameBarDefaultDetail
        : _msiCenterRuns ? Strings.GameBarMsiCenterWarning
        : Strings.GameBarCustomDetail;

    public ICommand ToggleMButtonOpensTsume { get; }

    public bool MButtonOpensTsume
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(MButtonDetail));

            if (_absorbing) return;
            _state.MButtonOpensTsume = value;
            _state.Save();
        }
    } = true;

    public string MButtonDetail => MButtonOpensTsume ? Strings.MButtonOpensTsumeOn : Strings.MButtonOpensTsumeOff;

    public BackButtonEditor M1 { get; }
    public BackButtonEditor M2 { get; }

    public bool IsControllerAvailable { get; private set => Set(ref field, value); } = true;

    public string Status { get; private set => Set(ref field, value); } = Strings.ControlsSaved;

    protected override void OnOpened() => _ = RefreshAsync();

    public Task RefreshAsync() => _reading is { IsCompleted: false } ? _reading : _reading = ReadAsync();

    private async Task ReadAsync()
    {
        var changesBefore = _changes;

        var msiRuns = await Task.Run(() => MsiCenter.IsRunning);
        _msiCenterRuns = msiRuns;
        Notify(nameof(GameBarDetail));

        var controller = await ConnectAsync();
        if (controller is null) return;

        var m1Raw = await controller.ReadProfileAsync(ProfileMap.M1ProfileStart, ProfileMap.BackButtonProfileLength);
        var m2Raw = m1Raw is null
            ? null
            : await controller.ReadProfileAsync(ProfileMap.M2ProfileStart, ProfileMap.BackButtonProfileLength);

        if (m1Raw is null || m2Raw is null)
        {
            Lost();
            return;
        }

        if (_changes != changesBefore || _writing) return;

        M1.Load(BackButtonBinding.Decode(m1Raw));
        M2.Load(BackButtonBinding.Decode(m2Raw));

        Status = msiRuns ? Strings.ControlsMsiCenterRuns : Strings.ControlsSaved;
    }

    private async Task<ClawController?> ConnectAsync()
    {
        _controller ??= await Task.Run(ClawController.Find);

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

    private void OnBackButtonChanged(BackButton button, BackButtonBinding binding)
    {
        _changes++;
        if (button == BackButton.M1) _pendingM1 = binding;
        else _pendingM2 = binding;

        if (_writing) return;
        _writing = true;
        _writer = WriteQueuedAsync();
    }

    private async Task WriteQueuedAsync()
    {
        try
        {
            while (_pendingM1 is not null || _pendingM2 is not null)
            {
                var m1 = _pendingM1;
                var m2 = _pendingM2;
                _pendingM1 = null;
                _pendingM2 = null;

                var controller = await ConnectAsync();
                if (controller is null) return;

                var ok = true;
                if (m1 is not null) ok = await controller.WriteBackButtonAsync(BackButton.M1, m1.Encode());
                if (ok && m2 is not null) ok = await controller.WriteBackButtonAsync(BackButton.M2, m2.Encode());

                if (ok && ((m1 is { Keys.Count: > 0 }) || (m2 is { Keys.Count: > 0 })))
                    await controller.EnsureBackButtonsActiveAsync();

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
}

/// <summary>One option in the Game Bar button dropdown.</summary>
public sealed record GameBarOption(GameBarAction Action, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Editor for one rear paddle (M1 or M2) supporting 1 to 3 simultaneous keys.</summary>
public sealed class BackButtonEditor : Observable
{
    private readonly Action<BackButton, BackButtonBinding> _onChanged;
    private byte _turboSpeed = 0x0C;
    private bool _absorbing;

    public BackButtonEditor(BackButton button, string title, string subtitle, Action<BackButton, BackButtonBinding> onChanged)
    {
        Button = button;
        Title = title;
        Subtitle = subtitle;
        _onChanged = onChanged;

        AddKey = new Command(AddSlot);

        _absorbing = true;
        Slots.Add(CreateSlot(1, BackButtonKey.Unbound));
        _absorbing = false;
        SyncMeta();
    }

    public BackButton Button { get; }
    public string Title { get; }
    public string Subtitle { get; }

    public ObservableCollection<KeySlotViewModel> Slots { get; } = [];

    public ICommand AddKey { get; }

    public string Summary { get; private set => Set(ref field, value); } = Strings.BackButtonUnbound;

    public Visibility AddKeyVisibility { get; private set => Set(ref field, value); } = Visibility.Collapsed;

    public void Load(BackButtonBinding binding)
    {
        _absorbing = true;
        _turboSpeed = binding.TurboSpeed;

        Slots.Clear();
        if (binding.Keys.Count == 0)
        {
            Slots.Add(CreateSlot(1, BackButtonKey.Unbound));
        }
        else
        {
            for (var i = 0; i < Math.Min(binding.Keys.Count, BackButtonBinding.MaxKeys); i++)
                Slots.Add(CreateSlot(i + 1, BackButtonKey.FromCode(binding.Keys[i])));
        }

        _absorbing = false;
        SyncMeta();
    }

    private KeySlotViewModel CreateSlot(int number, BackButtonKey key) =>
        new(number, key, OnSlotChanged, RemoveSlot);

    private void AddSlot()
    {
        if (Slots.Count >= BackButtonBinding.MaxKeys) return;

        _absorbing = true;
        Slots.Add(CreateSlot(Slots.Count + 1, BackButtonKey.Unbound));
        _absorbing = false;

        SyncMeta();
    }

    private void RemoveSlot(KeySlotViewModel slot)
    {
        if (Slots.Count <= 1) return;

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
        {
            Slots[i].Number = i + 1;
            Slots[i].CanRemove = Slots.Count > 1;
        }

        var binding = CurrentBinding();
        Summary = binding.FormatSummary(Strings.BackButtonUnbound);

        var hasAnyKey = Slots.Any(slot => slot.SelectedKey.Code != BackButtonKey.UnboundCode);
        AddKeyVisibility = Slots.Count < BackButtonBinding.MaxKeys && hasAnyKey
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private BackButtonBinding CurrentBinding()
    {
        var keys = Slots
            .Select(slot => slot.SelectedKey.Code)
            .Where(code => code is not (0 or BackButtonKey.UnboundCode))
            .Take(BackButtonBinding.MaxKeys)
            .ToList();

        return new BackButtonBinding(keys, _turboSpeed);
    }

    private void Emit() => _onChanged(Button, CurrentBinding());
}

/// <summary>One key dropdown row inside M1 or M2.</summary>
public sealed class KeySlotViewModel : Observable
{
    private readonly Action _changed;

    public KeySlotViewModel(int number, BackButtonKey initial, Action changed, Action<KeySlotViewModel> remove)
    {
        _changed = changed;
        Number = number;
        _selectedKey = initial;
        Remove = new Command(() => remove(this));
    }

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
