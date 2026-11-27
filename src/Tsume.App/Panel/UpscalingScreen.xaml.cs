using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingScreen
{
    public UpscalingScreen()
    {
        InitializeComponent();
        ScreenList.Configure(List);

        // Games load after the screen opens, and a rescan can remove a row.
        // Focus goes to the first game unless it is already in the list.
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PanelViewModel oldPanel) oldPanel.Upscaling.ListChanged -= OnListChanged;
        if (e.NewValue is PanelViewModel newPanel) newPanel.Upscaling.ListChanged += OnListChanged;
    }

    private void OnListChanged() => Controls.Screen.KeepFocus(this, unlessIn: List);
}
