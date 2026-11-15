using System.Management;

namespace Tsume.Hardware.Diagnostics;

/// <summary>
/// Processor package watts, from the energy meter Windows keeps for Intel chips (RAPL). The meter is a
/// running total — picowatt-hours against milliseconds — so watts come from the difference of two readings.
/// </summary>
public sealed class PackagePower : IDisposable
{
    private const string Query =
        "SELECT Energy, Time FROM Win32_PerfRawData_PowerMeterCounter_EnergyMeter WHERE Name='RAPL_Package0_PKG'";

    private const double JoulesPerPicowattHour = 3600e-12;

    private readonly ManagementObjectSearcher _meter = new(@"root\CIMV2", Query);

    /// <summary>One look at the meter. Null when this machine has no such meter.</summary>
    public Reading? Read()
    {
        try
        {
            using var results = _meter.Get();
            foreach (var row in results)
                using (row)
                    return new Reading(Convert.ToUInt64(row["Energy"]), Convert.ToUInt64(row["Time"]));
        }
        catch (ManagementException)
        {
            // No meter: the caller reports "not measured".
        }

        return null;
    }

    /// <summary>Average watts between two looks at the meter.</summary>
    public static double? Watts(Reading? from, Reading? to)
    {
        if (from is not { } a || to is not { } b || b.Milliseconds <= a.Milliseconds) return null;

        var joules = (b.PicowattHours - a.PicowattHours) * JoulesPerPicowattHour;
        var seconds = (b.Milliseconds - a.Milliseconds) / 1000.0;
        return joules / seconds;
    }

    public void Dispose() => _meter.Dispose();

    public readonly record struct Reading(ulong PicowattHours, ulong Milliseconds);
}
