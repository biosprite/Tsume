using System.Runtime.InteropServices;

namespace Tsume.Hardware;

/// <summary>Whether the machine runs on battery, decided as MSI Center M decides it.</summary>
public static class PowerSource
{
    /// <summary>
    /// True only when Windows reports the AC line offline, as MSI Center M tests it; an unknown line or a
    /// failed call counts as wall power.
    /// </summary>
    public static bool IsOnBattery() => GetSystemPowerStatus(out var status) && status.ACLineStatus == AcLineOffline;

    /// <summary>The battery's remaining charge (0–100 %), or null when there is no battery or Windows does not know.</summary>
    public static int? BatteryPercent() =>
        GetSystemPowerStatus(out var status)
        && (status.BatteryFlag & NoSystemBattery) == 0
        && status.BatteryLifePercent <= 100
            ? status.BatteryLifePercent
            : null;

    private const byte AcLineOffline = 0;
    private const byte NoSystemBattery = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
