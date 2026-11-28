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
    private readonly SavedState _state;
    private ClawController? _controller;
    private AcerLightingHid? _acerHid;
    private ushort? _minFirmwareVersion;
    private bool _boardKnown;
    private bool _usesAcerHid;
    private bool _appliedAcerOnStartup;
    private bool _disposed;

    public LightingViewModel(PerformanceViewModel performance, SavedState state)
    {
        _performance = performance;
        _state = state;

        ToggleSameOnBoth = new Command(() => SameOnBoth = !SameOnBoth);
        ToggleSticksLit = new Command(() => SticksLit = !SticksLit);
        ToggleButtonsLit = new Command(() => ButtonsLit = !ButtonsLit);
        ToggleButtonsSteady = new Command(() => ButtonsSteady = !ButtonsSteady);

        var current = state.AcerLighting ?? StickLighting.Default;
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

    /// <summary>False on Acer 0xFF5A lighting where one colour controls both stick rings together.</summary>
    public bool SupportsPerStickColour => !_usesAcerHid;

    public string LeftLabel => !_usesAcerHid && !SameOnBoth ? Strings.LeftStick : Strings.BothSticks;

    public bool ShowsRightPicker => SupportsPerStickColour && !SameOnBoth;

    /// <summary>Secondary zone: face buttons on MSI, rear Predator logo on Acer Atlas 8.</summary>
    public bool ShowsSecondaryZone => !_usesAcerHid || _acerHid is null || _acerHid.HasLogo;
    public string SecondaryZoneHeading => _usesAcerHid ? Strings.LogoHeading : Strings.ButtonsHeading;
    public string SecondaryZoneToggleLabel => _usesAcerHid ? Strings.LogoLights : Strings.ButtonLights;
    public string SecondaryZoneToggleDetail => _usesAcerHid ? Strings.LogoLightsDetail : Strings.ButtonLightsDetail;
    public string SecondaryZoneSteadyDetail => _usesAcerHid ? Strings.LogoSteadyDetail : Strings.ButtonsSteadyDetail;
    public string SecondaryZoneColourLabel => _usesAcerHid ? Strings.RearLogo : Strings.FaceButtons;

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

    /// <summary>Shown only where it has an effect: on MSI, Spin holds buttons steady; on Acer, the rear logo breathes unless held steady.</summary>
    public bool ShowsButtonsSteady =>
        Effect is LightingEffect.Breathe or LightingEffect.Rainbow
        || (_usesAcerHid && Effect == LightingEffect.Spin);

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

        if (_usesAcerHid)
        {
            var acer = await ConnectAcerAsync(refreshIfIncomplete: true);
            if (_disposed) return;

            if (acer is not null)
            {
                Notify(nameof(ShowsSecondaryZone));

                // 0xFF5A has no read-back command: show the last saved setup unless the user already edited.
                if (_changes == changesBefore && !_writing && _state.AcerLighting is { } saved)
                {
                    _absorbing = true;
                    Show(saved);
                    _absorbing = false;
                }

                var lightingRuns = await Task.Run(() => AcerSense.IsLightingRunning);
                if (_disposed) return;

                Status = lightingRuns ? Strings.LightingAcerSenseRuns : Strings.LightingAcerSaved;
                return;
            }
        }

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

        var oemRuns = _usesAcerHid
            ? await Task.Run(() => AcerSense.IsLightingRunning)
            : await Task.Run(() => MsiCenter.IsRunning);
        if (_disposed) return;

        Status = oemRuns
            ? (_usesAcerHid ? Strings.LightingAcerSenseRuns : Strings.LightingMsiCenterRuns)
            : Strings.LightingSaved;
    }

    /// <summary>Re-applies saved Acer 0xFF5A lighting after sleep/resume because USB stick RGB resets on sleep.</summary>
    public Task ReapplyAfterResumeAsync() => ReapplyAcerSavedAsync(initialDelayMs: 1500, dropCachedFirst: true);

    private Task ReapplyAcerOnStartupAsync()
    {
        if (_appliedAcerOnStartup) return Task.CompletedTask;
        _appliedAcerOnStartup = true;
        return ReapplyAcerSavedAsync(initialDelayMs: 0, dropCachedFirst: false);
    }

    private async Task ReapplyAcerSavedAsync(int initialDelayMs, bool dropCachedFirst)
    {
        if (_disposed || !_usesAcerHid || _state.AcerLighting is not { } saved) return;

        if (initialDelayMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(initialDelayMs));
            if (_disposed) return;
        }

        if (_changes > 0 || _writing) return;
        if (await Task.Run(() => AcerSense.IsLightingRunning) || _disposed) return;

        if (dropCachedFirst)
        {
            _acerHid?.Dispose();
            _acerHid = null;
        }

        // USB stick lighting (0502:F901) may enumerate a second or two after boot/resume while the I2C
        // rear logo (0CF2:5130) is already present. Retry briefly until sticks appear so both zones restore.
        AcerLightingHid? acer = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            acer = await ConnectAcerAsync(refreshIfIncomplete: true);
            if (_disposed || _changes > 0 || _writing) return;
            if (acer is { HasSticks: true }) break;

            if (attempt + 1 < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
                if (_disposed || _changes > 0 || _writing) return;
            }
        }

        if (_disposed || acer is null || _changes > 0 || _writing) return;
        Notify(nameof(ShowsSecondaryZone));

        await Task.Run(() => acer.Apply(saved));
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
        var usesAcerHid = string.Equals(device.Controls?.LightingProtocol, ControlsProfile.AcerHidLighting, StringComparison.Ordinal);

        if (_boardKnown == boardKnown && _minFirmwareVersion == minFirmware && _usesAcerHid == usesAcerHid) return;

        var protocolChanged = _usesAcerHid != usesAcerHid;
        _boardKnown = boardKnown;
        _minFirmwareVersion = minFirmware;
        _usesAcerHid = usesAcerHid;

        if (protocolChanged)
        {
            _acerHid?.Dispose();
            _acerHid = null;
            Notify(nameof(SupportsPerStickColour));
            Notify(nameof(LeftLabel));
            Notify(nameof(ShowsRightPicker));
            Notify(nameof(ShowsSecondaryZone));
            Notify(nameof(ShowsButtonsSteady));
            Notify(nameof(SecondaryZoneHeading));
            Notify(nameof(SecondaryZoneToggleLabel));
            Notify(nameof(SecondaryZoneToggleDetail));
            Notify(nameof(SecondaryZoneSteadyDetail));
            Notify(nameof(SecondaryZoneColourLabel));
            _ = RefreshAfterAdoptAsync();
            if (_usesAcerHid) _ = ReapplyAcerOnStartupAsync();
        }
        else if (_controller is not null && _controller.IsUnsupportedProfileFirmware(_minFirmwareVersion, _boardKnown))
        {
            _controller.Dispose();
            _controller = null;
            _ = RefreshAfterAdoptAsync();
        }
        else if (_controller is null && _acerHid is null && !IsAvailable)
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

    private async Task<AcerLightingHid?> ConnectAcerAsync(bool refreshIfIncomplete = false)
    {
        if (_disposed) return null;

        var current = _acerHid;
        var shouldProbe = current is null || (refreshIfIncomplete && !current.HasSticks);
        if (shouldProbe)
        {
            var found = await Task.Run(AcerLightingHid.Find);
            if (_disposed)
            {
                found?.Dispose();
                return null;
            }

            if (_acerHid is null)
            {
                _acerHid = found;
            }
            else if (found is not null && ((found.HasSticks && !_acerHid.HasSticks) || found.ZoneCount > _acerHid.ZoneCount))
            {
                _acerHid.Dispose();
                _acerHid = found;
            }
            else
            {
                found?.Dispose();
            }
        }

        if (_acerHid is not null) IsAvailable = true;
        return _acerHid;
    }

    private void LostAcer(AcerLightingHid failed)
    {
        if (!ReferenceEquals(_acerHid, failed)) return;

        failed.Dispose();
        _acerHid = null;
        Status = Strings.ControllerNotAnswering;
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

                if (_usesAcerHid)
                {
                    var acer = await ConnectAcerAsync(refreshIfIncomplete: true);
                    if (_disposed) return;

                    if (acer is not null)
                    {
                        var applied = await Task.Run(() => acer.Apply(setup));
                        if (_disposed) return;

                        if (applied)
                        {
                            _state.AcerLighting = setup;
                            _state.Save();
                        }
                        else
                        {
                            LostAcer(acer);
                        }

                        continue;
                    }
                }

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
        _acerHid?.Dispose();
        _acerHid = null;
        _controller?.Dispose();
        _controller = null;
    }
}
