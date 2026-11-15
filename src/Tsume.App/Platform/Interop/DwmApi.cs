using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

/// <summary>Desktop Window Manager calls, and the one GDI region they need. Names mirror the Win32 headers.</summary>
internal static partial class DwmApi
{
    public const uint DWM_BB_ENABLE = 0x1;
    public const uint DWM_BB_BLURREGION = 0x2;

    public const uint DWMWA_NCRENDERING_POLICY = 2;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const uint DWMWA_BORDER_COLOR = 34;

    public const int DWMNCRP_DISABLED = 1;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint Flags;
        public int Enable;
        public nint BlurRegion;
        public int TransitionOnMaximized;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmEnableBlurBehindWindow(nint window, in DWM_BLURBEHIND blurBehind);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint window, uint attribute, in int value, int size);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);
}
