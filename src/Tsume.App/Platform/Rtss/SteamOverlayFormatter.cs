using System.Globalization;
using System.Text;

namespace Tsume.App.Platform.Rtss;

/// <summary>
/// Builds SteamOS / Steam Deck-inspired RTSS OSD hypertext strings for Levels 1–4.
/// Uses RTSS's native <c>&lt;FR&gt;</c> and <c>&lt;FT&gt;</c> macros for instant frame rate and frametime,
/// Steam Deck accent colors, and an embedded frametime graph (<c>&lt;OBJ=00000000&gt;</c>) on Level 4.
/// </summary>
public static class SteamOverlayFormatter
{
    // SteamOS / MangoHud inspired palette (RRGGBB hex for RTSS <C=...>).
    private const string ColFps = "66E066";
    private const string ColGpu = "5CE68A";
    private const string ColCpu = "66B8FF";
    private const string ColRam = "B899FF";
    private const string ColFan = "A8B4C4";
    private const string ColBatGood = "7CE67C";
    private const string ColBatWarn = "F5C542";
    private const string ColBatLow = "FF5C5C";
    private const string ColTempWarn = "F5B841";
    private const string ColTempHot = "FF5C5C";
    private const string ColValue = "F2F5F8";
    private const string ColMuted = "8E99A8";

    /// <summary>Formats the RTSS hypertext payload for the requested Steam-style level.</summary>
    public static string Format(RtssOverlayLevel level, TelemetryFrame frame, bool hasGraph) => level switch
    {
        RtssOverlayLevel.Fps      => FormatFps(),
        RtssOverlayLevel.Bar      => FormatBar(frame),
        RtssOverlayLevel.Detailed => FormatDetailed(frame, includeGraph: false, includeHeader: false),
        RtssOverlayLevel.Full     => FormatDetailed(frame, includeGraph: hasGraph, includeHeader: true),
        _                         => "",
    };

    /// <summary>Level 1: Minimal Steam Deck green FPS counter.</summary>
    private static string FormatFps() => $"<C={ColFps}><FR><C>";

    /// <summary>Level 2: Compact single-line Steam Deck horizontal performance strip.</summary>
    private static string FormatBar(TelemetryFrame f)
    {
        var sb = new StringBuilder(256);
        var sep = $" <C={ColMuted}>|<C> ";

        // Battery
        if (f.Battery is { } bat)
        {
            var batCol = BatteryColor(bat);
            sb.Append($"<C={batCol}>BAT<C> <C={ColValue}>{bat.Percent}%<C>");
            if (bat.Watts is { } w)
            {
                var sign = w > 0 ? "+" : "";
                sb.Append($" <C={ColMuted}>{sign}{w.ToString("0.0", CultureInfo.InvariantCulture)}W<C>");
            }
            else if (bat.PluggedIn)
            {
                sb.Append($" <C={ColMuted}>AC<C>");
            }
            sb.Append(sep);
        }

        // GPU
        if (f.GpuPercent is { } gpu)
        {
            sb.Append($"<C={ColGpu}>GPU<C> <C={ColValue}>{gpu}%<C>");
            if (f.VramUsedGb is { } vram)
                sb.Append($" <C={ColMuted}>{vram.ToString("0.0", CultureInfo.InvariantCulture)}G<C>");
            sb.Append(sep);
        }

        // CPU
        if (f.CpuPercent is not null || f.CpuTempC is not null)
        {
            sb.Append($"<C={ColCpu}>CPU<C>");
            if (f.CpuPercent is { } cpu)
                sb.Append($" <C={ColValue}>{cpu}%<C>");
            if (f.CpuTempC is { } temp)
                sb.Append($" <C={TempColor(temp)}>{temp}C<C>");
            sb.Append(sep);
        }

        // RAM
        if (f.RamUsedGb is { } ram)
        {
            sb.Append($"<C={ColRam}>RAM<C> <C={ColValue}>{ram.ToString("0.0", CultureInfo.InvariantCulture)}G<C>");
            sb.Append(sep);
        }

        // FPS + Frametime (native RTSS macros)
        sb.Append($"<C={ColFps}><FR><C> <C={ColMuted}>FPS<C> <C={ColValue}><FT><C><C={ColMuted}>ms<C>");
        return sb.ToString();
    }

    /// <summary>Levels 3 &amp; 4: Stacked Steam Deck performance readout, plus live frametime graph on Level 4.</summary>
    private static string FormatDetailed(TelemetryFrame f, bool includeGraph, bool includeHeader)
    {
        var sb = new StringBuilder(512);

        if (includeHeader)
        {
            var mode = string.IsNullOrWhiteSpace(f.PowerModeLabel) ? "TSUME" : f.PowerModeLabel.ToUpperInvariant();
            var time = f.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            sb.Append($"<C={ColMuted}>{mode}");
            if (f.SustainedWatts is { } pl1 && f.BoostWatts is { } pl2)
                sb.Append($"  {pl1}W/{pl2}W");
            sb.Append($"  {time}<C>\n");
        }

        // FPS & Frametime
        sb.Append($"<C={ColFps}>FPS<C>   <C={ColValue}><FR><C>  <C={ColMuted}><FT> ms<C>");

        if (includeGraph)
            sb.Append($"\n<C={ColFps}><OBJ=00000000><C>");

        // GPU
        if (f.GpuPercent is not null || f.VramUsedGb is not null)
        {
            sb.Append($"\n<C={ColGpu}>GPU<C>  ");
            if (f.GpuPercent is { } gpu)
                sb.Append($" <C={ColValue}>{gpu,3}%<C>");
            if (f.VramUsedGb is { } vram)
                sb.Append($"  <C={ColMuted}>{vram.ToString("0.0", CultureInfo.InvariantCulture)} GB<C>");
        }

        // CPU
        if (f.CpuPercent is not null || f.CpuTempC is not null)
        {
            sb.Append($"\n<C={ColCpu}>CPU<C>  ");
            if (f.CpuPercent is { } cpu)
                sb.Append($" <C={ColValue}>{cpu,3}%<C>");
            if (f.CpuTempC is { } temp)
                sb.Append($"  <C={TempColor(temp)}>{temp} C<C>");
        }

        // RAM
        if (f.RamUsedGb is { } ramUsed && f.RamTotalGb is { } ramTotal)
        {
            sb.Append($"\n<C={ColRam}>RAM<C>   <C={ColValue}>{ramUsed.ToString("0.0", CultureInfo.InvariantCulture)}<C><C={ColMuted}> / {ramTotal.ToString("0", CultureInfo.InvariantCulture)} GB<C>");
        }

        // Fan & Power Limit
        if (f.FanRpm is not null || (!includeHeader && f.SustainedWatts is not null))
        {
            sb.Append($"\n<C={ColFan}>FAN<C>  ");
            if (f.FanRpm is { } rpm)
                sb.Append($" <C={ColValue}>{rpm:N0}<C><C={ColMuted}> rpm<C>");
            if (!includeHeader && f.SustainedWatts is { } pl1)
                sb.Append($"  <C={ColMuted}>{pl1}W<C>");
        }

        // Battery
        if (f.Battery is { } bat)
        {
            var batCol = BatteryColor(bat);
            sb.Append($"\n<C={batCol}>BAT<C>   <C={ColValue}>{bat.Percent}%<C>");

            if (bat.Watts is { } w)
            {
                var sign = w > 0 ? "+" : "";
                sb.Append($"  <C={ColMuted}>{sign}{w.ToString("0.0", CultureInfo.InvariantCulture)} W<C>");
            }
            else if (bat.PluggedIn)
            {
                sb.Append($"  <C={ColMuted}>AC<C>");
            }

            if (bat.EstimatedMinutes is { } mins and > 0)
            {
                var hours = mins / 60;
                var rem = mins % 60;
                sb.Append(hours > 0
                    ? $"  <C={ColMuted}>{hours}h {rem:00}m<C>"
                    : $"  <C={ColMuted}>{rem}m<C>");
            }
        }

        return sb.ToString();
    }

    private static string BatteryColor(Battery.Reading bat) =>
        bat.PluggedIn || bat.Flow == Battery.Flow.Charging ? ColBatGood
        : bat.Percent <= 15                                ? ColBatLow
        : bat.Percent <= 30                                ? ColBatWarn
        : ColBatGood;

    private static string TempColor(int celsius) =>
        celsius >= 90 ? ColTempHot
        : celsius >= 80 ? ColTempWarn
        : ColValue;
}
