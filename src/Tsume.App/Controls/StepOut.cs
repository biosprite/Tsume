using System.Windows;
using System.Windows.Input;

namespace Tsume.App.Controls;

/// <summary>
/// Moves focus with the arrow keys before the ScrollViewer sees them. Left to itself it swallows
/// a key whose target lies outside it, and jumps a line when the target is scrolled out of view.
/// </summary>
public static class StepOut
{
    /// <param name="contents">What the list scrolls: the element directly inside it.</param>
    public static void LetFocusLeave(UIElement contents) => contents.KeyDown += (_, e) =>
    {
        FocusNavigationDirection? direction = e.Key switch
        {
            Key.Up    => FocusNavigationDirection.Up,
            Key.Down  => FocusNavigationDirection.Down,
            Key.Left  => FocusNavigationDirection.Left,
            Key.Right => FocusNavigationDirection.Right,
            _         => null,
        };

        if (direction is null || Keyboard.FocusedElement is not FrameworkElement from) return;
        if (from is System.Windows.Controls.Primitives.TextBoxBase && e.Key is Key.Left or Key.Right) return;
        if (from.PredictFocus(direction.Value) is UIElement next) e.Handled = next.Focus();
    };
}
