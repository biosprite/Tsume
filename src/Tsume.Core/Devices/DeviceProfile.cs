using Tsume.Core.Cooling;
using Tsume.Core.Performance;

namespace Tsume.Core.Devices;

/// <summary>One performance mode a machine offers, and the limits it applies — none for a mode set by hand.</summary>
public sealed record ModePreset(PerformanceMode Mode, PowerLimits? Limits = null);

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

    /// <summary>The fan settings it offers. Empty if the fans cannot be controlled.</summary>
    public IReadOnlyList<FanMode> FanModes { get; init; } = [];

    /// <summary>
    /// The percentages the battery can stop charging at, lowest first; 100 charges to full.
    /// Empty if charging cannot be capped.
    /// </summary>
    public IReadOnlyList<int> ChargeLimits { get; init; } = [];

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

    /// <summary>Whether a mode's own limits can be replaced: it has some, and the machine takes limits by hand.</summary>
    public bool CanCustomize(PerformanceMode mode) =>
        ManualRange is not null && Modes.Any(preset => preset.Mode == mode && preset.Limits is not null);

    /// <summary>Content equality; record equality compares the lists by reference.</summary>
    public bool Matches(DeviceProfile other) =>
        Name == other.Name
        && Modes.SequenceEqual(other.Modes)
        && ManualRange == other.ManualRange
        && ManualDefault == other.ManualDefault
        && FanModes.SequenceEqual(other.FanModes)
        && ChargeLimits.SequenceEqual(other.ChargeLimits);

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
