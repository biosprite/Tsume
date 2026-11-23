using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class RtssDownloadScreen
{
    public RtssDownloadScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // Finishing an install or uninstall swaps which sections are shown.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PanelViewModel panel)
                panel.Rtss.Settled += () => { if (panel.Rtss.Download.IsOpen) Controls.Screen.KeepFocus(this); };
        };
    }
}
