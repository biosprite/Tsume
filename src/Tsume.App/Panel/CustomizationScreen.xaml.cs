namespace Tsume.App.Panel;

public partial class CustomizationScreen
{
    public CustomizationScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        Controls.StepOut.LetFocusLeave((System.Windows.UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);
    }
}
