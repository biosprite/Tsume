using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// A row of mutually exclusive choices with a thumb that slides to the chosen one.
/// One focus stop: left and right move the selection.
/// </summary>
public sealed class SegmentedControl : Control
{
    // Both follow the colour scheme: the chosen label sits on the accent itself.
    private static Color ChosenText => Colours.OnAccent;
    private static Color RestingText => Colours.MutedBrush.Color;

    private readonly TranslateTransform _slide = new();
    private Border? _thumb;
    private UniformGrid? _labels;

    public SegmentedControl()
    {
        Focusable = true;

        // The labels own their brushes, so they are recoloured when the colour scheme changes.
        Loaded += (_, _) => { Colours.Changed += Recolour; Recolour(); };
        Unloaded += (_, _) => Colours.Changed -= Recolour;
    }

    private void Recolour() => PlaceThumb(animate: false);

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<string>), typeof(SegmentedControl),
        new PropertyMetadata(Array.Empty<string>(), (d, _) => ((SegmentedControl)d).BuildLabels()));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SegmentedControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((SegmentedControl)d).PlaceThumb(animate: true)));

    public static readonly DependencyProperty DisabledIndicesProperty = DependencyProperty.Register(
        nameof(DisabledIndices), typeof(IReadOnlySet<int>), typeof(SegmentedControl),
        new PropertyMetadata(null, (d, _) => ((SegmentedControl)d).PlaceThumb(animate: false)));

    /// <summary>The labels, left to right.</summary>
    public IReadOnlyList<string> Segments
    {
        get => (IReadOnlyList<string>)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Segment indices that are greyed out and cannot be selected.</summary>
    public IReadOnlySet<int>? DisabledIndices
    {
        get => (IReadOnlySet<int>?)GetValue(DisabledIndicesProperty);
        set => SetValue(DisabledIndicesProperty, value);
    }

    public static readonly DependencyProperty ReselectProperty = DependencyProperty.Register(
        nameof(Reselect), typeof(ICommand), typeof(SegmentedControl), new PropertyMetadata(null));

    /// <summary>
    /// Run when the chosen segment is picked again (tapped, or A), which otherwise does nothing. For a choice
    /// that may need applying again, such as a fan profile the fans have drifted off.
    /// </summary>
    public ICommand? Reselect
    {
        get => (ICommand?)GetValue(ReselectProperty);
        set => SetValue(ReselectProperty, value);
    }

    private bool IsDisabled(int index) => DisabledIndices?.Contains(index) == true;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _thumb = GetTemplateChild("PART_Thumb") as Border;
        _labels = GetTemplateChild("PART_Labels") as UniformGrid;

        if (_thumb is not null) _thumb.RenderTransform = _slide;
        if (_labels is not null) _labels.SizeChanged += (_, _) => PlaceThumb(animate: false);

        BuildLabels();
    }

    private void BuildLabels()
    {
        if (_labels is null) return;

        _labels.Children.Clear();

        foreach (var segment in Segments)
        {
            _labels.Children.Add(new TextBlock
            {
                Text = segment,
                Style = (Style)FindResource("Text.Segment"),
                Foreground = new SolidColorBrush(RestingText),
            });
        }

        PlaceThumb(animate: false);
    }

    /// <summary>Moves the thumb under the selected label and recolours the labels to match.</summary>
    private void PlaceThumb(bool animate)
    {
        if (_thumb is null || _labels is null || Segments.Count == 0) return;

        var width = _labels.ActualWidth / Segments.Count;
        var x = width * SelectedIndex;
        var shouldAnimate = animate && IsVisible && _labels.ActualWidth > 0;

        _thumb.Width = width;

        if (shouldAnimate) _slide.Animate(TranslateTransform.XProperty, x, Motion.Smooth, Motion.Spring);
        else _slide.Snap(TranslateTransform.XProperty, x);

        for (var i = 0; i < _labels.Children.Count; i++)
        {
            var label = (TextBlock)_labels.Children[i];
            var disabled = IsDisabled(i);
            var brush = (SolidColorBrush)label.Foreground;
            var colour = i == SelectedIndex && !disabled ? ChosenText : RestingText;

            label.Opacity = disabled ? 0.35 : 1.0;
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(colour, shouldAnimate ? Motion.Smooth : TimeSpan.Zero));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter && Reselect is { } again)
        {
            if (again.CanExecute(SelectedIndex)) again.Execute(SelectedIndex);
            e.Handled = true;
            return;
        }

        var step = e.Key switch { Key.Left => -1, Key.Right => +1, _ => 0 };
        if (step == 0) return;

        for (var next = SelectedIndex + step; next >= 0 && next < Segments.Count; next += step)
        {
            if (IsDisabled(next)) continue;

            SelectedIndex = next;
            break;
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_labels is null || Segments.Count == 0) return;

        Focus();

        var x = e.GetPosition(_labels).X;
        var target = Math.Clamp((int)(x / (_labels.ActualWidth / Segments.Count)), 0, Segments.Count - 1);
        if (target != SelectedIndex)
        {
            if (!IsDisabled(target)) SelectedIndex = target;
        }
        else if (Reselect is { } again && again.CanExecute(target)) again.Execute(target);
        e.Handled = true;
    }
}
