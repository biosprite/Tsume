using System.Diagnostics;
using System.Text;
using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Performance;
using Tsume.Core.Telemetry;

namespace Tsume.Hardware.Msi;

/// <summary>
/// An MSI handheld: <see cref="IDevice"/> carried out through the board's embedded controller. All addresses
/// and codes come from the <see cref="MsiBoard"/> it is given.
/// </summary>
public sealed class MsiDevice(IMsiFirmware firmware, MsiBoard board) : IDevice
{
    /// <summary>MSI Center M's supervisor process. While it runs, MSI Center M keeps the mode.</summary>
    private const string MsiCenterSupervisor = "MSI_Center_M_Server";

    private readonly MsiRegisters _at = board.Registers;

    /// <summary>Keeps the several writes of one change together: the pipe and the service's keepers both make them.</summary>
    private readonly Lock _oneIntentionAtATime = new();

    /// <summary>The device for this board, or null unless it is an MSI board with a verified map.</summary>
    public static IDevice? TryOpen(Board machine) =>
        MsiBoards.Find(machine) is { } known ? new MsiDevice(new MsiAcpiBridge(machine, known), known) : null;

    public DeviceProfile Profile => board.Profile;

    public bool MakerToolIsRunning
    {
        get
        {
            var found = Process.GetProcessesByName(MsiCenterSupervisor);
            foreach (var process in found) process.Dispose();
            return found.Length > 0;
        }
    }

    /// <summary>
    /// The shift mode written to register <c>0xD2</c>. MSI's EC ignores the power-limit registers
    /// (<c>0x50</c>/<c>0x51</c>) in its built-in shift modes (Endurance/Balanced/Performance) and
    /// enforces its own hardcoded table instead, so boards that support Manual use Manual shift mode
    /// whenever Tsume applies watts.
    /// </summary>
    public static PerformanceMode ShiftModeFor(MsiBoard board, PerformanceMode mode) =>
        board.Shift.Codes.ContainsKey(PerformanceMode.Manual) ? PerformanceMode.Manual : mode;

    public PerformanceMode HardwareModeFor(PerformanceMode mode) => ShiftModeFor(board, mode);

    public void SetPerformance(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null)
    {
        var limits = Profile.LimitsFor(mode, manual, custom)
            ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "This device does not offer that mode.");

        var shiftMode = ShiftModeFor(board, mode);

        lock (_oneIntentionAtATime)
        {
            Write(_at.ShiftMode, nameof(_at.ShiftMode), board.Shift.Encode(shiftMode));

            if (_at.PowerLimitFastBoost is { } fastBoostReg)
            {
                var fppt = limits.FastBoost ?? limits.Boost;
                // MSI's AMD order (UserScenarioX.setPowerLimit_AMD):
                // SPL -> floor (7 W), SPPT -> floor (9 W), then FPPT -> SPPT -> SPL.
                Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), board.PowerFloor);
                Write(_at.PowerLimitBoost, nameof(_at.PowerLimitBoost), board.BoostFloor ?? board.PowerFloor);
                Write(fastBoostReg, nameof(_at.PowerLimitFastBoost), (byte)fppt);
                Write(_at.PowerLimitBoost, nameof(_at.PowerLimitBoost), (byte)limits.Boost);
                Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), (byte)limits.Sustained);
            }
            else
            {
                // MSI's Intel order: sustained to the floor first, so boost is never below sustained mid-change.
                Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), board.PowerFloor);
                Write(_at.PowerLimitBoost, nameof(_at.PowerLimitBoost), (byte)limits.Boost);
                Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), (byte)limits.Sustained);
            }

            // Clear AMD STAMP boost after writing limits (mirrors UserScenarioX.setPowerLimit_AMDSTAMP(false)):
            // bit 7 = supported, bit 0 = reset/off, bit 1 = on (cleared so both bits are never set together).
            if (_at.AmdStamp is { } stampReg)
            {
                var stamp = Read(stampReg, nameof(_at.AmdStamp));
                if ((stamp & _at.AmdStampSupported) != 0)
                {
                    var cleared = (byte)((stamp & ~_at.AmdStampActive) | _at.AmdStampReset);
                    if (cleared != stamp)
                        Write(stampReg, nameof(_at.AmdStamp), cleared);
                }
            }
        }
    }

    /// <summary>Three register reads (four on AMD 3-tier boards) — much cheaper than a snapshot.</summary>
    public (PerformanceMode Mode, PowerLimits Limits) Performance()
    {
        lock (_oneIntentionAtATime)
        {
            return (ReadMode(), ReadLimits());
        }
    }

    private PerformanceMode ReadMode() => board.Shift.Decode(Read(_at.ShiftMode, nameof(_at.ShiftMode)));

    private PowerLimits ReadLimits() => new(
        Read(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained)),
        Read(_at.PowerLimitBoost, nameof(_at.PowerLimitBoost)),
        _at.PowerLimitFastBoost is { } fastBoostReg
            ? Read(fastBoostReg, nameof(_at.PowerLimitFastBoost))
            : null);

    public void SetFan(FanMode mode)
    {
        lock (_oneIntentionAtATime) switch (mode)
        {
            case FanMode.Auto:
                SetBits(_at.CoolerBoost, nameof(_at.CoolerBoost), _at.CoolerBoostOn, on: false);
                SetBits(_at.FanControl, nameof(_at.FanControl), _at.CustomFanCurve, on: false);
                break;

            case FanMode.Max:
                SetBits(_at.CoolerBoost, nameof(_at.CoolerBoost), _at.CoolerBoostOn, on: true);
                break;

            case FanMode.Custom:
                throw new NotSupportedException("Custom fan curves arrive in milestone 4.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    /// <summary>
    /// Writes one byte: the "limit on" bit plus the percentage. The firmware stops charging there and, by its
    /// own rule, resumes ten points lower.
    /// </summary>
    public void SetChargeLimit(int percent)
    {
        if (!Profile.ChargeLimits.Contains(percent))
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "This device does not offer that charge limit.");

        lock (_oneIntentionAtATime)
        {
            var wanted = (byte)(_at.ChargeLimitOn | percent);
            Write(_at.ChargeLimit, nameof(_at.ChargeLimit), wanted);

            // The firmware may accept a write yet keep its own value, so read back and fail if it did.
            var kept = Read(_at.ChargeLimit, nameof(_at.ChargeLimit));
            if (kept != wanted)
                throw new FirmwareException($"The firmware did not keep the charge limit: wrote 0x{wanted:X2}, read 0x{kept:X2}.");
        }

        // MSI Center M writes its remembered level over the firmware's whenever it starts, so update its record too.
        MsiCenterBattery.Mirror(percent);
    }

    public int? ChargeLimit()
    {
        lock (_oneIntentionAtATime)
        {
            return ReadChargeLimit();
        }
    }

    private int? ReadChargeLimit()
    {
        var value = Read(_at.ChargeLimit, nameof(_at.ChargeLimit));
        if ((value & _at.ChargeLimitOn) == 0) return null;

        var percent = value & ~_at.ChargeLimitOn;
        return percent is > 0 and <= 100 ? percent : null;
    }

    public HardwareSnapshot Snapshot()
    {
        lock (_oneIntentionAtATime)
        {
            return new()
            {
                Mode = ReadMode(),
                Limits = ReadLimits(),
                Fan = ReadFanMode(),
                ChargeLimit = Profile.ChargeLimits.Count > 0 ? ReadChargeLimit() : null,
                CpuTemperature = ReadCpuTemperature(),
                FanRpm = ReadFanRpm(),
                FirmwareVersion = ReadFirmwareVersion(),
            };
        }
    }

    public LiveReadings Readings() => new(ReadCpuTemperature(), ReadFanRpm());

    private FanMode ReadFanMode()
    {
        if ((Read(_at.CoolerBoost, nameof(_at.CoolerBoost)) & _at.CoolerBoostOn) != 0) return FanMode.Max;
        if ((Read(_at.FanControl, nameof(_at.FanControl)) & _at.CustomFanCurve) != 0) return FanMode.Custom;
        return FanMode.Auto;
    }

    private int? ReadCpuTemperature()
    {
        var celsius = Read(_at.CpuTemperature, nameof(_at.CpuTemperature));
        return celsius is > 0 and < 120 ? celsius : null;
    }

    /// <summary>
    /// The firmware reports each fan as a 16-bit big-endian tachometer period. RPM = the board's tachometer
    /// constant / period; a period of zero means the fan is stopped.
    /// </summary>
    private int? ReadFanRpm()
    {
        var reply = firmware.Get("Get_Fan", selector: 0);
        if (!reply.Accepted) return null;

        var period = (reply.Data[0] << 8) | reply.Data[1];
        return period == 0 ? 0 : board.Tachometer / period;
    }

    private string? ReadFirmwareVersion()
    {
        var reply = firmware.Get("Get_EC");
        if (!reply.Accepted) return null;

        // Data[1..] holds "1T91EMS1.103" followed by the build date and time.
        var text = Encoding.ASCII.GetString(reply.Data, 1, 12).TrimEnd('\0', ' ');
        return text.Length > 0 ? text : null;
    }

    private byte Read(byte register, string name)
    {
        var reply = firmware.Get("Get_Data", register);
        return reply.Accepted
            ? reply.Data[0]
            : throw new FirmwareException($"The firmware refused to read {name}.");
    }

    private void Write(byte register, string name, byte value)
    {
        var reply = firmware.Set("Set_Data", register, [value]);
        if (!reply.Accepted)
            throw new FirmwareException($"The firmware refused to write {name} = 0x{value:X2}.");
    }

    private void SetBits(byte register, string name, byte mask, bool on)
    {
        var current = Read(register, name);
        var wanted = (byte)(on ? current | mask : current & ~mask);
        if (wanted != current) Write(register, name, wanted);
    }

    public void Dispose() => firmware.Dispose();
}
