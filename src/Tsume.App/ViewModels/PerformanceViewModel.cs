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

    /// <summary>Called after the presets page changed and saved these modes' limits; re-applies the mode in force if it is among them.</summary>
    public void OnPresetsChanged(IReadOnlyCollection<PerformanceMode> modes)
    {
        NotifyLimits();
        if (!modes.Contains(Mode)) return;

        _changes++;
        Apply();
    }

    /// <summary>
    /// The helper's limits for the mode: the user's, lowered to the battery cap while it applies (as the helper
    /// lowers what it writes; <see cref="DeviceProfile.CapForBattery"/>).
    /// </summary>
    private PowerLimits? InForce
    {
        get
        {
            var limits = Device.LimitsFor(Mode, Manual, CustomFor(Mode));
            return limits is { } found && _onBattery ? Device.CapForBattery(found) : limits;
        }
    }

    /// <summary>True while on battery on a machine with a battery cap; never set on any other machine.</summary>
    private bool _onBattery;

    /// <summary>Asks Windows for the power source, only on a machine with a battery cap.</summary>
    private void ReadPowerSource()
    {
        var onBattery = Device.BatteryCap is not null && Battery.IsOnBattery();
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
    /// The note shows only while the helper is lowering this mode's limits to the battery cap; collapsed in a mode
    /// the cap does not touch, while the helper is offline, and on every machine without a cap.
    /// </summary>
    public bool ShowsBatteryCapNote =>
        _onBattery && IsHelperOnline
        && Device.LimitsFor(Mode, Manual, CustomFor(Mode)) is { } own && Device.CapForBattery(own) != own;

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
        if (Device.BatteryCap is not null) Notify(nameof(ShowsBatteryCapNote));   // it depends on the mode's limits
    }

    /// <summary>Processor temperature, e.g. "61 °C", or a dash when the firmware did not answer.</summary>
    public string CpuReading { get; private set => Set(ref field, value); } = Strings.NoReading;

    /// <summary>Fan speed, e.g. "3,200 rpm", or a dash when the firmware did not answer.</summary>
    public string FanReading { get; private set => Set(ref field, value); } = Strings.NoReading;

    private void Show(int? cpuCelsius, int? fanRpm)
    {
        CpuReading = cpuCelsius is { } celsius ? Strings.Temperature(celsius) : Strings.NoReading;
        FanReading = fanRpm is { } rpm ? Strings.FanSpeed(rpm) : Strings.NoReading;
    }

    public ICommand CycleFan { get; }

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
        FanMode.Custom => Strings.FanCustom,
        _              => Strings.FanAuto,
    };

    public bool IsFanMax => Fan == FanMode.Max;

    private async void NextFanMode()
    {
        if (!IsHelperOnline || !Device.Offers(FanMode.Max)) return;

        var next = Fan == FanMode.Max ? FanMode.Auto : FanMode.Max;
        Absorb(await _helper.SendAsync(new SetFan(next)));
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

    /// <summary>Reads only the live numbers. Called once a second while the panel is open.</summary>
    public async Task RefreshTelemetryAsync()
    {
        if (!IsHelperOnline || _applying) return;

        var reply = await _helper.SendAsync(new GetReadings());

        if (reply.Readings is { } readings)
        {
            Show(readings.CpuTemperature, readings.FanRpm);
            ReadPowerSource();   // nothing on a machine without a battery cap
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

                _state.Mode = mode;
                if (Device.ManualRange is not null && _manualChosen) _state.Manual = manual;
                _state.Save();

                var reply = await _helper.SendAsync(new SetPerformance(mode, manual, custom));
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
    private void Absorb(HelperReply reply, bool adoptMode = false)
    {
        if (reply.Device is { } device) Adopt(device);

        IsUnsupported = reply.Unsupported;
        IsHelperOnline = reply.Snapshot is not null;
        if (Device.BatteryCap is not null) Notify(nameof(ShowsBatteryCapNote));   // it hides while the helper is offline

        if (reply.Snapshot is not { } snapshot) return;

        Fan = snapshot.Fan;
        Show(snapshot.CpuTemperature, snapshot.FanRpm);
        ReadPowerSource();   // nothing on a machine without a battery cap

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
