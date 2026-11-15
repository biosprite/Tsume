using System.Runtime.InteropServices;
using static Tsume.App.Platform.Interop.User32;

namespace Tsume.App.Platform;

/// <summary>
/// Reads and sets the signal actually sent to the primary display. On a variable-refresh screen
/// <c>ChangeDisplaySettings</c> moves only the desktop rate: the Claw stays at 120 Hz (315.5 MHz) with frames held back.
/// </summary>
internal static class DisplaySignal
{
    /// <summary>The signal's refresh rate, or null if Windows will not say.</summary>
    public static int? CurrentRate() =>
        Layout.Read() is { } layout ? Rate(layout.Signal) : null;

    /// <summary>The rates the panel has an EDID timing for at the current size, lowest first. Empty when unknown.</summary>
    public static IReadOnlyList<int> Rates() =>
        Layout.Read() is { } layout ? [.. Timings(layout).Select(timing => timing.Rate).Distinct().Order()] : [];

    /// <summary>Sends the panel its own timing for <paramref name="hertz"/>, and has Windows remember it.</summary>
    public static bool SetRate(int hertz)
    {
        if (Layout.Read() is not { } layout) return false;

        var wanted = Timings(layout).Where(timing => timing.Rate == hertz).ToList();
        if (wanted.Count == 0) return false;

        var timing = wanted[0];
        ref var signal = ref layout.Signal;

        signal.PixelRate = timing.PixelClock;
        signal.HSyncFreq = new DISPLAYCONFIG_RATIONAL { Numerator = timing.PixelClock, Denominator = timing.TotalWidth };
        signal.VSyncFreq = new DISPLAYCONFIG_RATIONAL { Numerator = timing.PixelClock, Denominator = timing.PixelsPerFrame };
        signal.TotalSize = new DISPLAYCONFIG_2DREGION { Width = timing.TotalWidth, Height = timing.TotalHeight };
        signal.VideoStandard &= ~VSYNC_FREQ_DIVIDER_MASK;   // desktop and signal at the same rate: nothing held back

        layout.Paths[layout.Path].Target.RefreshRate = signal.VSyncFreq;

        return SetDisplayConfig(
            (uint)layout.Paths.Length, layout.Paths,
            (uint)layout.Modes.Length, layout.Modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_VIRTUAL_REFRESH_RATE_AWARE) == 0;
    }

    private static int Rate(in DISPLAYCONFIG_VIDEO_SIGNAL_INFO signal) => (int)Math.Round(Hertz(signal.VSyncFreq));

    private static double Hertz(DISPLAYCONFIG_RATIONAL rate) =>
        rate.Denominator == 0 ? 0 : (double)rate.Numerator / rate.Denominator;

    /// <summary>The panel's timings at the signal's current picture size.</summary>
    private static IEnumerable<PanelTiming> Timings(Layout layout)
    {
        var size = layout.Signal.ActiveSize;
        return PanelTimings.Of(layout.DevicePath())
            .Where(timing => timing.Width == size.Width && timing.Height == size.Height);
    }

    /// <summary>Windows' whole display arrangement, and where the primary display sits in it.</summary>
    private sealed class Layout
    {
        public required DISPLAYCONFIG_PATH_INFO[] Paths { get; init; }
        public required DISPLAYCONFIG_MODE_INFO[] Modes { get; init; }

        /// <summary>Index of the primary display's path.</summary>
        public required int Path { get; init; }

        private int Target => (int)Paths[Path].Target.ModeInfoIdx;

        public ref DISPLAYCONFIG_VIDEO_SIGNAL_INFO Signal => ref Modes[Target].TargetMode;

        public static Layout? Read()
        {
            const uint flags = QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_REFRESH_RATE_AWARE;

            if (GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount) != 0) return null;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, 0) != 0) return null;

            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);

            for (var path = 0; path < paths.Length; path++)
            {
                var source = paths[path].Source.ModeInfoIdx;
                var target = paths[path].Target.ModeInfoIdx;
                if (source >= modes.Length || target >= modes.Length) continue;
                if (modes[target].InfoType != DISPLAYCONFIG_MODE_INFO_TYPE_TARGET) continue;

                // The primary display is the one whose piece of desktop starts at the origin.
                if (modes[source].SourceMode is { X: 0, Y: 0 })
                    return new Layout { Paths = paths, Modes = modes, Path = path };
            }

            return null;
        }

        public string DevicePath()
        {
            var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                Type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                AdapterId = Paths[Path].Target.AdapterId,
                Id = Paths[Path].Target.Id,
            };

            return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.DevicePath : "";
        }
    }
}
