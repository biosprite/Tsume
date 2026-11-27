using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Theme;

namespace Tsume.App.Controls;

/// <summary>
/// Touch- and gamepad-friendly dropdown: Up/Down step smoothly by pixel, Left/Right jump a page,
/// Space/Enter confirm, and Escape/B close.
/// </summary>
public sealed class KeyDropdown : ComboBox
{
    private const double RowHeight = 40;
    private const int PageStep = 6;
    private static readonly Duration GlideTime = TimeSpan.FromMilliseconds(95);

    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.RegisterAttached(
        "ScrollOffset", typeof(double), typeof(KeyDropdown),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    private static KeyDropdown? _openDropdown;
    private static int _openCount;

    static KeyDropdown()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KeyDropdown), new FrameworkPropertyMetadata(typeof(KeyDropdown)));
    }

    public KeyDropdown()
    {
        Unloaded += OnUnloaded;
    }

    public static bool IsAnyOpen => Volatile.Read(ref _openCount) > 0;

    public static bool CloseIfOpen()
    {
        if (_openDropdown is not { IsDropDownOpen: true } open) return false;
        open.IsDropDownOpen = false;
        open.Focus();
        return true;
    }

    private ScrollViewer? _dropDownScroll;
    private double _scrollGoal = double.NaN;
    private int _focusedIndex = -1;
    private bool _countedOpen;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _dropDownScroll = GetTemplateChild("PART_DropDownScroll") as ScrollViewer;
        if (_dropDownScroll is null) return;

        _dropDownScroll.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
        _dropDownScroll.PreviewTouchDown += (_, _) => StopGlide();
        _dropDownScroll.PreviewMouseWheel += (_, _) => StopGlide();

        // Intercept default RequestBringIntoView jumps so focus moves glide smoothly instead of snapping.
        if (_dropDownScroll.Content is FrameworkElement content)
            content.RequestBringIntoView += (_, e) => e.Handled = true;
    }

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        _openDropdown = this;
        MarkOpen();
        StopGlide();

        var index = SelectedIndex >= 0 ? SelectedIndex : 0;
        _focusedIndex = index;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsDropDownOpen || Items.Count == 0) return;

            var target = Math.Clamp(index, 0, Items.Count - 1);
            if (_dropDownScroll is not null)
            {
                var centered = Math.Max(0, (target - 2) * RowHeight);
                _dropDownScroll.ScrollToVerticalOffset(Math.Min(centered, _dropDownScroll.ScrollableHeight));
                _dropDownScroll.UpdateLayout();
            }

            if (ItemContainerGenerator.ContainerFromIndex(target) is ComboBoxItem item)
                item.Focus();
        });
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        StopGlide();
        base.OnDropDownClosed(e);
        MarkClosed();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopGlide();
        MarkClosed();
    }

    private void MarkOpen()
    {
        if (_countedOpen) return;
        _countedOpen = true;
        Interlocked.Increment(ref _openCount);
    }

    private void MarkClosed()
    {
        if (_openDropdown == this) _openDropdown = null;
        if (!_countedOpen) return;
        _countedOpen = false;
        Interlocked.Decrement(ref _openCount);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsDropDownOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    IsDropDownOpen = false;
                    Focus();
                    e.Handled = true;
                    return;

                case Key.Space:
                case Key.Enter:
                    CommitFocusedAndClose();
                    e.Handled = true;
                    return;

                case Key.Up:
                    MoveFocusBy(-1);
                    e.Handled = true;
                    return;

                case Key.Down:
                    MoveFocusBy(1);
                    e.Handled = true;
                    return;

                case Key.Left:
                case Key.PageUp:
                    MoveFocusBy(-PageStep);
                    e.Handled = true;
                    return;

                case Key.Right:
                case Key.PageDown:
                    MoveFocusBy(PageStep);
                    e.Handled = true;
                    return;

                case Key.Home:
                    MoveFocusTo(0);
                    e.Handled = true;
                    return;

                case Key.End:
                    MoveFocusTo(Items.Count - 1);
                    e.Handled = true;
                    return;
            }
        }
        else if (e.Key is Key.Space or Key.Enter)
        {
            IsDropDownOpen = true;
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsDropDownOpen && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            return;

        base.OnKeyDown(e);
    }

    private void CommitFocusedAndClose()
    {
        if (Keyboard.FocusedElement is ComboBoxItem item)
        {
            var value = ItemContainerGenerator.ItemFromContainer(item);
            if (value != DependencyProperty.UnsetValue) SelectedItem = value;
        }
        else if (_focusedIndex >= 0 && _focusedIndex < Items.Count)
        {
            SelectedIndex = _focusedIndex;
        }

        IsDropDownOpen = false;
        Focus();
    }

    private void MoveFocusBy(int delta)
    {
        if (Items.Count == 0) return;

        var current = Keyboard.FocusedElement is ComboBoxItem focused
            ? ItemContainerGenerator.IndexFromContainer(focused)
            : _focusedIndex;
        if (current < 0) current = SelectedIndex >= 0 ? SelectedIndex : 0;

        var last = Items.Count - 1;
        var next = delta < 0 && current == 0 ? last
            : delta > 0 && current == last ? 0
            : Math.Clamp(current + delta, 0, last);

        MoveFocusTo(next);
    }

    private void MoveFocusTo(int target)
    {
        if (Items.Count == 0) return;

        target = Math.Clamp(target, 0, Items.Count - 1);
        _focusedIndex = target;

        if (ItemContainerGenerator.ContainerFromIndex(target) is ComboBoxItem item)
        {
            item.Focus();
            GlideToRow(target);
            return;
        }

        // Far jump outside the virtualization cache: scroll into range so the container realizes, then focus.
        if (_dropDownScroll is not null)
        {
            StopGlide();
            var goal = ComputeTargetOffset(target);
            _dropDownScroll.ScrollToVerticalOffset(goal);
            _dropDownScroll.UpdateLayout();
        }

        if (ItemContainerGenerator.ContainerFromIndex(target) is ComboBoxItem realized)
            realized.Focus();
    }

    private void GlideToRow(int index)
    {
        if (_dropDownScroll is null || _dropDownScroll.ScrollableHeight <= 0) return;

        var to = ComputeTargetOffset(index);
        var from = double.IsNaN(_scrollGoal) ? _dropDownScroll.VerticalOffset : _scrollGoal;
        if (Math.Abs(to - from) < 0.5) return;

        _dropDownScroll.Snap(ScrollOffsetProperty, _dropDownScroll.VerticalOffset);
        _scrollGoal = to;

        _dropDownScroll.Animate(ScrollOffsetProperty, to, GlideTime, Motion.Settle, then: () =>
        {
            if (_scrollGoal == to) _scrollGoal = double.NaN;
        });
    }

    private double ComputeTargetOffset(int index)
    {
        if (_dropDownScroll is null) return 0;

        var viewport = _dropDownScroll.ViewportHeight > 0 ? _dropDownScroll.ViewportHeight : 290;
        var from = double.IsNaN(_scrollGoal) ? _dropDownScroll.VerticalOffset : _scrollGoal;
        var rowTop = index * RowHeight;
        var rowBottom = rowTop + RowHeight;
        var room = RowHeight;

        var to = from;
        if (rowTop - room < from) to = rowTop - room;
        else if (rowBottom + room > from + viewport) to = rowBottom + room - viewport;

        return Math.Clamp(to, 0, _dropDownScroll.ScrollableHeight);
    }

    private void StopGlide()
    {
        if (_dropDownScroll is null || double.IsNaN(_scrollGoal)) return;

        _dropDownScroll.Snap(ScrollOffsetProperty, _dropDownScroll.VerticalOffset);
        _scrollGoal = double.NaN;
    }
}
