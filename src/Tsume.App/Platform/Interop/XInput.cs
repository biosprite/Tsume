using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>The Xbox controller API. The Claw's built-in pad speaks it in XInput mode.</summary>
internal static partial class XInput
{
    public const uint ERROR_SUCCESS = 0;
    public const int MaxControllers = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [LibraryImport("xinput1_4.dll")]
    public static partial uint XInputGetState(uint user, out XINPUT_STATE state);
}
