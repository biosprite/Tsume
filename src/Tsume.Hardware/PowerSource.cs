using System.Runtime.InteropServices;

namespace Tsume.Hardware;

/// <summary>Whether the machine runs on battery, decided as MSI Center M decides it.</summary>
public static class PowerSource
{
    /// <summary>
    /// True only when Windows reports the AC line offline, as MSI Center M's
    /// <c>SystemInformation.PowerStatus.PowerLineStatus == Offline</c>; an unknown line or a failed call counts as wall power.
    /// </summary>
    public static bool IsOnBattery() => GetSystemPowerStatus(out var status) && status.ACLineStatus == AcLineOffline;

    private const byte AcLineOffline = 0;

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
