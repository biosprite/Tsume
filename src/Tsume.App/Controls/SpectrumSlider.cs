using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Tsume.App.Theme;
using Tsume.Core.Lighting;

namespace Tsume.App.Controls;

/// <summary>Which half of a colour a <see cref="SpectrumSlider"/> chooses.</summary>
public enum SpectrumPart
{
    /// <summary>Around the colour wheel, red to red.</summary>
    Hue,

    /// <summary>From white to the pure hue.</summary>
    Saturation,
}

/// <summary>
/// A strip of colour with a thumb. Two of them, one per <see cref="SpectrumPart"/>, bound to the
/// same colour reach every colour; brightness is a separate slider.
/// </summary>
public sealed class SpectrumSlider : Control
{
    private const double ThumbSize = 28;

    /// <summary>A direction pressed again this soon after the last time is being held, not tapped.</summary>
    private const int HeldWithin = 160;

    private static readonly Brush Rainbow = MakeRainbow();

    private FrameworkElement? _track;
    private Border? _strip;
    private Shape? _dot;
    private readonly TranslateTransform _slide = new();

    // The saturation strip's own brush: only its far end changes, with the hue.
    private readonly GradientStop _pure = new(Colors.Red, 1);
    private readonly LinearGradientBrush _fade;

    /// <summary>
    /// The hue last seen. White has none, so it is remembered here for the hue thumb
    /// and for saturation to come back to.
    /// </summary>
    private double _hue;

    /// <summary>Where the thumb sits, 0–1.</summary>
    private double _ratio;

    private bool _dragging;
    private TouchDevice? _finger;

    private Key _lastKey;
    private long _lastKeyAt;
    private int _streak;

    public SpectrumSlider()
    {
        Focusable = true;
        _fade = new LinearGradientBrush([new GradientStop(Colors.White, 0), _pure], 0);
    }

    public static readonly DependencyProperty ColourProperty = DependencyProperty.Register(
        nameof(Colour), typeof(Color), typeof(SpectrumSlider),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((SpectrumSlider)d).Absorb((Color)e.NewValue)));

    public static readonly DependencyProperty PartProperty = DependencyProperty.Register(
        nameof(Part), typeof(SpectrumPart), typeof(SpectrumSlider),
        new PropertyMetadata(SpectrumPart.Hue, (d, _) => ((SpectrumSlider)d).Absorb(((SpectrumSlider)d).Colour)));

    /// <summary>The colour being chosen. This strip changes only its own part of it.</summary>
    public Color Colour { get => (Color)GetValue(ColourProperty); set => SetValue(ColourProperty, value); }

    public SpectrumPart Part { get => (SpectrumPart)GetValue(PartProperty); set => SetValue(PartProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _track = GetTemplateChild("PART_Track") as FrameworkElement;
        _strip = GetTemplateChild("PART_Strip") as Border;
        _dot = GetTemplateChild("PART_Dot") as Shape;

        if (GetTemplateChild("PART_Thumb") is UIElement thumb) thumb.RenderTransform = _slide;
        if (_track is not null) _track.SizeChanged += (_, _) => Draw(animate: false);

        Draw(animate: false);
    }

    /// <summary>The colour changed, here or elsewhere: work out where that puts the thumb.</summary>
    private void Absorb(Color colour)
    {
        var rgb = new Rgb(colour.R, colour.G, colour.B);
        var saturation = rgb.Saturation;

        // 8-bit channels hold hue coarsely near white: keep the remembered hue while it
        // still reproduces this colour to within rounding (distance 3).
        if (saturation > 0 && Rgb.FromHue(_hue, saturation).DistanceTo(rgb) > 3) _hue = rgb.Hue;

        if (_dragging) return;   // the thumb is under a finger, and stays there

        _ratio = Part == SpectrumPart.Hue ? _hue / 360 : saturation;
        Draw(animate: true);
    }

    private void Draw(bool animate)
    {
        if (_track is null) return;

        var pure = Rgb.FromHue(_hue);
        var pureColour = Color.FromRgb(pure.R, pure.G, pure.B);

        if (_strip is not null)
        {
            if (Part == SpectrumPart.Saturation) _pure.Color = pureColour;
            _strip.Background = Part == SpectrumPart.Hue ? Rainbow : _fade;
        }

        if (_dot is not null)
            _dot.Fill = new SolidColorBrush(Part == SpectrumPart.Hue ? pureColour : Colour);

        var x = _ratio * Math.Max(0, _track.ActualWidth - ThumbSize);

        if (animate && !_dragging && IsLoaded) _slide.Animate(TranslateTransform.XProperty, x, Motion.Quick, Motion.Settle);
        else _slide.Snap(TranslateTransform.XProperty, x);
    }

    private void Choose(double ratio)
    {
        _ratio = Math.Clamp(ratio, 0, 1);

        Rgb chosen;
        if (Part == SpectrumPart.Hue)
        {
            _hue = _ratio * 360;

            // Changing the hue of white would do nothing, so it picks the pure hue instead.
            var saturation = new Rgb(Colour.R, Colour.G, Colour.B).Saturation;
            chosen = Rgb.FromHue(_hue, saturation > 0 ? saturation : 1);
        }
        else
        {
            chosen = Rgb.FromHue(_hue, _ratio);
        }

        var moving = _dragging;
        _dragging = true;   // the thumb is already where it was put: Absorb leaves it be
        SetCurrentValue(ColourProperty, Color.FromRgb(chosen.R, chosen.G, chosen.B));
        _dragging = moving;

        Draw(animate: false);
    }

    /// <summary>The thumb's centre goes under the finger, and stops at the strip's rounded ends.</summary>
    private void Follow(double x)
    {
        if (_track is null || _track.ActualWidth <= ThumbSize) return;
        Choose((x - ThumbSize / 2) / (_track.ActualWidth - ThumbSize));
    }

    /// <summary>A tap moves a small step; held, the steps grow, as on the level slider.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var direction = e.Key switch { Key.Left => -1, Key.Right => +1, _ => 0 };
        if (direction == 0) return;

        var now = Environment.TickCount64;
        _streak = e.Key == _lastKey && now - _lastKeyAt < HeldWithin ? _streak + 1 : 0;
        _lastKey = e.Key;
        _lastKeyAt = now;

        var step = Part == SpectrumPart.Hue ? 1.0 / 180 : 1.0 / 50;   // 2° of hue, 2 % of saturation
        Choose(_ratio + direction * step * Math.Min(1 + _streak / 2, 5));
        e.Handled = true;
    }

    // Touch is handled directly, as in LevelSlider: promoted mouse events arrive late inside a touch-scrolling list.
    protected override void OnTouchDown(TouchEventArgs e)
    {
        if (_finger is not null) return;

        Focus();
        _finger = e.TouchDevice;
        CaptureTouch(_finger);
        _dragging = true;
        Follow(e.GetTouchPoint(_track).Position.X);
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        if (e.TouchDevice != _finger) return;

        Follow(e.GetTouchPoint(_track).Position.X);
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        if (e.TouchDevice != _finger) return;

        ReleaseTouchCapture(_finger);
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        if (e.TouchDevice != _finger) return;

        _finger = null;
        _dragging = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        _dragging = true;
        Follow(e.GetPosition(_track).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging && _finger is null) Follow(e.GetPosition(_track).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_finger is null) _dragging = false;
    }

    /// <summary>Once around the wheel. Straight lines between these six are the pure hues exactly.</summary>
    private static Brush MakeRainbow()
    {
        var stops = new GradientStopCollection();
        for (var sixth = 0; sixth <= 6; sixth++)
        {
            var colour = Rgb.FromHue(sixth * 60);
            stops.Add(new GradientStop(Color.FromRgb(colour.R, colour.G, colour.B), sixth / 6.0));
        }

        var brush = new LinearGradientBrush(stops, 0);
        brush.Freeze();
        return brush;
    }
}
