using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Controls;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class MainTilesScreen
{
    private const double DragThreshold = 8;

    private MainTilesViewModel? _vm;
    private MainTileSlotViewModel? _dragCandidate;
    private Point _dragStart;
    private Point _dragTileOffset;
    private Size _dragTileSize;
    private bool _isDragging;

    public MainTilesScreen()
    {
        InitializeComponent();
        ScreenList.Configure(List);

        Loaded += (_, _) => HookViewModel();
        Unloaded += (_, _) =>
        {
            CancelDrag();
            UnhookViewModel();
        };
    }

    private void HookViewModel()
    {
        if (DataContext is not PanelViewModel panel) return;
        var vm = panel.Settings.Controls.MainTilesPage;
        if (ReferenceEquals(_vm, vm)) return;

        UnhookViewModel();
        _vm = vm;
        _vm.SelectionClearedByBack += OnSelectionClearedByBack;
    }

    private void UnhookViewModel()
    {
        if (_vm is null) return;
        _vm.SelectionClearedByBack -= OnSelectionClearedByBack;
        _vm = null;
    }

    private void OnSelectionClearedByBack(MainTileSlotViewModel slot)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusSlot(ActiveGrid, slot));
    }

    private void OnActiveGridMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        // Let the tiny corner '-' remove button act as a 1-click hide, never a drag start.
        if (IsInsideRemoveBadge(source)) return;

        var tile = FindAncestor<Tile>(source);
        if (tile?.DataContext is not MainTileSlotViewModel slot) return;

        _dragCandidate = slot;
        _dragStart = e.GetPosition(PreviewStage);
        _dragTileOffset = e.GetPosition(tile);
        _dragTileSize = new Size(tile.ActualWidth, tile.ActualHeight);
        _isDragging = false;
    }

    private void OnActiveGridMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag();
            return;
        }

        var current = e.GetPosition(PreviewStage);

        if (!_isDragging)
        {
            var delta = current - _dragStart;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

            _isDragging = true;
            _dragCandidate.IsDragging = true;

            DragGhostGlyph.Text = _dragCandidate.Glyph;
            DragGhostLabel.Text = _dragCandidate.Label;
            DragGhost.Width = Math.Max(48, _dragTileSize.Width);
            DragGhost.Height = Math.Max(48, _dragTileSize.Height);
            DragGhost.Visibility = Visibility.Visible;

            ActiveGrid.CaptureMouse();
        }

        PositionGhost(current);
        TryReorderAt(current);
    }

    private void OnActiveGridMouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _isDragging;
        CancelDrag();

        // Prevent a completed drag-drop from also firing the tile's selection click.
        if (wasDragging) e.Handled = true;
    }

    private void OnActiveGridLostMouseCapture(object sender, MouseEventArgs e) => CancelDrag();

    private void PositionGhost(Point stagePoint)
    {
        var x = stagePoint.X - _dragTileOffset.X;
        var y = stagePoint.Y - _dragTileOffset.Y;
        Canvas.SetLeft(DragGhost, x);
        Canvas.SetTop(DragGhost, y);
    }

    private void TryReorderAt(Point stagePoint)
    {
        if (_vm is null || _dragCandidate is null) return;

        for (var i = 0; i < ActiveGrid.Items.Count; i++)
        {
            if (ActiveGrid.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var bounds = new Rect(
                container.TranslatePoint(new Point(0, 0), PreviewStage),
                new Size(container.ActualWidth, container.ActualHeight));

            if (!bounds.Contains(stagePoint)) continue;

            _vm.MoveTile(_dragCandidate, i);
            return;
        }
    }

    private void CancelDrag()
    {
        if (_dragCandidate is not null)
        {
            _dragCandidate.IsDragging = false;
            _dragCandidate = null;
        }

        _isDragging = false;
        DragGhost.Visibility = Visibility.Collapsed;

        if (ActiveGrid.IsMouseCaptured) ActiveGrid.ReleaseMouseCapture();
    }

    private void OnMoveSelectedClick(object sender, RoutedEventArgs e)
    {
        // If moving hit the first or last slot and disabled the focused button, hand focus to its sibling.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(sender, MoveLeftButton) && !MoveLeftButton.IsEnabled)
            {
                if (MoveRightButton.IsEnabled) MoveRightButton.Focus();
                else HideSelectedButton.Focus();
            }
            else if (ReferenceEquals(sender, MoveRightButton) && !MoveRightButton.IsEnabled)
            {
                if (MoveLeftButton.IsEnabled) MoveLeftButton.Focus();
                else HideSelectedButton.Focus();
            }
        });
    }

    private void OnHideSelectedClick(object sender, RoutedEventArgs e)
    {
        var previousIndex = _vm is { SelectedTile: { } sel }
            ? _vm.ActiveTiles.IndexOf(sel)
            : 0;

        ScheduleFocusAfterActiveRemoval(Math.Max(0, previousIndex));
    }

    private void OnQuickRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: MainTileSlotViewModel slot }) return;
        var removedIndex = _vm.ActiveTiles.IndexOf(slot);
        var hadKeyboardFocus = Keyboard.FocusedElement is DependencyObject focused
            && FindAncestor<FrameworkElement>(focused)?.DataContext == slot;

        if (hadKeyboardFocus) ScheduleFocusAfterActiveRemoval(Math.Max(0, removedIndex));
    }

    private void OnAvailableTileClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { DataContext: MainTileSlotViewModel slot }) return;
        var removedIndex = _vm.AvailableTiles.IndexOf(slot);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (AvailableGrid.Items.Count > 0)
            {
                FocusIndex(AvailableGrid, Math.Clamp(removedIndex, 0, AvailableGrid.Items.Count - 1));
                return;
            }

            if (ActiveGrid.Items.Count > 0)
            {
                FocusIndex(ActiveGrid, ActiveGrid.Items.Count - 1);
                return;
            }

            ColumnsPicker.Focus();
        });
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => Screen.KeepFocus(this);

    private void ScheduleFocusAfterActiveRemoval(int preferredIndex)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ActiveGrid.Items.Count > 0)
            {
                FocusIndex(ActiveGrid, Math.Clamp(preferredIndex, 0, ActiveGrid.Items.Count - 1));
                return;
            }

            if (AvailableGrid.Items.Count > 0)
            {
                FocusIndex(AvailableGrid, 0);
                return;
            }

            ColumnsPicker.Focus();
        });
    }

    private static void FocusSlot(ItemsControl grid, MainTileSlotViewModel slot)
    {
        var index = grid.Items.IndexOf(slot);
        if (index >= 0) FocusIndex(grid, index);
    }

    private static void FocusIndex(ItemsControl grid, int index)
    {
        if (index < 0 || index >= grid.Items.Count) return;

        var container = grid.ItemContainerGenerator.ContainerFromIndex(index);
        if (container is null)
        {
            grid.UpdateLayout();
            container = grid.ItemContainerGenerator.ContainerFromIndex(index);
        }

        if (container is not null && FindDescendant<Tile>(container) is { } tile)
            tile.Focus();
    }

    private static bool IsInsideRemoveBadge(DependencyObject start)
    {
        for (var at = start; at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is Button { Name: "RemoveBadge" }) return true;

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var at = start; at is not null; at = VisualTreeHelper.GetParent(at))
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
