namespace Tsume.Hardware.Msi;

/// <summary>
/// One MSI board's embedded-controller register addresses. They differ between boards, so each board in
/// <see cref="MsiBoards"/> states its own, confirmed against that machine.
/// </summary>
public sealed record MsiRegisters
{
    /// <summary>Sustained CPU power limit, in watts (Intel PL1 / AMD SPL).</summary>
    public required byte PowerLimitSustained { get; init; }

    /// <summary>Short boost CPU power limit, in watts (Intel PL2 / AMD SPPT).</summary>
    public required byte PowerLimitBoost { get; init; }

    /// <summary>Fast boost APU power limit, in watts (AMD FPPT). Null on Intel boards.</summary>
    public byte? PowerLimitFastBoost { get; init; }

    /// <summary>Live CPU temperature in °C. Read-only.</summary>
    public required byte CpuTemperature { get; init; }

    /// <summary>Holds the bit that pins the fans at full speed ("Cooler Boost").</summary>
    public required byte CoolerBoost { get; init; }

    /// <summary>AMD STAMP boost control register (bit 7 supported, bit 0 reset/off, bit 1 on). Null on Intel boards.</summary>
    public byte? AmdStamp { get; init; }

    /// <summary>Performance ("shift") mode. See <see cref="MsiShiftModes"/>.</summary>
    public required byte ShiftMode { get; init; }

    /// <summary>Holds the bit that makes the fans follow the custom curve instead of the built-in one.</summary>
    public required byte FanControl { get; init; }

    /// <summary>Battery charge limit: bit 7 enables it, bits 0–6 are the percentage.</summary>
    public required byte ChargeLimit { get; init; }

    public byte CoolerBoostOn { get; init; } = 0b1000_0000;
    public byte CustomFanCurve { get; init; } = 0b1000_0000;
    public byte ChargeLimitOn { get; init; } = 0b1000_0000;
    public byte AmdStampSupported { get; init; } = 0b1000_0000;
    public byte AmdStampReset { get; init; } = 0b0000_0001;
    public byte AmdStampActive { get; init; } = 0b0000_0010;

    /// <summary>Every register by name, in address order — for the reports.</summary>
    public IEnumerable<(string Name, byte Address)> All
    {
        get
        {
            var list = new List<(string Name, byte Address)>
            {
                (nameof(PowerLimitSustained), PowerLimitSustained),
                (nameof(PowerLimitBoost), PowerLimitBoost),
                (nameof(CpuTemperature), CpuTemperature),
                (nameof(CoolerBoost), CoolerBoost),
                (nameof(ShiftMode), ShiftMode),
                (nameof(FanControl), FanControl),
                (nameof(ChargeLimit), ChargeLimit),
            };
            if (PowerLimitFastBoost is { } fastBoost) list.Add((nameof(PowerLimitFastBoost), fastBoost));
            if (AmdStamp is { } amdStamp) list.Add((nameof(AmdStamp), amdStamp));
            return list.OrderBy(register => register.Address);
        }
    }

    /// <summary>Exactly the registers Tsume writes.</summary>
    public IEnumerable<(string Name, byte Address)> Written
    {
        get
        {
            yield return (nameof(ShiftMode), ShiftMode);
            yield return (nameof(PowerLimitSustained), PowerLimitSustained);
            yield return (nameof(PowerLimitBoost), PowerLimitBoost);
            if (PowerLimitFastBoost is { } fastBoost) yield return (nameof(PowerLimitFastBoost), fastBoost);
            yield return (nameof(CoolerBoost), CoolerBoost);
            yield return (nameof(FanControl), FanControl);
            yield return (nameof(ChargeLimit), ChargeLimit);
        }
    }
}
