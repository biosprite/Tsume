using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// A chunky pill slider: the whole pill is the track, and it fills from the left.
/// Drag anywhere on it, or use left and right.
/// </summary>
public sealed class LevelSlider : RangeBase
{
    private const double CornerRadius = 18;

    private FrameworkElement? _track;
    private FrameworkElement? _fill;
    private bool _dragging;
    private TouchDevice? _finger;

    /// <summary>A direction pressed again this soon after the last time is being held, not tapped.</summary>
    private const int HeldWithin = 160;

    private Key _lastKey;
    private long _lastKeyAt;
    private int _streak;

    public LevelSlider() => Focusable = true;

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(LevelSlider), new PropertyMetadata(""));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(LevelSlider), new PropertyMetadata(""));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(LevelSlider), new PropertyMetadata(""));

    /// <summary>How full the pill is drawn, 0–1. Animated towards the real value.</summary>
    private static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        "Fill", typeof(double), typeof(LevelSlider),
        new PropertyMetadata(0.0, (d, _) => ((LevelSlider)d).DrawFill()));

    /// <summary>Icon shown at the left edge.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>Optional name shown after the icon.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Suffix for the number at the right edge, such as "%" or " W".</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    private double Ratio => Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _track = GetTemplateChild("PART_Track") as FrameworkElement;
        _fill = GetTemplateChild("PART_Fill") as FrameworkElement;

        if (_track is not null) _track.SizeChanged += (_, _) => OnTrackResized();

        this.Snap(FillProperty, Ratio);
    }

    private void OnTrackResized()
    {
        // Round the fill off with the pill, instead of letting it poke out of the corners.
        _track!.Clip = new RectangleGeometry(new Rect(_track.RenderSize), CornerRadius, CornerRadius);
        DrawFill();
    }

    private void DrawFill()
    {
        if (_track is null || _fill is null) return;
        _fill.Width = Math.Max(0, _track.ActualWidth * (double)GetValue(FillProperty));
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        MoveFill();
    }

    protected override void OnMinimumChanged(double oldMinimum, double newMinimum) => MoveFill();
    protected override void OnMaximumChanged(double oldMaximum, double newMaximum) => MoveFill();

    /// <summary>Under a finger the fill is placed by the finger; otherwise it eases to the new value.</summary>
    private void MoveFill()
    {
        if (!_dragging) this.Animate(FillProperty, Ratio, Motion.Quick, Motion.Settle);
    }

    /// <summary>A tap moves one step; while the key is held the steps grow.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var direction = e.Key switch { Key.Left => -1, Key.Right => +1, _ => 0 };
        if (direction == 0) return;

        var now = Environment.TickCount64;
        _streak = e.Key == _lastKey && now - _lastKeyAt < HeldWithin ? _streak + 1 : 0;
        _lastKey = e.Key;
        _lastKeyAt = now;

        var fastest = Math.Max(LargeChange, (Maximum - Minimum) / 10);
        var step = Math.Min(LargeChange * (1 + _streak / 2), fastest);

        Value = Math.Clamp(Value + direction * step, Minimum, Maximum);
        e.Handled = true;
    }

    // Touch is handled directly: inside a touch-scrolling list the promoted mouse events
    // arrive late, held back until the list is sure the touch is not a scroll.
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
        LetGo();
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

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_finger is null) LetGo();
    }

    /// <summary>The value moves in whole steps; the fill stays exactly under the finger.</summary>
    private void Follow(double x)
    {
        if (_track is null || _track.ActualWidth <= 0) return;

        var ratio = Math.Clamp(x / _track.ActualWidth, 0, 1);
        var raw = Minimum + ratio * (Maximum - Minimum);

        Value = SmallChange > 0 ? Math.Round(raw / SmallChange) * SmallChange : raw;
        this.Snap(FillProperty, ratio);
    }

    /// <summary>The finger lifted: the fill settles on the step the value landed on.</summary>
    private void LetGo()
    {
        if (!_dragging) return;

        _dragging = false;
        MoveFill();
    }
}
