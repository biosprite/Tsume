using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tsume.App.Controls;

/// <summary>
/// Fluent-style indeterminate circular progress ring with a smoothly breathing, orbiting arc.
/// Animations run only while <see cref="IsActive"/> is true and the control is visible and loaded.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    private static readonly Brush DefaultForeground = Frozen(Color.FromRgb(0x52, 0xC8, 0xFF));
    private static readonly Brush DefaultTrack = Frozen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(true, OnIsActiveChanged));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(DefaultForeground, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty BaseRotationProperty = DependencyProperty.Register(
        "BaseRotation", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty StartAngleProperty = DependencyProperty.Register(
        "StartAngle", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SweepAngleProperty = DependencyProperty.Register(
        "SweepAngle", typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _animating;
    private Pen? _trackPen;
    private Brush? _trackPenBrush;
    private double _trackPenThickness;
    private Pen? _arcPen;
    private Brush? _arcPenBrush;
    private double _arcPenThickness;

    public ProgressRing()
    {
        Loaded += (_, _) => UpdateAnimationState();
        Unloaded += (_, _) => StopAnimations();
        IsVisibleChanged += (_, _) => UpdateAnimationState();
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    private double BaseRotation => (double)GetValue(BaseRotationProperty);
    private double StartAngle => (double)GetValue(StartAngleProperty);
    private double SweepAngle => (double)GetValue(SweepAngleProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 24.0 : Math.Min(availableSize.Width, 24.0);
        var h = double.IsInfinity(availableSize.Height) ? 24.0 : Math.Min(availableSize.Height, 24.0);
        var side = Math.Min(w, h);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2.0) return;

        var thickness = Math.Clamp(RingThickness, 1.0, size / 2.5);
        var radius = (size - thickness) / 2.0;
        if (radius <= 0.5) return;

        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);

        if (TrackBrush is { } track)
        {
            if (_trackPen is null || !ReferenceEquals(_trackPenBrush, track) || _trackPenThickness != thickness)
            {
                var pen = new Pen(track, thickness);
                if (pen.CanFreeze) pen.Freeze();
                _trackPen = pen;
                _trackPenBrush = track;
                _trackPenThickness = thickness;
            }
            drawingContext.DrawEllipse(null, _trackPen, center, radius, radius);
        }

        if (Foreground is not { } fg) return;

        var startDeg = BaseRotation + StartAngle - 90.0;
        var sweepDeg = Math.Clamp(SweepAngle, 5.0, 355.0);
        var endDeg = startDeg + sweepDeg;

        var startRad = startDeg * (Math.PI / 180.0);
        var endRad = endDeg * (Math.PI / 180.0);

        var startPt = new Point(
            center.X + radius * Math.Cos(startRad),
            center.Y + radius * Math.Sin(startRad));
        var endPt = new Point(
            center.X + radius * Math.Cos(endRad),
            center.Y + radius * Math.Sin(endRad));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(startPt, isFilled: false, isClosed: false);
            ctx.ArcTo(
                endPt,
                new Size(radius, radius),
                rotationAngle: 0.0,
                isLargeArc: sweepDeg > 180.0,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }
        geometry.Freeze();

        if (_arcPen is null || !ReferenceEquals(_arcPenBrush, fg) || _arcPenThickness != thickness)
        {
            var pen = new Pen(fg, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            if (pen.CanFreeze) pen.Freeze();
            _arcPen = pen;
            _arcPenBrush = fg;
            _arcPenThickness = thickness;
        }
        drawingContext.DrawGeometry(null, _arcPen, geometry);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProgressRing ring) ring.UpdateAnimationState();
    }

    private void UpdateAnimationState()
    {
        if (IsActive && IsLoaded && IsVisible) StartAnimations();
        else StopAnimations();
    }

    private void StartAnimations()
    {
        if (_animating) return;
        _animating = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var baseSpin = new DoubleAnimation(0.0, 360.0, TimeSpan.FromSeconds(2.1))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        var startAnim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        startAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        startAnim.KeyFrames.Add(new EasingDoubleKeyFrame(60.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7)), ease));
        startAnim.KeyFrames.Add(new EasingDoubleKeyFrame(360.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4)), ease));

        var sweepAnim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        sweepAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(20.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweepAnim.KeyFrames.Add(new EasingDoubleKeyFrame(250.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7)), ease));
        sweepAnim.KeyFrames.Add(new EasingDoubleKeyFrame(20.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4)), ease));

        BeginAnimation(BaseRotationProperty, baseSpin);
        BeginAnimation(StartAngleProperty, startAnim);
        BeginAnimation(SweepAngleProperty, sweepAnim);
    }

    private void StopAnimations()
    {
        if (!_animating) return;
        _animating = false;

        BeginAnimation(BaseRotationProperty, null);
        BeginAnimation(StartAngleProperty, null);
        BeginAnimation(SweepAngleProperty, null);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
