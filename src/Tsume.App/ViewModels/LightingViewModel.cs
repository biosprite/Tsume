using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Devices;
using Tsume.Core.Lighting;

namespace Tsume.App.ViewModels;

/// <summary>
/// The lighting screen for the stick rings and face buttons (or rear logo on Acer Atlas 8). Changes are written live
/// to the controller and persisted once editing settles or when leaving the screen. Runs on the UI thread; controller
/// I/O is overlapped and serialized.
/// </summary>
public sealed class LightingViewModel : LayerViewModel, IDisposable
{
    private static readonly LightingEffect[] EffectOrder =
        [LightingEffect.Off, LightingEffect.Steady, LightingEffect.Breathe, LightingEffect.Rainbow, LightingEffect.Spin];

    /// <summary>Minimum spacing between live Acer <c>0xA4</c> writes while dragging a slider (~25 Hz cap).</summary>
    public static readonly TimeSpan DefaultAcerWriteInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>Idle settle delay before persisting Acer lighting edits to <c>state.json</c>.</summary>
    public static readonly TimeSpan DefaultAcerSaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly PerformanceViewModel _performance;
    private readonly SavedState _state;
    private readonly Func<IAcerLightingDevice?> _findAcer;
    private readonly Func<bool> _isAcerLightingRunning;
    private readonly Action _saveState;
    private readonly TimeSpan _acerWriteMinInterval;
    private readonly TimeSpan _acerSaveDelay;
    private readonly int _resumeInitialDelayMs;
    private readonly int _resumeRetryDelayMs;

    /// <summary>Serializes all Acer <c>0xFF5A</c> connect, apply, reapply, and dispose operations.</summary>
    private readonly SemaphoreSlim _acerGate = new(1, 1);

    private ClawController? _controller;
    private IAcerLightingDevice? _acerHid;
    private CancellationTokenSource? _reapplyCancel;
    private CancellationTokenSource? _acerSaveCancel;
    private long _lastAcerWriteTicks;
    private ushort? _minFirmwareVersion;
    private bool _boardKnown;
    private bool _usesAcerHid;
    private bool _appliedAcerOnStartup;
    private bool _acerStateDirty;
    private bool _disposed;

    public LightingViewModel(
        PerformanceViewModel performance,
        SavedState state,
        Func<IAcerLightingDevice?>? findAcer = null,
        Func<bool>? isAcerLightingRunning = null,
        Action? saveState = null,
        TimeSpan? acerWriteMinInterval = null,
        TimeSpan? acerSaveDelay = null,
        int resumeInitialDelayMs = 1500,
        int resumeRetryDelayMs = 1500)
    {
        _performance = performance;
        _state = state;
        _findAcer = findAcer ?? AcerLightingHid.Find;
        _isAcerLightingRunning = isAcerLightingRunning ?? (() => AcerSense.IsLightingRunning);
        _saveState = saveState ?? state.Save;
        _acerWriteMinInterval = acerWriteMinInterval ?? DefaultAcerWriteInterval;
        _acerSaveDelay = acerSaveDelay ?? DefaultAcerSaveDelay;
        _resumeInitialDelayMs = Math.Max(0, resumeInitialDelayMs);
        _resumeRetryDelayMs = Math.Max(0, resumeRetryDelayMs);

        ToggleSameOnBoth = new Command(() => SameOnBoth = !SameOnBoth);
        ToggleSticksLit = new Command(() => SticksLit = !SticksLit);
        ToggleButtonsLit = new Command(() => ButtonsLit = !ButtonsLit);
        ToggleButtonsSteady = new Command(() => ButtonsSteady = !ButtonsSteady);

        var current = state.AcerLighting ?? StickLighting.Default;
        _absorbing = true;
        Show(current, snapColours: state.AcerLighting is null);
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
            IAcerLightingDevice? acer;
            await _acerGate.WaitAsync();
            try
            {
                if (_disposed || !_usesAcerHid) return;
                acer = await ConnectAcerLockedAsync(refreshIfIncomplete: true);
            }
            finally
            {
                if (_disposed) DisposeAcerLocked();
                _acerGate.Release();
            }

            if (_disposed || !_usesAcerHid) return;

            if (acer is null)
            {
                IsAvailable = false;
                Status = Strings.ControllerNotFound;
                return;
            }

            IsAvailable = true;
            Notify(nameof(ShowsSecondaryZone));

            // 0xFF5A has no read-back command: show the last saved setup unless the user already edited.
            if (_changes == changesBefore && !_writing && _state.AcerLighting is { } saved)
            {
                _absorbing = true;
                Show(saved, snapColours: false);
                _absorbing = false;
            }

            var lightingRuns = await Task.Run(_isAcerLightingRunning);
            if (_disposed || !_usesAcerHid) return;

            Status = lightingRuns ? Strings.LightingAcerSenseRuns : Strings.LightingAcerSaved;
            return;
        }

        var controller = await ConnectAsync();
        if (_disposed || _usesAcerHid || controller is null) return;

        var bytes = await controller.ReadProfileRangeAsync(ProfileMap.ActiveAnimation, ProfileMap.AnimationLength);
        if (_disposed || _usesAcerHid) return;

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

        var oemRuns = await Task.Run(() => MsiCenter.IsRunning);
        if (_disposed || _usesAcerHid) return;

        Status = oemRuns ? Strings.LightingMsiCenterRuns : Strings.LightingSaved;
    }

    /// <summary>Re-applies saved Acer 0xFF5A lighting after sleep/resume or display wake because USB stick RGB resets on sleep.</summary>
    public Task ReapplyAfterResumeAsync() =>
        ReapplyAcerSavedAsync(initialDelayMs: _resumeInitialDelayMs, dropCachedFirst: true);

    private Task ReapplyAcerOnStartupAsync()
    {
        if (_appliedAcerOnStartup) return Task.CompletedTask;
        _appliedAcerOnStartup = true;
        return ReapplyAcerSavedAsync(initialDelayMs: 0, dropCachedFirst: false);
    }

    private void CancelPendingReapply()
    {
        var previous = _reapplyCancel;
        _reapplyCancel = null;
        if (previous is null) return;

        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
    }

    private async Task ReapplyAcerSavedAsync(int initialDelayMs, bool dropCachedFirst)
    {
        if (_disposed || !_usesAcerHid || _state.AcerLighting is null) return;

        CancelPendingReapply();
        var cts = _reapplyCancel = new CancellationTokenSource();
        var token = cts.Token;

        // Edits made before this call are already in `_state.AcerLighting` (or still being written); only an edit made
        // while this re-apply is waiting should win over it. `_changes` counts for the whole session, so snapshot it.
        var changesBefore = _changes;

        try
        {
            if (initialDelayMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(initialDelayMs), token);

            if (_disposed || !_usesAcerHid || token.IsCancellationRequested || _changes != changesBefore || _writing || _queued is not null)
                return;

            if (await Task.Run(_isAcerLightingRunning, token))
                return;

            // USB stick lighting (0502:F901) may enumerate a second or two after boot/resume while the I2C
            // rear logo (0CF2:5130) is already present. Retry briefly until sticks appear so both zones restore once.
            const int maxAttempts = 3;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (_disposed || !_usesAcerHid || token.IsCancellationRequested || _changes != changesBefore || _writing || _queued is not null)
                    return;

                var done = false;
                await _acerGate.WaitAsync(token);
                try
                {
                    if (_disposed || !_usesAcerHid || token.IsCancellationRequested
                        || _changes != changesBefore || _writing || _queued is not null
                        || _state.AcerLighting is not { } saved)
                    {
                        return;
                    }

                    if (dropCachedFirst && attempt == 0)
                        DisposeAcerLocked();

                    var acer = await ConnectAcerLockedAsync(refreshIfIncomplete: true);
                    if (_disposed || !_usesAcerHid || token.IsCancellationRequested
                        || _changes != changesBefore || _writing || _queued is not null)
                    {
                        return;
                    }

                    var isLastAttempt = attempt + 1 == maxAttempts;
                    if (acer is not null && (acer.HasSticks || isLastAttempt))
                    {
                        IsAvailable = true;
                        Notify(nameof(ShowsSecondaryZone));

                        var applied = await Task.Run(() => acer.Apply(saved, force: true));
                        if (_disposed) return;

                        if (applied)
                        {
                            _lastAcerWriteTicks = Stopwatch.GetTimestamp();
                            if (Status == Strings.ControllerNotAnswering || Status == Strings.ControllerNotFound)
                                Status = Strings.LightingAcerSaved;
                            if (acer.HasSticks || isLastAttempt)
                                done = true;
                        }
                        else
                        {
                            // Stale or unresponsive handle during wake settle: drop and re-probe on the next attempt.
                            DisposeAcerLocked();
                        }
                    }
                }
                finally
                {
                    if (_disposed) DisposeAcerLocked();
                    _acerGate.Release();
                }

                if (done || attempt + 1 >= maxAttempts) return;

                if (_resumeRetryDelayMs > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(_resumeRetryDelayMs), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer wake event, a user edit, protocol switch, or disposal.
        }
        finally
        {
            if (ReferenceEquals(_reapplyCancel, cts))
            {
                _reapplyCancel = null;
                cts.Dispose();
            }
        }
    }

    private void Show(StickLighting lighting, bool snapColours = true)
    {
        EffectIndex = Array.IndexOf(EffectOrder, lighting.Effect);
        StickBrightness = lighting.StickBrightness;
        ButtonBrightness = lighting.ButtonBrightness;
        Speed = lighting.Speed;
        SticksLit = lighting.SticksLit;
        ButtonsLit = lighting.ButtonsLit;
        ButtonsSteady = lighting.ButtonsSteady;

        var left = snapColours ? LightingPalette.Snap(lighting.LeftStick) : lighting.LeftStick;
        var right = snapColours ? LightingPalette.Snap(lighting.RightStick) : lighting.RightStick;
        var buttons = snapColours ? LightingPalette.Snap(lighting.Buttons) : lighting.Buttons;

        SameOnBoth = left == right;
        LeftColour = Shown(left);
        RightColour = Shown(right);
        ButtonsColour = Shown(buttons);
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
            CancelPendingReapply();
            _controller?.Dispose();
            _controller = null;
            _unsaved = false;
            _ = DisposeAcerAsync();

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

    private async Task DisposeAcerAsync()
    {
        await _acerGate.WaitAsync();
        try
        {
            DisposeAcerLocked();
        }
        finally
        {
            _acerGate.Release();
        }
    }

    private void DisposeAcerLocked()
    {
        var existing = _acerHid;
        _acerHid = null;
        existing?.Dispose();
    }

    /// <summary>Must be called while holding <see cref="_acerGate"/>.</summary>
    private async Task<IAcerLightingDevice?> ConnectAcerLockedAsync(bool refreshIfIncomplete = false)
    {
        if (_disposed) return null;

        var current = _acerHid;
        var shouldProbe = current is null || (refreshIfIncomplete && !current.HasSticks);
        if (shouldProbe)
        {
            var found = await Task.Run(_findAcer);
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

    private async Task<ClawController?> ConnectAsync()
    {
        if (_disposed || _usesAcerHid) return null;

        if (_controller is null)
        {
            // Only probe MSI USB IDs for the HJC lighting profile block so Acer 0502/0489 controllers
            // are never read or flashed as MSI lighting during early boot or transient 0xFF5A errors.
            var found = await Task.Run(() => ClawController.Find(ClawController.MsiVendorId, ClawController.MsiProductIds));
            if (_disposed || _usesAcerHid)
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

    /// <summary>Waits until all currently queued lighting writes finish. Exposed for deterministic unit tests.</summary>
    public Task WaitForPendingWritesAsync() => _writing ? _writer : Task.CompletedTask;

    /// <summary>Queues the current setup for writing. Only the newest is sent; changes made during a write replace each other.</summary>
    private void Changed()
    {
        if (_absorbing || _disposed) return;

        _changes++;
        _queued = Current;
        CancelPendingReapply();

        if (_writing) return;
        _writing = true;
        _writer = WriteQueuedAsync();
    }

    private async Task WriteQueuedAsync()
    {
        try
        {
            while (!_disposed && _queued is not null)
            {
                if (_usesAcerHid)
                {
                    if (_lastAcerWriteTicks != 0 && _acerWriteMinInterval > TimeSpan.Zero)
                    {
                        var remaining = _acerWriteMinInterval - Stopwatch.GetElapsedTime(_lastAcerWriteTicks);
                        if (remaining > TimeSpan.Zero)
                        {
                            await Task.Delay(remaining);
                            if (_disposed || _queued is null) return;
                        }
                    }

                    var setup = _queued;
                    _queued = null;

                    await _acerGate.WaitAsync();
                    try
                    {
                        if (_disposed || !_usesAcerHid) return;

                        var acer = await ConnectAcerLockedAsync(refreshIfIncomplete: true);
                        if (_disposed || !_usesAcerHid) return;

                        if (acer is null)
                        {
                            IsAvailable = false;
                            Status = Strings.ControllerNotFound;
                            return;
                        }

                        var applied = await Task.Run(() => acer.Apply(setup));
                        if (_disposed || !_usesAcerHid) return;

                        if (!applied)
                        {
                            // Re-enumerate once on stale/reset handle so the very first edit after sleep still lands.
                            DisposeAcerLocked();
                            acer = await ConnectAcerLockedAsync(refreshIfIncomplete: true);
                            if (_disposed || !_usesAcerHid) return;

                            if (acer is not null)
                            {
                                applied = await Task.Run(() => acer.Apply(setup, force: true));
                                if (_disposed || !_usesAcerHid) return;
                            }
                        }

                        if (applied)
                        {
                            _lastAcerWriteTicks = Stopwatch.GetTimestamp();
                            Notify(nameof(ShowsSecondaryZone));
                            if (Status == Strings.ControllerNotAnswering || Status == Strings.ControllerNotFound)
                                Status = Strings.LightingAcerSaved;
                            MarkAcerStateDirty(setup);
                        }
                        else
                        {
                            DisposeAcerLocked();
                            Status = Strings.ControllerNotAnswering;
                        }
                    }
                    finally
                    {
                        if (_disposed) DisposeAcerLocked();
                        _acerGate.Release();
                    }

                    continue;
                }

                var msiSetup = _queued;
                _queued = null;

                var controller = await ConnectAsync();
                if (_disposed || _usesAcerHid || controller is null) return;

                var wrote = await controller.WriteLightingAsync(ProfileMap.ActiveAnimation, msiSetup.Encode(), _minFirmwareVersion, _boardKnown);
                if (_disposed || _usesAcerHid) return;

                if (wrote) _unsaved = true;
                else Lost(controller);
            }
        }
        finally
        {
            _writing = false;
        }
    }

    private void MarkAcerStateDirty(StickLighting setup)
    {
        _state.AcerLighting = setup;
        _acerStateDirty = true;

        var previous = _acerSaveCancel;
        _acerSaveCancel = null;
        if (previous is not null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
            previous.Dispose();
        }

        if (_acerSaveDelay <= TimeSpan.Zero)
        {
            FlushAcerStateSave();
            return;
        }

        var cts = _acerSaveCancel = new CancellationTokenSource();
        _ = SaveAcerStateAfterDelayAsync(cts);
    }

    private async Task SaveAcerStateAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_acerSaveDelay, cts.Token);
            if (_disposed || cts.IsCancellationRequested) return;
            FlushAcerStateSave();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_acerSaveCancel, cts))
            {
                _acerSaveCancel = null;
                cts.Dispose();
            }
        }
    }

    private void FlushAcerStateSave()
    {
        var pending = _acerSaveCancel;
        _acerSaveCancel = null;
        if (pending is not null)
        {
            try { pending.Cancel(); }
            catch (ObjectDisposedException) { }
            pending.Dispose();
        }

        if (!_acerStateDirty) return;
        _acerStateDirty = false;
        _saveState();
    }

    /// <summary>After the last write lands, saves MSI lighting to controller flash (or flushes Acer lighting to state.json).</summary>
    protected override async void OnClosed()
    {
        try
        {
            while (_writing) await _writer;

            if (_disposed) return;

            if (_usesAcerHid)
            {
                FlushAcerStateSave();
                return;
            }

            if (!_unsaved || _controller is not { } controller) return;

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
        CancelPendingReapply();
        FlushAcerStateSave();
        _performance.PropertyChanged -= OnPerformancePropertyChanged;

        if (_acerGate.Wait(0))
        {
            try { DisposeAcerLocked(); }
            finally { _acerGate.Release(); }
        }

        _controller?.Dispose();
        _controller = null;
    }
}
