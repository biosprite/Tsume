using System.Text.Json.Serialization;

namespace Tsume.App.Platform.Rtss;

/// <summary>SteamOS-style performance overlay levels drawn by Tsume through RTSS.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RtssOverlayLevel
{
    Off = 0,
    Fps = 1,
    Bar = 2,
    Detailed = 3,
    Full = 4,
}

/// <summary>Which screen corner Tsume's Steam-style RTSS overlay anchors to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RtssCorner
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

/// <summary>
/// Snapshot of the user's own RTSS Global profile appearance taken before Tsume applies its
/// Steam-style styling, so turning Tsume's overlay off puts their settings back.
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
    public const int MinScale = 1;
    public const int MaxScale = 4;
    public const int MinCustomFps = 15;
    public const int MaxCustomFps = 240;

    /// <summary>
    /// True to draw Tsume's Steam-style overlay into RTSS; false to leave the overlay layout
    /// completely to the user's own RTSS / Afterburner / HWiNFO setup.
    /// </summary>
    public bool UseTsumeOverlay { get; init; } = true;

    public RtssOverlayLevel Level { get; init; } = RtssOverlayLevel.Off;

    public RtssCorner Corner { get; init; } = RtssCorner.TopLeft;

    /// <summary>RTSS zoom factor (1–4) when Tsume's Steam-style overlay is active.</summary>
    public int Scale { get; init; } = 2;

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
