namespace Tsume.Core.Performance;

/// <summary>
/// Processor power limits in watts. Sustained is Intel's PL1 (AMD's STAPM and slow limit);
/// Boost is Intel's PL2 (AMD's fast limit). The accepted range is in <see cref="Devices.PowerRange"/>.
/// </summary>
public readonly record struct PowerLimits(int Sustained, int Boost)
{
    public override string ToString() => $"{Sustained} W / {Boost} W";
}
