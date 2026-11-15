using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Tsume.App.Platform.Interop;
using static Tsume.App.Platform.Interop.DwmApi;

namespace Tsume.App.Shell;

/// <summary>
/// Makes a window per-pixel transparent through DWM instead of WPF's <c>AllowsTransparency</c>.
/// That makes a layered window: each frame is copied off the GPU (about half a core while animating)
/// and is not tied to the display's refresh (about 24 fps on a still screen).
/// </summary>
internal static class SeeThrough
{
    /// <summary>Call once the window has a handle, before it is first shown.</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (HwndSource.FromHwnd(handle) is not { CompositionTarget: { } target }) return;

        // WPF: start every frame from transparent rather than from black.
        target.BackgroundColor = Colors.Transparent;

        // "Blur behind" over an empty region blurs nothing, but switches on
        // per-pixel transparency for an ordinary window.
        var nowhere = CreateRectRgn(0, 0, -1, -1);
        DwmEnableBlurBehindWindow(handle, new DWM_BLURBEHIND
        {
            Flags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            Enable = 1,
            BlurRegion = nowhere,
        });
        DeleteObject(nowhere);

        // No frame, border, shadow or rounding from Windows: the panel draws its own.
        // Older Windows builds reject the last two attributes; that is harmless.
        DwmSetWindowAttribute(handle, DWMWA_NCRENDERING_POLICY, DWMNCRP_DISABLED, sizeof(int));
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND, sizeof(int));
        DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE, sizeof(int));
    }
}
