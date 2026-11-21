using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>
/// Legacy WinMM joystick polling (<c>winmm.dll</c>). Reads the controller's HID gamepad interface directly,
/// without going through <c>InputHost.dll</c> — used as a fallback when <c>xinput1_4.dll</c> is muted by
/// Windows' Xbox Full Screen Experience gamepad interception.
/// </summary>
internal static partial class WinMm
{
    public const uint JOYERR_NOERROR = 0;
    public const int MaxControllers = 4;

    public const uint JOY_RETURNX = 0x0000_0001;
    public const uint JOY_RETURNY = 0x0000_0002;
    public const uint JOY_RETURNPOV = 0x0000_0040;
    public const uint JOY_RETURNBUTTONS = 0x0000_0080;
    public const uint JOY_RETURN_NAV = JOY_RETURNX | JOY_RETURNY | JOY_RETURNPOV | JOY_RETURNBUTTONS;

    public const uint JOY_POVCENTERED = 65535;

    // Standard Xbox 360 / XUSB HID button bits (HID Usage Page 0x09, Usages 1..10).
    public const uint ButtonA = 1u << 0;
    public const uint ButtonB = 1u << 1;
    public const uint ButtonView = 1u << 6;
    public const uint ButtonMenu = 1u << 7;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOYINFOEX
    {
        public uint Size;
        public uint Flags;
        public uint X;
        public uint Y;
        public uint Z;
        public uint R;
        public uint U;
        public uint V;
        public uint Buttons;
        public uint ButtonNumber;
        public uint Pov;
        public uint Reserved1;
        public uint Reserved2;

        public static JOYINFOEX Create() => new() { Size = (uint)Marshal.SizeOf<JOYINFOEX>(), Flags = JOY_RETURN_NAV };
    }

    [LibraryImport("winmm.dll")]
    public static partial uint joyGetPosEx(uint id, ref JOYINFOEX info);
}
