using Tsume.Core.Devices;
using Tsume.Core.Performance;

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

    /// <summary>
    /// True only for a board whose probe, write check and mode check have been run and recorded on a real unit
    /// (docs/hardware/*.txt). Writes to any other board first require the firmware to report WMI interface
    /// major version 2 (<see cref="MsiAcpiBridge"/>), and the charge limit is written only if the firmware
    /// offers one (<see cref="MsiDevice.SetChargeLimit"/>).
    /// </summary>
    public bool CheckedOnDevice { get; init; }

    /// <summary>
    /// The shift mode to write instead of the usual one when the sustained limit being applied equals a given
    /// value. Null on boards without such a rule.
    /// </summary>
    public SustainedShiftRule? SustainedShift { get; init; }
}

/// <summary>
/// When the sustained limit applied is <paramref name="SustainedWatts"/>, the shift register is set to
/// <paramref name="Mode"/>'s code instead of the one <see cref="MsiDevice.ShiftModeFor"/> would otherwise choose.
/// </summary>
public sealed record SustainedShiftRule(int SustainedWatts, PerformanceMode Mode);
