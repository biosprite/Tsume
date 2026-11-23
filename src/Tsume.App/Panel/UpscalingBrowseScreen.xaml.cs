using System.Windows;
using System.Windows.Threading;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingBrowseScreen
{
    public UpscalingBrowseScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, "one folder up", and Add this folder, which are outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // Opening a folder replaces every row, so scroll to the top and put focus on the first item
        // (or straight onto Add/Remove this folder when inside a folder with no subfolders).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not PanelViewModel panel) return;

            var browse = panel.Upscaling.Browse;
            browse.Moved += () =>
            {
                List.ScrollToTop();
                if (browse.FolderActionShown == Visibility.Visible && browse.Entries.Count == 0)
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FolderAction.Focus());
                else
                    Controls.Screen.KeepFocus(this);
            };
        };
    }
}
