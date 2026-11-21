using System.Windows;
using System.Windows.Interop;
using Tsume.App.ViewModels;
using Tsume.Core.Ipc;

namespace Tsume.App.Shell;

/// <summary>
/// Standalone desktop window for read-only Hardware Discovery (Quick Scan, Guided Capture,
/// Live Watch Mode, and Report Preview). Stays open while you switch to OEM software beside it.
/// </summary>
public partial class DeviceDiscoveryWindow
{
    private static DeviceDiscoveryWindow? _openInstance;

    private readonly DeviceDiscoveryViewModel _vm;

    public DeviceDiscoveryWindow(HelperClient helper)
    {
        InitializeComponent();

        _vm = new DeviceDiscoveryViewModel(helper, Dispatcher);
        DataContext = _vm;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _vm.AttachWindowHandle(() => hwnd);
        };

        Closed += (_, _) =>
        {
            _vm.Dispose();
            if (ReferenceEquals(_openInstance, this)) _openInstance = null;
        };
    }

    /// <summary>Shows the Hardware Discovery window, or brings the existing one forward.</summary>
    public static void ShowOrActivate(HelperClient helper)
    {
        if (_openInstance is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new DeviceDiscoveryWindow(helper);
        _openInstance = window;
        window.Show();
        window.Activate();
    }

    private void OnSelectQuickScan(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 0;
    private void OnSelectGuided(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 1;
    private void OnSelectWatch(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 2;
    private void OnSelectPreview(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 3;
}
