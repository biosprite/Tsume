namespace Tsume.Hardware.Msi;

/// <summary>
/// One MSI board's embedded-controller register addresses. They differ between boards, so each board in
/// <see cref="MsiBoards"/> states its own, confirmed against that machine.
/// </summary>
public sealed record MsiRegisters
{
    /// <summary>Sustained CPU power limit, in watts.</summary>
    public required byte PowerLimitSustained { get; init; }

    /// <summary>Boost CPU power limit, in watts.</summary>
    public required byte PowerLimitBoost { get; init; }

    /// <summary>Live CPU temperature in °C. Read-only.</summary>
    public required byte CpuTemperature { get; init; }

    /// <summary>Holds the bit that pins the fans at full speed ("Cooler Boost").</summary>
    public required byte CoolerBoost { get; init; }

    /// <summary>Performance ("shift") mode. See <see cref="MsiShiftModes"/>.</summary>
    public required byte ShiftMode { get; init; }

    /// <summary>Holds the bit that makes the fans follow the custom curve instead of the built-in one.</summary>
    public required byte FanControl { get; init; }

    /// <summary>Battery charge limit: bit 7 enables it, bits 0–6 are the percentage.</summary>
    public required byte ChargeLimit { get; init; }

    public byte CoolerBoostOn { get; init; } = 0b1000_0000;
    public byte CustomFanCurve { get; init; } = 0b1000_0000;
    public byte ChargeLimitOn { get; init; } = 0b1000_0000;

    /// <summary>Every register by name, in address order — for the reports.</summary>
    public IEnumerable<(string Name, byte Address)> All => new (string Name, byte Address)[]
    {
        (nameof(PowerLimitSustained), PowerLimitSustained),
        (nameof(PowerLimitBoost), PowerLimitBoost),
        (nameof(CpuTemperature), CpuTemperature),
        (nameof(CoolerBoost), CoolerBoost),
        (nameof(ShiftMode), ShiftMode),
        (nameof(FanControl), FanControl),
        (nameof(ChargeLimit), ChargeLimit),
    }.OrderBy(register => register.Address);

    /// <summary>Exactly the registers Tsume writes.</summary>
    public IEnumerable<(string Name, byte Address)> Written =>
    [
        (nameof(ShiftMode), ShiftMode),
        (nameof(PowerLimitSustained), PowerLimitSustained),
        (nameof(PowerLimitBoost), PowerLimitBoost),
        (nameof(CoolerBoost), CoolerBoost),
        (nameof(FanControl), FanControl),
        (nameof(ChargeLimit), ChargeLimit),
    ];
}
