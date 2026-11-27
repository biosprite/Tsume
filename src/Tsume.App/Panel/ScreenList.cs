using System.Windows;
using System.Windows.Controls;
using Tsume.App.Controls;

namespace Tsume.App.Panel;

/// <summary>Shared scroll, touch-boundary, and focus-exit wiring for panel screen lists.</summary>
internal static class ScreenList
{
    public static void Configure(ScrollViewer list)
    {
        // Stops Windows moving the window when a touch list is dragged past its end.
        list.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach controls outside the list (such as Back).
        StepOut.LetFocusLeave((UIElement)list.Content);

        // Scrolls smoothly to the focused item.
        SmoothScroll.Follow(list);
    }
}
