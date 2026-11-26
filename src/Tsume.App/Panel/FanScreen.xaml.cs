using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class FanScreen
{
    public FanScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // Reset disables itself once the curve matches stock; move focus back onto the curve so the controller is never stranded.
        ResetTile.IsEnabledChanged += (_, e) => { if (e.NewValue is false) RestoreFocus(preferCurve: true); };
        ResetTile.IsVisibleChanged += (_, e) => { if (e.NewValue is false) RestoreFocus(preferCurve: true); };
        CurveEditor.IsVisibleChanged += (_, e) => { if (e.NewValue is false) RestoreFocus(preferCurve: false); };
    }

    private void RestoreFocus(bool preferCurve) => Dispatcher.BeginInvoke(
        DispatcherPriority.Input, () =>
        {
            if (!IsVisible || DataContext is PanelViewModel { Fan.IsOpen: false }) return;
            if (Keyboard.FocusedElement is UIElement { IsVisible: true, IsEnabled: true } now && IsAncestorOf(now)) return;

            if (preferCurve && CurveEditor is { IsVisible: true, IsEnabled: true } && CurveEditor.Focus()) return;
            Controls.Screen.KeepFocus(this);
        });
}
