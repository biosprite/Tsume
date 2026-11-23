using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class RtssScreen
{
    public RtssScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // Switching between not-installed / stopped / running hides and reveals controls.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PanelViewModel panel) panel.Rtss.Settled += () => { if (!panel.Rtss.Download.IsOpen) Controls.Screen.KeepFocus(this); };
        };
    }
}
