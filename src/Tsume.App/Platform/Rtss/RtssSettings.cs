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

/// <summary>Color variant of the Essential overlay layouts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RtssOverlayTheme
{
    Dark = 0,
    Smoke = 1,
    White = 2,
    Float = 3,
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

    public RtssOverlayTheme Theme { get; init; } = RtssOverlayTheme.Dark;

    /// <summary>Last custom frame-rate limit chosen with the slider (FPS).</summary>
    public int CustomFrameRateLimit { get; init; } = 45;

    /// <summary>
    /// Opt-in: true to create RTSS <c>EnableHooking=0</c> exclusion profiles for detected anti-cheat games.
    /// Defaults to false because RTSS is signed and whitelisted by most anti-cheats (e.g. Easy Anti-Cheat).
    /// </summary>
    public bool SkipAntiCheatGames { get; init; } = false;

    /// <summary>Backed-up RTSS Global appearance from before Tsume modified it; null when unmodified.</summary>
    public RtssAppearanceBackup? SavedAppearance { get; init; }
}
