using System.Windows;
using System.Windows.Threading;
using Tsume.App.Controls;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class DedicatedActionsScreen
{
    private DedicatedActionsViewModel? _vm;

    public DedicatedActionsScreen()
    {
        InitializeComponent();
        ScreenList.Configure(List);

        Loaded += (_, _) => HookViewModel();
        Unloaded += (_, _) => UnhookViewModel();
    }

    private void HookViewModel()
    {
        if (DataContext is not PanelViewModel panel) return;
        var vm = panel.Settings.Controls.DedicatedActionsPage;
        if (ReferenceEquals(_vm, vm)) return;

        UnhookViewModel();
        _vm = vm;
        _vm.CollapsedByBack += OnCollapsedByBack;
    }

    private void UnhookViewModel()
    {
        if (_vm is null) return;
        _vm.CollapsedByBack -= OnCollapsedByBack;
        _vm = null;
    }

    private void OnCollapsedByBack(DedicatedActionEditor editor)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var container = ActionsList.ItemContainerGenerator.ContainerFromItem(editor);
            var header = container is null ? null : KeySlotFocus.FindDescendant<Tile>(container);
            if (header is not null && header.Focus()) return;
            CategoryTabs.Focus();
        });
    }

    private void OnRemoveKeyClick(object sender, RoutedEventArgs e) => KeySlotFocus.OnRemoveKeyClick(this, sender);

    private void OnAddKeyClick(object sender, RoutedEventArgs e) => KeySlotFocus.OnAddKeyClick(this, sender);
}
