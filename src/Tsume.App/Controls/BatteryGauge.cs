using System.Windows;
using System.Windows.Media;

namespace Tsume.App.Controls;

/// <summary>A small battery outline that fills with the charge level.</summary>
public sealed class BatteryGauge : FrameworkElement
{
    // These two follow the colour scheme: the brushes are recoloured in place.
    private static Brush Outline => Theme.Colours.MutedBrush;
    private static Brush Normal => Theme.Colours.TextBrush;

    private static readonly Brush Charging = Frozen(Color.FromRgb(0x3D, 0xDC, 0x97));
    private static readonly Brush Low = Frozen(Color.FromRgb(0xFF, 0x5D, 0x5D));

    public static readonly DependencyProperty PercentProperty = Register<int>(nameof(Percent));
    public static readonly DependencyProperty IsChargingProperty = Register<bool>(nameof(IsCharging));
    public static readonly DependencyProperty IsLowProperty = Register<bool>(nameof(IsLow));

    public int Percent { get => (int)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool IsCharging { get => (bool)GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    public bool IsLow { get => (bool)GetValue(IsLowProperty); set => SetValue(IsLowProperty, value); }

    protected override Size MeasureOverride(Size available) => new(31, 15);

    protected override void OnRender(DrawingContext canvas)
    {
        const double bodyWidth = 27, height = 15, inset = 2.5;

        // Body, then the little terminal nub on the right.
        canvas.DrawRoundedRectangle(null, new Pen(Outline, 1.5), new Rect(0.75, 0.75, bodyWidth - 1.5, height - 1.5), 4, 4);
        canvas.DrawRoundedRectangle(Outline, null, new Rect(bodyWidth + 1, 4.5, 2.5, 6), 1.2, 1.2);

        var level = Math.Clamp(Percent, 0, 100) / 100.0;
        var fill = IsCharging ? Charging : IsLow ? Low : Normal;
        var width = Math.Max(2, (bodyWidth - inset * 2) * level);

        canvas.DrawRoundedRectangle(fill, null, new Rect(inset, inset, width, height - inset * 2), 2, 2);
    }

    private static DependencyProperty Register<T>(string name) => DependencyProperty.Register(
        name, typeof(T), typeof(BatteryGauge),
        new FrameworkPropertyMetadata(default(T), FrameworkPropertyMetadataOptions.AffectsRender));

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
