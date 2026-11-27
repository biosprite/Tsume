using Tsume.App.Platform.Interop;
using static Tsume.App.Platform.Interop.User32;

namespace Tsume.App.Platform;

/// <summary>
/// Refresh rate of the primary display: the real signal rate (<see cref="DisplaySignal"/>),
/// falling back to the desktop rate only when the panel's timings cannot be read.
/// </summary>
public static class DisplayMode
{
    public static int CurrentRefreshRate() => DisplaySignal.CurrentRate() ?? DesktopRate();

    /// <summary>VRR frame-rate cap using the Reflex ceiling (<c>Hz - Hz² / 3600</c>), or 0 if below <paramref name="minFps"/>.</summary>
    public static int VrrFrameRateLimit(int refreshHz, int minFps = 15, int maxFps = 240)
    {
        if (refreshHz < minFps) return 0;
        var cap = (int)Math.Floor(refreshHz - refreshHz * (double)refreshHz / 3600.0);
        return Math.Clamp(cap, minFps, maxFps);
    }

    /// <summary>VRR frame-rate cap matching OptiScaler's calculator (0.3 ms margin; 0.1 FPS for XeLL, whole FPS for Anti-Lag 2).</summary>
    public static double OptiScalerVrrFrameRateLimit(
        int refreshHz,
        bool roundToWholeFps = false,
        double minFps = 15,
        double maxFps = 240)
    {
        if (refreshHz < minFps) return 0;
        const double marginMs = 0.3;
        var cap = Math.Round(10000.0 / (1000.0 / refreshHz + marginMs), MidpointRounding.AwayFromZero) / 10.0;
        if (roundToWholeFps)
            cap = Math.Round(cap, MidpointRounding.AwayFromZero);
        return Math.Clamp(cap, minFps, maxFps);
    }

    /// <summary>Every refresh rate the display offers at its current resolution, lowest first.</summary>
    public static IReadOnlyList<int> AvailableRefreshRates() =>
        DisplaySignal.Rates() is { Count: > 1 } real ? real : DesktopRates();

    public static bool SetRefreshRate(int hertz) =>
        DisplaySignal.Rates().Contains(hertz) ? DisplaySignal.SetRate(hertz) : SetDesktopRate(hertz);

    private static int DesktopRate() => Current() is { } mode ? (int)mode.DisplayFrequency : 0;

    private static IReadOnlyList<int> DesktopRates()
    {
        if (Current() is not { } now) return [];

        var rates = new SortedSet<int>();
        var mode = DEVMODE.Create();

        for (var index = 0; EnumDisplaySettings(null, index, ref mode); index++)
        {
            if (mode.PelsWidth == now.PelsWidth && mode.PelsHeight == now.PelsHeight && mode.BitsPerPel == now.BitsPerPel)
                rates.Add((int)mode.DisplayFrequency);
        }

        return [.. rates];
    }

    private static bool SetDesktopRate(int hertz)
    {
        if (Current() is not { } mode) return false;

        mode.DisplayFrequency = (uint)hertz;
        mode.Fields = DM_DISPLAYFREQUENCY;

        return ChangeDisplaySettingsEx(null, ref mode, 0, CDS_UPDATEREGISTRY, 0) == DISP_CHANGE_SUCCESSFUL;
    }

    private static DEVMODE? Current()
    {
        var mode = DEVMODE.Create();
        return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode) ? mode : null;
    }
}
