using System.Text.Json.Serialization;
using Tsume.Core.Cooling;
using Tsume.Core.Performance;

namespace Tsume.Core.Devices;

/// <summary>
/// One performance mode a machine offers, and the limits it applies — none for a mode set by hand.
/// <paramref name="OnBattery"/>, if set, replaces them on battery.
/// </summary>
public sealed record ModePreset(
    PerformanceMode Mode,
    PowerLimits? Limits = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PowerLimits? OnBattery = null);

/// <summary>
/// What one machine can do: its modes, limits, fan settings and charge limits. The helper sends it
/// to the panel and refuses anything outside it. Firmware access stays in Tsume.Hardware.
/// </summary>
public sealed record DeviceProfile
{
    /// <summary>E.g. "MSI Claw 8 EX AI+ (MS-1T91)".</summary>
    public required string Name { get; init; }

    /// <summary>The modes it offers, quietest first — the order the panel lays them out in.</summary>
    public required IReadOnlyList<ModePreset> Modes { get; init; }

    /// <summary>What the manual sliders may be set to. Null if the limits cannot be set by hand.</summary>
    public PowerRange? ManualRange { get; init; }

    /// <summary>Where the manual sliders start before anyone has touched them.</summary>
    public PowerLimits? ManualDefault { get; init; }

    /// <summary>
    /// The highest watts per tier while the machine runs on battery (see <see cref="CapForBattery"/>); null when
    /// battery and wall power allow the same. Omitted from JSON when null, so profiles without one are sent as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PowerLimits? BatteryCap { get; init; }

    /// <summary>The fan settings it offers. Empty if the fans cannot be controlled.</summary>
    public IReadOnlyList<FanMode> FanModes { get; init; } = [];

    /// <summary>
    /// What a custom fan curve may hold; null if the machine takes none. Set exactly when <see cref="FanModes"/>
    /// lists <see cref="FanMode.Custom"/>. Omitted from JSON when null, so profiles without one are sent as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FanCurveLimits? FanCurve { get; init; }

    /// <summary>
    /// The percentages the battery can stop charging at, lowest first; 100 charges to full.
    /// Empty if charging cannot be capped.
    /// </summary>
    public IReadOnlyList<int> ChargeLimits { get; init; } = [];

    /// <summary>Default temperature (°C) where the overlay switches to warning (amber) color.</summary>
    public const int DefaultTempWarnC = 75;

    /// <summary>Default temperature (°C) where the overlay switches to hot (red) color.</summary>
    public const int DefaultTempHotC = 85;

    /// <summary>Points below the charge limit where charging resumes; null if unknown.</summary>
    public int? ChargeResumeGap { get; init; }

    /// <summary>Temperature (°C) where the overlay turns amber; tuned per chip (Intel handhelds run hotter than AMD).</summary>
    public int TempWarnC { get; init; } = DefaultTempWarnC;

    /// <summary>Temperature (°C) where the overlay turns red; tuned per chip.</summary>
    public int TempHotC { get; init; } = DefaultTempHotC;

    /// <summary>Dedicated buttons and verified back-button map; null if unverified.</summary>
    public ControlsProfile? Controls { get; init; }

    public bool Offers(PerformanceMode mode) => Modes.Any(preset => preset.Mode == mode);

    public bool Offers(FanMode mode) => FanModes.Contains(mode);

    /// <summary>
    /// The limits a mode applies: its preset, or for Manual, <paramref name="manual"/> clamped to the range.
    /// Null when the machine does not offer the mode or its limits are not known.
    /// </summary>
    /// <param name="custom">Replaces a preset's limits, clamped to the manual range; ignored if there is none.</param>
    public PowerLimits? LimitsFor(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null) =>
        Modes.FirstOrDefault(preset => preset.Mode == mode) switch
        {
            null                    => null,
            { Limits: { } preset }  => custom is { } own && ManualRange is { } range ? range.Clamp(own) : preset,
            _                       => ManualRange?.Clamp(manual),
        };

    /// <summary>True when the limits differ on battery (a battery cap or a preset's battery limits).</summary>
    [JsonIgnore]
    public bool FollowsPowerSource => BatteryCap is not null || Modes.Any(preset => preset.OnBattery is not null);

    /// <summary>
    /// A mode's limits on battery, capped: <paramref name="custom"/> (the user's battery preset) if given, else the
    /// preset's battery limits, else its own. Wall-power custom limits do not carry over.
    /// </summary>
    public PowerLimits? LimitsOnBattery(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null) =>
        Modes.FirstOrDefault(preset => preset.Mode == mode) switch
        {
            null => null,
            { Limits: { } stock } preset => CapForBattery(
                custom is { } own && RangeOnBattery is { } range ? range.Clamp(own) : preset.OnBattery ?? stock),
            _ => ManualRange is { } manualRange ? CapForBattery(manualRange.Clamp(manual)) : null,
        };

    /// <summary>The manual range lowered to the <see cref="BatteryCap"/>: what battery presets may be set to.</summary>
    [JsonIgnore]
    public PowerRange? RangeOnBattery => ManualRange is not { } range ? null : BatteryCap is not { } cap ? range : range with
    {
        MaxSustained = Math.Min(range.MaxSustained, cap.Sustained),
        MaxBoost = Math.Min(range.MaxBoost, cap.Boost),
        MaxFastBoost = range.MaxFastBoost is { } fast ? Math.Min(fast, cap.FastBoost ?? fast) : null,
    };

    /// <summary>
    /// <paramref name="limits"/> as applied on battery: each tier lowered to <see cref="BatteryCap"/>, never raised.
    /// If that leaves a tier closer to the one above it than the manual range's headroom allows, the lower tier is
    /// lowered further (the cap is a ceiling, so the upper one cannot go up). Unchanged without a cap.
    /// </summary>
    public PowerLimits CapForBattery(PowerLimits limits)
    {
        if (BatteryCap is not { } cap) return limits;

        int? fast = limits.FastBoost is { } f ? Math.Min(f, cap.FastBoost ?? f) : null;

        var boost = Math.Min(limits.Boost, cap.Boost);
        if (fast is { } fastNow)
            boost = Math.Min(boost, fastNow - Math.Max(0, ManualRange?.MinFastBoostHeadroom ?? 0));

        var sustained = Math.Min(limits.Sustained, cap.Sustained);
        sustained = Math.Min(sustained, boost - Math.Max(0, ManualRange?.MinBoostHeadroom ?? 0));

        return new PowerLimits(sustained, boost, fast);
    }

    /// <summary>Whether a mode's own limits can be replaced: it has some, and the machine takes limits by hand.</summary>
    public bool CanCustomize(PerformanceMode mode) =>
        ManualRange is not null && Modes.Any(preset => preset.Mode == mode && preset.Limits is not null);

    /// <summary>Content equality; record equality compares the lists by reference.</summary>
    public bool Matches(DeviceProfile other) =>
        Name == other.Name
        && Modes.SequenceEqual(other.Modes)
        && ManualRange == other.ManualRange
        && ManualDefault == other.ManualDefault
        && BatteryCap == other.BatteryCap
        && FanModes.SequenceEqual(other.FanModes)
        && (FanCurve is null ? other.FanCurve is null : other.FanCurve is not null && FanCurve.Matches(other.FanCurve))
        && ChargeLimits.SequenceEqual(other.ChargeLimits)
        && ChargeResumeGap == other.ChargeResumeGap
        && TempWarnC == other.TempWarnC
        && TempHotC == other.TempHotC
        && (Controls is null ? other.Controls is null : other.Controls is not null && Controls.Matches(other.Controls));

    /// <summary>
    /// Used before the helper has answered, or without one. The three stock modes are
    /// listed, but no limits are known and nothing can apply them.
    /// </summary>
    public static DeviceProfile Unknown { get; } = new()
    {
        Name = "unknown",
        Modes = [new(PerformanceMode.Endurance), new(PerformanceMode.Balanced), new(PerformanceMode.Performance)],
    };
}
