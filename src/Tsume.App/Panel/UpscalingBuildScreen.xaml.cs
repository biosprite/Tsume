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
        ScreenList.Configure(List);

        // The focused "Update games" button disappears once they are updated.
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PanelViewModel oldPanel)
        {
            oldPanel.Upscaling.Build.Settled -= OnBuildSettled;
            oldPanel.Upscaling.Build.OptiPatcherSettled -= OnOptiPatcherSettled;
        }

        if (e.NewValue is PanelViewModel newPanel)
        {
            newPanel.Upscaling.Build.Settled += OnBuildSettled;
            newPanel.Upscaling.Build.OptiPatcherSettled += OnOptiPatcherSettled;
        }
    }

    private void OnBuildSettled() => Controls.Screen.KeepFocus(this);

    private void OnOptiPatcherSettled() => Dispatcher.BeginInvoke(
        DispatcherPriority.Input, () =>
        {
            if (DataContext is not PanelViewModel panel || !panel.Upscaling.Build.IsOpen) return;
            if (Keyboard.FocusedElement is UIElement { IsVisible: true, IsEnabled: true } now && IsAncestorOf(now)) return;

            if (OptiPatcherDownloadTile is { IsVisible: true, IsEnabled: true })
                OptiPatcherDownloadTile.Focus();
            else if (OptiPatcherActionTile is { IsVisible: true, IsEnabled: true })
                OptiPatcherActionTile.Focus();
            else
                Controls.Screen.KeepFocus(this);
        });
}
