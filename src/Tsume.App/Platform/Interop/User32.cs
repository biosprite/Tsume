using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>Window, keyboard and display calls. Names mirror the Win32 headers.</summary>
internal static partial class User32
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x0000_0080;
    public const int WS_EX_TOPMOST = 0x0000_0008;
    public const int WS_EX_TRANSPARENT = 0x0000_0020;
    public const int WS_EX_NOACTIVATE = 0x0800_0000;

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowBand(nint window, out uint band);

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>With <see cref="SM_CYSCREEN"/>: the primary screen's height in real pixels.</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    public const int VK_LBUTTON = 0x01;
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const ushort VK_G = 0x47;
    public const int VK_RWIN = 0x5C;
    public const ushort VK_MENU_MASK = 0xE8;   // unassigned VK sent to prevent Start menu on Win release

    /// <summary>Negative while the key or mouse button is held, whichever window has the focus.</summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    public delegate bool EnumChildProc(nint window, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint lParam);

    public const uint GW_HWNDPREV = 3;

    /// <summary>With <see cref="GW_HWNDPREV"/>: the next window up the stacking order, or zero at the top.</summary>
    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint window, uint relation);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? title);

    /// <summary>Fired when the foreground window changes.</summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>Fired when a window is about to be minimized.</summary>
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;

    /// <summary>Fired when a minimized window is about to be restored.</summary>
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;

    /// <summary>Fired on the desktop window whenever top-level windows change places in the stacking order.</summary>
    public const uint EVENT_OBJECT_REORDER = 0x8004;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

    /// <summary>
    /// With <see cref="WINEVENT_OUTOFCONTEXT"/>, the callback runs on the calling thread as it pumps messages.
    /// The caller keeps the delegate alive for as long as the hook lives.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

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
    public const ushort VK_F11 = 0x7A;

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
    public static void SendChord(params ushort[] keys) => SendChord(extraInfo: 0, keys);

    /// <summary>Presses the keys in order, then releases them in reverse, tagging each event with <paramref name="extraInfo"/>.</summary>
    public static void SendChord(nint extraInfo, params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];

        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false, extraInfo);
            inputs[^(i + 1)] = Key(keys[i], up: true, extraInfo);
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        static INPUT Key(ushort key, bool up, nint extra) => new()
        {
            Type = INPUT_KEYBOARD,
            Keyboard = new KEYBDINPUT { VirtualKey = key, Flags = up ? KEYEVENTF_KEYUP : 0, ExtraInfo = extra },
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

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    public const uint WM_INPUT = 0x00FF;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_XBUTTONUP = 0x020C;
    public const uint WM_MOUSEHWHEEL = 0x020E;

    public const uint LLMHF_INJECTED = 0x0000_0001;
    public const uint LLMHF_LOWER_IL_INJECTED = 0x0000_0002;
    public const uint LLKHF_EXTENDED = 0x0000_0001;
    public const uint LLKHF_INJECTED = 0x0000_0010;
    public const uint LLKHF_ALTDOWN = 0x0000_0020;
    public const uint LLKHF_LOWER_IL_INJECTED = 0x0000_0002;

    /// <summary>Touch and pen events promoted to mouse messages carry this signature in the upper 24 bits of ExtraInfo.</summary>
    public const ulong MI_WP_SIGNATURE = 0xFF51_5700;
    public const ulong MI_WP_SIGNATURE_MASK = 0xFFFF_FF00;

    public delegate nint LowLevelHookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint window, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint window, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelHookProc callback, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    public const ushort HID_USAGE_GENERIC_JOYSTICK = 0x04;
    public const ushort HID_USAGE_GENERIC_GAMEPAD = 0x05;
    public const ushort HID_USAGE_PAGE_CONSUMER = 0x0C;
    public const ushort HID_USAGE_CONSUMER_CONTROL = 0x01;
    public const uint RIDEV_REMOVE = 0x0000_0001;
    public const uint RIDEV_PAGEONLY = 0x0000_0020;
    public const uint RIDEV_INPUTSINK = 0x0000_0100;
    public const uint RID_INPUT = 0x1000_0003;
    public const uint RID_HEADER = 0x1000_0005;
    public const uint RIM_TYPEMOUSE = 0;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const uint RIM_TYPEHID = 2;
    public const uint RIDI_DEVICENAME = 0x2000_0007;
    public const uint RIDI_DEVICEINFO = 0x2000_000B;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    public static partial uint GetRawInputData(nint rawInput, uint command, out RAWINPUTHEADER data, ref uint size, uint headerSize);

    [LibraryImport("user32.dll")]
    public static partial uint GetRawInputData(nint rawInput, uint command, [Out] byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(nint device, uint command, [Out] char[]? data, ref uint size);
}
