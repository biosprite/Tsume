using System.Windows;
using Tsume.App.Controls;

namespace Tsume.App.Panel;

public partial class ControlsScreen
{
    public ControlsScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        SmoothScroll.Follow(List);
    }
}
