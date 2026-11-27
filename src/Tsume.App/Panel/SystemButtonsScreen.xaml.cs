using System.Windows;

namespace Tsume.App.Panel;

public partial class SystemButtonsScreen
{
    public SystemButtonsScreen()
    {
        InitializeComponent();
        ScreenList.Configure(List);
    }

    private void OnRemoveKeyClick(object sender, RoutedEventArgs e) => KeySlotFocus.OnRemoveKeyClick(this, sender);

    private void OnAddKeyClick(object sender, RoutedEventArgs e) => KeySlotFocus.OnAddKeyClick(this, sender);
}
