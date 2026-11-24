using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>The Xbox controller API. The Claw's built-in pad speaks it in XInput mode.</summary>
internal static partial class XInput
{
    public const uint ERROR_SUCCESS = 0;
    public const int MaxControllers = 4;
    public const ushort GuideButton = 0x0400;

    private static bool _useLegacyState;

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

    /// <summary>Undocumented ordinal #100 in xinput1_4.dll; identical to XInputGetState except bit 0x0400 reports the Xbox/Guide button.</summary>
    [LibraryImport("xinput1_4.dll", EntryPoint = "#100")]
    private static partial uint XInputGetStateEx(uint user, out XINPUT_STATE state);

    public static bool TryGetState(uint user, out XINPUT_STATE state)
    {
        if (!_useLegacyState)
        {
            try
            {
                return XInputGetStateEx(user, out state) == ERROR_SUCCESS;
            }
            catch (EntryPointNotFoundException)
            {
                _useLegacyState = true;
            }
        }

        return XInputGetState(user, out state) == ERROR_SUCCESS;
    }
}
