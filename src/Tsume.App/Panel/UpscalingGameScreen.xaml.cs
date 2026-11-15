using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingGameScreen
{
    public UpscalingGameScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // An action can remove the button that was pressed (Remove, once removed).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PanelViewModel panel) panel.Upscaling.Game.Settled += () => Controls.Screen.KeepFocus(this);
        };
    }
}
