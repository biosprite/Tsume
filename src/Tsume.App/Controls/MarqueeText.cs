using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Tsume.App.Controls;

/// <summary>
/// Single-line text that shows an ellipsis when idle and, while <see cref="IsActive"/> is true
/// (e.g. hovered by mouse or focused by controller), gently pans to the end of the text and back
/// whenever it is wider than the available space. Each pass eases in and out at both ends,
/// cross-fades with the ellipsis text while resting at the start, and fades the clipped edges.
/// Panning stops after <see cref="MaxCycles"/> passes, and only the most recently activated
/// instance pans at a time, so the render loop doesn't keep ticking over a game.
/// </summary>
public sealed class MarqueeText : System.Windows.Controls.Panel
{
    private const double ScrollPixelsPerSecond = 40;
    private const double MinLegSeconds = 0.8;
    private const double TailPad = 4;
    private const double EdgeFadeWidth = 14;
    private const int MaxCycles = 5;

    private static readonly TimeSpan StartPause = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan EndPause = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(180);

    private static readonly Color Opaque = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

    // UI-thread only: the single instance currently panning.
    private static MarqueeText? s_scrolling;

    private readonly TextBlock _staticText = new() { TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _scrollText = new()
    {
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.None,
        Visibility = Visibility.Hidden,
    };
    private readonly TranslateTransform _offset = new();

    private double _runningOverflow;
    private string _runningText = "";
    private int _generation;

    // Set once the passes have played out (or another instance took over);
    // cleared when the control is re-activated or its text changes.
    private bool _spent;

    public MarqueeText()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;

        _scrollText.RenderTransform = _offset;

        Children.Add(_staticText);
        Children.Add(_scrollText);

        Loaded += (_, _) => UpdateScroll();
        Unloaded += (_, _) => StopScroll();
        SizeChanged += (_, _) => UpdateScroll();
        // The scrolling text is arranged at its full width, so this catches the text
        // getting wider or narrower (font fallback, DPI) even when this panel's size doesn't change.
        _scrollText.SizeChanged += (_, _) => UpdateScroll();
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateScroll(); else StopScroll(); };
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChanged));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(MarqueeText),
        new PropertyMetadata(null, OnTextStyleChanged));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(MarqueeText),
        new PropertyMetadata(false, OnIsActiveChanged));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Style? TextStyle { get => (Style?)GetValue(TextStyleProperty); set => SetValue(TextStyleProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MarqueeText)d;
        var text = (string?)e.NewValue ?? "";
        control._staticText.Text = text;
        control._scrollText.Text = text;
        control.StopScroll();
        control._spent = false;
        if (control.IsActive) control.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, control.UpdateScroll);
    }

    private static void OnTextStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MarqueeText)d;
        var style = (Style?)e.NewValue;
        control._staticText.Style = style;
        control._scrollText.Style = style;
        control.InvalidateMeasure();
        if (control.IsActive) control.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, control.UpdateScroll);
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MarqueeText)d;
        control._spent = false;
        if ((bool)e.NewValue) control.UpdateScroll();
        else control.StopScroll();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _staticText.Measure(availableSize);
        _scrollText.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        return _staticText.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _staticText.Arrange(new Rect(finalSize));
        _scrollText.Arrange(new Rect(0, 0, Math.Max(finalSize.Width, _scrollText.DesiredSize.Width), finalSize.Height));
        return finalSize;
    }

    private void UpdateScroll()
    {
        if (!IsActive || _spent || !IsLoaded || !IsVisible || ActualWidth <= 0)
        {
            StopScroll();
            return;
        }

        var overflow = _scrollText.DesiredSize.Width - ActualWidth;
        if (overflow <= 1.0)
        {
            StopScroll();
            return;
        }

        var text = Text;
        if (_scrollText.Visibility == Visibility.Visible
            && _runningText == text
            && Math.Abs(_runningOverflow - overflow) < 0.5)
        {
            return;
        }

        // Only one marquee runs at a time: the newest activation (hover or focus) wins.
        if (s_scrolling is { } other && other != this)
        {
            other._spent = true;
            other.StopScroll();
        }

        _runningText = text;
        _runningOverflow = overflow;

        // One pass: rest on the ellipsis text, cross-fade to the full text while easing out
        // to its end, hold, ease back at the same pace, then cross-fade back to the ellipsis.
        var distance = overflow + TailPad;
        var leg = TimeSpan.FromSeconds(Math.Max(MinLegSeconds, distance / ScrollPixelsPerSecond));

        var t1 = StartPause;
        var t2 = t1 + leg;
        var t3 = t2 + EndPause;
        var t4 = t3 + leg;
        var t5 = t4 + Fade;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();

        var pan = new DoubleAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(MaxCycles) };
        pan.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pan.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t1)));
        pan.KeyFrames.Add(new EasingDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t2), ease));
        pan.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(t3)));
        pan.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(t4), ease));
        pan.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t5)));

        var staticOpacity = Opacities((1, TimeSpan.Zero), (1, t1), (0, t1 + Fade), (0, t4), (1, t5));
        var scrollOpacity = Opacities((0, TimeSpan.Zero), (0, t1), (1, t1 + Fade), (1, t4), (0, t5));

        // Edge fades show only while text is actually hidden past that edge.
        var leftEdge = Alphas((true, TimeSpan.Zero), (true, t1), (false, t1 + Fade), (false, t4 - Fade), (true, t4), (true, t5));
        var rightEdge = Alphas((true, TimeSpan.Zero), (true, t1), (false, t1 + Fade), (false, t2 - Fade), (true, t2),
                               (true, t3), (false, t3 + Fade), (false, t4), (true, t5));

        var leftStop = new GradientStop(Opaque, 0);
        var rightStop = new GradientStop(Opaque, 1);
        var fade = Math.Min(0.25, EdgeFadeWidth / ActualWidth);
        OpacityMask = new LinearGradientBrush(
            new GradientStopCollection { leftStop, new(Opaque, fade), new(Opaque, 1 - fade), rightStop },
            new Point(0, 0.5), new Point(1, 0.5));

        var generation = ++_generation;
        pan.Completed += (_, _) =>
        {
            if (generation != _generation) return;
            _spent = true;
            StopScroll();
        };

        _scrollText.Visibility = Visibility.Visible;
        s_scrolling = this;

        // Started in the same tick, so all clocks stay in step.
        _offset.BeginAnimation(TranslateTransform.XProperty, pan);
        _staticText.BeginAnimation(OpacityProperty, staticOpacity);
        _scrollText.BeginAnimation(OpacityProperty, scrollOpacity);
        leftStop.BeginAnimation(GradientStop.ColorProperty, leftEdge);
        rightStop.BeginAnimation(GradientStop.ColorProperty, rightEdge);
    }

    private void StopScroll()
    {
        _generation++;
        if (s_scrolling == this) s_scrolling = null;
        _runningOverflow = 0;
        _runningText = "";
        _offset.BeginAnimation(TranslateTransform.XProperty, null);
        _offset.X = 0;
        _staticText.BeginAnimation(OpacityProperty, null);
        _scrollText.BeginAnimation(OpacityProperty, null);
        OpacityMask = null;
        _scrollText.Visibility = Visibility.Hidden;
    }

    private static DoubleAnimationUsingKeyFrames Opacities(params (double Value, TimeSpan At)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(MaxCycles) };
        foreach (var (value, at) in frames)
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(at)));
        return animation;
    }

    private static ColorAnimationUsingKeyFrames Alphas(params (bool Opaque, TimeSpan At)[] frames)
    {
        var animation = new ColorAnimationUsingKeyFrames { RepeatBehavior = new RepeatBehavior(MaxCycles) };
        foreach (var (opaque, at) in frames)
            animation.KeyFrames.Add(new LinearColorKeyFrame(opaque ? Opaque : Clear, KeyTime.FromTimeSpan(at)));
        return animation;
    }
}
