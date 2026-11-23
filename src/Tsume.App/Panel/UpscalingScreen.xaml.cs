using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingScreen
{
    public UpscalingScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, Rescan, Add a game and Add folder, which are outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // Games load after the screen opens, and a rescan can remove a row.
        // Focus goes to the first game unless it is already in the list.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PanelViewModel panel)
                panel.Upscaling.ListChanged += () => Controls.Screen.KeepFocus(this, unlessIn: List);
        };
    }
}
