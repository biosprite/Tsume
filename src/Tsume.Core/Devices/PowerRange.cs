using System.Text.Json.Serialization;
using Tsume.Core.Performance;

namespace Tsume.Core.Devices;

/// <summary>The power limits one machine accepts when they are set by hand.</summary>
/// <param name="Min">The lowest sustained limit its firmware accepts.</param>
/// <param name="MaxSustained">The highest sustained limit Tsume will ask for.</param>
/// <param name="MaxBoost">The highest boost limit its firmware accepts.</param>
/// <param name="MinBoostHeadroom">Minimum watts boost must stay above sustained (e.g. 1 W on Lunar Lake to avoid PL1 == PL2 throttling).</param>
/// <param name="MinBoostFloor">Lowest boost limit the firmware accepts regardless of sustained (e.g. 9 W SPPT floor on AMD Z2 Extreme).</param>
/// <param name="MaxFastBoost">Highest fast-boost (AMD FPPT) limit the firmware accepts; null on 2-tier Intel boards.</param>
/// <param name="MinFastBoostHeadroom">Minimum watts fast-boost must stay above boost.</param>
/// <param name="MinFastBoostFloor">Lowest fast-boost limit the firmware accepts regardless of boost (e.g. 11 W FPPT floor on AMD Z2 Extreme).</param>
public readonly record struct PowerRange(
    int Min,
    int MaxSustained,
    int MaxBoost,
    int MinBoostHeadroom = 0,
    int MinBoostFloor = 0,
    int? MaxFastBoost = null,
    int MinFastBoostHeadroom = 0,
    int MinFastBoostFloor = 0)
{
    /// <summary>True when this machine exposes a 3rd fast-boost power tier (AMD FPPT).</summary>
    [JsonIgnore]
    public bool HasFastBoost => MaxFastBoost is not null;

    /// <summary>The lowest boost limit this machine accepts.</summary>
    [JsonIgnore]
    public int MinBoost => MinBoostFor(Min);

    /// <summary>The highest boost limit that still leaves <see cref="MinFastBoostHeadroom"/> watts below <see cref="MaxFastBoost"/>.</summary>
    [JsonIgnore]
    public int EffectiveMaxBoost => MaxFastBoost is { } maxFast
        ? Math.Min(MaxBoost, Math.Max(Math.Max(Min, MinBoostFloor), maxFast - Math.Max(0, MinFastBoostHeadroom)))
        : MaxBoost;

    /// <summary>The highest sustained limit that still leaves <see cref="MinBoostHeadroom"/> watts below <see cref="EffectiveMaxBoost"/>.</summary>
    [JsonIgnore]
    public int EffectiveMaxSustained => Math.Min(MaxSustained, Math.Max(Min, EffectiveMaxBoost - Math.Max(0, MinBoostHeadroom)));

    /// <summary>The lowest fast-boost limit this machine accepts (0 on 2-tier boards).</summary>
    [JsonIgnore]
    public int MinFastBoost => HasFastBoost ? MinFastBoostFor(MinBoost) : 0;

    /// <summary>The lowest boost limit allowed for a given sustained limit.</summary>
    public int MinBoostFor(int sustained) => Math.Min(EffectiveMaxBoost, Math.Max(MinBoostFloor, sustained + Math.Max(0, MinBoostHeadroom)));

    /// <summary>The lowest fast-boost limit allowed for a given boost limit.</summary>
    public int MinFastBoostFor(int boost) => MaxFastBoost is { } maxFast
        ? Math.Min(maxFast, Math.Max(MinFastBoostFloor, boost + Math.Max(0, MinFastBoostHeadroom)))
        : 0;

    /// <summary>Returns limits inside their ranges, preserving monotonic ordering across all active tiers.</summary>
    public PowerLimits Clamp(PowerLimits limits)
    {
        var sustained = Math.Clamp(limits.Sustained, Min, EffectiveMaxSustained);
        var boost = Math.Clamp(limits.Boost, MinBoostFor(sustained), EffectiveMaxBoost);

        if (MaxFastBoost is not { } maxFast)
            return new PowerLimits(sustained, boost);

        var minFast = MinFastBoostFor(boost);
        var fast = Math.Clamp(limits.FastBoost ?? minFast, minFast, maxFast);
        return new PowerLimits(sustained, boost, fast);
    }
}
