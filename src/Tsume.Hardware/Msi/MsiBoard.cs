using Tsume.Core.Devices;

namespace Tsume.Hardware.Msi;

/// <summary>
/// What differs between MSI boards, as data: register addresses, mode codes and what the board offers.
/// <see cref="MsiDevice"/> is the same code for all of them.
/// </summary>
public sealed record MsiBoard
{
    /// <summary>The mainboard's model name, e.g. "MS-1T91". A machine is recognised by it.</summary>
    public required string Model { get; init; }

    public required MsiRegisters Registers { get; init; }
    public required MsiShiftModes Shift { get; init; }

    /// <summary>What the panel is told this machine can do.</summary>
    public required DeviceProfile Profile { get; init; }

    /// <summary>Watts the sustained limit is dropped to while limits change, so boost is never below sustained.</summary>
    public required byte PowerFloor { get; init; }

    /// <summary>Watts the boost limit (SPPT) is dropped to before writing FPPT on AMD 3-tier boards. Null on Intel boards.</summary>
    public byte? BoostFloor { get; init; }

    /// <summary>Fan speed in RPM is this divided by the tachometer period the firmware reports.</summary>
    public int Tachometer { get; init; } = 480_000;
}
