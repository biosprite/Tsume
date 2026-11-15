using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingBuildScreen
{
    public UpscalingBuildScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // The focused "Update games" button disappears once they are updated.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PanelViewModel panel) panel.Upscaling.Build.Settled += () => Controls.Screen.KeepFocus(this);
        };
    }
}
