using Tsume.Core.Performance;

namespace Tsume.Core.Devices;

/// <summary>The power limits one machine accepts when they are set by hand.</summary>
/// <param name="Min">The lowest limit its firmware accepts.</param>
/// <param name="MaxSustained">The highest sustained limit Tsume will ask for.</param>
/// <param name="MaxBoost">The highest boost limit its firmware accepts.</param>
public readonly record struct PowerRange(int Min, int MaxSustained, int MaxBoost)
{
    /// <summary>Returns limits inside their ranges, with boost never below sustained.</summary>
    public PowerLimits Clamp(PowerLimits limits)
    {
        var sustained = Math.Clamp(limits.Sustained, Min, MaxSustained);
        var boost = Math.Clamp(limits.Boost, sustained, MaxBoost);
        return new PowerLimits(sustained, boost);
    }
}
