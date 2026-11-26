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

    /// <summary>Firmware stock fan curve, used when live tables cannot be trusted. Null on unverified boards.</summary>
    public StockFanSpeeds? StockFanCurve { get; init; }

    /// <summary>EC temperature-table layout and falling hysteresis mode.</summary>
    public MsiFanTemperatureMap FanTemperatures { get; init; } = MsiFanTemperatureMap.Shifted;
}

/// <summary>
/// Indices of the six rising temperatures in <c>Get_Temperature(1|2)</c>, optional 7th slot, and falling hysteresis
/// in °C for <c>Get_Thermal(1|2)[1..6]</c> (null when the EC stores relative deltas there).
/// </summary>
public sealed record MsiFanTemperatureMap(int[] Points, int? OverflowIndex = null, int? FallingHysteresisC = 6)
{
    /// <summary>MS-1T91: rising points at 0x69..0x6E, 7th slot at 0x6F, absolute falling hysteresis.</summary>
    public static readonly MsiFanTemperatureMap Base68 = new([0, 3, 4, 5, 6, 7], OverflowIndex: 1, FallingHysteresisC: 6);

    /// <summary>MS-1T52, MS-1T42, MS-1T41: rising points at 0x6A..0x6F (0x69 stays 0), absolute falling hysteresis.</summary>
    public static readonly MsiFanTemperatureMap Shifted = new([3, 4, 5, 6, 7, 1], OverflowIndex: null, FallingHysteresisC: 6);

    /// <summary>MS-1T8K: rising points at 0x6A..0x6F, relative falling deltas (left untouched).</summary>
    public static readonly MsiFanTemperatureMap ShiftedRelative = new([3, 4, 5, 6, 7, 1], OverflowIndex: null, FallingHysteresisC: null);
}

/// <summary>Stock six-point fan speeds and optional temperatures/hysteresis per fan, coolest first.</summary>
public sealed record StockFanSpeeds(
    int[] Fan1,
    int[]? Fan2,
    int[]? Temperatures = null,
    int? OverflowTemp = null,
    int[]? Falling1 = null,
    int[]? Falling2 = null);

/// <summary>
/// When the sustained limit applied is <paramref name="SustainedWatts"/>, the shift register is set to
/// <paramref name="Mode"/>'s code instead of the one <see cref="MsiDevice.ShiftModeFor"/> would otherwise choose.
/// </summary>
public sealed record SustainedShiftRule(int SustainedWatts, PerformanceMode Mode);
