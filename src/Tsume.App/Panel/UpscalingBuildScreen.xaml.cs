using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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
            if (DataContext is PanelViewModel panel)
            {
                panel.Upscaling.Build.Settled += () => Controls.Screen.KeepFocus(this);
                panel.Upscaling.Build.OptiPatcherSettled += () => Dispatcher.BeginInvoke(
                    DispatcherPriority.Input, () =>
                    {
                        if (!panel.Upscaling.Build.IsOpen) return;
                        if (Keyboard.FocusedElement is UIElement { IsVisible: true, IsEnabled: true } now && IsAncestorOf(now)) return;

                        if (OptiPatcherDownloadTile is { IsVisible: true, IsEnabled: true })
                            OptiPatcherDownloadTile.Focus();
                        else if (OptiPatcherActionTile is { IsVisible: true, IsEnabled: true })
                            OptiPatcherActionTile.Focus();
                        else
                            Controls.Screen.KeepFocus(this);
                    });
            }
        };
    }
}
