using System.Management;

namespace Tsume.Hardware.Diagnostics;

/// <summary>
/// Processor package watts, from the Windows Energy Meter Interface (Intel RAPL / AMD energy counters).
/// The meter is a running total — picowatt-hours against milliseconds — so watts come from the difference of two readings.
/// </summary>
public sealed class PackagePower : IDisposable
{
    private const string Query =
        "SELECT Name, Energy, Time FROM Win32_PerfRawData_PowerMeterCounter_EnergyMeter";

    private const double JoulesPerPicowattHour = 3600e-12;

    private readonly ManagementObjectSearcher _meter = new(@"root\CIMV2", Query);

    /// <summary>One look at the meter. Null when this machine has no such meter.</summary>
    public Reading? Read()
    {
        try
        {
            Reading? best = null;
            var bestRank = int.MaxValue;

            using var results = _meter.Get();
            foreach (var row in results)
            {
                using (row)
                {
                    var name = $"{row["Name"]}".Trim();
                    var rank = MeterRank(name);
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        best = new Reading(Convert.ToUInt64(row["Energy"]), Convert.ToUInt64(row["Time"]));
                        if (rank == 0) break;
                    }
                }
            }

            return best;
        }
        catch (ManagementException)
        {
            // No meter: the caller reports "not measured".
        }

        return null;
    }

    private static int MeterRank(string name)
    {
        if (string.IsNullOrEmpty(name)) return int.MaxValue;
        if (name.Equals("RAPL_Package0_PKG", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("DRAM", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("PP0", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("PP1", StringComparison.OrdinalIgnoreCase))
            return 2;
        if ((name.Contains("Socket", StringComparison.OrdinalIgnoreCase)
             || name.Contains("APU", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Total", StringComparison.OrdinalIgnoreCase))
            && !name.Contains("DRAM", StringComparison.OrdinalIgnoreCase))
            return 3;
        return int.MaxValue;
    }

    /// <summary>Average watts between two looks at the meter.</summary>
    public static double? Watts(Reading? from, Reading? to)
    {
        if (from is not { } a || to is not { } b
            || b.Milliseconds <= a.Milliseconds
            || b.PicowattHours < a.PicowattHours)
            return null;

        var joules = (b.PicowattHours - a.PicowattHours) * JoulesPerPicowattHour;
        var seconds = (b.Milliseconds - a.Milliseconds) / 1000.0;
        return joules / seconds;
    }

    public void Dispose() => _meter.Dispose();

    public readonly record struct Reading(ulong PicowattHours, ulong Milliseconds);
}
