using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Controls;

namespace Tsume.App.Panel;

/// <summary>Shared key-slot focus management and visual-tree helpers for key-binding screens.</summary>
internal static class KeySlotFocus
{
    public static void OnRemoveKeyClick(FrameworkElement screen, object sender)
    {
        if (sender is not DependencyObject button) return;
        var slotsList = FindAncestor<ItemsControl>(button);
        if (slotsList is null) return;

        var removedIndex = button is FrameworkElement fe
            ? slotsList.Items.IndexOf(fe.DataContext)
            : 0;

        ScheduleFocusSlot(screen, slotsList, Math.Max(0, removedIndex));
    }

    public static void OnAddKeyClick(FrameworkElement screen, object sender)
    {
        if (sender is not DependencyObject button) return;
        var card = FindAncestor<StackPanel>(button);
        var slotsList = card is null ? null : FindDescendant<ItemsControl>(card);
        if (slotsList is null) return;

        ScheduleFocusSlot(screen, slotsList, int.MaxValue);
    }

    public static void ScheduleFocusSlot(FrameworkElement screen, ItemsControl slotsList, int preferredIndex)
    {
        screen.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (slotsList.Items.Count == 0)
            {
                Screen.KeepFocus(screen);
                return;
            }

            var index = Math.Clamp(preferredIndex, 0, slotsList.Items.Count - 1);
            var container = slotsList.ItemContainerGenerator.ContainerFromIndex(index);
            if (container is null)
            {
                slotsList.UpdateLayout();
                container = slotsList.ItemContainerGenerator.ContainerFromIndex(index);
            }

            var dropdown = container is null ? null : FindDescendant<KeyDropdown>(container);

            if (dropdown is not null && dropdown.Focus()) return;
            Screen.KeepFocus(screen);
        });
    }

    public static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var at = VisualTreeHelper.GetParent(start); at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is T match) return match;

        return null;
    }

    public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }

        return null;
    }
}
