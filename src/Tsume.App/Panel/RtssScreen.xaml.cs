using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class RtssScreen
{
    public RtssScreen()
    {
        InitializeComponent();
        ScreenList.Configure(List);

        // Switching between not-installed / stopped / running hides and reveals controls.
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PanelViewModel oldPanel) oldPanel.Rtss.Settled -= OnRtssSettled;
        if (e.NewValue is PanelViewModel newPanel) newPanel.Rtss.Settled += OnRtssSettled;
    }

    private void OnRtssSettled()
    {
        if (DataContext is PanelViewModel panel && !panel.Rtss.Download.IsOpen)
            Controls.Screen.KeepFocus(this);
    }
}
