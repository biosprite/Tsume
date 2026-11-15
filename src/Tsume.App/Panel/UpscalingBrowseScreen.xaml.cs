using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingBrowseScreen
{
    public UpscalingBrowseScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back and "one folder up", which are outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // Opening a folder replaces every row, so scroll to the top and focus the first one.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not PanelViewModel panel) return;

            panel.Upscaling.Browse.Moved += () =>
            {
                List.ScrollToTop();
                Controls.Screen.KeepFocus(this);
            };
        };
    }
}
