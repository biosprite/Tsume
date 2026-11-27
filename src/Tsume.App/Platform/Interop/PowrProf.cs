using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>Power-management calls. Names mirror the Win32 headers.</summary>
internal static partial class PowrProf
{
    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvents);

    public const int SystemBatteryState = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_BATTERY_STATE
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare1, Spare2, Spare3;
        public byte Tag;
        public uint MaxCapacity;          // mWh
        public uint RemainingCapacity;    // mWh
        public int Rate;                  // mW — negative while discharging
        public uint EstimatedTime;        // seconds
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [LibraryImport("powrprof.dll")]
    public static partial uint CallNtPowerInformation(
        int level, nint input, uint inputSize, out SYSTEM_BATTERY_STATE output, uint outputSize);

    [LibraryImport("powrprof.dll")]
    public static partial int CallNtPowerInformation(
        int informationLevel, nint inputBuffer, uint inputBufferLength, nint outputBuffer, uint outputBufferLength);
}
