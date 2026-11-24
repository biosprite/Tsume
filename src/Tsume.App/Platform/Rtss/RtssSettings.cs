using System.Text.Json.Serialization;

namespace Tsume.App.Platform.Rtss;

/// <summary>Performance overlay levels drawn by Tsume through RTSS, each mapped to an Essential layout.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RtssOverlayLevel
{
    Off = 0,
    Fps = 1,
    Bar = 2,
    Detailed = 3,
    Full = 4,
}

/// <summary>Screen anchor preset (or free-dragged custom position) for an individual FPS overlay level.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RtssOverlayAlignment
{
    TopLeft = 0,
    TopCenter = 1,
    TopRight = 2,
    MiddleLeft = 3,
    Center = 4,
    MiddleRight = 5,
    BottomLeft = 6,
    BottomCenter = 7,
    BottomRight = 8,
    Custom = 9,
}

/// <summary>
/// Saved position for a single overlay level. Custom positions are stored as normalized <c>0..1</c> fractions of
/// the screen's usable travel range so they stay inside the screen if display resolution changes.
/// </summary>
public sealed record RtssOverlayPlacement(
    RtssOverlayAlignment Alignment,
    double? CustomX = null,
    double? CustomY = null);

/// <summary>Pixel dimensions and screen-space alignment math for Tsume's four Essential overlay levels.</summary>
public static class RtssOverlayMetrics
{
    public const int EdgeMargin = 24;
    public const int SnapThresholdPx = 22;

    public static (int Width, int Height) GetSize(RtssOverlayLevel level) => level switch
    {
        RtssOverlayLevel.Fps      => (72, 72),
        RtssOverlayLevel.Bar      => (452, 72),
        RtssOverlayLevel.Detailed => (686, 93),
        RtssOverlayLevel.Full     => (225, 464),
        _                         => (0, 0),
    };

    public static RtssOverlayAlignment DefaultAlignment(RtssOverlayLevel level) => level switch
    {
        RtssOverlayLevel.Full => RtssOverlayAlignment.MiddleRight,
        _                     => RtssOverlayAlignment.TopCenter,
    };

    public static RtssOverlayPlacement DefaultPlacement(RtssOverlayLevel level) =>
        new(DefaultAlignment(level));

    public static (int X, int Y) ResolveAnchor(
        RtssOverlayLevel level, RtssOverlayAlignment alignment, int screenWidth, int screenHeight)
    {
        var (w, h) = GetSize(level);
        var maxX = Math.Max(0, screenWidth - w);
        var maxY = Math.Max(0, screenHeight - h);
        var left = Math.Min(EdgeMargin, maxX);
        var right = Math.Max(0, screenWidth - w - EdgeMargin);
        var centerX = Math.Clamp((screenWidth - w) / 2, 0, maxX);
        var top = Math.Min(EdgeMargin, maxY);
        var bottom = Math.Max(0, screenHeight - h - EdgeMargin);
        var middleY = Math.Clamp((screenHeight - h) / 2, 0, maxY);

        return alignment switch
        {
            RtssOverlayAlignment.TopLeft      => (left, top),
            RtssOverlayAlignment.TopCenter    => (centerX, top),
            RtssOverlayAlignment.TopRight     => (right, top),
            RtssOverlayAlignment.MiddleLeft   => (left, middleY),
            RtssOverlayAlignment.Center       => (centerX, middleY),
            RtssOverlayAlignment.MiddleRight  => (right, middleY),
            RtssOverlayAlignment.BottomLeft   => (left, bottom),
            RtssOverlayAlignment.BottomCenter => (centerX, bottom),
            RtssOverlayAlignment.BottomRight  => (right, bottom),
            _                                 => ResolveAnchor(level, DefaultAlignment(level), screenWidth, screenHeight),
        };
    }

    public static (int X, int Y) ResolvePosition(
        RtssOverlayLevel level, RtssOverlayPlacement placement, int screenWidth, int screenHeight)
    {
        if (placement.Alignment != RtssOverlayAlignment.Custom
            || placement.CustomX is not { } fx
            || placement.CustomY is not { } fy)
        {
            return ResolveAnchor(level, placement.Alignment, screenWidth, screenHeight);
        }

        var (w, h) = GetSize(level);
        var maxX = Math.Max(0, screenWidth - w);
        var maxY = Math.Max(0, screenHeight - h);
        var x = (int)Math.Round(Math.Clamp(fx, 0.0, 1.0) * maxX);
        var y = (int)Math.Round(Math.Clamp(fy, 0.0, 1.0) * maxY);
        return (Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));
    }

    public static RtssOverlayPlacement CreateCustomPlacement(
        RtssOverlayLevel level, int x, int y, int screenWidth, int screenHeight)
    {
        var (w, h) = GetSize(level);
        var maxX = Math.Max(1, screenWidth - w);
        var maxY = Math.Max(1, screenHeight - h);
        var fx = Math.Clamp(x / (double)maxX, 0.0, 1.0);
        var fy = Math.Clamp(y / (double)maxY, 0.0, 1.0);
        return new RtssOverlayPlacement(RtssOverlayAlignment.Custom, fx, fy);
    }

    public static bool TrySnapToAlignment(
        RtssOverlayLevel level, int x, int y, int screenWidth, int screenHeight,
        out RtssOverlayAlignment snapped, out int snappedX, out int snappedY)
    {
        snapped = RtssOverlayAlignment.Custom;
        snappedX = x;
        snappedY = y;

        var bestDistSq = SnapThresholdPx * SnapThresholdPx;
        var found = false;

        for (var i = 0; i <= (int)RtssOverlayAlignment.BottomRight; i++)
        {
            var candidate = (RtssOverlayAlignment)i;
            var (ax, ay) = ResolveAnchor(level, candidate, screenWidth, screenHeight);
            var dx = x - ax;
            var dy = y - ay;
            var distSq = dx * dx + dy * dy;
            if (distSq <= bestDistSq)
            {
                bestDistSq = distSq;
                snapped = candidate;
                snappedX = ax;
                snappedY = ay;
                found = true;
            }
        }

        return found;
    }
}

/// <summary>
/// Snapshot of the user's own RTSS Global profile appearance that older Tsume versions took before restyling
/// RTSS for their Steam-style overlay. Tsume now only reads it once, to put those values back.
/// </summary>
public sealed record RtssAppearanceBackup(
    int PositionX,
    int PositionY,
    int ZoomRatio,
    int EnableFill,
    uint FillColor);

/// <summary>Saved RTSS preferences, stored inside <see cref="SavedState"/>.</summary>
public sealed record RtssSettings
{
    public const int MinCustomFps = 15;
    public const int MaxCustomFps = 240;

    /// <summary>
    /// True to draw Tsume's overlay into RTSS; false to leave the overlay layout
    /// completely to the user's own RTSS / Afterburner / HWiNFO setup.
    /// </summary>
    public bool UseTsumeOverlay { get; init; } = true;

    public RtssOverlayLevel Level { get; init; } = RtssOverlayLevel.Off;

    /// <summary>Last non-Off level shown, restored when toggling the FPS overlay back on via a shortcut.</summary>
    public RtssOverlayLevel LastActiveLevel { get; init; } = RtssOverlayLevel.Fps;

    /// <summary>
    /// True once a non-Off level has been picked. Until then Tsume leaves RTSS's OverlayEditor plugin and the
    /// user's own layout alone, even though <see cref="UseTsumeOverlay"/> defaults to on.
    /// </summary>
    public bool OverlayClaimed { get; init; }

    /// <summary>Last custom frame-rate limit chosen with the slider (FPS).</summary>
    public int CustomFrameRateLimit { get; init; } = 45;

    /// <summary>
    /// Opt-in: true to create RTSS <c>EnableHooking=0</c> exclusion profiles for detected anti-cheat games.
    /// Defaults to false because RTSS is signed and whitelisted by most anti-cheats (e.g. Easy Anti-Cheat).
    /// </summary>
    public bool SkipAntiCheatGames { get; init; } = false;

    /// <summary>Backed-up RTSS Global appearance from before Tsume modified it; null when unmodified.</summary>
    public RtssAppearanceBackup? SavedAppearance { get; init; }

    /// <summary>Per-level screen alignment / custom dragged position. Unlisted levels use their default anchor.</summary>
    public Dictionary<RtssOverlayLevel, RtssOverlayPlacement>? Placements { get; init; }

    /// <summary>
    /// True to keep an invisible touch-drag handle over the active FPS overlay so it can be dragged directly
    /// on touchscreen handhelds without opening Tsume. Off unless chosen: the handle takes every tap and click
    /// that lands on the overlay, so the game under it gets none of them.
    /// </summary>
    public bool TouchDragEnabled { get; init; }

    public RtssOverlayPlacement GetPlacement(RtssOverlayLevel level) =>
        Placements is not null && Placements.TryGetValue(level, out var saved)
            ? saved
            : RtssOverlayMetrics.DefaultPlacement(level);

    public RtssSettings WithPlacement(RtssOverlayLevel level, RtssOverlayPlacement placement)
    {
        var copy = Placements is not null
            ? new Dictionary<RtssOverlayLevel, RtssOverlayPlacement>(Placements)
            : [];
        copy[level] = placement;
        return this with { Placements = copy };
    }
}
