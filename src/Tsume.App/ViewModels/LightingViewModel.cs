using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Devices;
using Tsume.Core.Lighting;

namespace Tsume.App.ViewModels;

/// <summary>
/// The lighting screen for the stick rings and face buttons. Changes are written live to the controller's RAM
/// and saved to its flash once, on leaving the screen. Runs on the UI thread; controller I/O is overlapped.
/// </summary>
public sealed class LightingViewModel : LayerViewModel, IDisposable
{
    private static readonly LightingEffect[] EffectOrder =
        [LightingEffect.Off, LightingEffect.Steady, LightingEffect.Breathe, LightingEffect.Rainbow, LightingEffect.Spin];

    private readonly PerformanceViewModel _performance;
    private ClawController? _controller;
    private ushort? _minFirmwareVersion;
    private bool _boardKnown;
    private bool _disposed;

    public LightingViewModel(PerformanceViewModel performance)
    {
        _performance = performance;

        ToggleSameOnBoth = new Command(() => SameOnBoth = !SameOnBoth);
        ToggleSticksLit = new Command(() => SticksLit = !SticksLit);
        ToggleButtonsLit = new Command(() => ButtonsLit = !ButtonsLit);
        ToggleButtonsSteady = new Command(() => ButtonsSteady = !ButtonsSteady);

        var current = StickLighting.Default;
        _absorbing = true;
        Show(current);
        _absorbing = false;

        _performance.PropertyChanged += OnPerformancePropertyChanged;
        AdoptDevice(_performance.Device);
    }

    private void OnPerformancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PerformanceViewModel.Device)) AdoptDevice(_performance.Device);
    }

    public IReadOnlyList<string> Effects { get; } =
        [Strings.EffectOff, Strings.EffectSteady, Strings.EffectBreathe, Strings.EffectRainbow, Strings.EffectSpin];

    /// <summary>The preset colour swatches.</summary>
    public IReadOnlyList<Color> Colours { get; } = [.. LightingPalette.Swatches.Select(Shown)];

    private static Color Shown(Rgb colour) => Color.FromRgb(colour.R, colour.G, colour.B);
    private static Rgb Sent(Color colour) => new(colour.R, colour.G, colour.B);

    public int EffectIndex
    {
        get;
        set
        {
            if (!Set(ref field, Math.Clamp(value, 0, EffectOrder.Length - 1))) return;
            Notify(nameof(Effect));
            Notify(nameof(IsLit));
            Notify(nameof(UsesColours));
            Notify(nameof(IsAnimated));
            Notify(nameof(ShowsButtonsSteady));
            Notify(nameof(ShowsButtonColour));
            Notify(nameof(TileDetail));
            Changed();
        }
    }

    public LightingEffect Effect => EffectOrder[EffectIndex];

    /// <summary>True unless the effect is Off; the main-screen tile glows while it is.</summary>
    public bool IsLit => Effect != LightingEffect.Off;

    /// <summary>Rainbow makes its own colours; Off has none to choose.</summary>
    public bool UsesColours => Effect is LightingEffect.Steady or LightingEffect.Breathe or LightingEffect.Spin;

    public bool IsAnimated => Effect is LightingEffect.Breathe or LightingEffect.Rainbow or LightingEffect.Spin;

    public string TileDetail => Effects[EffectIndex];

    /// <summary>Lowest brightness a slider allows; fully dark is done with the group's switch.</summary>
    public static double MinBrightness => 1;

    public double StickBrightness
    {
        get;
        set { if (Set(ref field, Math.Round(Math.Clamp(value, MinBrightness, 100)))) Changed(); }
    }

    public double ButtonBrightness
    {
        get;
        set { if (Set(ref field, Math.Round(Math.Clamp(value, MinBrightness, 100)))) Changed(); }
    }

    public static double MinSpeed => StickLighting.MinSpeed;
    public static double MaxSpeed => StickLighting.MaxSpeed;

    public double Speed
    {
        get;
        set { if (Set(ref field, Math.Round(Math.Clamp(value, MinSpeed, MaxSpeed)))) Changed(); }
    }

    public Color LeftColour
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            if (SameOnBoth) RightColour = value;
            Changed();
        }
    }

    public Color RightColour
    {
        get;
        set { if (Set(ref field, value)) Changed(); }
    }

    public Color ButtonsColour
    {
        get;
        set { if (Set(ref field, value)) Changed(); }
    }

    public ICommand ToggleSameOnBoth { get; }

    /// <summary>The right stick takes the left stick's colour, and its own picker is hidden.</summary>
    public bool SameOnBoth
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(ShowsRightPicker));
            Notify(nameof(LeftLabel));
            if (value) RightColour = LeftColour;
        }
    }

    public string LeftLabel => SameOnBoth ? Strings.BothSticks : Strings.LeftStick;

    public bool ShowsRightPicker => !SameOnBoth;

    public ICommand ToggleSticksLit { get; }

    /// <summary>Off: the rings around the sticks stay dark.</summary>
    public bool SticksLit
    {
        get;
        set
        {
            if (Set(ref field, value)) Changed();
        }
    } = true;

    public ICommand ToggleButtonsLit { get; }

    /// <summary>Off: A B X Y stay dark.</summary>
    public bool ButtonsLit
    {
        get;
        set
        {
            if (Set(ref field, value)) Changed();
        }
    } = true;

    public ICommand ToggleButtonsSteady { get; }

    /// <summary>A B X Y hold their colour while the sticks breathe or cycle.</summary>
    public bool ButtonsSteady
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(ShowsButtonColour));
            Changed();
        }
    }

    /// <summary>Shown only where it has an effect: in Spin the buttons are steady already.</summary>
    public bool ShowsButtonsSteady => Effect is LightingEffect.Breathe or LightingEffect.Rainbow;

    /// <summary>Rainbow has no colour to choose, unless the buttons are held steady.</summary>
    public bool ShowsButtonColour => UsesColours || (Effect == LightingEffect.Rainbow && ButtonsSteady);

    /// <summary>Status line: where changes are saved, or why they cannot be.</summary>
    public string Status { get; private set => Set(ref field, value); } = Strings.LightingSaved;

    /// <summary>False when there is no controller to talk to; the controls grey out.</summary>
    public bool IsAvailable { get; private set => Set(ref field, value); } = true;

    private StickLighting Current => new(
        Effect,
        Sent(LeftColour),
        Sent(SameOnBoth ? LeftColour : RightColour),
        Sent(ButtonsColour),
        (int)StickBrightness,
        (int)ButtonBrightness,
        (int)Speed,
        SticksLit,
        ButtonsLit,
        ButtonsSteady && ShowsButtonsSteady);

    private bool _absorbing;
    private int _changes;
    private Task? _reading;

    protected override void OnOpened() => _ = RefreshAsync();

    /// <summary>Reads the controller's current lighting. Also called as the panel opens, to keep the tile current.</summary>
    public Task RefreshAsync() => _reading is { IsCompleted: false } ? _reading : _reading = ReadAsync();

    private async Task ReadAsync()
    {
        if (_disposed) return;

        var changesBefore = _changes;

        var controller = await ConnectAsync();
        if (_disposed || controller is null) return;

        var bytes = await controller.ReadProfileRangeAsync(ProfileMap.ActiveAnimation, ProfileMap.AnimationLength);
        if (_disposed) return;

        if (bytes is null)
        {
            Lost(controller);
            return;
        }

        // A change made during the read wins over what was read.
        if (_changes != changesBefore || _writing) return;

        _absorbing = true;
        Show(StickLighting.Decode(bytes));
        _absorbing = false;

        var msiCenterRuns = await Task.Run(() => MsiCenter.IsRunning);
        if (_disposed) return;

        Status = msiCenterRuns ? Strings.LightingMsiCenterRuns : Strings.LightingSaved;
    }

    private void Show(StickLighting lighting)
    {
        EffectIndex = Array.IndexOf(EffectOrder, lighting.Effect);
        StickBrightness = lighting.StickBrightness;
        ButtonBrightness = lighting.ButtonBrightness;
        Speed = lighting.Speed;
        SticksLit = lighting.SticksLit;
        ButtonsLit = lighting.ButtonsLit;
        ButtonsSteady = lighting.ButtonsSteady;

        var left = LightingPalette.Snap(lighting.LeftStick);
        var right = LightingPalette.Snap(lighting.RightStick);

        SameOnBoth = left == right;
        LeftColour = Shown(left);
        RightColour = Shown(right);
        ButtonsColour = Shown(LightingPalette.Snap(lighting.Buttons));
    }

    private void AdoptDevice(DeviceProfile device)
    {
        var boardKnown = !ReferenceEquals(device, DeviceProfile.Unknown) && device.Controls is not null;
        var minFirmware = device.Controls?.BackButtons?.MinFirmwareVersion;
        if (_boardKnown == boardKnown && _minFirmwareVersion == minFirmware) return;

        _boardKnown = boardKnown;
        _minFirmwareVersion = minFirmware;

        if (_controller is not null && _controller.IsUnsupportedProfileFirmware(_minFirmwareVersion, _boardKnown))
        {
            _controller.Dispose();
            _controller = null;
            _ = RefreshAfterAdoptAsync();
        }
        else if (_controller is null && !IsAvailable)
        {
            _ = RefreshAfterAdoptAsync();
        }
    }

    private async Task RefreshAfterAdoptAsync()
    {
        if (_reading is { IsCompleted: false } pending)
            await pending;

        await RefreshAsync();
    }

    private async Task<ClawController?> ConnectAsync()
    {
        if (_disposed) return null;

        if (_controller is null)
        {
            var found = await Task.Run(ClawController.Find);
            if (_disposed)
            {
                found?.Dispose();
                return null;
            }

            if (_controller is not null)
            {
                found?.Dispose();
            }
            else if (found is not null && found.IsUnsupportedProfileFirmware(_minFirmwareVersion, _boardKnown))
            {
                var version = found.FirmwareVersion;
                var raw = found.FirmwareVersionRaw;
                found.Dispose();
                IsAvailable = false;
                Status = raw == 0 ? Strings.ControllerNotAnswering : Strings.ControllerFirmwareTooOld(version);
                return null;
            }
            else
            {
                _controller = found;
            }
        }

        IsAvailable = _controller is not null;
        if (_controller is null) Status = Strings.ControllerNotFound;

        return _controller;
    }

    /// <summary>The controller stopped answering (asleep or gone); the next attempt finds it again.</summary>
    /// <remarks>Only drops <paramref name="failed"/> if it is still the current one, so a stale failure never discards a newer controller.</remarks>
    private void Lost(ClawController failed)
    {
        if (!ReferenceEquals(_controller, failed)) return;

        failed.Dispose();
        _controller = null;
        Status = Strings.ControllerNotAnswering;
    }

    private StickLighting? _queued;
    private bool _writing;
    private Task _writer = Task.CompletedTask;
    private bool _unsaved;

    /// <summary>Queues the current setup for writing. Only the newest is sent; changes made during a write replace each other.</summary>
    private void Changed()
    {
        if (_absorbing || _disposed) return;

        _changes++;
        _queued = Current;

        if (_writing) return;
        _writing = true;
        _writer = WriteQueuedAsync();
    }

    private async Task WriteQueuedAsync()
    {
        try
        {
            while (!_disposed && _queued is { } setup)
            {
                _queued = null;

                var controller = await ConnectAsync();
                if (_disposed || controller is null) return;

                var wrote = await controller.WriteLightingAsync(ProfileMap.ActiveAnimation, setup.Encode(), _minFirmwareVersion, _boardKnown);
                if (_disposed) return;

                if (wrote) _unsaved = true;
                else Lost(controller);
            }
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>After the last write lands, saves the lighting to the controller's flash so it survives a restart.</summary>
    protected override async void OnClosed()
    {
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
        _controller?.Dispose();
        _controller = null;
    }
}
