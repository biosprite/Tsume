using System.Text.Json.Serialization;
using Tsume.Core.Performance;

namespace Tsume.Core.Devices;

/// <summary>The power limits one machine accepts when they are set by hand.</summary>
/// <param name="Min">The lowest sustained limit its firmware accepts.</param>
/// <param name="MaxSustained">The highest sustained limit Tsume will ask for.</param>
/// <param name="MaxBoost">The highest boost limit its firmware accepts.</param>
/// <param name="MinBoostHeadroom">Minimum watts boost must stay above sustained (e.g. 1 W on Lunar Lake to avoid PL1 == PL2 throttling).</param>
public readonly record struct PowerRange(int Min, int MaxSustained, int MaxBoost, int MinBoostHeadroom = 0)
{
    /// <summary>The lowest boost limit this machine accepts.</summary>
    [JsonIgnore]
    public int MinBoost => MinBoostFor(Min);

    /// <summary>The highest sustained limit that still leaves <see cref="MinBoostHeadroom"/> watts below <see cref="MaxBoost"/>.</summary>
    [JsonIgnore]
    public int EffectiveMaxSustained => Math.Min(MaxSustained, Math.Max(Min, MaxBoost - Math.Max(0, MinBoostHeadroom)));

    /// <summary>The lowest boost limit allowed for a given sustained limit.</summary>
    public int MinBoostFor(int sustained) => Math.Min(MaxBoost, sustained + Math.Max(0, MinBoostHeadroom));

    /// <summary>Returns limits inside their ranges, with boost at least <see cref="MinBoostHeadroom"/> watts above sustained.</summary>
    public PowerLimits Clamp(PowerLimits limits)
    {
        var sustained = Math.Clamp(limits.Sustained, Min, EffectiveMaxSustained);
        var boost = Math.Clamp(limits.Boost, MinBoostFor(sustained), MaxBoost);
        return new PowerLimits(sustained, boost);
    }
}
