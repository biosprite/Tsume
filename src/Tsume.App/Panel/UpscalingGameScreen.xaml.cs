using System.Windows;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class UpscalingGameScreen
{
    public UpscalingGameScreen()
    {
        InitializeComponent();
        ScreenList.Configure(List);

        // An action can remove the button that was pressed (Remove, once removed).
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PanelViewModel oldPanel) oldPanel.Upscaling.Game.Settled -= OnGameSettled;
        if (e.NewValue is PanelViewModel newPanel) newPanel.Upscaling.Game.Settled += OnGameSettled;
    }

    private void OnGameSettled() => Controls.Screen.KeepFocus(this);
}
