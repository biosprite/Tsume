using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// Makes a scrolling list glide to the newly focused control instead of jumping,
/// stopping with some room to spare so the next control is already showing.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Room left between the focused control and the edge of the list.</summary>
    private const double Room = 28;

    /// <summary>Closer to either end than this, and the list goes all the way.</summary>
    private const double NearEnd = 44;

    /// <summary>How far down the list is. Animated; the list follows it.</summary>
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    /// <summary>Where the list is heading while it is on its way; otherwise not a number.</summary>
    private static readonly DependencyProperty GoalProperty = DependencyProperty.RegisterAttached(
        "Goal", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    public static void Follow(ScrollViewer list)
    {
        // Hooked on the content, not the ScrollViewer, which would otherwise handle the request first by jumping.
        ((FrameworkElement)list.Content).RequestBringIntoView += (_, e) =>
        {
            e.Handled = true;
            if (e.TargetObject is Visual wanted && list.IsAncestorOf(wanted)) Show(list, wanted, e.TargetRect);
        };

        // Touch or the wheel cancels the glide where it stands.
        list.PreviewTouchDown  += (_, _) => Stop(list);
        list.PreviewMouseWheel += (_, _) => Stop(list);
    }

    private static void Show(ScrollViewer list, Visual wanted, Rect part)
    {
        if (part.IsEmpty) part = new Rect(((UIElement)wanted).RenderSize);

        // Where the control sits in the whole list, not just in the part on show.
        var seen   = wanted.TransformToAncestor(list).TransformBounds(part);
        var top    = seen.Top + list.VerticalOffset - Room;
        var bottom = seen.Bottom + list.VerticalOffset + Room;

        // Measured from the glide's goal, not the current offset, so a held d-pad keeps it moving ahead of focus.
        var heading = (double)list.GetValue(GoalProperty);
        var from    = double.IsNaN(heading) ? list.VerticalOffset : heading;
        var to      = from;

        if (top < from || bottom - top > list.ViewportHeight) to = top;
        else if (bottom > from + list.ViewportHeight) to = bottom - list.ViewportHeight;

        // Only the end it is heading for: in a list that barely scrolls, both ends are near.
        if (to < from && to < NearEnd) to = 0;
        if (to > from && to > list.ScrollableHeight - NearEnd) to = list.ScrollableHeight;

        // The first control shows the very top, and the last the very bottom: what lies beyond them (a note,
        // a caption) cannot take focus, so nothing else would ever bring it back into view.
        if (wanted is FrameworkElement control)
        {
            if (bottom <= list.ViewportHeight && !HasNeighbour(list, control, FocusNavigationDirection.Up)) to = 0;
            else if (top >= list.ScrollableHeight && !HasNeighbour(list, control, FocusNavigationDirection.Down)) to = list.ScrollableHeight;
        }

        to = Math.Clamp(to, 0, list.ScrollableHeight);

        if (Math.Abs(to - from) < 0.5) return;

        list.Snap(OffsetProperty, list.VerticalOffset);
        list.SetValue(GoalProperty, to);

        list.Animate(OffsetProperty, to, Motion.Smooth, Motion.Settle, then: () =>
        {
            if ((double)list.GetValue(GoalProperty) == to) list.ClearValue(GoalProperty);
        });
    }

    /// <summary>Whether focus could move from <paramref name="control"/> to another control in the list, that way.</summary>
    private static bool HasNeighbour(ScrollViewer list, FrameworkElement control, FocusNavigationDirection way) =>
        control.PredictFocus(way) is Visual next && next != control && list.IsAncestorOf(next);

    private static void Stop(ScrollViewer list)
    {
        if (double.IsNaN((double)list.GetValue(GoalProperty))) return;

        list.Snap(OffsetProperty, list.VerticalOffset);
        list.ClearValue(GoalProperty);
    }
}
