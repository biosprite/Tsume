using System.Windows;
using System.Windows.Media;

namespace Tsume.App.Theme;

/// <summary>
/// Applies a <see cref="ColourScheme"/> app-wide. Application resource brushes are frozen, so each
/// Palette.xaml brush is replaced, not recoloured; only <c>DynamicResource</c> references see the change.
/// </summary>
public static class Colours
{
    private static readonly Color Dark = Color.FromRgb(0x0B, 0x0D, 0x11);
    private static readonly Color Light = Color.FromRgb(0xF4, 0xF6, 0xFA);

    /// <summary>Text with less contrast than this against the background is replaced by a legible colour.</summary>
    private const double LeastContrast = 2.5;

    public static ColourScheme Current { get; private set; } = ColourScheme.Default;

    /// <summary>Ordinary text. Recoloured in place, so anything drawn with it follows.</summary>
    public static SolidColorBrush TextBrush { get; } = new(Light);

    /// <summary>Supporting text and outlines.</summary>
    public static SolidColorBrush MutedBrush { get; } = new(Color.FromRgb(0x8E, 0x96, 0xA6));

    /// <summary>Words that sit on the accent itself: dark on a bright accent, light on a deep one.</summary>
    public static Color OnAccent { get; private set; } = Dark;

    /// <summary>Raised after a new scheme is in place.</summary>
    public static event Action? Changed;

    /// <param name="animate">Cross-fade the accent; leave off while a slider is being dragged.</param>
    public static void Apply(ColourScheme scheme, bool animate = false)
    {
        Current = scheme;

        var shades = Shades(scheme);
        var resources = Application.Current.Resources;

        foreach (var (name, colour) in shades)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            resources[name] = brush;   // laid over Palette.xaml's entry of the same name
        }

        TextBrush.Color = shades["Text"];
        MutedBrush.Color = shades["Text.Muted"];
        OnAccent = shades["Switch.KnobOn"];

        Accent.MoveTo(scheme.AccentColour, animate);
        Changed?.Invoke();
    }

    /// <summary>Every brush in Palette.xaml that follows the scheme, by its name there.</summary>
    private static Dictionary<string, Color> Shades(ColourScheme scheme)
    {
        // The default scheme uses Palette.xaml's hand-picked values exactly.
        if (scheme == ColourScheme.Default) return new()
        {
            ["Panel.Fill"]      = Color.FromArgb(0xF5, 0x13, 0x15, 0x1A),
            ["Panel.Edge"]      = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
            ["Panel.Scrim"]     = Color.FromArgb(0xB8, 0x0A, 0x0B, 0x0E),
            ["Sheet.Fill"]      = Color.FromRgb(0x1B, 0x1E, 0x25),
            ["Surface"]         = Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF),
            ["Surface.Hover"]   = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF),
            ["Surface.Divider"] = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            ["Switch.Groove"]   = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
            ["Switch.KnobOn"]   = Dark,
            ["Text"]            = Light,
            ["Text.Muted"]      = Color.FromRgb(0x8E, 0x96, 0xA6),
            ["Text.Faint"]      = Color.FromRgb(0x5C, 0x64, 0x72),
        };

        var back = scheme.BackgroundColour;
        var text = scheme.TextColour;
        var accent = scheme.AccentColour;

        if (Contrast(text, back) < LeastContrast) text = Clearest(on: back);

        // Surfaces are the text colour at low alpha over the background.
        Color Film(byte strength) => Color.FromArgb(strength, text.R, text.G, text.B);

        return new()
        {
            ["Panel.Fill"]      = Color.FromArgb(0xF5, back.R, back.G, back.B),
            ["Panel.Edge"]      = Film(0x26),
            ["Panel.Scrim"]     = WithAlpha(0xB8, Mix(back, Colors.Black, 0.47)),
            ["Sheet.Fill"]      = Mix(back, text, 0.035),
            ["Surface"]         = Film(0x0E),
            ["Surface.Hover"]   = Film(0x18),
            ["Surface.Divider"] = Film(0x14),
            ["Switch.Groove"]   = Film(0x26),
            ["Switch.KnobOn"]   = Clearest(on: accent, preferLight: 1.5),
            ["Text"]            = text,
            ["Text.Muted"]      = Mix(text, back, 0.43),
            ["Text.Faint"]      = Mix(text, back, 0.65),
        };
    }

    /// <summary>Dark or light, whichever reads better on <paramref name="on"/>.</summary>
    /// <param name="preferLight">How many times better dark must read to be chosen; on mid-tones light looks right.</param>
    private static Color Clearest(Color on, double preferLight = 1) =>
        Contrast(Dark, on) >= Contrast(Light, on) * preferLight ? Dark : Light;

    /// <summary><paramref name="from"/> moved <paramref name="amount"/> (0–1) of the way to <paramref name="to"/>.</summary>
    private static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    private static Color WithAlpha(byte alpha, Color colour) => Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    /// <summary>WCAG contrast ratio: 1 (identical) to 21 (black on white).</summary>
    private static double Contrast(Color a, Color b)
    {
        var (lighter, darker) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color colour)
    {
        static double Linear(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);
    }
}
