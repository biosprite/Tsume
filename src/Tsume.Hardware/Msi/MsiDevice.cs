using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
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
/// <param name="fanStockFile">Where the firmware's own fan speeds are kept; null for the helper's data folder.</param>
public sealed class MsiDevice(IMsiFirmware firmware, MsiBoard board, string? fanStockFile = null) : IDevice
{
    /// <summary>MSI Center M's supervisor process. While it runs, MSI Center M keeps the mode.</summary>
    private const string MsiCenterSupervisor = "MSI_Center_M_Server";

    // Fan/temperature tables are 8 bytes; thermal hysteresis tables are 7 bytes.
    // Byte 2 of Get_Temperature is the EC critical shutdown threshold and is never modified.
    private const int FanTableLength = 8;
    private const int ThermalTableLength = 7;

    private const int FirstSpeed = 1;
    private const int FirstFalling = 1;
    private const int CriticalTempIndex = 2;
    private const int CurvePoints = 6;

    /// <summary>A curve temperature outside this range means the table is not what Tsume expects.</summary>
    private const int CoolestPoint = 20, HottestPoint = 110;

    private readonly MsiRegisters _at = board.Registers;

    private readonly MsiFanStock _stockFile = new(fanStockFile ?? Path.Combine(Core.DataFolder.Path, "fan-stock.json"));

    // The fan fields below are guarded by _oneIntentionAtATime.

    /// <summary>What fan-stock.json holds for this board, once read; see <see cref="LoadFanEntry"/>.</summary>
    private MsiFanStock.Entry? _fanEntry;
    private bool _fanEntryLoaded;

    /// <summary>The controller firmware's version text once read; it cannot change without a restart.</summary>
    private string? _firmwareBuild;

    /// <summary>The curve the helper keeps putting back; tables holding it are never taken for the firmware's own.</summary>
    private int[]? _chosenCurve;

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
    /// The shift mode written to register <c>0xD2</c> when <paramref name="limits"/> are applied. On MS-1T91
    /// the EC was observed to ignore the power-limit registers (<c>0x50</c>/<c>0x51</c>) in its built-in shift
    /// modes (Endurance/Balanced/Performance) and enforce its own table instead; this is not confirmed on other
    /// boards. So boards that support Manual use Manual shift mode whenever Tsume applies watts, except where
    /// the board's <see cref="MsiBoard.SustainedShift"/> rule matches the sustained limit.
    /// </summary>
    public static PerformanceMode ShiftModeFor(MsiBoard board, PerformanceMode mode, PowerLimits limits)
    {
        if (board.SustainedShift is { } rule && limits.Sustained == rule.SustainedWatts)
            return rule.Mode;

        return board.Shift.Codes.ContainsKey(PerformanceMode.Manual) ? PerformanceMode.Manual : mode;
    }

    public PerformanceMode HardwareModeFor(PerformanceMode mode, PowerLimits limits) => ShiftModeFor(board, mode, limits);

    /// <summary>
    /// The profile's limits, or its battery limits while on battery, as MSI Center M does. The power source is
    /// asked only when the limits depend on it and the caller did not supply it.
    /// </summary>
    public PowerLimits? LimitsToApply(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null, bool? onBattery = null,
        PowerLimits? customOnBattery = null) =>
        Profile.FollowsPowerSource && (onBattery ?? PowerSource.IsOnBattery())
            ? Profile.LimitsOnBattery(mode, manual, customOnBattery)
            : Profile.LimitsFor(mode, manual, custom);

    public void SetPerformance(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null, bool? onBattery = null,
        PowerLimits? customOnBattery = null)
    {
        var limits = LimitsToApply(mode, manual, custom, onBattery, customOnBattery)
            ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "This device does not offer that mode.");

        var shiftMode = ShiftModeFor(board, mode, limits);

        lock (_oneIntentionAtATime)
        {
            if (_at.PowerLimitFastBoost is { } fastBoostReg)
            {
                var fppt = limits.FastBoost ?? limits.Boost;

                // The sequence writes ShiftMode and drops SPL and SPPT to their floors first, so a write refused
                // part-way would leave the APU shifted or capped there. Read the original state before writing
                // anything so ShiftMode side effects cannot overwrite the snapshot we roll back to.
                byte? beforeShift = null;
                PowerLimits? beforeLimits = null;
                try
                {
                    beforeShift = Read(_at.ShiftMode, nameof(_at.ShiftMode));
                    beforeLimits = ReadLimits();
                }
                catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
                {
                    // Cannot read: go ahead without whatever part could not be read.
                }

                Write(_at.ShiftMode, nameof(_at.ShiftMode), board.Shift.Encode(shiftMode));

                try
                {
                    // MSI's AMD order:
                    // SPL -> floor (7 W), SPPT -> floor (9 W), then FPPT -> SPPT -> SPL.
                    Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), board.PowerFloor);
                    Write(_at.PowerLimitBoost, nameof(_at.PowerLimitBoost), board.BoostFloor ?? board.PowerFloor);
                    Write(fastBoostReg, nameof(_at.PowerLimitFastBoost), (byte)fppt);
                    Write(_at.PowerLimitBoost, nameof(_at.PowerLimitBoost), (byte)limits.Boost);
                    Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), (byte)limits.Sustained);
                }
                catch (Exception) when (beforeShift is not null || beforeLimits is not null)
                {
                    // Best effort, in the same safe order as the change itself (shift mode, then floors, then
                    // highest limit first) so no limit is ever written below the one under it; then the original error.
                    if (beforeShift is { } originalShift)
                        TryWrite(_at.ShiftMode, originalShift);

                    if (beforeLimits is { } original)
                    {
                        TryWrite(_at.PowerLimitSustained, board.PowerFloor);
                        TryWrite(_at.PowerLimitBoost, board.BoostFloor ?? board.PowerFloor);
                        TryWrite(fastBoostReg, (byte)(original.FastBoost ?? original.Boost));
                        TryWrite(_at.PowerLimitBoost, (byte)original.Boost);
                        TryWrite(_at.PowerLimitSustained, (byte)original.Sustained);
                    }
                    throw;
                }
            }
            else
            {
                Write(_at.ShiftMode, nameof(_at.ShiftMode), board.Shift.Encode(shiftMode));

                // MSI's Intel order: sustained to the floor first, so boost is never below sustained mid-change.
                Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), board.PowerFloor);
                Write(_at.PowerLimitBoost, nameof(_at.PowerLimitBoost), (byte)limits.Boost);
                Write(_at.PowerLimitSustained, nameof(_at.PowerLimitSustained), (byte)limits.Sustained);
            }

            // Turn AMD STAMP boost off after writing limits, as MSI Center M does:
            // if bit 7 (supported) reads set, write the byte back with bit 0 set — always, other bits as read.
            if (_at.AmdStamp is { } stampReg)
            {
                try
                {
                    var stamp = Read(stampReg, nameof(_at.AmdStamp));
                    if ((stamp & _at.AmdStampSupported) != 0)
                        Write(stampReg, nameof(_at.AmdStamp), (byte)(stamp | _at.AmdStampReset));
                }
                catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
                {
                    // Best effort: the limits are already written, so a failed STAMP read or write must not fail
                    // the change (the helper would report failure and not remember the choice). Tsume.Hardware has
                    // no log to record it in.
                }
            }
        }
    }

    /// <summary>One write whose failure is ignored — for putting limits back after another write failed.</summary>
    private void TryWrite(byte register, byte value)
    {
        try
        {
            firmware.Set("Set_Data", register, [value]);
        }
        catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
        {
            // The original failure is what gets reported.
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

    /// <summary>
    /// Auto, as MSI Center M does it: Cooler Boost off, the firmware's own speeds written back, then the custom
    /// curve off. The firmware keeps following its tables with the custom curve off, so the speeds matter. If they
    /// cannot all be put back, the custom curve is still switched off, and the tables stay marked as possibly
    /// custom so they are never later taken for the firmware's own. Max sets Cooler Boost and leaves the curve.
    /// </summary>
    public void SetFan(FanMode mode)
    {
        lock (_oneIntentionAtATime) switch (mode)
        {
            case FanMode.Auto:
                SetBits(_at.CoolerBoost, nameof(_at.CoolerBoost), _at.CoolerBoostOn, on: false);
                if (Profile.FanCurve is not null && CustomCurveOn()) MarkTables(mayBeCustom: true);
                if (PutBackStockSpeeds()) MarkTables(mayBeCustom: false);
                SetBits(_at.FanControl, nameof(_at.FanControl), _at.CustomFanCurve, on: false);
                break;

            case FanMode.Max:
                SetBits(_at.CoolerBoost, nameof(_at.CoolerBoost), _at.CoolerBoostOn, on: true);
                break;

            case FanMode.Custom:
                throw new NotSupportedException("Custom fans need a curve: set one with SetFanCurve.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    public FanMode Fan()
    {
        lock (_oneIntentionAtATime)
        {
            return ReadFanMode();
        }
    }

    public FanCurveState? FanCurve()
    {
        if (Profile.FanCurve is not { } limits) return null;

        lock (_oneIntentionAtATime)
        {
            if (ReadFanTables(limits) is not { } now) return null;

            var speeds = SpeedsIn(now.Fan1);
            var stock = KnownStock(now, CustomCurveOn());
            return new()
            {
                Temperatures = now.Temperatures,
                Speeds = speeds,
                Stock = stock?.Fan1 ?? speeds,
                StockTemperatures = stock?.Temperatures,
                StockKnown = stock is not null,
            };
        }
    }

    /// <summary>
    /// Writes fan speeds, custom temperatures, and falling hysteresis (with read-back verification), then enables
    /// Custom mode and clears Cooler Boost. Rolls back modified tables on failure.
    /// </summary>
    public IReadOnlyList<int> SetFanCurve(IReadOnlyList<int> speeds)
    {
        var limits = Profile.FanCurve ?? throw new NotSupportedException("This device takes no custom fan curve.");

        lock (_oneIntentionAtATime)
        {
            // Nothing is written until the tables have been read and found sane, and the stock tables saved if they can be.
            var before = ReadFanTables(limits)
                ?? throw new FirmwareException("The firmware's fan curve does not look as expected, so Tsume does not write one.");
            var customBefore = CustomCurveOn();
            var boostBefore = (Read(_at.CoolerBoost, nameof(_at.CoolerBoost)) & _at.CoolerBoostOn) != 0;
            var stock = KnownStock(before, customBefore);

            if (!LoadFanEntry())
                throw new FirmwareException("Tsume could not read what it saved about the fan tables, so it does not write a curve now.");
            var markedBefore = _fanEntry?.TablesMayBeCustom == true;

            // The stock speeds are unknown, yet nothing says the tables are custom: they may hold the only copy of
            // the firmware's own curve, and a curve written over it could never be undone.
            if (stock is null && !customBefore && !markedBefore)
                throw new FirmwareException("Tsume could not save the firmware's own fan curve, so it does not write one.");

            var customTemps = TargetTemperaturesFor(limits, before.Temp1);
            var canRestoreTemps = stock?.Temperatures is not null
                && (board.FanTemperatures.FallingHysteresisC is null || stock.Falling1 is not null);
            var writeCustomTemps = customTemps is not null && (canRestoreTemps || before.Temperatures.SequenceEqual(customTemps));
            var activeTemps = writeCustomTemps ? customTemps! : before.Temperatures;

            // An unknown stock curve does not lower the floors: what the firmware holds now may be anyone's curve.
            IReadOnlyList<int>? stockAtActive = stock is null ? null
                : stock.Temperatures is { } stockTemps && !stockTemps.SequenceEqual(activeTemps)
                    ? FanCurveLimits.Sample(stockTemps, stock.Fan1, activeTemps)
                    : stock.Fan1;
            var curve = limits.Clamp(activeTemps, speeds, stockAtActive);

            // Fan 2 is written only when its own speeds can be put back too (or nothing can be, the tables already custom).
            var writeFan2 = before.Fan2 is not null && (stock is null || stock.Fan2 is not null);
            var writeTemp2 = writeCustomTemps && writeFan2 && before.Temp2 is not null;
            var writeThermal1 = writeCustomTemps && board.FanTemperatures.FallingHysteresisC is not null && before.Thermal1 is not null;
            var writeThermal2 = writeCustomTemps && writeFan2 && board.FanTemperatures.FallingHysteresisC is not null
                && before.Thermal2 is not null && (stock is null || stock.Falling2 is not null);

            if (!MarkTables(mayBeCustom: true))
                throw new FirmwareException("Tsume could not save its fan state, so it does not write a curve.");

            try
            {
                WriteSpeeds(1, before.Fan1, curve);
                if (writeFan2) WriteSpeeds(2, before.Fan2!, curve);

                if (writeCustomTemps)
                {
                    WriteTemperatures(1, before.Temp1, activeTemps, OverflowFor(activeTemps, before.Temp1));
                    if (writeTemp2) WriteTemperatures(2, before.Temp2!, activeTemps, OverflowFor(activeTemps, before.Temp2!));

                    if (board.FanTemperatures.FallingHysteresisC is { } hysteresis)
                    {
                        var falling = FallingFor(activeTemps, hysteresis);
                        if (writeThermal1) WriteFalling(1, before.Thermal1!, falling);
                        if (writeThermal2) WriteFalling(2, before.Thermal2!, falling);
                    }
                }

                SetBits(_at.FanControl, nameof(_at.FanControl), _at.CustomFanCurve, on: true);
                SetBits(_at.CoolerBoost, nameof(_at.CoolerBoost), _at.CoolerBoostOn, on: false);

                if (ReadFanMode() != FanMode.Custom)
                    throw new FirmwareException("The firmware did not keep the fans on the custom curve.");
            }
            catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
            {
                if (RollBackCurve(before, writeFan2, writeCustomTemps, writeTemp2, writeThermal1, writeThermal2, customBefore, boostBefore)
                    && !markedBefore)
                    MarkTables(mayBeCustom: false);
                if (error is FirmwareException) throw;
                throw new FirmwareException($"Writing the fan curve failed: {error.Message}");
            }

            return curve;
        }
    }

    public bool HoldsFanCurve(IReadOnlyList<int> speeds)
    {
        if (Profile.FanCurve is not { } limits || speeds.Count != CurvePoints) return false;

        lock (_oneIntentionAtATime)
        {
            if (!CustomCurveOn() || ReadFanTables(limits) is not { } now) return false;
            if (!SpeedsIn(now.Fan1).SequenceEqual(speeds)) return false;

            // Fan 2 is compared only where SetFanCurve writes it.
            var stock = KnownStock(now, customOn: true);
            var checksFan2 = now.Fan2 is not null && (stock is null || stock.Fan2 is not null);
            if (checksFan2 && !SpeedsIn(now.Fan2!).SequenceEqual(speeds)) return false;

            if (TargetTemperaturesFor(limits, now.Temp1) is { } wantedTemps)
            {
                var canRestoreTemps = stock?.Temperatures is not null
                    && (board.FanTemperatures.FallingHysteresisC is null || stock.Falling1 is not null);
                if (canRestoreTemps)
                {
                    if (!now.Temperatures.SequenceEqual(wantedTemps)) return false;
                    if (checksFan2 && now.Temp2 is { } temp2 && !TemperaturesIn(temp2).SequenceEqual(wantedTemps)) return false;

                    if (board.FanTemperatures.FallingHysteresisC is { } hysteresis)
                    {
                        var wantedFalling = FallingFor(wantedTemps, hysteresis);
                        if (now.Thermal1 is { } thermal1 && !FallingIn(thermal1).SequenceEqual(wantedFalling)) return false;
                        if (checksFan2 && now.Thermal2 is { } thermal2 && (stock is null || stock.Falling2 is not null)
                            && !FallingIn(thermal2).SequenceEqual(wantedFalling))
                            return false;
                    }
                }
            }

            return true;
        }
    }

    public void NoteChosenFanCurve(IReadOnlyList<int>? speeds)
    {
        lock (_oneIntentionAtATime)
        {
            _chosenCurve = speeds?.ToArray();
        }
    }

    /// <summary>What the firmware's curve tables hold. Secondary tables are null when the firmware has none.</summary>
    private sealed record FanTables(
        int[] Temperatures,
        byte[] Temp1,
        byte[]? Temp2,
        byte[] Fan1,
        byte[]? Fan2,
        byte[]? Thermal1,
        byte[]? Thermal2);

    /// <summary>
    /// The curve tables, or null unless they make sense: six temperatures non-decreasing within a plausible range,
    /// and every speed within the board's <see cref="FanCurveLimits.MaxSpeed"/>. Tsume writes no curve to a firmware
    /// whose tables look otherwise. Fan 2 counts as having no table when the firmware refuses it, or when its six
    /// speeds are all zero while fan 1's are not.
    /// </summary>
    private FanTables? ReadFanTables(FanCurveLimits limits)
    {
        if (ReadTemperatureTable(1) is not { } temp1) return null;

        var temperatures = TemperaturesIn(temp1);
        if (!IsSaneTemperatures(temperatures)) return null;

        if (ReadFanTable(1) is not { } fan1 || !IsSaneSpeeds(SpeedsIn(fan1), limits)) return null;

        var fan2 = ReadFanTable(2);
        if (fan2 is not null && SpeedsIn(fan2).All(speed => speed == 0) && SpeedsIn(fan1).Any(speed => speed != 0))
            fan2 = null;
        if (fan2 is not null && !IsSaneSpeeds(SpeedsIn(fan2), limits)) return null;

        byte[]? temp2 = null;
        if (fan2 is not null && ReadTemperatureTable(2) is { } rawTemp2 && IsSaneTemperatures(TemperaturesIn(rawTemp2)))
            temp2 = rawTemp2;

        byte[]? thermal1 = null;
        byte[]? thermal2 = null;
        if (board.FanTemperatures.FallingHysteresisC is not null)
        {
            thermal1 = ReadThermalTable(1);
            if (fan2 is not null) thermal2 = ReadThermalTable(2);
        }

        return new(temperatures, temp1, temp2, fan1, fan2, thermal1, thermal2);
    }

    /// <summary>One fan's eight-byte temperature table, or null if the firmware refuses it.</summary>
    private byte[]? ReadTemperatureTable(byte fan)
    {
        var reply = firmware.Get("Get_Temperature", fan);
        return reply.Accepted && reply.Data.Length >= FanTableLength ? reply.Data[..FanTableLength] : null;
    }

    /// <summary>One fan's eight-byte speed table, or null if the firmware refuses it.</summary>
    private byte[]? ReadFanTable(byte fan)
    {
        var reply = firmware.Get("Get_Fan", fan);
        return reply.Accepted && reply.Data.Length >= FanTableLength ? reply.Data[..FanTableLength] : null;
    }

    /// <summary>One fan's seven-byte thermal/hysteresis table, or null if the firmware refuses it.</summary>
    private byte[]? ReadThermalTable(byte fan)
    {
        var reply = firmware.Get("Get_Thermal", fan);
        return reply.Accepted && reply.Data.Length >= ThermalTableLength ? reply.Data[..ThermalTableLength] : null;
    }

    /// <summary>The six rising curve temperatures from an eight-byte <c>Get_Temperature</c> table, coolest first.</summary>
    private int[] TemperaturesIn(byte[] table) => [.. board.FanTemperatures.Points.Select(at => (int)table[at])];

    /// <summary>The optional 7th rising temperature byte (<c>0x6F</c> on MS-1T91), or null on boards without one.</summary>
    private int? OverflowIn(byte[] table) => board.FanTemperatures.OverflowIndex is { } at ? table[at] : null;

    /// <summary>Bytes 1–6. Byte 0 is the fan's live duty and changes by itself; byte 7 is the firmware's.</summary>
    private static int[] SpeedsIn(byte[] table) => [.. table.Skip(FirstSpeed).Take(CurvePoints).Select(speed => (int)speed)];

    /// <summary>Bytes 1–6 of <c>Get_Thermal(1|2)</c>: the six falling hysteresis values.</summary>
    private static int[] FallingIn(byte[] table) => [.. table.Skip(FirstFalling).Take(CurvePoints).Select(temp => (int)temp)];

    /// <summary>Validates six non-decreasing temperatures in range (stock tables on some boards repeat the top point).</summary>
    private static bool IsSaneTemperatures(int[]? temperatures)
    {
        if (temperatures is not { Length: CurvePoints }) return false;
        for (var point = 0; point < temperatures.Length; point++)
        {
            if (temperatures[point] is < CoolestPoint or > HottestPoint) return false;
            if (point > 0 && temperatures[point] < temperatures[point - 1]) return false;
        }
        return temperatures[^1] > temperatures[0];
    }

    /// <summary>Returns the board's custom temperatures if valid and strictly below critical (<c>table[2]</c>).</summary>
    private static IReadOnlyList<int>? TargetTemperaturesFor(FanCurveLimits limits, byte[] tempTable)
    {
        if (limits.CustomTemperatures is not { Count: CurvePoints } custom) return null;
        for (var i = 0; i < custom.Count; i++)
        {
            if (custom[i] is < CoolestPoint or > HottestPoint) return null;
            if (i > 0 && custom[i] <= custom[i - 1]) return null;
        }

        var critical = tempTable[CriticalTempIndex];
        if (critical is >= 80 and <= 120 && custom[^1] >= critical) return null;

        return custom;
    }

    /// <summary>Optional 7th rising slot (<c>0x6F</c>): kept above the last curve point and below critical.</summary>
    private int? OverflowFor(IReadOnlyList<int> temperatures, byte[] tempTable)
    {
        if (board.FanTemperatures.OverflowIndex is null) return null;

        var critical = tempTable[CriticalTempIndex];
        var ceiling = critical is >= 80 and <= 120 ? critical - 2 : HottestPoint;
        return Math.Clamp(temperatures[^1] + 5, temperatures[^1], ceiling);
    }

    /// <summary>Absolute falling thresholds: <paramref name="hysteresis"/> °C below each rising point.</summary>
    private static int[] FallingFor(IReadOnlyList<int> temperatures, int hysteresis) =>
        [.. temperatures.Select(temp => Math.Max(10, temp - hysteresis))];

    private bool CustomCurveOn() => (Read(_at.FanControl, nameof(_at.FanControl)) & _at.CustomFanCurve) != 0;

    /// <summary>Reads fan-stock.json once. False while it cannot be read; then nothing is saved over it.</summary>
    private bool LoadFanEntry()
    {
        if (_fanEntryLoaded) return true;
        if (!_stockFile.TryLoad(board.Model, out var entry)) return false;

        _fanEntry = entry;
        _fanEntryLoaded = true;
        return true;
    }

    /// <summary>Sets or clears the "tables may hold a custom curve" marker, on disk. False if it could not be saved.</summary>
    private bool MarkTables(bool mayBeCustom)
    {
        if (!LoadFanEntry()) return false;
        if (_fanEntry?.TablesMayBeCustom == mayBeCustom) return true;

        _fanEntry = (_fanEntry ?? new(board.Model, null, null, null, false)) with { TablesMayBeCustom = mayBeCustom };
        return _stockFile.Save(_fanEntry);
    }

    private string? FirmwareBuild() => _firmwareBuild ??= ReadFirmwareBuild();

    /// <summary>
    /// The firmware's own speeds and temperatures, or null if they are not known. In order:
    /// <list type="bullet">
    /// <item>Saved ones for this board, unless the firmware build is definitely another and the tables are known
    /// to be clean, when they are taken afresh. A build that cannot be read is not taken for a change.</item>
    /// <item>Otherwise, when the tables are known to be clean, they are taken and saved: the custom curve is off,
    /// they are not marked as possibly custom, they do not hold the curve the helper keeps, and on a board known
    /// to have two tables both were read.</item>
    /// <item>Otherwise the board's built-in ones, if it has them; otherwise none.</item>
    /// </list>
    /// Seeing the custom curve on marks the tables as possibly custom, so speeds left by another program are never
    /// taken later.
    /// </summary>
    private StockFanSpeeds? KnownStock(FanTables now, bool customOn)
    {
        var loaded = LoadFanEntry();
        if (customOn && loaded) MarkTables(mayBeCustom: true);

        var entry = loaded ? _fanEntry : null;
        var clean = loaded
            && !customOn
            && entry?.TablesMayBeCustom != true
            && (_chosenCurve is null || !SpeedsIn(now.Fan1).SequenceEqual(_chosenCurve))
            && (now.Fan2 is not null || board.StockFanCurve?.Fan2 is null);

        if (entry is { Fan1: { } fan1 } && IsSaneStock(entry))
        {
            var build = FirmwareBuild();
            var newBuild = build is not null && entry.Firmware is not null && entry.Firmware != build;
            if (newBuild && clean) return Capture(now, build);

            var updated = entry;

            // A second table seen only now, on clean tables, is the firmware's own too.
            if (updated.Fan2 is null && now.Fan2 is { } table2 && clean)
                updated = updated with { Fan2 = SpeedsIn(table2) };

            // Backfill stock temperatures/hysteresis for fan-stock.json files saved before temperature writes existed.
            if (updated.Temperatures is null)
            {
                var customTemps = Profile.FanCurve?.CustomTemperatures;
                var stillFactoryTemps = clean || customTemps is null || !now.Temperatures.SequenceEqual(customTemps);
                if (stillFactoryTemps)
                {
                    updated = updated with
                    {
                        Temperatures = now.Temperatures,
                        OverflowTemp = OverflowIn(now.Temp1),
                        Falling1 = now.Thermal1 is { } t1 ? FallingIn(t1) : null,
                        Falling2 = now.Thermal2 is { } t2 ? FallingIn(t2) : null,
                    };
                }
                else if (board.StockFanCurve is { Temperatures: { } fallbackTemps } fallback)
                {
                    updated = updated with
                    {
                        Temperatures = fallbackTemps,
                        OverflowTemp = fallback.OverflowTemp,
                        Falling1 = fallback.Falling1,
                        Falling2 = fallback.Falling2,
                    };
                }
            }
            else if (updated.Falling2 is null && now.Thermal2 is { } rawThermal2 && clean)
            {
                updated = updated with { Falling2 = FallingIn(rawThermal2) };
            }

            if (!ReferenceEquals(updated, entry))
            {
                _fanEntry = updated;
                _stockFile.Save(_fanEntry);
            }

            return new(
                fan1,
                updated.Fan2,
                updated.Temperatures ?? board.StockFanCurve?.Temperatures,
                updated.OverflowTemp ?? board.StockFanCurve?.OverflowTemp,
                updated.Falling1 ?? board.StockFanCurve?.Falling1,
                updated.Falling2 ?? board.StockFanCurve?.Falling2);
        }

        if (clean) return Capture(now, FirmwareBuild());

        return board.StockFanCurve;
    }

    /// <summary>Takes the tables in force as the firmware's own and saves them. Only for tables known to be clean.</summary>
    private StockFanSpeeds Capture(FanTables now, string? build)
    {
        var stock = new StockFanSpeeds(
            SpeedsIn(now.Fan1),
            now.Fan2 is { } table2 ? SpeedsIn(table2) : null,
            now.Temperatures,
            OverflowIn(now.Temp1),
            now.Thermal1 is { } thermal1 ? FallingIn(thermal1) : null,
            now.Thermal2 is { } thermal2 ? FallingIn(thermal2) : null);

        _fanEntry = new(
            board.Model, build, stock.Fan1, stock.Fan2, TablesMayBeCustom: false,
            stock.Temperatures, stock.OverflowTemp, stock.Falling1, stock.Falling2);
        _stockFile.Save(_fanEntry);   // if it fails the speeds are still known until the helper restarts
        return stock;
    }

    private bool IsSaneStock(MsiFanStock.Entry saved) =>
        Profile.FanCurve is { } limits
        && IsSaneSpeeds(saved.Fan1, limits)
        && (saved.Fan2 is null || IsSaneSpeeds(saved.Fan2, limits))
        && (saved.Temperatures is null || IsSaneTemperatures(saved.Temperatures));

    private static bool IsSaneSpeeds(int[]? speeds, FanCurveLimits limits) =>
        speeds is { Length: CurvePoints } && speeds.All(speed => speed >= 0 && speed <= limits.MaxSpeed);

    /// <summary>
    /// Writes the firmware's own speeds, temperatures, and falling hysteresis back before Auto switches the custom
    /// curve off. True when every present table now holds them. False when the tables make no sense or the stock
    /// values are not known, and then nothing unknown is overwritten.
    /// </summary>
    private bool PutBackStockSpeeds()
    {
        if (Profile.FanCurve is not { } limits || ReadFanTables(limits) is not { } now) return false;
        if (KnownStock(now, CustomCurveOn()) is not { } stock) return false;

        var allRestored = true;

        WriteSpeeds(1, now.Fan1, stock.Fan1);
        if (now.Fan2 is { } table2)
        {
            if (stock.Fan2 is { } stock2) WriteSpeeds(2, table2, stock2);
            else allRestored = false;
        }

        if (stock.Temperatures is { } stockTemps)
        {
            WriteTemperatures(1, now.Temp1, stockTemps, stock.OverflowTemp);
            if (now.Temp2 is { } temp2) WriteTemperatures(2, temp2, stockTemps, stock.OverflowTemp);
        }
        else if (TargetTemperaturesFor(limits, now.Temp1) is not null)
        {
            allRestored = false;
        }

        if (board.FanTemperatures.FallingHysteresisC is not null)
        {
            if (now.Thermal1 is { } thermal1)
            {
                if (stock.Falling1 is { } falling1) WriteFalling(1, thermal1, falling1);
                else allRestored = false;
            }
            if (now.Thermal2 is { } thermal2)
            {
                if (stock.Falling2 is { } falling2) WriteFalling(2, thermal2, falling2);
                else allRestored = false;
            }
        }

        return allRestored;
    }

    /// <summary>
    /// Writes six speeds into bytes 1–6 of one fan's table as read, keeping bytes 0 and 7, and reads them back.
    /// A table whose bytes 1–6 already hold them is not written.
    /// </summary>
    private void WriteSpeeds(byte fan, byte[] table, IReadOnlyList<int> speeds)
    {
        var wanted = WithSpeeds(table, speeds);
        if (Curve(wanted).SequenceEqual(Curve(table))) return;

        if (!firmware.Set("Set_Fan", fan, wanted).Accepted)
            throw new FirmwareException($"The firmware refused to write fan {fan}'s curve.");

        var kept = firmware.Get("Get_Fan", fan);
        if (!kept.Accepted || kept.Data.Length < FanTableLength || !Curve(kept.Data).SequenceEqual(Curve(wanted)))
            throw new FirmwareException(
                $"The firmware did not keep fan {fan}'s curve: wrote {Convert.ToHexString(wanted)}, " +
                $"read {(kept.Accepted ? Convert.ToHexString(kept.Data, 0, FanTableLength) : "nothing")}.");
    }

    /// <summary>Writes rising temperatures into a fan's table, preserving critical byte 2 and untouched bytes, then reads back.</summary>
    private void WriteTemperatures(byte fan, byte[] table, IReadOnlyList<int> temperatures, int? overflow)
    {
        var wanted = WithTemperatures(table, temperatures, overflow);
        if (wanted.SequenceEqual(table)) return;

        if (!firmware.Set("Set_Temperature", fan, wanted).Accepted)
            throw new FirmwareException($"The firmware refused to write fan {fan}'s temperatures.");

        var kept = firmware.Get("Get_Temperature", fan);
        if (!kept.Accepted || kept.Data.Length < FanTableLength || !TemperatureSlotsMatch(kept.Data, wanted))
            throw new FirmwareException(
                $"The firmware did not keep fan {fan}'s temperatures: wrote {Convert.ToHexString(wanted)}, " +
                $"read {(kept.Accepted ? Convert.ToHexString(kept.Data, 0, FanTableLength) : "nothing")}.");
    }

    /// <summary>Writes falling hysteresis bytes 1–6, preserving byte 0, then reads back.</summary>
    private void WriteFalling(byte fan, byte[] table, IReadOnlyList<int> falling)
    {
        var wanted = WithFalling(table, falling);
        if (FallingSlice(wanted).SequenceEqual(FallingSlice(table))) return;

        if (!firmware.Set("Set_Thermal", fan, wanted).Accepted)
            throw new FirmwareException($"The firmware refused to write fan {fan}'s hysteresis.");

        var kept = firmware.Get("Get_Thermal", fan);
        if (!kept.Accepted || kept.Data.Length < ThermalTableLength || !FallingSlice(kept.Data).SequenceEqual(FallingSlice(wanted)))
            throw new FirmwareException(
                $"The firmware did not keep fan {fan}'s hysteresis: wrote {Convert.ToHexString(wanted)}, " +
                $"read {(kept.Accepted ? Convert.ToHexString(kept.Data, 0, ThermalTableLength) : "nothing")}.");
    }

    private static ReadOnlySpan<byte> Curve(byte[] table) => table.AsSpan(FirstSpeed, CurvePoints);

    private static ReadOnlySpan<byte> FallingSlice(byte[] table) => table.AsSpan(FirstFalling, CurvePoints);

    private static byte[] WithSpeeds(byte[] table, IReadOnlyList<int> speeds)
    {
        var wanted = table.ToArray();
        for (var point = 0; point < CurvePoints; point++)
            wanted[FirstSpeed + point] = (byte)Math.Clamp(speeds[point], 0, byte.MaxValue);
        return wanted;
    }

    private byte[] WithTemperatures(byte[] table, IReadOnlyList<int> temperatures, int? overflow)
    {
        var wanted = table.ToArray();
        var map = board.FanTemperatures;
        for (var point = 0; point < CurvePoints; point++)
            wanted[map.Points[point]] = (byte)Math.Clamp(temperatures[point], CoolestPoint, HottestPoint);
        if (map.OverflowIndex is { } at && overflow is { } value)
            wanted[at] = (byte)Math.Clamp(value, CoolestPoint, HottestPoint);
        return wanted;
    }

    private bool TemperatureSlotsMatch(byte[] kept, byte[] wanted)
    {
        var map = board.FanTemperatures;
        foreach (var at in map.Points)
            if (kept[at] != wanted[at]) return false;
        return map.OverflowIndex is not { } overflowAt || kept[overflowAt] == wanted[overflowAt];
    }

    private static byte[] WithFalling(byte[] table, IReadOnlyList<int> falling)
    {
        var wanted = table.ToArray();
        for (var point = 0; point < CurvePoints; point++)
            wanted[FirstFalling + point] = (byte)Math.Clamp(falling[point], 0, byte.MaxValue);
        return wanted;
    }

    /// <summary>
    /// Best effort after a failed <see cref="SetFanCurve"/>: the tables, the custom curve setting and Cooler Boost
    /// go back as they were found. Each step's own failure is ignored so the original one is reported.
    /// </summary>
    /// <returns>True when every step succeeded.</returns>
    private bool RollBackCurve(
        FanTables before,
        bool fan2Written,
        bool tempsWritten,
        bool temp2Written,
        bool thermal1Written,
        bool thermal2Written,
        bool customBefore,
        bool boostBefore)
    {
        var restored = TryRestoreSpeedTable(1, before.Fan1);
        if (fan2Written) restored &= TryRestoreSpeedTable(2, before.Fan2!);
        if (tempsWritten) restored &= TryRestoreTemperatureTable(1, before.Temp1);
        if (temp2Written) restored &= TryRestoreTemperatureTable(2, before.Temp2!);
        if (thermal1Written) restored &= TryRestoreThermalTable(1, before.Thermal1!);
        if (thermal2Written) restored &= TryRestoreThermalTable(2, before.Thermal2!);

        try
        {
            SetBits(_at.FanControl, nameof(_at.FanControl), _at.CustomFanCurve, on: customBefore);
            SetBits(_at.CoolerBoost, nameof(_at.CoolerBoost), _at.CoolerBoostOn, on: boostBefore);
            return restored;
        }
        catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
        {
            return false;
        }
    }

    /// <summary>Puts bytes 1–6 of a speed table back as found; false on any failure.</summary>
    private bool TryRestoreSpeedTable(byte fan, byte[] found)
    {
        try
        {
            var table = ReadFanTable(fan) ?? found;
            WriteSpeeds(fan, table, SpeedsIn(found));
            return true;
        }
        catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
        {
            return false;
        }
    }

    /// <summary>Puts rising temperature bytes back as found; false on any failure.</summary>
    private bool TryRestoreTemperatureTable(byte fan, byte[] found)
    {
        try
        {
            var table = ReadTemperatureTable(fan) ?? found;
            WriteTemperatures(fan, table, TemperaturesIn(found), OverflowIn(found));
            return true;
        }
        catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
        {
            return false;
        }
    }

    /// <summary>Puts falling hysteresis bytes 1–6 back as found; false on any failure.</summary>
    private bool TryRestoreThermalTable(byte fan, byte[] found)
    {
        try
        {
            var table = ReadThermalTable(fan) ?? found;
            WriteFalling(fan, table, FallingIn(found));
            return true;
        }
        catch (Exception error) when (error is FirmwareException or ManagementException or COMException)
        {
            return false;
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
            // MSI Center M treats bit 7 as a capability bit and changes the byte only when it already reads set.
            // Boards not checked on a real unit follow it.
            if (!board.CheckedOnDevice
                && (Read(_at.ChargeLimit, nameof(_at.ChargeLimit)) & _at.ChargeLimitOn) == 0)
                throw new FirmwareException("This device's firmware does not offer a charge limit.");

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
                FanSpeed = ReadFanSpeed(),
                FirmwareVersion = ReadFirmwareVersion(),
            };
        }
    }

    public LiveReadings Readings()
    {
        lock (_oneIntentionAtATime)
        {
            return new(ReadCpuTemperature(), ReadFanRpm(), ReadFanSpeed());
        }
    }

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

    /// <summary>
    /// Byte 0 of <c>Get_Fan(1)</c>: the fan's live duty in the firmware's own curve units (<c>0..MaxSpeed</c>).
    /// Null when this board takes no curve or the firmware did not answer with a sane reading.
    /// </summary>
    private int? ReadFanSpeed()
    {
        if (Profile.FanCurve is not { } limits) return null;

        var reply = firmware.Get("Get_Fan", selector: 1);
        if (!reply.Accepted || reply.Data.Length < FanTableLength) return null;

        var duty = (int)reply.Data[0];
        return duty <= Math.Max(limits.MaxSpeed, 150) ? duty : null;
    }

    /// <summary>The firmware's whole version text, build date and time included ("1T91EMS1.1030902202615:19:39").</summary>
    private string? ReadFirmwareBuild()
    {
        var reply = firmware.Get("Get_EC");
        if (!reply.Accepted) return null;

        var text = Encoding.ASCII.GetString(reply.Data, 1, 28).TrimEnd('\0', ' ');
        return text.Length > 0 ? text : null;
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
