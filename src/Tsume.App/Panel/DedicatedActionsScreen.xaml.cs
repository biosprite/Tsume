using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Controls;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class DedicatedActionsScreen
{
    private DedicatedActionsViewModel? _vm;

    public DedicatedActionsScreen()
    {
        InitializeComponent();

        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
        StepOut.LetFocusLeave((UIElement)List.Content);
        SmoothScroll.Follow(List);

        Loaded += (_, _) => HookViewModel();
        Unloaded += (_, _) => UnhookViewModel();
    }

    private void HookViewModel()
    {
        if (DataContext is not PanelViewModel panel) return;
        var vm = panel.Settings.Controls.DedicatedActionsPage;
        if (ReferenceEquals(_vm, vm)) return;

        UnhookViewModel();
        _vm = vm;
        _vm.CollapsedByBack += OnCollapsedByBack;
    }

    private void UnhookViewModel()
    {
        if (_vm is null) return;
        _vm.CollapsedByBack -= OnCollapsedByBack;
        _vm = null;
    }

    private void OnCollapsedByBack(DedicatedActionEditor editor)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var container = ActionsList.ItemContainerGenerator.ContainerFromItem(editor);
            var header = container is null ? null : FindDescendant<Tile>(container);
            if (header is not null && header.Focus()) return;
            CategoryTabs.Focus();
        });
    }

    private void OnRemoveKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject button) return;
        var slotsList = FindAncestor<ItemsControl>(button);
        if (slotsList is null) return;

        var removedIndex = button is FrameworkElement fe
            ? slotsList.Items.IndexOf(fe.DataContext)
            : 0;

        ScheduleFocusSlot(slotsList, Math.Max(0, removedIndex));
    }

    private void OnAddKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject button) return;
        var card = FindAncestor<StackPanel>(button);
        var slotsList = card is null ? null : FindDescendant<ItemsControl>(card);
        if (slotsList is null) return;

        ScheduleFocusSlot(slotsList, int.MaxValue);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject button) return;
        var reveal = FindAncestor<Reveal>(button);
        var card = reveal is null ? null : FindAncestor<StackPanel>(reveal);
        var header = card is null ? null : FindDescendant<Tile>(card);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (header is not null && header.Focus()) return;
            Screen.KeepFocus(this);
        });
    }

    private void ScheduleFocusSlot(ItemsControl slotsList, int preferredIndex)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (slotsList.Items.Count == 0)
            {
                Screen.KeepFocus(this);
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
            Screen.KeepFocus(this);
        });
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var at = VisualTreeHelper.GetParent(start); at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is T match) return match;

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
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
