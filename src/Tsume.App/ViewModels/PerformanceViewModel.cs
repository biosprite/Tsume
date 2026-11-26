using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Ipc;
using Tsume.Core.Performance;
using Tsume.Core.Telemetry;

namespace Tsume.App.ViewModels;

/// <summary>
/// Performance mode, manual power limits, and the fans. A mode change asks the helper to set the
/// firmware, so it takes effect only while the helper is running. Windows' own power mode is left alone.
/// </summary>
public sealed class PerformanceViewModel : Observable
{
    private readonly HelperClient _helper;
    private readonly SavedState _state;

    /// <summary>Waits for rapid mode switches or slider drags to settle before touching the firmware.</summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>True while values are being copied in from the hardware, so they are not sent straight back.</summary>
    private bool _absorbing;

    /// <summary>Counts user changes, so a reading requested before a change does not undo it.</summary>
    private int _changes;

    private bool _applying;
    private bool _applyAgain;
    private bool _reconnecting;

    public PerformanceViewModel(HelperClient helper, SavedState state)
    {
        _helper = helper;
        _state = state;
        Device = state.Device ?? DeviceProfile.Unknown;
        _manualChosen = state.Manual is not null || Device.ManualDefault is not null;

        _settle.Tick += (_, _) => { _settle.Stop(); Apply(); };

        _absorbing = true;
        Mode = state.Mode;
        var manual = state.Manual ?? Device.ManualDefault ?? default;
        if (Device.ManualRange is { } range) manual = range.Clamp(manual);
        Sustained = manual.Sustained;
        Boost = manual.Boost;
        FastBoost = manual.FastBoost ?? Device.ManualRange?.MinFastBoostFor(manual.Boost) ?? 0;
        _absorbing = false;

        CycleFan = new Command(NextFanMode);
    }

    /// <summary>False until the manual sliders hold a chosen value: the user's, or the device's default.</summary>
    private bool _manualChosen;

    /// <summary>
    /// What this machine offers: modes, their limits, and the manual range. Reported by the helper
    /// each time the panel opens; until then, the profile saved from the previous run is used.
    /// </summary>
    public DeviceProfile Device { get; private set; }

    private void Adopt(DeviceProfile device)
    {
        if (device.Matches(Device)) return;

        Device = device;
        _state.Device = device;
        _state.Save();
        Notify(nameof(Device));

        // Bindings re-read everything from the profile. Values the controls push back
        // meanwhile are not user choices and must not reach the firmware.
        var previousManual = Manual;
        var before = _absorbing;
        _absorbing = true;

        Notify(nameof(HasFastBoost));
        Notify(nameof(MinWatts));
        Notify(nameof(MinBoostWatts));
        Notify(nameof(MinFastBoostWatts));
        Notify(nameof(MaxSustainedWatts));
        Notify(nameof(MaxBoostWatts));
        Notify(nameof(MaxFastBoostWatts));

        if (!_manualChosen && device.ManualDefault is { } start)
        {
            var clamped = device.ManualRange?.Clamp(start) ?? start;
            Sustained = clamped.Sustained;
            Boost = clamped.Boost;
            if (clamped.FastBoost is { } fast) FastBoost = fast;
            _manualChosen = true;
        }
        else if (_manualChosen && device.ManualRange is { } range)
        {
            var clamped = range.Clamp(previousManual);
            Sustained = clamped.Sustained;
            Boost = clamped.Boost;
            if (clamped.FastBoost is { } fast) FastBoost = fast;
        }

        Notify(nameof(Modes));
        Notify(nameof(ModeIndex));
        Notify(nameof(ShowsLimitSliders));
        Notify(nameof(FanDetail));
        Notify(nameof(FanLimits));
        if (FanLimits is null) FanCurve = null;
        NotifyLimits();
        Notify(nameof(BatteryCapNote));
        ReadPowerSource();

        _absorbing = before;
    }

    /// <summary>Segment labels: the modes this machine offers, quietest first.</summary>
    public IReadOnlyList<string> Modes => [.. Device.Modes.Select(preset => Label(preset.Mode))];

    /// <summary>A mode's display name.</summary>
    public static string Label(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Endurance   => Strings.ModeEndurance,
        PerformanceMode.Balanced    => Strings.ModeBalanced,
        PerformanceMode.Performance => Strings.ModePerformance,
        PerformanceMode.Manual      => Strings.ModeManual,
        _                           => mode.ToString(),
    };

    /// <summary>The mode's index among those the machine offers; bound to the segmented control.</summary>
    public int ModeIndex
    {
        get
        {
            for (var i = 0; i < Device.Modes.Count; i++)
                if (Device.Modes[i].Mode == Mode) return i;

            return 0;   // mode not offered by this machine: only until the helper reports the one in force
        }
        set
        {
            if (value >= 0 && value < Device.Modes.Count) Mode = Device.Modes[value].Mode;
        }
    }

    /// <summary>Steps to the next performance mode this machine offers, wrapping back to the first.</summary>
    public void CycleMode()
    {
        if (Device.Modes.Count < 2) return;
        ModeIndex = (ModeIndex + 1) % Device.Modes.Count;
    }

    public PerformanceMode Mode
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;

            Notify(nameof(ModeIndex));
            Notify(nameof(IsManual));
            Notify(nameof(ShowsLimitSliders));
            NotifyLimits();

            if (_absorbing) return;

            _changes++;
            ScheduleApply();
        }
    }

    public bool IsManual => Mode == PerformanceMode.Manual;

    /// <summary>The sliders show only in Manual mode, and only on a machine whose limits can be set.</summary>
    public bool ShowsLimitSliders => IsManual && Device.ManualRange is not null;

    /// <summary>True on 3-tier AMD devices that expose an independent Fast Boost (FPPT) slider.</summary>
    public bool HasFastBoost => Device.ManualRange?.HasFastBoost == true;

    // Slider ranges, as doubles to bind to Slider.Minimum/Maximum. On battery on a machine with a battery cap the
    // maxima stop at the cap, as MSI Center M's battery sliders do; on every other machine they never move.
    public double MinWatts => Device.ManualRange?.Min ?? 0;
    public double MinBoostWatts => Device.ManualRange?.MinBoost ?? 0;
    public double MinFastBoostWatts => Device.ManualRange?.MinFastBoost ?? 0;
    public double MaxSustainedWatts => SliderCap is { } cap
        ? Math.Min(Device.ManualRange?.EffectiveMaxSustained ?? 1, cap.Sustained)
        : Device.ManualRange?.EffectiveMaxSustained ?? 1;
    public double MaxBoostWatts => SliderCap is { } cap
        ? Math.Min(Device.ManualRange?.EffectiveMaxBoost ?? 1, cap.Boost)
        : Device.ManualRange?.EffectiveMaxBoost ?? 1;
    public double MaxFastBoostWatts => SliderCap is { FastBoost: { } capFast }
        ? Math.Min(Device.ManualRange?.MaxFastBoost ?? 1, capFast)
        : Device.ManualRange?.MaxFastBoost ?? 1;

    /// <summary>The battery cap while the sliders are held to it; null on wall power and on a machine without one.</summary>
    private PowerLimits? SliderCap => _onBattery ? Device.BatteryCap : null;

    // What the user chose. The sliders show these, lowered to the battery cap while it applies, and the helper
    // is always sent these and lowers them the same way, so wall-power choices survive a spell on battery.
    private double _sustained;
    private double _boost;
    private double _fastBoost;

    /// <summary>What the sliders show while the battery cap applies: what the helper writes for the user's limits.</summary>
    private PowerLimits Shown => Device.CapForBattery(Manual);

    private double MinBoostFor(double sustained) =>
        Device.ManualRange is { } range ? range.MinBoostFor((int)sustained) : sustained;

    private double MinFastBoostFor(double boost) =>
        Device.ManualRange is { HasFastBoost: true } range ? range.MinFastBoostFor((int)boost) : boost;

    public double Sustained
    {
        get => SliderCap is null ? _sustained : Shown.Sustained;
        set
        {
            var rounded = Math.Round(value);
            if (Device.ManualRange is { } range)
                rounded = Math.Clamp(rounded, range.Min, range.EffectiveMaxSustained);

            // Only for the user's own moves: a value adopted from the helper or a new profile is stored as it is.
            if (SliderCap is not null && !_absorbing)
            {
                // The user cannot go past the battery slider's end, and a value equal to the one already shown
                // (the user's own, capped) is not a new choice: their wall-power value stays.
                rounded = Math.Min(rounded, MaxSustainedWatts);
                if (rounded == Sustained)
                {
                    if (rounded != value) Notify();
                    return;
                }
            }

            if (!Set(ref _sustained, rounded))
            {
                if (rounded != value) Notify();
                return;
            }

            var minBoost = MinBoostFor(_sustained);
            if (Boost < minBoost) Boost = minBoost;
            OnManualChanged();
        }
    }

    public double Boost
    {
        get => SliderCap is null ? _boost : Shown.Boost;
        set
        {
            var rounded = Math.Round(value);
            var floor = MinBoostFor(Sustained);
            var clamped = Device.ManualRange is { } range
                ? Math.Clamp(rounded, floor, range.EffectiveMaxBoost)
                : Math.Max(rounded, floor);

            if (SliderCap is not null && !_absorbing)
            {
                clamped = Math.Min(clamped, MaxBoostWatts);
                if (clamped == Boost)
                {
                    if (clamped != value) Notify();
                    return;
                }
            }

            if (!Set(ref _boost, clamped))
            {
                if (clamped != value) Notify();
                return;
            }

            if (HasFastBoost)
            {
                var minFast = MinFastBoostFor(_boost);
                if (FastBoost < minFast) FastBoost = minFast;
            }
            OnManualChanged();
        }
    }

    public double FastBoost
    {
        get => SliderCap is null ? _fastBoost : Shown.FastBoost ?? _fastBoost;
        set
        {
            var rounded = Math.Round(value);
            var clamped = Device.ManualRange is { MaxFastBoost: { } maxFast } range
                ? Math.Clamp(rounded, range.MinFastBoostFor((int)Boost), maxFast)
                : rounded;

            if (SliderCap is not null && !_absorbing)
            {
                clamped = Math.Min(clamped, MaxFastBoostWatts);
                if (clamped == FastBoost)
                {
                    if (clamped != value) Notify();
                    return;
                }
            }

            if (Set(ref _fastBoost, clamped))
                OnManualChanged();
            else if (clamped != value)
                Notify();
        }
    }

    /// <summary>The user's own limits, as saved and as sent to the helper: never the battery-capped ones shown.</summary>
    private PowerLimits Manual => HasFastBoost
        ? new((int)_sustained, (int)_boost, (int)_fastBoost)
        : new((int)_sustained, (int)_boost);

    private void OnManualChanged()
    {
        NotifyLimits();
        // With no range there are no sliders, so a change here is not a user choice.
        if (_absorbing || Device.ManualRange is null) return;

        _changes++;
        _manualChosen = true;
        if (!IsManual) return;

        ScheduleApply();
    }

    private void ScheduleApply()
    {
        _applyAgain = false;
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>The user's limits for a mode, set on the performance presets page; null if it keeps the machine's.</summary>
    public PowerLimits? CustomFor(PerformanceMode mode) =>
        _state.Presets is { } chosen && chosen.TryGetValue(mode, out var own) ? own : null;

    /// <summary>The same on battery, on a machine whose limits differ there.</summary>
    public PowerLimits? CustomOnBatteryFor(PerformanceMode mode) =>
        _state.BatteryPresets is { } chosen && chosen.TryGetValue(mode, out var own) ? own : null;

    /// <summary>Called after the presets page changed and saved these modes' limits; re-applies the mode in force if it is among them.</summary>
    public void OnPresetsChanged(IReadOnlyCollection<PerformanceMode> modes)
    {
        NotifyLimits();
        if (!modes.Contains(Mode)) return;

        _changes++;
        Apply();
    }

    /// <summary>The limits the helper writes for the mode, on battery or wall power.</summary>
    private PowerLimits? InForce => _onBattery
        ? Device.LimitsOnBattery(Mode, Manual, CustomOnBatteryFor(Mode))
        : Device.LimitsFor(Mode, Manual, CustomFor(Mode));

    /// <summary>True while on battery, only on a machine whose limits differ there.</summary>
    private bool _onBattery;

    /// <summary>Asks Windows for the power source, only on a machine whose limits differ on battery.</summary>
    private void ReadPowerSource()
    {
        var onBattery = Device.FollowsPowerSource && Battery.IsOnBattery();
        if (onBattery == _onBattery) return;

        // The sliders' ends and values move together. Whatever the controls push back meanwhile is not a user
        // choice. Values first when the range shrinks, range first when it grows, so a value is never outside it.
        var before = _absorbing;
        _absorbing = true;
        _onBattery = onBattery;
        if (onBattery)
        {
            NotifySliderValues();
            NotifySliderMaxima();
        }
        else
        {
            NotifySliderMaxima();
            NotifySliderValues();
        }
        _absorbing = before;

        NotifyLimits();
        Notify(nameof(BatteryCapNote));
        Notify(nameof(ShowsBatteryCapNote));
    }

    private void NotifySliderValues()
    {
        Notify(nameof(Sustained));
        Notify(nameof(Boost));
        Notify(nameof(FastBoost));
    }

    private void NotifySliderMaxima()
    {
        Notify(nameof(MaxSustainedWatts));
        Notify(nameof(MaxBoostWatts));
        Notify(nameof(MaxFastBoostWatts));
    }

    /// <summary>E.g. "Limited to 35 W on battery".</summary>
    public string BatteryCapNote => Device.BatteryCap is { } cap
        ? Strings.BatteryCapNote(cap.FastBoost ?? Math.Max(cap.Sustained, cap.Boost))
        : string.Empty;

    /// <summary>
    /// The note shows only while the helper is lowering the Manual sliders to the battery cap (presets have their
    /// own battery limits); collapsed otherwise, while the helper is offline, and on every machine without a cap.
    /// </summary>
    public bool ShowsBatteryCapNote =>
        _onBattery && IsHelperOnline && Mode == PerformanceMode.Manual
        && Device.LimitsFor(Mode, Manual) is { } own && Device.CapForBattery(own) != own;

    /// <summary>The power limits currently in force, or null when the device has none.</summary>
    public PowerLimits? ActiveLimits => InForce;

    /// <summary>The sustained limit in force, e.g. "25 W".</summary>
    public string SustainedInForce => InForce is { } limits ? Strings.Watts(limits.Sustained) : Strings.NoReading;

    /// <summary>The boost limit in force, e.g. "37 W" on Intel or "45/55 W" (SPPT/FPPT) on 3-tier AMD.</summary>
    public string BoostInForce => InForce is { } limits
        ? limits.FastBoost is { } fast
            ? Strings.WattsPair(limits.Boost, fast)
            : Strings.Watts(limits.Boost)
        : Strings.NoReading;

    private void NotifyLimits()
    {
        Notify(nameof(SustainedInForce));
        Notify(nameof(BoostInForce));
        if (Device.FollowsPowerSource) Notify(nameof(ShowsBatteryCapNote));   // it depends on the mode's limits
    }

    /// <summary>Processor temperature, e.g. "61 °C", or a dash when the firmware did not answer.</summary>
    public string CpuReading { get; private set => Set(ref field, value); } = Strings.NoReading;

    /// <summary>Fan speed, e.g. "3,200 rpm", or a dash when the firmware did not answer.</summary>
    public string FanReading { get; private set => Set(ref field, value); } = Strings.NoReading;

    /// <summary>Processor temperature in °C, for the fan curve; null when the firmware did not answer.</summary>
    public int? CpuCelsius { get; private set => Set(ref field, value); }

    /// <summary>Fan speed in RPM, for the fan curve; null when the firmware did not answer.</summary>
    public int? FanRpm { get; private set => Set(ref field, value); }

    /// <summary>
    /// Takes the firmware's fan mode. Outside Max it is also the user's own mode, the one Max returns to: whatever
    /// set it (this panel, MSI Center M, a restart), the panel follows the fans rather than an older choice.
    /// </summary>
    private void TakeFan(FanMode mode)
    {
        Fan = mode;
        if (mode == FanMode.Max || FanLimits is null) return;

        var curveOn = mode == FanMode.Custom;
        if (_state.FanCurveOn == curveOn) return;

        _state.FanCurveOn = curveOn;
        _state.Save();
    }

    private void Show(int? cpuCelsius, int? fanRpm)
    {
        CpuReading = cpuCelsius is { } celsius ? Strings.Temperature(celsius) : Strings.NoReading;
        FanReading = fanRpm is { } rpm ? Strings.FanSpeed(rpm) : Strings.NoReading;
        CpuCelsius = cpuCelsius;
        FanRpm = fanRpm;
    }

    /// <summary>
    /// Toggles Max. Max is a boost on top of the user's own fan mode, so turning it off goes back to that mode:
    /// their custom curve if they chose one, otherwise Auto.
    /// </summary>
    public ICommand CycleFan { get; }

    /// <summary>The fan mode in force, as the firmware last reported it.</summary>
    public FanMode Fan
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(FanDetail));
            Notify(nameof(IsFanMax));
        }
    }

    public string FanDetail => !IsHelperOnline || !Device.Offers(FanMode.Max) ? Strings.FanUnavailable : Fan switch
    {
        FanMode.Max    => Strings.FanMax,
        FanMode.Custom => FanProfileInForce is { } profile ? Strings.FanCustomProfile(profile + 1) : Strings.FanCustom,
        _              => Strings.FanAuto,
    };

    public bool IsFanMax => Fan == FanMode.Max;

    /// <summary>What a custom fan curve may hold on this machine; null on one that takes none.</summary>
    public FanCurveLimits? FanLimits => Device.Offers(FanMode.Custom) ? Device.FanCurve : null;

    /// <summary>
    /// The machine's fan curve as last read: its temperatures, the speeds it holds, and its own. Null until the
    /// helper has sent one, and on a helper too old to know about curves.
    /// </summary>
    public FanCurveState? FanCurve
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(FanDetail));   // which profile is in force
        }
    }

    /// <summary>True once the helper has answered a request for the curve, whether or not it sent one.</summary>
    public bool FanCurveAsked { get; private set => Set(ref field, value); }

    /// <summary>True while fan requests are on their way. Readings from before them are not shown, and polling waits.</summary>
    public bool IsFanBusy { get; private set => Set(ref field, value); }

    /// <summary>
    /// The newest fan request waiting to go. Fan requests go one at a time, since the pipe keeps no order between
    /// them, and a newer choice replaces one still waiting: only the latest intention reaches the fans.
    /// </summary>
    private Func<Task>? _fanNext;

    /// <summary>The mode the newest fan request is heading for; null once they have all been answered.</summary>
    private FanMode? _fanWanted;

    /// <summary>Where the fans are heading: the mode last asked for if it is still on its way, otherwise the one in force.</summary>
    public FanMode WantedFan => _fanWanted ?? Fan;

    private void NextFanMode()
    {
        if (!IsHelperOnline || !Device.Offers(FanMode.Max)) return;

        if (WantedFan != FanMode.Max) Queue(() => SendFanAsync(new SetFan(FanMode.Max)), FanMode.Max);
        else if (_state.FanCurveOn && FanLimits is not null) Queue(ReturnFromMaxAsync, FanMode.Custom, FanProfile);
        else Queue(ReturnFromMaxAsync, FanMode.Auto);
    }

    /// <summary>
    /// Puts the fans on Auto or Max, replacing any fan request still waiting. Custom goes through
    /// <see cref="ChooseFanProfile"/> or <see cref="KeepFanCurve"/>.
    /// </summary>
    public void ChooseFan(FanMode mode)
    {
        if (!IsHelperOnline || mode == FanMode.Custom || !Device.Offers(mode)) return;
        Queue(() => SendFanAsync(new SetFan(mode)), mode);
    }

    /// <summary>
    /// Keeps <paramref name="speeds"/> in <paramref name="profile"/> and, with <paramref name="send"/>, makes the fans
    /// follow it, leaving Max and replacing any fan request still waiting. Without, the curve is only kept for
    /// later: an edit that lands after the user moved on (to Auto, Max, or another profile) must not undo that choice.
    /// </summary>
    public void KeepFanCurve(int profile, IReadOnlyList<int> speeds, bool send)
    {
        if (FanLimits is not { } limits || FanCurve is not { } curve || speeds.Count != curve.Temperatures.Count) return;

        var temperatures = CustomTemperaturesOf(curve);
        var legal = limits.Clamp(temperatures, speeds, StockForCustom(curve));
        _state.KeepFanCurve(profile, [.. legal]);
        _state.Save();   // the base mode is taken from the fans' answer, not set here

        if (send && IsHelperOnline) Queue(() => SendFanAsync(new SetFanCurve(legal)), FanMode.Custom, profile);
        Notify(nameof(FanDetail));
    }

    /// <summary>The active custom fan profile, 0-based.</summary>
    public int FanProfile => _state.ActiveFanProfile;

    /// <summary>
    /// Makes <paramref name="profile"/> the active one and, with <paramref name="send"/>, has the fans follow it: just
    /// another curve in the fan line, so a newer choice still replaces it while it waits.
    /// </summary>
    public void ChooseFanProfile(int profile, bool send)
    {
        if (profile < 0 || profile >= SavedState.FanProfileCount) return;

        if (profile != _state.FanProfile)
        {
            _state.FanProfile = profile;
            _state.Save();
            Notify(nameof(FanProfile));
            Notify(nameof(FanDetail));
        }

        if (send && IsHelperOnline && FanCurve is { } curve)
            Queue(() => SendFanAsync(new SetFanCurve(ProfileCurve(profile, curve))), FanMode.Custom, profile);
    }

    /// <summary>Temperature points for Custom and Max: chipset custom scale when configured, else firmware temperatures.</summary>
    public IReadOnlyList<int> CustomTemperaturesOf(FanCurveState curve) =>
        FanLimits?.CustomTemperatures is { Count: var count } custom && count == curve.Temperatures.Count
            ? custom
            : curve.Temperatures;

    /// <summary>Stock speeds resampled onto <see cref="CustomTemperaturesOf"/>, or null when stock is unknown.</summary>
    public IReadOnlyList<int>? StockForCustom(FanCurveState curve)
    {
        if (!curve.StockKnown) return null;

        var target = CustomTemperaturesOf(curve);
        var stockTemps = curve.EffectiveStockTemperatures;
        return stockTemps.SequenceEqual(target)
            ? curve.Stock
            : FanCurveLimits.Sample(stockTemps, curve.Stock, target);
    }

    /// <summary>
    /// A profile's curve, made legal for <paramref name="curve"/>. One never edited, or saved for a curve with a
    /// different number of points (another machine or firmware), starts from the firmware's own curve sampled onto
    /// the chipset's custom temperatures; when that is unknown, the curve in force. Nothing is saved until edited.
    /// </summary>
    public IReadOnlyList<int> ProfileCurve(int profile, FanCurveState curve)
    {
        if (FanLimits is not { } limits) return curve.Speeds;

        var temperatures = CustomTemperaturesOf(curve);
        var stock = StockForCustom(curve);
        var saved = _state.FanCurveIn(profile);
        var speeds = saved is not null && saved.Count == temperatures.Count ? saved
            : stock ?? curve.Speeds;
        return limits.Clamp(temperatures, speeds, stock);
    }

    /// <summary>True while the fans are on Custom and follow the active profile's temperatures and speeds exactly.</summary>
    public bool ActiveProfileInForce =>
        Fan == FanMode.Custom && FanCurve is { } curve && FanLimits is not null
        && curve.Temperatures.SequenceEqual(CustomTemperaturesOf(curve))
        && ProfileCurve(FanProfile, curve).SequenceEqual(curve.Speeds);

    /// <summary>
    /// The profile whose curve the fans follow, for the Fans tile: the one on its way while a profile is being sent,
    /// otherwise the active one or another that matches the curve in force. Null when that is not known (no curve
    /// on this machine, or not read yet) or matches none, as when another tool set it.
    /// </summary>
    public int? FanProfileInForce
    {
        get
        {
            if (FanLimits is null) return null;
            if (_sendingProfile is { } sending) return sending;
            if (FanCurve is not { } curve) return null;
            if (!curve.Temperatures.SequenceEqual(CustomTemperaturesOf(curve))) return null;
            if (ProfileCurve(FanProfile, curve).SequenceEqual(curve.Speeds)) return FanProfile;

            for (var profile = 0; profile < SavedState.FanProfileCount; profile++)
                if (ProfileCurve(profile, curve).SequenceEqual(curve.Speeds)) return profile;
            return null;
        }
    }

    /// <summary>
    /// Max goes off: back to the active profile if the user's mode is Custom, otherwise Auto. Auto makes the helper
    /// forget the curve, so a curve that cannot be checked against the machine's is sent anyway for the helper to clamp.
    /// </summary>
    private async Task ReturnFromMaxAsync()
    {
        if (_state.FanCurveOn && FanLimits is not null)
        {
            if (FanCurve is null) await ReadFanCurveAsync();

            if (FanCurve is { } curve)
            {
                await SendFanAsync(new SetFanCurve(ProfileCurve(FanProfile, curve)));
                return;
            }

            if (_state.FanCurveIn(FanProfile) is { } saved)
            {
                await SendFanAsync(new SetFanCurve([.. saved]));
                return;
            }
        }

        await SendFanAsync(new SetFan(FanMode.Auto));
    }

    /// <summary>
    /// Reads the machine's fan curve, unless a fan request is already waiting (its answer carries the curve).
    /// Returns whether it was asked for.
    /// </summary>
    public bool RefreshFanCurve()
    {
        if (!IsHelperOnline || FanLimits is null || _fanNext is not null) return false;
        Queue(ReadFanCurveAsync, wanted: null);
        return true;
    }

    /// <summary>True once the curve has been asked for since the helper last came online, so the Fans tile can name the profile.</summary>
    private bool _curveSought;

    private async Task ReadFanCurveAsync()
    {
        var reply = await SendFanAsync(new GetFanCurve());
        if (!reply.Unreachable) FanCurveAsked = true;
    }

    /// <summary>The profile whose curve is on its way to the fans, so the tile keeps naming it until the answer comes.</summary>
    private int? _sendingProfile;

    /// <summary>Puts a fan request at the back of the line, in place of any still waiting, and starts the line moving.</summary>
    /// <param name="wanted">The mode it heads for; null for a request that only reads.</param>
    /// <param name="profile">The custom profile it sends, if any.</param>
    private async void Queue(Func<Task> request, FanMode? wanted, int? profile = null)
    {
        _fanNext = request;
        if (wanted is not null)
        {
            _fanWanted = wanted;
            _sendingProfile = profile;
            Notify(nameof(FanDetail));
        }
        if (IsFanBusy) return;

        IsFanBusy = true;
        try
        {
            while (_fanNext is { } next)
            {
                _fanNext = null;
                await next();
            }
        }
        finally
        {
            _fanWanted = null;
            _sendingProfile = null;
            IsFanBusy = false;
            Notify(nameof(FanDetail));
        }
    }

    private async Task<HelperReply> SendFanAsync(HelperRequest request)
    {
        var reply = await _helper.SendAsync(request);

        // A failure without a snapshot (as from a helper too old to know the request) is still an answer, and
        // Absorb would show the helper as offline. So only a snapshot, no answer at all, or an unsupported
        // machine changes what is shown.
        if (reply.Snapshot is not null || reply.Unreachable || reply.Unsupported) Absorb(reply, fanReply: true);
        else TakeCurve(reply.FanCurve);

        return reply;
    }

    /// <summary>Keeps a curve from the helper, if this machine takes one and it holds a speed for every point.</summary>
    private void TakeCurve(FanCurveState? curve)
    {
        if (curve is null || FanLimits is null) return;

        var points = curve.Temperatures.Count;
        if (points == 0 || curve.Speeds.Count != points || curve.Stock.Count != points) return;

        FanCurve = curve;
    }

    public bool IsHelperOnline
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(IsHelperOffline));
            Notify(nameof(FanDetail));
        }
    }

    public bool IsHelperOffline => !IsHelperOnline;

    /// <summary>True when the helper answered that it does not know this machine.</summary>
    private bool IsUnsupported
    {
        get;
        set
        {
            if (Set(ref field, value)) Notify(nameof(HelperNote));
        }
    }

    /// <summary>Shown in place of the readings while the firmware cannot be reached.</summary>
    public string HelperNote => IsUnsupported ? Strings.DeviceNotSupported : Strings.HelperOffline;

    /// <summary>Reads the current hardware state into the view. Called each time the panel opens.</summary>
    public async Task RefreshAsync()
    {
        var changesBefore = _changes;
        var reply = await _helper.SendAsync(new GetSnapshot());

        // A user change made while the helper was answering wins: adopting the older
        // reading's mode would move the selector back without the firmware following.
        Absorb(reply, adoptMode: _changes == changesBefore && !_applying && !_settle.IsEnabled);
    }

    /// <summary>
    /// Reads only the live numbers. Called once a second while the panel is open. While the helper reads as
    /// offline it asks for a full snapshot instead, so one missed answer (the helper's pipe serves one request
    /// at a time) never leaves the readout offline until the panel reopens. Not on a machine the helper does
    /// not support: that answer will not change while the panel is open.
    /// </summary>
    public async Task RefreshTelemetryAsync()
    {
        if (_applying || IsFanBusy) return;

        if (!IsHelperOnline)
        {
            if (_reconnecting || IsUnsupported) return;
            _reconnecting = true;
            try { await RefreshAsync(); }
            finally { _reconnecting = false; }
            return;
        }

        var reply = await _helper.SendAsync(new GetReadings());

        if (reply.Readings is { } readings)
        {
            Show(readings.CpuTemperature, readings.FanRpm);
            ReadPowerSource();   // nothing on a machine whose limits are the same on battery
        }
        else Absorb(reply);   // no readings means the helper is gone
    }

    private async void Apply()
    {
        _settle.Stop();

        if (_applying)
        {
            _applyAgain = true;
            return;
        }

        _applying = true;
        try
        {
            do
            {
                _applyAgain = false;

                var mode = Mode;
                var manual = Manual;
                var custom = CustomFor(mode);
                var customOnBattery = CustomOnBatteryFor(mode);

                _state.Mode = mode;
                if (Device.ManualRange is not null && _manualChosen) _state.Manual = manual;
                _state.Save();

                var reply = await _helper.SendAsync(new SetPerformance(mode, manual, custom, customOnBattery));
                Absorb(reply);
            }
            while (_applyAgain);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// True on a machine with a battery cap when <paramref name="read"/> is the sliders' limits lowered to it.
    /// Not tied to the power source right now: for a moment after plugging in, the firmware still holds the capped values.
    /// </summary>
    private bool IsBatteryCapOf(PowerLimits read) =>
        Device.BatteryCap is not null && Device.ManualRange is { } range && read == Device.CapForBattery(range.Clamp(Manual));

    /// <summary>Shows a helper reply. The mode and manual limits are taken from it only when <paramref name="adoptMode"/> is set.</summary>
    /// <param name="fanReply">The answer to a fan request, so its fan mode is the newest there is.</param>
    private void Absorb(HelperReply reply, bool adoptMode = false, bool fanReply = false)
    {
        if (reply.Device is { } device) Adopt(device);

        IsUnsupported = reply.Unsupported;
        IsHelperOnline = reply.Snapshot is not null;
        if (Device.FollowsPowerSource) Notify(nameof(ShowsBatteryCapNote));   // it hides while the helper is offline

        TakeCurve(reply.FanCurve);
        if (reply.Snapshot is not { } snapshot)
        {
            _curveSought = false;   // ask again once the helper is back
            return;
        }

        // A reading taken before a fan request was answered would put the old mode back for a moment.
        if (fanReply || !IsFanBusy) TakeFan(snapshot.Fan);

        // On Custom the Fans tile names the profile in force, which needs the curve: read it once, in the fan line
        // behind anything the user asked for, without waiting for the Fans page to be opened.
        if (snapshot.Fan == FanMode.Custom && FanCurve is null && !_curveSought) _curveSought = RefreshFanCurve();
        Show(snapshot.CpuTemperature, snapshot.FanRpm);
        ReadPowerSource();   // nothing on a machine whose limits are the same on battery

        if (!adoptMode) return;

        // Something else (MSI Center M, a previous session) may have changed the mode.
        _absorbing = true;
        Mode = snapshot.Mode;
        // On battery the helper writes the user's Manual limits lowered to the cap and keeps the user's own for
        // wall power, so a read-back equal to them capped is not a new choice: the sliders keep the user's.
        if (snapshot.Mode == PerformanceMode.Manual && !IsBatteryCapOf(snapshot.Limits))
        {
            var limits = Device.ManualRange is { } range ? range.Clamp(snapshot.Limits) : snapshot.Limits;
            Sustained = limits.Sustained;
            Boost = limits.Boost;
            if (limits.FastBoost is { } fast) FastBoost = fast;
        }
        _absorbing = false;
    }
}
