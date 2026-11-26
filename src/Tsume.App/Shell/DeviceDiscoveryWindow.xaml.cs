using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private const double DefaultWidth = 980;
    private const double DefaultHeight = 700;
    private const double DefaultMinWidth = 720;
    private const double DefaultMinHeight = 440;
    private const double WorkAreaMargin = 24;

    private static DeviceDiscoveryWindow? _openInstance;

    private readonly DeviceDiscoveryViewModel _vm;
    private nint _hwnd;

    public DeviceDiscoveryWindow(HelperClient helper)
    {
        InitializeComponent();
        ApplyReadableBrushes();

        _vm = new DeviceDiscoveryViewModel(helper, Dispatcher);
        DataContext = _vm;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _vm.AttachWindowHandle(() => _hwnd);
            ApplyTitleBarTheme();
            FitToWorkArea(recenter: true);
        };

        Colours.Changed += OnThemeChanged;
        KeyDown += OnWindowKeyDown;

        Closed += (_, _) =>
        {
            Colours.Changed -= OnThemeChanged;
            KeyDown -= OnWindowKeyDown;
            _vm.Dispose();
            if (ReferenceEquals(_openInstance, this)) _openInstance = null;
        };
    }

    private void OnThemeChanged()
    {
        ApplyReadableBrushes();
        ApplyTitleBarTheme();
    }

    private void ApplyReadableBrushes()
    {
        var scheme = Colours.Current;
        var back = scheme == ColourScheme.Default
            ? Color.FromRgb(0x13, 0x15, 0x1A)
            : scheme.BackgroundColour;
        var text = Colours.TextBrush.Color;

        // Hardware Discovery nests cards and panels on top of Surface, so keep secondary and subtle
        // text higher-contrast than the overlay's default Text.Muted / Text.Faint values.
        Resources["Text.Muted"] = Freeze(Mix(text, back, 0.22));
        Resources["Text.Faint"] = Freeze(Mix(text, back, 0.36));

        var isLight = (0.2126 * back.R + 0.7152 * back.G + 0.0722 * back.B) > 128;
        Resources["Dev.SubCard"] = Freeze(isLight
            ? Color.FromArgb(0x0C, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x40, 0x08, 0x0A, 0x0E));
        Resources["Dev.ScrollThumb"] = Freeze(text);
        Resources["Dev.ScrollTrack"] = Freeze(text);
        Resources["Dev.RingTrack"] = Freeze(Color.FromArgb(isLight ? (byte)0x24 : (byte)0x28, text.R, text.G, text.B));
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        byte Channel(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!_vm.IsGuidedTab) return;
        if (Keyboard.FocusedElement is TextBoxBase { IsReadOnly: false }) return;

        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Z && mods == ModifierKeys.Control)
        {
            if (_vm.CanUndoGuided)
            {
                _vm.UndoGuidedCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if ((e.Key == Key.Y && mods == ModifierKeys.Control)
                 || (e.Key == Key.Z && mods == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            if (_vm.CanRedoGuided)
            {
                _vm.RedoGuidedCommand.Execute(null);
                e.Handled = true;
            }
        }
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
            existing.FitToWorkArea(recenter: false);
            existing.Activate();
            return;
        }

        var window = new DeviceDiscoveryWindow(helper);
        _openInstance = window;
        window.Show();
        window.Activate();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitToWorkArea(recenter: false);
    }

    /// <summary>
    /// Keeps the window inside the current monitor's usable work area (above the taskbar),
    /// clamping initial and minimum sizes on smaller displays such as 7" 1080p handhelds at 150–200% DPI.
    /// </summary>
    private void FitToWorkArea(bool recenter)
    {
        if (WindowState != WindowState.Normal) return;

        var work = GetCurrentWorkAreaDip();
        if (work.Width <= 0 || work.Height <= 0) return;

        var maxWidth = Math.Max(320, work.Width - WorkAreaMargin);
        var maxHeight = Math.Max(320, work.Height - WorkAreaMargin);

        MinWidth = Math.Min(DefaultMinWidth, maxWidth);
        MinHeight = Math.Min(DefaultMinHeight, maxHeight);

        if (recenter)
        {
            Width = Math.Min(DefaultWidth, maxWidth);
            Height = Math.Min(DefaultHeight, maxHeight);
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
        }
        else
        {
            if (Width > maxWidth) Width = maxWidth;
            if (Height > maxHeight) Height = maxHeight;
            Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
            Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        }
    }

    private Rect GetCurrentWorkAreaDip()
    {
        if (_hwnd != 0)
        {
            var monitor = User32.MonitorFromWindow(_hwnd, User32.MONITOR_DEFAULTTONEAREST);
            if (monitor != 0)
            {
                var info = User32.MONITORINFO.Create();
                if (User32.GetMonitorInfo(monitor, ref info))
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                    var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                    var left = info.WorkArea.Left / scaleX;
                    var top = info.WorkArea.Top / scaleY;
                    var width = (info.WorkArea.Right - info.WorkArea.Left) / scaleX;
                    var height = (info.WorkArea.Bottom - info.WorkArea.Top) / scaleY;
                    if (width > 0 && height > 0) return new Rect(left, top, width, height);
                }
            }
        }

        return SystemParameters.WorkArea;
    }

    private void OnSelectQuickScan(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 0;
    private void OnSelectGuided(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 1;
    private void OnSelectWatch(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 2;
    private void OnSelectPreview(object sender, RoutedEventArgs e) => _vm.ActiveTabIndex = 3;
}
