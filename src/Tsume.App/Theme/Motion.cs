using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tsume.App.Theme;

/// <summary>The shared animation durations and easing curves.</summary>
public static class Motion
{
    /// <summary>Direct feedback: presses, slider fills.</summary>
    public static readonly Duration Quick = TimeSpan.FromMilliseconds(120);

    /// <summary>State changes: colour fades, the segment thumb, panel exit.</summary>
    public static readonly Duration Smooth = TimeSpan.FromMilliseconds(240);

    /// <summary>Arrivals: the panel sliding in.</summary>
    public static readonly Duration Arrive = TimeSpan.FromMilliseconds(420);

    /// <summary>Gap between one section starting to appear and the next.</summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(38);

    /// <summary>Fast start, long soft landing. For things arriving.</summary>
    public static readonly IEasingFunction Glide = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Gentle ease-out. For things settling into a new state.</summary>
    public static readonly IEasingFunction Settle = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Slow start, fast finish. For things leaving.</summary>
    public static readonly IEasingFunction Depart = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });

    /// <summary>A small overshoot. For the segment thumb snapping into place.</summary>
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    /// <summary>Animates a numeric property from wherever it is now to <paramref name="to"/>.</summary>
    public static void Animate(
        this IAnimatable target,
        DependencyProperty property,
        double to,
        Duration duration,
        IEasingFunction easing,
        TimeSpan delay = default,
        Action? then = null)
    {
        var animation = new DoubleAnimation(to, duration) { EasingFunction = easing, BeginTime = delay };
        if (then is not null) animation.Completed += (_, _) => then();

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Stops any animation on a property and pins it at <paramref name="value"/>.</summary>
    public static void Snap(this IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    /// <summary>Runs <paramref name="then"/> once WPF has drawn <paramref name="count"/> more frames.</summary>
    public static void AfterFrames(int count, Action then)
    {
        EventHandler? onFrame = null;
        onFrame = (_, _) =>
        {
            if (--count > 0) return;

            CompositionTarget.Rendering -= onFrame;
            then();
        };

        CompositionTarget.Rendering += onFrame;
    }

    private static T Frozen<T>(T easing) where T : Freezable
    {
        easing.Freeze();
        return easing;
    }
}
