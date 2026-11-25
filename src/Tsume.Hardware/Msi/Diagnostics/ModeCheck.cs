using System.Text;
using Tsume.Core.Performance;
using Tsume.Hardware.Diagnostics;

namespace Tsume.Hardware.Msi.Diagnostics;

/// <summary>
/// Switches performance mode, checks the registers hold, measures package power under full load to confirm
/// the processor obeys, then restores the starting state. Only MSI's stock Balanced and Endurance are applied.
/// </summary>
// Endurance is the telling one: its 20 W boost ceiling shows at once, whereas Balanced's 37 W boost
// can hide the 25 W sustained limit for as long as the chip may burst.
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

        report.AppendLine("  step                 shift   sustained  boost    registers   package power under load");

        // Not disposed here: the bridge it shares is the caller's to close.
        var device = new MsiDevice(acpi, board);

        using var power = new PackagePower();
        var allHeld = true;

        try
        {
            report.AppendLine(Line("as found", original, held: null, UnderLoad(power)));

            foreach (var mode in new[] { PerformanceMode.Balanced, PerformanceMode.Endurance })
            {
                device.SetPerformance(mode, default);

                var expected = Registers.For(board, mode);
                var watts = UnderLoad(power);

                // Read after the load, to also catch anything that puts the old values back within seconds.
                var now = Registers.Read(acpi, board);
                var held = now == expected;
                allHeld &= held;

                report.AppendLine(Line(mode.ToString(), now, held, watts));
            }
        }
        catch (FirmwareException refused)
        {
            allHeld = false;
            report.AppendLine($"  Stopped early: {refused.Message}");
        }
        finally
        {
            Registers.Restore(acpi, board, original);
        }

        var restored = Registers.Read(acpi, board);
        var back = restored == original;
        report.AppendLine(Line("restored", restored, back, UnderLoad(power)));

        report.AppendLine();
        report.AppendLine(allHeld
            ? "  Both modes were written and held."
            : "  AT LEAST ONE MODE DID NOT HOLD — see the 'registers' column.");
        report.AppendLine(back
            ? "  The starting state is back exactly as found."
            : $"  THE STARTING STATE WAS NOT RESTORED. It was: {original}");

        return report.ToString();
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

    private static string Line(string step, Registers? state, bool? held, double? watts)
    {
        var values = state is { } s
            ? $"0x{s.ShiftMode:X2}    {s.Sustained,2} W       {s.Boost,2} W "
            : "--      --         --   ";

        var verdict = held switch { null => "", true => "ok", false => "WRONG" };
        var measured = watts is { } w ? $"{w,5:F1} W" : "not measured";

        return $"  {step,-19}  {values}    {verdict,-10}  {measured}";
    }

    /// <summary>The three registers a mode is made of.</summary>
    private readonly record struct Registers(byte ShiftMode, byte Sustained, byte Boost)
    {
        public static Registers For(MsiBoard board, PerformanceMode mode)
        {
            var limits = board.Profile.LimitsFor(mode, default) ?? throw new ArgumentOutOfRangeException(nameof(mode));
            return new Registers(board.Shift.Encode(MsiDevice.ShiftModeFor(board, mode)), (byte)limits.Sustained, (byte)limits.Boost);
        }

        public static Registers? Read(MsiAcpiBridge acpi, MsiBoard board)
        {
            var shift = acpi.Get("Get_Data", board.Registers.ShiftMode);
            var sustained = acpi.Get("Get_Data", board.Registers.PowerLimitSustained);
            var boost = acpi.Get("Get_Data", board.Registers.PowerLimitBoost);

            return shift.Accepted && sustained.Accepted && boost.Accepted
                ? new Registers(shift.Data[0], sustained.Data[0], boost.Data[0])
                : null;
        }

        /// <summary>
        /// Writes the raw bytes back, bypassing <see cref="MsiDevice"/>, so a state that is not one of Tsume's
        /// modes is still restored. Same order as any change: sustained to the floor, boost, sustained.
        /// </summary>
        public static void Restore(MsiAcpiBridge acpi, MsiBoard board, Registers original)
        {
            acpi.Set("Set_Data", board.Registers.ShiftMode, [original.ShiftMode]);
            acpi.Set("Set_Data", board.Registers.PowerLimitSustained, [board.PowerFloor]);
            acpi.Set("Set_Data", board.Registers.PowerLimitBoost, [original.Boost]);
            acpi.Set("Set_Data", board.Registers.PowerLimitSustained, [original.Sustained]);
        }

        public override string ToString() => $"shift 0x{ShiftMode:X2}, {Sustained} W / {Boost} W";
    }
}
