using System.Text;
using Tsume.Core.Performance;
using Tsume.Hardware.Diagnostics;

namespace Tsume.Hardware.Msi.Diagnostics;

/// <summary>
/// Switches performance mode, checks the registers hold, measures package power under full load to confirm
/// the processor obeys, then restores the starting state. Applies Tsume's own Balanced and Endurance presets
/// through <see cref="MsiDevice"/>, so with the shift code it uses for watts (Manual on most boards).
/// </summary>
// Endurance is the telling one: its boost ceiling is the lowest, so it shows at once, whereas Balanced's
// higher boost can hide its sustained limit for as long as the chip may burst.
public static class ModeCheck
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Measure = TimeSpan.FromSeconds(6);

    public static string Run(MsiAcpiBridge acpi)
    {
        var report = new StringBuilder();
        report.AppendLine("── Mode check: change the mode, read it back, measure, restore ──");

        if (acpi.Verified is not { } board)
            return report.AppendLine($"  {acpi.Machine} is not a verified device. Nothing was written.").ToString();

        if (Registers.Read(acpi, board) is not { } original)
            return report.AppendLine("  Could not read the starting state. Nothing was written.").ToString();

        var hasFastBoost = board.Registers.PowerLimitFastBoost is not null;
        byte? originalStamp = null;
        if (board.Registers.AmdStamp is { } stampReg)
        {
            if (acpi.Get("Get_Data", stampReg) is not { Accepted: true } stampReply)
                return report.AppendLine("  Could not read the starting STAMP register. Nothing was written.").ToString();
            originalStamp = stampReply.Data[0];
        }

        report.AppendLine(hasFastBoost
            ? "  step                 shift   sustained  boost    fast     registers   package power under load"
            : "  step                 shift   sustained  boost    registers   package power under load");

        // Not disposed here: the bridge it shares is the caller's to close.
        var device = new MsiDevice(acpi, board);

        using var power = new PackagePower();
        var allHeld = true;

        try
        {
            report.AppendLine(Line("as found", original, hasFastBoost, held: null, UnderLoad(power)));

            foreach (var mode in new[] { PerformanceMode.Balanced, PerformanceMode.Endurance })
            {
                device.SetPerformance(mode, default);

                var expected = Registers.For(device, board, mode);
                var watts = UnderLoad(power);

                // Read after the load, to also catch anything that puts the old values back within seconds.
                var now = Registers.Read(acpi, board);
                var held = now == expected;
                allHeld &= held;

                report.AppendLine(Line(mode.ToString(), now, hasFastBoost, held, watts));
            }
        }
        catch (FirmwareException refused)
        {
            allHeld = false;
            report.AppendLine($"  Stopped early: {refused.Message}");
        }
        finally
        {
            try
            {
                // Skip writing if nothing changed (for instance when RequireRegisterInterface refused the very first write).
                if (Registers.Read(acpi, board) != original)
                    Registers.Restore(acpi, board, original);
            }
            catch (FirmwareException restoreError)
            {
                report.AppendLine($"  Restore failed: {restoreError.Message}");
            }
            finally
            {
                // Runs even if putting the other registers back threw, and never hides that failure with its
                // own: a STAMP that could not be put back shows in the verdict below instead.
                try
                {
                    RestoreStamp(acpi, board, originalStamp);
                }
                catch (FirmwareException)
                {
                }
            }
        }

        var restored = Registers.Read(acpi, board);
        var back = restored == original;
        report.AppendLine(Line("restored", restored, hasFastBoost, back, UnderLoad(power)));

        // Intel boards have no STAMP register: null here, and the report reads exactly as before.
        bool? stampBack = null;
        string? stampLine = null;
        if (board.Registers.AmdStamp is { } checkStampReg && originalStamp is { } stampFound)
        {
            byte? stampAfter = acpi.Get("Get_Data", checkStampReg) is { Accepted: true } stampReply ? stampReply.Data[0] : null;
            stampBack = stampAfter == stampFound;
            stampLine = stampBack == true
                ? $"  STAMP 0x{checkStampReg:X2} is back as found (0x{stampFound:X2})."
                : $"  STAMP 0x{checkStampReg:X2} WAS NOT RESTORED: it was 0x{stampFound:X2}, reads {(stampAfter is { } a ? $"0x{a:X2}" : "--")}.";
        }

        report.AppendLine();
        report.AppendLine(allHeld
            ? "  Both modes were written and held."
            : "  AT LEAST ONE MODE DID NOT HOLD — see the 'registers' column.");
        report.AppendLine(back && stampBack != false
            ? "  The starting state is back exactly as found."
            : back
                ? "  The power and shift registers are back as found, but STAMP is not (see below)."
                : $"  THE STARTING STATE WAS NOT RESTORED. It was: {original}");
        if (stampLine is not null)
            report.AppendLine(stampLine);

        return report.ToString();
    }

    /// <summary>Puts the AMD STAMP register back if it changed. Nothing on boards without one.</summary>
    private static void RestoreStamp(MsiAcpiBridge acpi, MsiBoard board, byte? originalStamp)
    {
        if (board.Registers.AmdStamp is { } stampReg
            && originalStamp is { } stampBefore
            && acpi.Get("Get_Data", stampReg) is { Accepted: true } stampNow
            && stampNow.Data[0] != stampBefore)
        {
            acpi.Set("Set_Data", stampReg, [stampBefore]);
        }
    }

    /// <summary>
    /// Keeps every core busy, lets the chip settle, then averages package power.
    /// Null when this machine has no energy meter.
    /// </summary>
    private static double? UnderLoad(PackagePower power)
    {
        using var stop = new CancellationTokenSource();

        var workers = Enumerable.Range(0, Environment.ProcessorCount)
            .Select(_ => new Thread(() => Spin(stop.Token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal })
            .ToList();

        workers.ForEach(worker => worker.Start());
        try
        {
            Thread.Sleep(Settle);
            var from = power.Read();
            Thread.Sleep(Measure);
            return PackagePower.Watts(from, power.Read());
        }
        finally
        {
            stop.Cancel();
            workers.ForEach(worker => worker.Join());
        }
    }

    private static void Spin(CancellationToken stop)
    {
        var x = 1.0001;
        while (!stop.IsCancellationRequested)
            for (var i = 0; i < 100_000; i++)
                x = Math.Sqrt(x * x + i);
    }

    private static string Line(string step, Registers? state, bool hasFastBoost, bool? held, double? watts)
    {
        string values;
        if (hasFastBoost)
        {
            values = state is { } s
                ? $"0x{s.ShiftMode:X2}    {s.Sustained,2} W       {s.Boost,2} W     {(s.FastBoost is { } f ? $"{f,2} W " : "--   ")}"
                : "--      --         --       --   ";
        }
        else
        {
            values = state is { } s
                ? $"0x{s.ShiftMode:X2}    {s.Sustained,2} W       {s.Boost,2} W "
                : "--      --         --   ";
        }

        var verdict = held switch { null => "", true => "ok", false => "WRONG" };
        var measured = watts is { } w ? $"{w,5:F1} W" : "not measured";

        return $"  {step,-19}  {values}    {verdict,-10}  {measured}";
    }

    /// <summary>The registers a mode is made of (three on Intel, four with FPPT on AMD).</summary>
    private readonly record struct Registers(byte ShiftMode, byte Sustained, byte Boost, byte? FastBoost = null)
    {
        /// <summary>What <paramref name="device"/> writes for <paramref name="mode"/> right now (battery limits included).</summary>
        public static Registers For(MsiDevice device, MsiBoard board, PerformanceMode mode)
        {
            var limits = device.LimitsToApply(mode, default) ?? throw new ArgumentOutOfRangeException(nameof(mode));
            return new Registers(
                board.Shift.Encode(MsiDevice.ShiftModeFor(board, mode, limits)),
                (byte)limits.Sustained,
                (byte)limits.Boost,
                limits.FastBoost is { } fppt ? (byte)fppt : null);
        }

        public static Registers? Read(MsiAcpiBridge acpi, MsiBoard board)
        {
            var shift = acpi.Get("Get_Data", board.Registers.ShiftMode);
            var sustained = acpi.Get("Get_Data", board.Registers.PowerLimitSustained);
            var boost = acpi.Get("Get_Data", board.Registers.PowerLimitBoost);

            if (!shift.Accepted || !sustained.Accepted || !boost.Accepted) return null;

            byte? fastBoost = null;
            if (board.Registers.PowerLimitFastBoost is { } fastReg)
            {
                var fast = acpi.Get("Get_Data", fastReg);
                if (!fast.Accepted) return null;
                fastBoost = fast.Data[0];
            }

            return new Registers(shift.Data[0], sustained.Data[0], boost.Data[0], fastBoost);
        }

        /// <summary>
        /// Writes the raw bytes back, bypassing <see cref="MsiDevice"/>, so a state that is not one of Tsume's
        /// modes is still restored. Same order as any change: floors first, then highest-to-lowest limit.
        /// </summary>
        public static void Restore(MsiAcpiBridge acpi, MsiBoard board, Registers original)
        {
            acpi.Set("Set_Data", board.Registers.ShiftMode, [original.ShiftMode]);
            acpi.Set("Set_Data", board.Registers.PowerLimitSustained, [board.PowerFloor]);
            if (board.Registers.PowerLimitFastBoost is { } fastReg && original.FastBoost is { } fppt)
            {
                acpi.Set("Set_Data", board.Registers.PowerLimitBoost, [board.BoostFloor ?? board.PowerFloor]);
                acpi.Set("Set_Data", fastReg, [fppt]);
            }
            acpi.Set("Set_Data", board.Registers.PowerLimitBoost, [original.Boost]);
            acpi.Set("Set_Data", board.Registers.PowerLimitSustained, [original.Sustained]);
        }

        public override string ToString() => FastBoost is { } fppt
            ? $"shift 0x{ShiftMode:X2}, {Sustained} W / {Boost} W / {fppt} W"
            : $"shift 0x{ShiftMode:X2}, {Sustained} W / {Boost} W";
    }
}
