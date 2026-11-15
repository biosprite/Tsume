using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tsume.App.Theme;

/// <summary>The highlight colour. Only <see cref="Colours"/> changes it.</summary>
public static class Accent
{
    /// <summary>Full-strength accent: the selected segment, focus rings, active tiles.</summary>
    public static SolidColorBrush Brush { get; } = new(ColourScheme.Default.AccentColour);

    /// <summary>The accent at low opacity: slider fills and tinted surfaces.</summary>
    public static SolidColorBrush Wash { get; } = new(Faded(ColourScheme.Default.AccentColour));

    /// <summary>Recolours every accented element, instantly or by a cross-fade.</summary>
    public static void MoveTo(Color colour, bool animate)
    {
        Brush.BeginAnimation(SolidColorBrush.ColorProperty, Fade(colour, animate));
        Wash.BeginAnimation(SolidColorBrush.ColorProperty, Fade(Faded(colour), animate));
    }

    private static Color Faded(Color colour) => Color.FromArgb(0x52, colour.R, colour.G, colour.B);

    private static ColorAnimation Fade(Color to, bool animate) =>
        new(to, animate ? Motion.Smooth : TimeSpan.Zero) { EasingFunction = Motion.Settle };
}
