using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Tsume.App.Controls;

/// <summary>
/// A dark, touch- and gamepad-friendly dropdown. While closed, arrow keys leave focus to move
/// between controls on the screen instead of silently changing the binding; Space / A opens the
/// list, arrows browse it, Space / Enter / tap picks an item, and Escape / B closes it.
/// </summary>
public sealed class KeyDropdown : ComboBox
{
    private static KeyDropdown? _openDropdown;

    static KeyDropdown()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(KeyDropdown), new FrameworkPropertyMetadata(typeof(KeyDropdown)));
    }

    public static bool CloseIfOpen()
    {
        if (_openDropdown is not { IsDropDownOpen: true } open) return false;
        open.IsDropDownOpen = false;
        open.Focus();
        return true;
    }

    private ScrollViewer? _dropDownScroll;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _dropDownScroll = GetTemplateChild("PART_DropDownScroll") as ScrollViewer;
        if (_dropDownScroll is not null)
            _dropDownScroll.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
    }

    protected override void OnDropDownOpened(EventArgs e)
    {
        base.OnDropDownOpened(e);
        _openDropdown = this;

        // Bring the current selection into view and give it keyboard focus so D-pad browsing starts there.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsDropDownOpen) return;

            var index = SelectedIndex >= 0 ? SelectedIndex : 0;
            if (_dropDownScroll is not null)
            {
                _dropDownScroll.ScrollToVerticalOffset(Math.Max(0, index - 2));
                _dropDownScroll.UpdateLayout();
            }

            if (ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem item)
            {
                item.BringIntoView();
                item.Focus();
            }
        });
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        base.OnDropDownClosed(e);
        if (_openDropdown == this) _openDropdown = null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsDropDownOpen)
        {
            if (e.Key == Key.Escape)
            {
                IsDropDownOpen = false;
                Focus();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space)
            {
                if (Keyboard.FocusedElement is ComboBoxItem item)
                {
                    var value = ItemContainerGenerator.ItemFromContainer(item);
                    if (value != DependencyProperty.UnsetValue) SelectedItem = value;
                }

                IsDropDownOpen = false;
                Focus();
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
        // While closed, let arrows bubble up to StepOut instead of ComboBox silently cycling items.
        if (!IsDropDownOpen && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
            return;

        base.OnKeyDown(e);
    }
}
