using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// A card with a name and a row of colour dots; a ring marks the chosen one.
/// The template adds two <see cref="SpectrumSlider"/> strips for any other colour; then no dot is ringed.
/// </summary>
public sealed class SwatchPicker : Control
{
    private const double DotSize = 24;
    private const double RingSize = 34;

    /// <summary>The ring is drawn in the text colour, so it shows on a light panel too.</summary>
    private static Brush RingBrush => Theme.Colours.TextBrush;

    private UniformGrid? _row;
    private readonly List<(Ellipse Ring, ScaleTransform Grow)> _dots = [];

    public SwatchPicker() => Focusable = true;

    public static readonly DependencyProperty ColoursProperty = DependencyProperty.Register(
        nameof(Colours), typeof(IReadOnlyList<Color>), typeof(SwatchPicker),
        new PropertyMetadata(Array.Empty<Color>(), (d, _) => ((SwatchPicker)d).BuildDots()));

    public static readonly DependencyProperty SelectedColourProperty = DependencyProperty.Register(
        nameof(SelectedColour), typeof(Color), typeof(SwatchPicker),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((SwatchPicker)d).MarkChoice(animate: true)));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SwatchPicker), new PropertyMetadata(""));

    public IReadOnlyList<Color> Colours
    {
        get => (IReadOnlyList<Color>)GetValue(ColoursProperty);
        set => SetValue(ColoursProperty, value);
    }

    /// <summary>The colour chosen: one of <see cref="Colours"/>, or any other, mixed on the strips.</summary>
    public Color SelectedColour { get => (Color)GetValue(SelectedColourProperty); set => SetValue(SelectedColourProperty, value); }

    /// <summary>Which dot the chosen colour is, or -1 for a colour of the person's own.</summary>
    private int SelectedIndex
    {
        get
        {
            for (var i = 0; i < Colours.Count; i++)
                if (Colours[i] == SelectedColour) return i;
            return -1;
        }
    }

    /// <summary>What these colours light, e.g. "Left stick".</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _row = GetTemplateChild("PART_Swatches") as UniformGrid;
        BuildDots();
    }

    private void BuildDots()
    {
        if (_row is null) return;

        _row.Children.Clear();
        _dots.Clear();

        foreach (var colour in Colours)
        {
            var grow = new ScaleTransform(1, 1);
            var ring = new Ellipse
            {
                Width = RingSize, Height = RingSize,
                Stroke = RingBrush, StrokeThickness = 2,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = grow,
            };

            _row.Children.Add(new Grid
            {
                Background = Brushes.Transparent,   // the whole cell takes the click, not just the dot
                Height = RingSize,
                Children =
                {
                    ring,
                    new Ellipse { Width = DotSize, Height = DotSize, Fill = Frozen(colour) },
                },
            });

            _dots.Add((ring, grow));
        }

        MarkChoice(animate: false);
    }

    private void MarkChoice(bool animate)
    {
        var selected = SelectedIndex;

        for (var i = 0; i < _dots.Count; i++)
        {
            var (ring, grow) = _dots[i];
            var chosen = i == selected;
            var scale = chosen ? 1.0 : 0.7;

            if (animate)
            {
                ring.Animate(OpacityProperty, chosen ? 1 : 0, Motion.Quick, Motion.Settle);
                grow.Animate(ScaleTransform.ScaleXProperty, scale, Motion.Smooth, Motion.Spring);
                grow.Animate(ScaleTransform.ScaleYProperty, scale, Motion.Smooth, Motion.Spring);
            }
            else
            {
                ring.Snap(OpacityProperty, chosen ? 1 : 0);
                grow.Snap(ScaleTransform.ScaleXProperty, scale);
                grow.Snap(ScaleTransform.ScaleYProperty, scale);
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch { Key.Left => -1, Key.Right => +1, _ => 0 };
        if (step == 0 || Colours.Count == 0) return;

        // From a custom colour, the first step lands on the nearest dot.
        var index = SelectedIndex;
        Select(index < 0 ? Nearest() : index + step);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        e.Handled = true;

        if (_row is null || Colours.Count == 0 || _row.ActualWidth <= 0) return;

        var x = e.GetPosition(_row).X;
        if (x < 0 || x > _row.ActualWidth) return;

        Select((int)(x / (_row.ActualWidth / Colours.Count)));
    }

    private void Select(int index) =>
        SetCurrentValue(SelectedColourProperty, Colours[Math.Clamp(index, 0, Colours.Count - 1)]);

    private int Nearest()
    {
        static int Apart(Color a, Color b) =>
            (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);

        var best = 0;
        for (var i = 1; i < Colours.Count; i++)
            if (Apart(Colours[i], SelectedColour) < Apart(Colours[best], SelectedColour)) best = i;
        return best;
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
