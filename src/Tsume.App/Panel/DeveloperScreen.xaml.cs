namespace Tsume.App.Panel;

public partial class DeveloperScreen
{
    public DeveloperScreen()
    {
        InitializeComponent();

        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
        Controls.StepOut.LetFocusLeave((System.Windows.UIElement)List.Content);
        Controls.SmoothScroll.Follow(List);
    }
}
