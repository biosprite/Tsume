using System.Windows;
using System.Windows.Controls;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// Wraps content that should grow into place and shrink away again.
/// Everything below it in the layout moves smoothly as it opens.
/// </summary>
public sealed class Reveal : Decorator
{
    public Reveal()
    {
        ClipToBounds = true;
        IsEnabled = false;   // closed until told otherwise
    }

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(Reveal),
        new PropertyMetadata(false, (d, e) => ((Reveal)d).OnIsOpenChanged((bool)e.NewValue)));

    /// <summary>How far open, 0–1. Drives both height and opacity.</summary>
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(Reveal),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => ((Reveal)d).Opacity = (double)e.NewValue));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }

    private double Progress => (double)GetValue(ProgressProperty);

    private void OnIsOpenChanged(bool open)
    {
        var target = open ? 1.0 : 0.0;

        // Hidden content must not take focus, or the focus ring vanishes into it.
        IsEnabled = open;

        // Before the panel is first shown there is nothing to animate from.
        if (IsLoaded) this.Animate(ProgressProperty, target, Motion.Smooth, Motion.Settle);
        else this.Snap(ProgressProperty, target);
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Child is null) return default;

        Child.Measure(new Size(available.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Progress);
    }

    protected override Size ArrangeOverride(Size final)
    {
        // The child keeps its full height; the Reveal simply shows more or less of it.
        Child?.Arrange(new Rect(0, 0, final.Width, Child.DesiredSize.Height));
        return final;
    }
}
