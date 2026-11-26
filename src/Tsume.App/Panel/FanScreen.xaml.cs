using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Tsume.App.Input;
using Tsume.App.Platform;
using Tsume.App.Shell;
using Tsume.App.ViewModels;

namespace Tsume.App.Panel;

public partial class FanScreen
{
    private FanViewModel? _fan;
    private IDisposable? _textInputScope;
    private bool _openedTouchKeyboard;
    private int _renameSession;
    private CancellationTokenSource? _touchKeyboardOp;

    public FanScreen()
    {
        InitializeComponent();

        // Stops Windows moving the window when a touch list is dragged past its end.
        List.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

        // Lets focus reach Back, which is outside the list.
        Controls.StepOut.LetFocusLeave((UIElement)List.Content);

        // Scrolls smoothly to the focused item.
        Controls.SmoothScroll.Follow(List);

        // Reset disables itself once the curve matches stock; move focus back onto the curve so the controller is never stranded.
        ResetTile.IsEnabledChanged += (_, e) => { if (e.NewValue is false) RestoreFocus(preferCurve: true); };
        ResetTile.IsVisibleChanged += (_, e) => { if (e.NewValue is false) RestoreFocus(preferCurve: true); };
        CurveEditor.IsVisibleChanged += (_, e) => { if (e.NewValue is false) RestoreFocus(preferCurve: false); };

        // Make the inline pencil button beside the profile selector reachable via D-pad for every profile.
        ProfileSelector.PreviewKeyDown += OnProfileSelectorKeyDown;
        RenameButton.PreviewKeyDown += OnRenameButtonKeyDown;
        CurveEditor.PreviewKeyDown += OnCurveEditorKeyDown;

        DataContextChanged += (_, _) => AttachViewModel();
        Loaded += (_, _) => AttachViewModel();
        Unloaded += (_, _) => EndTextInput();
    }

    private void AttachViewModel()
    {
        var next = (DataContext as PanelViewModel)?.Settings.Fan ?? DataContext as FanViewModel;
        if (ReferenceEquals(_fan, next)) return;

        if (_fan is not null) _fan.PropertyChanged -= OnFanPropertyChanged;
        _fan = next;
        if (_fan is not null) _fan.PropertyChanged += OnFanPropertyChanged;
    }

    private void OnFanPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FanViewModel.IsRenamingProfile) || _fan is null) return;

        if (_fan.IsRenamingProfile)
            BeginTextInput();
        else
            EndTextInput(restoreFocus: true);
    }

    private void BeginTextInput()
    {
        var session = ++_renameSession;
        CancelTouchKeyboardOp();

        _textInputScope ??= TextInputMode.Enter();
        SetKeyboardPriority(yield: true);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, async () =>
        {
            if (session != _renameSession || _fan is not { IsRenamingProfile: true } || !IsVisible) return;

            ProfileNameBox.Focus();
            ProfileNameBox.SelectAll();
            ProfileNameBox.BringIntoView();

            // Only pop up Windows' on-screen keyboard when no external USB/Bluetooth keyboard is connected.
            if (PhysicalKeyboard.IsConnected()) return;

            PreShrinkForTouchKeyboard();

            var token = ResetTouchKeyboardOp();
            var hwnd = WindowHandle();
            var opened = await QuickActions.ShowTouchKeyboardAsync(hwnd, token);
            if (token.IsCancellationRequested || session != _renameSession || _fan is not { IsRenamingProfile: true })
            {
                if (opened) QuickActions.HideTouchKeyboard(hwnd);
                return;
            }

            if (opened) _openedTouchKeyboard = true;

            if (IsVisible && ProfileNameBox.IsVisible)
            {
                ProfileNameBox.Focus();
                ProfileNameBox.BringIntoView();
            }
        });
    }

    private void EndTextInput(bool restoreFocus = false)
    {
        var session = ++_renameSession;
        CancelTouchKeyboardOp();

        _textInputScope?.Dispose();
        _textInputScope = null;

        if (_openedTouchKeyboard)
        {
            _openedTouchKeyboard = false;
            QuickActions.HideTouchKeyboard(WindowHandle());
        }

        SetKeyboardPriority(yield: false);

        if (!restoreFocus) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (session != _renameSession || !IsVisible || _fan is not { IsOpen: true }) return;
            if (Window.GetWindow(this) is OverlayWindow { IsOpen: false }) return;
            if (ProfileSelector is { IsVisible: true, IsEnabled: true } && ProfileSelector.Focus()) return;
            RestoreFocus(preferCurve: false);
        });
    }

    private CancellationToken ResetTouchKeyboardOp()
    {
        _touchKeyboardOp?.Cancel();
        _touchKeyboardOp?.Dispose();
        _touchKeyboardOp = new CancellationTokenSource();
        return _touchKeyboardOp.Token;
    }

    private void CancelTouchKeyboardOp()
    {
        _touchKeyboardOp?.Cancel();
        _touchKeyboardOp?.Dispose();
        _touchKeyboardOp = null;
    }

    private void SetKeyboardPriority(bool yield)
    {
        if (Window.GetWindow(this) is OverlayWindow overlay)
            overlay.SetYieldToKeyboard(yield);
    }

    private void PreShrinkForTouchKeyboard()
    {
        if (Window.GetWindow(this) is OverlayWindow overlay)
            overlay.PreShrinkForTouchKeyboard();
    }

    private void OnProfileNameKeyDown(object sender, KeyEventArgs e)
    {
        if (_fan is null) return;

        if (e.Key == Key.Enter)
        {
            _fan.CommitRename.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _fan.CancelRename.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnTouchKeyboardClick(object sender, RoutedEventArgs e)
    {
        if (_fan is not { IsRenamingProfile: true }) return;
        if (ProfileNameBox.IsVisible) ProfileNameBox.Focus();

        var session = _renameSession;
        var token = ResetTouchKeyboardOp();
        var hwnd = WindowHandle();
        var wasVisible = QuickActions.IsTouchKeyboardVisible();
        if (!wasVisible) PreShrinkForTouchKeyboard();
        var toggled = await QuickActions.ToggleTouchKeyboardAsync(hwnd, token);
        if (!toggled) return;

        if (token.IsCancellationRequested || session != _renameSession || _fan is not { IsRenamingProfile: true })
        {
            if (!wasVisible) QuickActions.HideTouchKeyboard(hwnd);
            return;
        }

        _openedTouchKeyboard = !wasVisible;

        if (IsVisible && ProfileNameBox.IsVisible)
            ProfileNameBox.Focus();
    }

    private void OnProfileSelectorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && RenameButton is { IsVisible: true, IsEnabled: true })
            e.Handled = RenameButton.Focus();
    }

    private void OnRenameButtonKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Left && ProfileSelector is { IsVisible: true, IsEnabled: true })
            e.Handled = ProfileSelector.Focus();
        else if (e.Key == Key.Down && CurveEditor is { IsVisible: true, IsEnabled: true })
            e.Handled = CurveEditor.Focus();
    }

    private void OnCurveEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && !CurveEditor.IsEditing && ProfileSelector is { IsVisible: true, IsEnabled: true })
            e.Handled = ProfileSelector.Focus();
    }

    private nint WindowHandle() =>
        Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : 0;

    private void RestoreFocus(bool preferCurve) => Dispatcher.BeginInvoke(
        DispatcherPriority.Input, () =>
        {
            if (!IsVisible || DataContext is PanelViewModel { Fan.IsOpen: false }) return;
            if (Keyboard.FocusedElement is UIElement { IsVisible: true, IsEnabled: true } now && IsAncestorOf(now)) return;

            if (preferCurve && CurveEditor is { IsVisible: true, IsEnabled: true } && CurveEditor.Focus()) return;
            Controls.Screen.KeepFocus(this);
        });
}
