using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>Window, keyboard and display calls. Names mirror the Win32 headers.</summary>
internal static partial class User32
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x0000_0080;
    public const int WS_EX_TOPMOST = 0x0000_0008;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint window, int index, nint value);

    public const nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    public const int SM_CYSCREEN = 1;

    /// <summary>With <see cref="SM_CYSCREEN"/>: the primary screen's height in real pixels.</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    public const int VK_LBUTTON = 0x01;
    public const int VK_RETURN = 0x0D;
    public const int VK_SPACE = 0x20;

    /// <summary>Negative while the key or mouse button is held, whichever window has the focus.</summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, nint processId);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint window, [Out] char[] name, int capacity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint window, int id);

    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    public const ushort VK_MENU = 0x12;       // Alt
    public const ushort VK_SNAPSHOT = 0x2C;   // PrtSc
    public const ushort VK_INSERT = 0x2D;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_D = 0x44;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public KEYBDINPUT Keyboard;
        private readonly ulong _padding;   // the union is as large as MOUSEINPUT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] INPUT[] inputs, int size);

    /// <summary>Presses the keys in order, then releases them in reverse.</summary>
    public static void SendChord(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];

        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false);
            inputs[^(i + 1)] = Key(keys[i], up: true);
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        static INPUT Key(ushort key, bool up) => new()
        {
            Type = INPUT_KEYBOARD,
            Keyboard = new KEYBDINPUT { VirtualKey = key, Flags = up ? KEYEVENTF_KEYUP : 0 },
        };
    }

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint DM_DISPLAYFREQUENCY = 0x0040_0000;
    public const uint CDS_UPDATEREGISTRY = 0x0000_0001;
    public const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;

        public static DEVMODE Create() => new() { Size = (ushort)Marshal.SizeOf<DEVMODE>() };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    public static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE mode, nint window, uint flags, nint parameter);

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x0000_0002;

    /// <summary>Without this flag Windows 11 reports and sets only the desktop's (virtual) rate, not the signal's.</summary>
    public const uint QDC_VIRTUAL_REFRESH_RATE_AWARE = 0x0000_0040;

    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x0000_0020;
    public const uint SDC_APPLY = 0x0000_0080;
    public const uint SDC_SAVE_TO_DATABASE = 0x0000_0200;
    public const uint SDC_VIRTUAL_REFRESH_RATE_AWARE = 0x0002_0000;

    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFF_FFFF;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    /// <summary>Bits 16–21 of <see cref="DISPLAYCONFIG_VIDEO_SIGNAL_INFO.VideoStandard"/>: signal rate ÷ this = desktop rate.</summary>
    public const uint VSYNC_FREQ_DIVIDER_MASK = 0x003F_0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION
    {
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DISPLAYCONFIG_RATIONAL RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO Source;
        public DISPLAYCONFIG_PATH_TARGET_INFO Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong PixelRate;
        public DISPLAYCONFIG_RATIONAL HSyncFreq;
        public DISPLAYCONFIG_RATIONAL VSyncFreq;
        public DISPLAYCONFIG_2DREGION ActiveSize;
        public DISPLAYCONFIG_2DREGION TotalSize;
        public uint VideoStandard;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public int X;
        public int Y;
    }

    /// <summary>Either a source (a piece of desktop) or a target (a signal); <see cref="InfoType"/> says which.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public LUID AdapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_VIDEO_SIGNAL_INFO TargetMode;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE SourceMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(
        uint flags,
        ref uint pathCount, [In, Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint modeCount, [In, Out] DISPLAYCONFIG_MODE_INFO[] modes,
        nint topology);

    [LibraryImport("user32.dll")]
    public static partial int SetDisplayConfig(
        uint pathCount, [In] DISPLAYCONFIG_PATH_INFO[] paths,
        uint modeCount, [In] DISPLAYCONFIG_MODE_INFO[] modes,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
}
