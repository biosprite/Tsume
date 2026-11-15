using System.Windows;
using System.Windows.Controls;

namespace Tsume.App.Controls;

/// <summary>A button with an icon, a name, and an optional line of detail.</summary>
public sealed class Tile : Button
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Tile), new PropertyMetadata(""));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Tile), new PropertyMetadata(""));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(Tile), new PropertyMetadata(""));

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(Tile), new PropertyMetadata(false));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(Tile), new PropertyMetadata(Orientation.Vertical));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>Vertical stacks the icon above the words; Horizontal puts it beside them.</summary>
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Current state in a word or two, e.g. "120 Hz". Hidden when empty.</summary>
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    /// <summary>Tints the tile with the accent to show the feature is active.</summary>
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
}
