using System.Windows;
using System.Windows.Threading;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingBrowseScreen
{
    public UpscalingBrowseScreen()
    {
        InitializeComponent();
        ScreenList.Configure(List);

        // Opening a folder replaces every row, so scroll to the top and put focus on the first item
        // (or straight onto Add/Remove this folder when inside a folder with no subfolders).
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PanelViewModel oldPanel) oldPanel.Upscaling.Browse.Moved -= OnBrowseMoved;
        if (e.NewValue is PanelViewModel newPanel) newPanel.Upscaling.Browse.Moved += OnBrowseMoved;
    }

    private void OnBrowseMoved()
    {
        if (DataContext is not PanelViewModel panel) return;

        var browse = panel.Upscaling.Browse;
        List.ScrollToTop();
        if (browse.FolderActionShown == Visibility.Visible && browse.Entries.Count == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FolderAction.Focus());
        else
            Controls.Screen.KeepFocus(this);
    }
}
