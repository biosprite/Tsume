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

    /// <summary>Waits for the manual sliders to stop moving before touching the firmware.</summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>True while values are being copied in from the hardware, so they are not sent straight back.</summary>
    private bool _absorbing;

    /// <summary>Counts user changes, so a reading requested before a change does not undo it.</summary>
    private int _changes;

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

        Notify(nameof(MinWatts));
        Notify(nameof(MinBoostWatts));
        Notify(nameof(MaxSustainedWatts));
        Notify(nameof(MaxBoostWatts));

        if (!_manualChosen && device.ManualDefault is { } start)
        {
            var clamped = device.ManualRange?.Clamp(start) ?? start;
            Sustained = clamped.Sustained;
            Boost = clamped.Boost;
            _manualChosen = true;
        }
        else if (_manualChosen && device.ManualRange is { } range)
        {
            var clamped = range.Clamp(previousManual);
            Sustained = clamped.Sustained;
            Boost = clamped.Boost;
        }

        Notify(nameof(Modes));
        Notify(nameof(ModeIndex));
        Notify(nameof(ShowsLimitSliders));
        Notify(nameof(FanDetail));
        NotifyLimits();

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
            Apply();
        }
    }

    public bool IsManual => Mode == PerformanceMode.Manual;

    /// <summary>The sliders show only in Manual mode, and only on a machine whose limits can be set.</summary>
    public bool ShowsLimitSliders => IsManual && Device.ManualRange is not null;

    // Slider ranges, as doubles to bind to Slider.Minimum/Maximum.
    public double MinWatts => Device.ManualRange?.Min ?? 0;
    public double MinBoostWatts => Device.ManualRange?.MinBoost ?? 0;
    public double MaxSustainedWatts => Device.ManualRange?.EffectiveMaxSustained ?? 1;
    public double MaxBoostWatts => Device.ManualRange?.MaxBoost ?? 1;

    private double MinBoostFor(double sustained) =>
        Device.ManualRange is { } range ? range.MinBoostFor((int)sustained) : sustained;

    public double Sustained
    {
        get;
        set
        {
            var rounded = Math.Round(value);
            if (Device.ManualRange is { } range)
                rounded = Math.Clamp(rounded, range.Min, range.EffectiveMaxSustained);

            if (!Set(ref field, rounded))
            {
                if (rounded != value) Notify();
                return;
            }

            var minBoost = MinBoostFor(field);
            if (Boost < minBoost) Boost = minBoost;
            OnManualChanged();
        }
    }

    public double Boost
    {
        get;
        set
        {
            var rounded = Math.Round(value);
            var floor = MinBoostFor(Sustained);
            var clamped = Device.ManualRange is { } range
                ? Math.Clamp(rounded, floor, range.MaxBoost)
                : Math.Max(rounded, floor);

            if (Set(ref field, clamped))
                OnManualChanged();
            else if (clamped != value)
                Notify();
        }
    }

    private PowerLimits Manual => new((int)Sustained, (int)Boost);

    private void OnManualChanged()
    {
        NotifyLimits();
        // With no range there are no sliders, so a change here is not a user choice.
        if (_absorbing || Device.ManualRange is null) return;

        _changes++;
        _manualChosen = true;
        if (!IsManual) return;

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

    private PowerLimits? InForce => Device.LimitsFor(Mode, Manual, CustomFor(Mode));

    /// <summary>The power limits currently in force, or null when the device has none.</summary>
    public PowerLimits? ActiveLimits => InForce;

    /// <summary>The sustained limit in force, e.g. "25 W".</summary>
    public string SustainedInForce => InForce is { } limits ? Strings.Watts(limits.Sustained) : Strings.NoReading;

    /// <summary>The boost limit in force, e.g. "37 W".</summary>
    public string BoostInForce => InForce is { } limits ? Strings.Watts(limits.Boost) : Strings.NoReading;

    private void NotifyLimits()
    {
        Notify(nameof(SustainedInForce));
        Notify(nameof(BoostInForce));
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
        Absorb(reply, adoptMode: _changes == changesBefore);
    }

    /// <summary>Reads only the live numbers. Called once a second while the panel is open.</summary>
    public async Task RefreshTelemetryAsync()
    {
        if (!IsHelperOnline) return;

        var reply = await _helper.SendAsync(new GetReadings());

        if (reply.Readings is { } readings) Show(readings.CpuTemperature, readings.FanRpm);
        else Absorb(reply);   // no readings means the helper is gone
    }

    private async void Apply()
    {
        _state.Mode = Mode;
        if (Device.ManualRange is not null && _manualChosen) _state.Manual = Manual;
        _state.Save();

        Absorb(await _helper.SendAsync(new SetPerformance(Mode, Manual, CustomFor(Mode))));
    }

    /// <summary>Shows a helper reply. The mode and manual limits are taken from it only when <paramref name="adoptMode"/> is set.</summary>
    private void Absorb(HelperReply reply, bool adoptMode = false)
    {
        if (reply.Device is { } device) Adopt(device);

        IsUnsupported = reply.Unsupported;
        IsHelperOnline = reply.Snapshot is not null;

        if (reply.Snapshot is not { } snapshot) return;

        Fan = snapshot.Fan;
        Show(snapshot.CpuTemperature, snapshot.FanRpm);

        if (!adoptMode) return;

        // Something else (MSI Center M, a previous session) may have changed the mode.
        _absorbing = true;
        Mode = snapshot.Mode;
        if (snapshot.Mode == PerformanceMode.Manual)
        {
            var limits = Device.ManualRange is { } range ? range.Clamp(snapshot.Limits) : snapshot.Limits;
            Sustained = limits.Sustained;
            Boost = limits.Boost;
        }
        _absorbing = false;
    }
}
