using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Tsume.App.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Controller;
using Tsume.Core.Devices;

namespace Tsume.App.ViewModels;

/// <summary>
/// Unified System buttons screen combining dedicated hardware buttons (Game Bar / OEM) and rear back paddles.
/// Hardware button selections stay bidirectionally synchronized with <see cref="DedicatedActionsViewModel"/>
/// so overriding a button in either place immediately reflects in both.
/// </summary>
public sealed class SystemButtonsViewModel : LayerViewModel, IDisposable
{
    private readonly PerformanceViewModel _performance;
    private readonly SavedState _state;
    private readonly DedicatedActionsViewModel _dedicatedActions;
    private readonly Dictionary<string, (BackButtonDefinition Button, BackButtonBinding Binding)> _pending = [];

    private readonly IReadOnlyList<HardwareButtonOption> _baseGameBarOptions;
    private readonly IReadOnlyList<HardwareButtonOption> _baseOemButtonOptions;

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

    public SystemButtonsViewModel(
        PerformanceViewModel performance,
        SavedState state,
        DedicatedActionsViewModel dedicatedActions)
    {
        _performance = performance;
        _state = state;
        _dedicatedActions = dedicatedActions;

        _baseGameBarOptions =
        [
            new(HardwareButtonAction.XboxGameBar, Strings.GameBarDefault),
            new(HardwareButtonAction.OpenTsume, Strings.DedicatedActionOpenTsume),
            new(HardwareButtonAction.SteamBigPicture, Strings.GameBarSteam),
            new(HardwareButtonAction.TaskManager, Strings.GameBarTaskManager),
            new(HardwareButtonAction.TouchKeyboard, Strings.GameBarTouchKeyboard),
            new(HardwareButtonAction.Screenshot, Strings.GameBarScreenshot),
            new(HardwareButtonAction.ShowDesktop, Strings.GameBarShowDesktop),
            new(HardwareButtonAction.Disabled, Strings.GameBarDisabled),
        ];

        _baseOemButtonOptions =
        [
            new(HardwareButtonAction.OpenTsume, Strings.DedicatedActionOpenTsume),
            new(HardwareButtonAction.XboxGameBar, Strings.MButtonXboxGameBar),
            new(HardwareButtonAction.SteamBigPicture, Strings.GameBarSteam),
            new(HardwareButtonAction.TaskManager, Strings.GameBarTaskManager),
            new(HardwareButtonAction.TouchKeyboard, Strings.GameBarTouchKeyboard),
            new(HardwareButtonAction.Screenshot, Strings.GameBarScreenshot),
            new(HardwareButtonAction.ShowDesktop, Strings.GameBarShowDesktop),
            new(HardwareButtonAction.Disabled, Strings.GameBarDisabled),
        ];

        GameBarOptions = new ObservableCollection<HardwareButtonOption>(_baseGameBarOptions);
        OemButtonOptions = new ObservableCollection<HardwareButtonOption>(_baseOemButtonOptions);

        SyncFromDedicatedActions();
        _dedicatedActions.BindingsChanged += SyncFromDedicatedActions;

        _performance.PropertyChanged += OnPerformancePropertyChanged;

        AdoptDevice(_performance.Device);
    }

    private void OnPerformancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerformanceViewModel.Device)) AdoptDevice(_performance.Device);
    }

    public bool HasAnyButtons =>
        _controls is { HasGameBarButton: true } or { HasOemButton: true }
        || _controls?.BackButtons is { Buttons.Count: > 0 };

    public Visibility SectionVisibility =>
        HasAnyButtons ? Visibility.Visible : Visibility.Collapsed;

    public string SummaryDetail
    {
        get
        {
            var hasHw = _controls is { HasGameBarButton: true } or { HasOemButton: true };
            var hasBack = _controls?.BackButtons is { Buttons.Count: > 0 };
            return (hasHw, hasBack) switch
            {
                (true, true)   => Strings.SystemButtonsDetailAll,
                (true, false)  => Strings.SystemButtonsDetailHardware,
                (false, true)  => Strings.SystemButtonsDetailBack,
                _              => Strings.SystemButtonsDetailAll,
            };
        }
    }

    public Visibility HardwareButtonsVisibility =>
        _controls is { HasGameBarButton: true } or { HasOemButton: true }
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility GameBarButtonVisibility =>
        _controls?.HasGameBarButton == true ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OemButtonVisibility =>
        _controls?.HasOemButton == true ? Visibility.Visible : Visibility.Collapsed;

    public string GameBarButtonLabel => _controls?.GameBarButtonLabel ?? Strings.GameBarButton;

    public string OemButtonLabel => _controls?.OemButtonLabel ?? Strings.MButton;

    public ObservableCollection<HardwareButtonOption> GameBarOptions { get; }

    public HardwareButtonOption SelectedGameBarOption
    {
        get;
        set
        {
            if (value is null || !Set(ref field, value)) return;

            Notify(nameof(GameBarAction));
            Notify(nameof(GameBarDetail));

            if (_absorbing) return;

            ApplyGameBarSelection(value);
        }
    } = null!;

    public HardwareButtonAction GameBarAction => SelectedGameBarOption.Action;

    public string GameBarDetail =>
        SelectedGameBarOption.CustomAction is not null ? Strings.SystemButtonCustomDetail
        : GameBarAction == HardwareButtonAction.XboxGameBar
            ? (_controls?.GameBarNativeWinG != false ? Strings.GameBarDefaultDetail : Strings.GameBarNonNativeDefaultDetail)
        : _msiCenterRuns && _controls?.EffectiveOemSoftware == OemSoftwareKind.AcerPredatorSense ? Strings.GameBarAcerSenseWarning
        : _msiCenterRuns && _controls?.EffectiveOemSoftware == OemSoftwareKind.MsiCenter ? Strings.GameBarMsiCenterWarning
        : Strings.GameBarCustomDetail;

    public ObservableCollection<HardwareButtonOption> OemButtonOptions { get; }

    public HardwareButtonOption SelectedOemButtonOption
    {
        get;
        set
        {
            if (value is null || !Set(ref field, value)) return;

            Notify(nameof(OemButtonAction));
            Notify(nameof(OemButtonDetail));

            if (_absorbing) return;

            ApplyOemSelection(value);
        }
    } = null!;

    public HardwareButtonAction OemButtonAction => SelectedOemButtonOption.Action;

    public string OemButtonDetail =>
        SelectedOemButtonOption.CustomAction is not null ? Strings.SystemButtonCustomDetail
        : _msiCenterRuns && _controls?.EffectiveOemSoftware == OemSoftwareKind.AcerPredatorSense ? Strings.MButtonAcerSenseWarning
        : _msiCenterRuns && _controls?.EffectiveOemSoftware == OemSoftwareKind.MsiCenter ? Strings.MButtonMsiCenterWarning
        : OemButtonAction switch
        {
            HardwareButtonAction.OpenTsume => Strings.MButtonDetailTsume,
            HardwareButtonAction.Disabled  => Strings.MButtonDetailDisabled,
            _                              => Strings.MButtonDetailCustom,
        };

    public ObservableCollection<BackButtonEditor> BackButtons { get; } = [];

    public Visibility BackButtonsVisibility =>
        _controls?.BackButtons is { Buttons.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public string BackButtonMultiHint =>
        Strings.BackButtonMultiHint(_controls?.BackButtons?.MaxSimultaneousKeys ?? BackButtonBinding.MaxKeys);

    public bool IsControllerAvailable { get; private set => Set(ref field, value); } = true;

    public string Status { get; private set => Set(ref field, value); } = Strings.ControlsSavedPanelOnly;

    /// <summary>True when the Game Bar button is explicitly disabled and should be suppressed.</summary>
    public bool IsGameBarDisabled =>
        SelectedGameBarOption.CustomAction is null && GameBarAction == HardwareButtonAction.Disabled;

    private void ApplyGameBarSelection(HardwareButtonOption option)
    {
        if (option.CustomAction is { } custom)
        {
            _state.GameBarButton = HardwareButtonAction.XboxGameBar;
            _dedicatedActions.AssignSingleHardwareButton(DedicatedActionKeys.XboxButtonCode, custom);
            return;
        }

        _state.GameBarButton = option.Action;

        if (option.Action is HardwareButtonAction.XboxGameBar or HardwareButtonAction.Disabled)
        {
            _dedicatedActions.AssignSingleHardwareButton(DedicatedActionKeys.XboxButtonCode, null);
            _state.Save();
            Notify(nameof(IsGameBarDisabled));
        }
        else if (ToDedicatedAction(option.Action) is { } mapped)
        {
            _dedicatedActions.AssignSingleHardwareButton(DedicatedActionKeys.XboxButtonCode, mapped);
        }
    }

    private void ApplyOemSelection(HardwareButtonOption option)
    {
        if (option.CustomAction is { } custom)
        {
            _dedicatedActions.AssignSingleHardwareButton(DedicatedActionKeys.OemButtonCode, custom);
            return;
        }

        _state.OemButton = option.Action;
        _state.MButton = null;
        _state.MButtonOpensTsume = option.Action == HardwareButtonAction.OpenTsume;

        var target = option.Action == HardwareButtonAction.Disabled ? null : ToDedicatedAction(option.Action);
        _dedicatedActions.AssignSingleHardwareButton(DedicatedActionKeys.OemButtonCode, target);
        _state.Save();
    }

    private void SyncFromDedicatedActions()
    {
        _absorbing = true;

        // Sync Xbox Game Bar button dropdown with Dedicated Actions.
        var xboxEditor = _dedicatedActions.FindSingleButtonAction(DedicatedActionKeys.XboxButtonCode);
        if (xboxEditor is not null)
        {
            var mapped = ToHardwareAction(xboxEditor.Action);
            if (mapped is { } hw && _baseGameBarOptions.FirstOrDefault(o => o.Action == hw) is { } standard)
            {
                ResetOptions(GameBarOptions, _baseGameBarOptions);
                SelectedGameBarOption = standard;
            }
            else
            {
                var custom = new HardwareButtonOption(HardwareButtonAction.XboxGameBar, xboxEditor.Title, xboxEditor.Action);
                ResetOptions(GameBarOptions, _baseGameBarOptions, custom);
                SelectedGameBarOption = custom;
            }
        }
        else
        {
            ResetOptions(GameBarOptions, _baseGameBarOptions);
            var fallback = _state.GameBarButton == HardwareButtonAction.Disabled
                ? HardwareButtonAction.Disabled
                : HardwareButtonAction.XboxGameBar;
            SelectedGameBarOption = GameBarOptions.First(o => o.Action == fallback);
        }

        // Sync M / OEM button dropdown with Dedicated Actions.
        var oemEditor = _dedicatedActions.FindSingleButtonAction(DedicatedActionKeys.OemButtonCode);
        if (oemEditor is not null)
        {
            var mapped = ToHardwareAction(oemEditor.Action);
            if (mapped is { } hw && _baseOemButtonOptions.FirstOrDefault(o => o.Action == hw) is { } standard)
            {
                ResetOptions(OemButtonOptions, _baseOemButtonOptions);
                SelectedOemButtonOption = standard;
            }
            else
            {
                var custom = new HardwareButtonOption(HardwareButtonAction.OpenTsume, oemEditor.Title, oemEditor.Action);
                ResetOptions(OemButtonOptions, _baseOemButtonOptions, custom);
                SelectedOemButtonOption = custom;
            }
        }
        else
        {
            ResetOptions(OemButtonOptions, _baseOemButtonOptions);
            SelectedOemButtonOption = OemButtonOptions.First(o => o.Action == HardwareButtonAction.Disabled);
        }

        _absorbing = false;
        Notify(nameof(IsGameBarDisabled));
        Notify(nameof(GameBarDetail));
        Notify(nameof(OemButtonDetail));
    }

    private static void ResetOptions(
        ObservableCollection<HardwareButtonOption> target,
        IReadOnlyList<HardwareButtonOption> baseline,
        HardwareButtonOption? extra = null)
    {
        var desiredCount = baseline.Count + (extra is not null ? 1 : 0);
        if (target.Count == desiredCount && (extra is null || target[^1] == extra))
            return;

        target.Clear();
        foreach (var opt in baseline) target.Add(opt);
        if (extra is not null) target.Add(extra);
    }

    private static DedicatedAction? ToDedicatedAction(HardwareButtonAction action) => action switch
    {
        HardwareButtonAction.OpenTsume       => DedicatedAction.OpenTsume,
        HardwareButtonAction.XboxGameBar     => DedicatedAction.XboxGameBar,
        HardwareButtonAction.SteamBigPicture => DedicatedAction.SteamBigPicture,
        HardwareButtonAction.TaskManager     => DedicatedAction.TaskManager,
        HardwareButtonAction.TouchKeyboard   => DedicatedAction.TouchKeyboard,
        HardwareButtonAction.Screenshot      => DedicatedAction.Screenshot,
        HardwareButtonAction.ShowDesktop     => DedicatedAction.ShowDesktop,
        _                                    => null,
    };

    private static HardwareButtonAction? ToHardwareAction(DedicatedAction action) => action switch
    {
        DedicatedAction.OpenTsume       => HardwareButtonAction.OpenTsume,
        DedicatedAction.XboxGameBar     => HardwareButtonAction.XboxGameBar,
        DedicatedAction.SteamBigPicture => HardwareButtonAction.SteamBigPicture,
        DedicatedAction.TaskManager     => HardwareButtonAction.TaskManager,
        DedicatedAction.TouchKeyboard   => HardwareButtonAction.TouchKeyboard,
        DedicatedAction.Screenshot      => HardwareButtonAction.Screenshot,
        DedicatedAction.ShowDesktop     => HardwareButtonAction.ShowDesktop,
        _                               => null,
    };

    private void AdoptDevice(DeviceProfile device)
    {
        if (_disposed) return;

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

        Notify(nameof(HasAnyButtons));
        Notify(nameof(SectionVisibility));
        Notify(nameof(SummaryDetail));
        Notify(nameof(HardwareButtonsVisibility));
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
        if (_disposed) return;

        var changesBefore = _changes;
        var controls = _controls;

        var oem = controls?.EffectiveOemSoftware ?? OemSoftwareKind.None;
        var oemRuns = oem switch
        {
            OemSoftwareKind.MsiCenter         => await Task.Run(() => MsiCenter.IsRunning),
            OemSoftwareKind.AcerPredatorSense => await Task.Run(() => AcerSense.IsRunning),
            _                                 => false,
        };
        if (_disposed) return;

        _msiCenterRuns = oemRuns;
        Notify(nameof(GameBarDetail));
        Notify(nameof(OemButtonDetail));

        var backProfile = controls?.BackButtons;
        if (backProfile is null || backProfile.Buttons.Count == 0)
        {
            Status = Strings.ControlsSavedPanelOnly;
            return;
        }

        var controller = await ConnectAsync(backProfile);
        if (_disposed || controller is null) return;

        var readings = new List<(BackButtonEditor Editor, BackButtonBinding Binding)>(BackButtons.Count);
        foreach (var editor in BackButtons)
        {
            var binding = await controller.ReadAsync(editor.Definition);
            if (_disposed) return;

            if (binding is null)
            {
                Lost(controller);
                return;
            }
            readings.Add((editor, binding));
        }

        if (_changes != changesBefore || _writing) return;

        foreach (var (editor, binding) in readings)
            editor.Load(binding);

        Status = oemRuns
            ? (oem == OemSoftwareKind.AcerPredatorSense ? Strings.ControlsAcerSenseRuns : Strings.ControlsMsiCenterRuns)
            : Strings.ControlsSaved;
    }

    private async Task<IBackButtonController?> ConnectAsync(BackButtonsProfile profile)
    {
        if (_disposed) return null;

        if (_controller is null)
        {
            var result = await Task.Run(() => BackButtonControllers.Open(profile));
            if (_disposed)
            {
                result.Controller?.Dispose();
                return null;
            }

            if (_controller is not null)
            {
                result.Controller?.Dispose();
            }
            else
            {
                _controller = result.Controller;

                if (_controller is null)
                {
                    IsControllerAvailable = false;
                    Status = result.Failure switch
                    {
                        BackButtonOpenFailure.FirmwareTooOld => Strings.ControllerFirmwareTooOld(result.FirmwareVersion ?? ""),
                        BackButtonOpenFailure.NotAnswering   => Strings.ControllerNotAnswering,
                        _                                    => Strings.ControllerNotFound,
                    };
                    return null;
                }
            }
        }

        IsControllerAvailable = true;
        return _controller;
    }

    /// <remarks>Only drops <paramref name="failed"/> if it is still the current one, so a stale failure never discards a newer controller.</remarks>
    private void Lost(IBackButtonController failed)
    {
        if (!ReferenceEquals(_controller, failed)) return;

        failed.Dispose();
        _controller = null;
        Status = Strings.ControllerNotAnswering;
    }

    private void OnBackButtonChanged(BackButtonDefinition button, BackButtonBinding binding)
    {
        if (_disposed || _controls?.BackButtons is null) return;

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
            while (!_disposed && _pending.Count > 0)
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
                if (_disposed || controller is null) return;

                var ok = true;
                var anyBound = false;
                foreach (var (button, binding) in batch)
                {
                    ok = await controller.WriteAsync(button, binding);
                    if (_disposed || !ok) break;
                    if (binding.Keys.Count > 0) anyBound = true;
                }

                if (_disposed) return;

                if (ok && anyBound)
                    await controller.EnsureActiveAsync();

                if (_disposed) return;

                if (ok) _unsaved = true;
                else Lost(controller);
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

        try
        {
            while (_writing) await _writer;

            if (_disposed || !_unsaved || _controller is not { } controller) return;

            var saved = await controller.SaveAsync();
            if (_disposed) return;

            if (saved) _unsaved = false;
            else Lost(controller);
        }
        catch (Exception error)
        {
            AsyncCommand.LogError(error);
        }
    }

    public override void FlushPending() => OnClosed();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _performance.PropertyChanged -= OnPerformancePropertyChanged;
        _dedicatedActions.BindingsChanged -= SyncFromDedicatedActions;
        _controller?.Dispose();
        _controller = null;
    }
}

public sealed record HardwareButtonOption(
    HardwareButtonAction Action,
    string Label,
    DedicatedAction? CustomAction = null)
{
    public override string ToString() => Label;
}
