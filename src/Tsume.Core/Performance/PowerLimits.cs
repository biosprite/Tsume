namespace Tsume.Core.Performance;

/// <summary>
/// Processor power limits in watts. Sustained is Intel's PL1 (AMD's SPL); Boost is Intel's PL2 (AMD's SPPT);
/// FastBoost is AMD's FPPT (null on 2-tier Intel boards). The accepted range is in <see cref="Devices.PowerRange"/>.
/// </summary>
public readonly record struct PowerLimits(int Sustained, int Boost, int? FastBoost = null)
{
    public override string ToString() => FastBoost is { } fast
        ? $"{Sustained} W / {Boost} W / {fast} W"
        : $"{Sustained} W / {Boost} W";
}
