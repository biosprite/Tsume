using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Tsume.App.Platform.Interop;
using Tsume.App.Theme;
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
    private nint _hwnd;

    public DeviceDiscoveryWindow(HelperClient helper)
    {
        InitializeComponent();

        _vm = new DeviceDiscoveryViewModel(helper, Dispatcher);
        DataContext = _vm;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _vm.AttachWindowHandle(() => _hwnd);
            ApplyTitleBarTheme();
        };

        Colours.Changed += ApplyTitleBarTheme;

        Closed += (_, _) =>
        {
            Colours.Changed -= ApplyTitleBarTheme;
            _vm.Dispose();
            if (ReferenceEquals(_openInstance, this)) _openInstance = null;
        };
    }

    private void ApplyTitleBarTheme()
    {
        if (_hwnd == 0) return;

        var dark = 1;
        DwmApi.DwmSetWindowAttribute(_hwnd, DwmApi.DWMWA_USE_IMMERSIVE_DARK_MODE, dark, sizeof(int));

        var back = Colours.Current == ColourScheme.Default
            ? Color.FromRgb(0x13, 0x15, 0x1A)
            : Colours.Current.BackgroundColour;
        var captionColorRef = back.R | (back.G << 8) | (back.B << 16);
        DwmApi.DwmSetWindowAttribute(_hwnd, DwmApi.DWMWA_CAPTION_COLOR, captionColorRef, sizeof(int));

        var text = Colours.TextBrush.Color;
        var textColorRef = text.R | (text.G << 8) | (text.B << 16);
        DwmApi.DwmSetWindowAttribute(_hwnd, DwmApi.DWMWA_TEXT_COLOR, textColorRef, sizeof(int));
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
