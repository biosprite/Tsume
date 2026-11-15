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

        _thumb.Width = width;

        if (animate) _slide.Animate(TranslateTransform.XProperty, x, Motion.Smooth, Motion.Spring);
        else _slide.Snap(TranslateTransform.XProperty, x);

        for (var i = 0; i < _labels.Children.Count; i++)
        {
            var brush = (SolidColorBrush)((TextBlock)_labels.Children[i]).Foreground;
            var colour = i == SelectedIndex ? ChosenText : RestingText;

            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(colour, animate ? Motion.Smooth : TimeSpan.Zero));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch { Key.Left => -1, Key.Right => +1, _ => 0 };
        if (step == 0) return;

        SelectedIndex = Math.Clamp(SelectedIndex + step, 0, Segments.Count - 1);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_labels is null || Segments.Count == 0) return;

        Focus();

        var x = e.GetPosition(_labels).X;
        SelectedIndex = Math.Clamp((int)(x / (_labels.ActualWidth / Segments.Count)), 0, Segments.Count - 1);
        e.Handled = true;
    }
}
