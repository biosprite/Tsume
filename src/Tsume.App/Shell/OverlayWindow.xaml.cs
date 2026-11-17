using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Input;
using Tsume.App.Platform.Interop;
using Tsume.App.Theme;
using Tsume.App.ViewModels;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Shell;

/// <summary>
/// The panel window: slides in and out, and turns controller buttons into key presses.
/// </summary>
public partial class OverlayWindow
{
    /// <summary>
    /// The screen height the panel is laid out for: the Claw's 1920 × 1200 at 150 %, in WPF units.
    /// On any other screen the panel is scaled to take up the same share of it.
    /// </summary>
    public const double DesignHeight = 800;

    /// <summary>The window's width on a screen <see cref="DesignHeight"/> tall, Stage's margin included.</summary>
    public const double DesignWidth = 448;

    /// <summary>How far each section rises as it fades in.</summary>
    private const double SectionRise = 18;

    private readonly PanelViewModel _panel;
    private readonly bool _stayOpen;
    private TaskCompletionSource? _closing;

    /// <summary>Checks that the panel is still in front after opening: 8 × 60 ms, about half a second.</summary>
    private const int SettleTicks = 8;
    private readonly DispatcherTimer _settling = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private static readonly Journal FocusLog = new("focus");

    private nint _wasInFront;
    private bool _tookAtOnce;
    private int _retakes;
    private int _settleTicks;

    /// <summary>What had focus as each layer opened, so focus can go back there as each one closes.</summary>
    private readonly Stack<IInputElement?> _focusBeneath = new();

    /// <param name="stayOpen">Development aid: keep the panel up when it loses focus.</param>
    public OverlayWindow(PanelViewModel panel, bool stayOpen = false)
    {
        InitializeComponent();

        DataContext = _panel = panel;
        _stayOpen = stayOpen;

        SourceInitialized += (_, _) =>
        {
            HideFromAltTab();
            SeeThrough.Apply(this);
        };
        Deactivated += (_, _) => { if (!_stayOpen && !_settling.IsEnabled) _ = CloseAsync(); };
        _settling.Tick += (_, _) => HoldTheFront();

        foreach (var layer in panel.Layers) layer.PropertyChanged += OnLayerMoved;
        Controls.StepOut.LetFocusLeave(Sections);   // down to Settings and Power
        Controls.SmoothScroll.Follow((System.Windows.Controls.ScrollViewer)Sections.Parent);
    }

    public bool IsOpen { get; private set; }

    /// <summary>Raised with the new state whenever the panel opens or starts closing.</summary>
    public event Action<bool>? OpenChanged;

    public void Toggle()
    {
        if (IsOpen) _ = CloseAsync();
        else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        _closing?.TrySetResult();
        RightOfWay.Claim();   // first, so everything below already runs ahead of the game
        OpenTrace.Begin();

        PlaceOnScreen();
        _panel.OnOpening();

        // Start off screen and invisible. Slide is inside Fit, so it moves in design units.
        Slide.Snap(TranslateTransform.XProperty, -DesignWidth);
        Stage.Snap(OpacityProperty, 0);
        SetSectionsBack();

        // Until the panel has settled in front, losing focus does not close it (see HoldTheFront).
        ForegroundWindow.BehindPanel = _wasInFront = ForegroundWindow.Current;
        _retakes = _settleTicks = 0;
        _settling.Start();

        Show();
        RaiseAboveEverything();
        _tookAtOnce = ForegroundWindow.Take(this);

        // After Show: started alongside it, this delays the window by a frame or three.
        DisplayClock.KeepAwake(OpenTrace.PaceThisTime);

        // Deliberately not waiting for the window to settle: the pause is felt
        // more than one late first frame is seen.
        PlayIn();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusFirstControl);
        OpenChanged?.Invoke(true);
    }

    /// <summary>
    /// Shows the panel once, invisibly, at start-up, so the few hundred milliseconds of
    /// first-time window construction are not spent on the first opening.
    /// </summary>
    public void WarmUp()
    {
        if (IsOpen || IsVisible) return;

        PlaceOnScreen();
        _panel.OnOpening();

        Slide.Snap(TranslateTransform.XProperty, -DesignWidth);
        Stage.Snap(OpacityProperty, 0);
        SetSectionsBack();

        ShowActivated = false;   // whatever is in front keeps the focus
        Show();

        // Run the entrance animation too, with the stage left transparent.
        Motion.AfterFrames(2, () => { if (!IsOpen) MoveIn(); });

        Motion.AfterFrames(8, () =>
        {
            ShowActivated = true;
            if (IsOpen) return;   // opened for real in the meantime

            Hide();
            _panel.OnClosed();
        });
    }

    /// <summary>
    /// Spans the whole screen height, taskbar included, so Stage's margin leaves equal gaps above and below.
    /// </summary>
    private void PlaceOnScreen()
    {
        Left = 0;
        Top = 0;
        Height = ScreenHeight();
        FitTo(Height);
    }

    /// <summary>
    /// The primary screen's height in this window's units. Not SystemParameters.PrimaryScreenHeight:
    /// that keeps using the scaling Tsume started with, and Windows' scaling may have changed since.
    /// </summary>
    private double ScreenHeight() =>
        User32.GetSystemMetrics(User32.SM_CYSCREEN) / VisualTreeHelper.GetDpi(this).DpiScaleY;

    /// <summary>
    /// Scales the panel to take the same share of a screen <paramref name="height"/> units tall as it does
    /// of the Claw's. Measured on every opening: the resolution may have changed since the last.
    /// </summary>
    private void FitTo(double height)
    {
        var factor = height / DesignHeight;

        Width = DesignWidth * factor;
        Fit.ScaleX = Fit.ScaleY = factor;

        // A cached bitmap is drawn at the element's own size, then stretched by Fit; this keeps it sharp.
        ShadowCache.RenderAtScale = factor;
    }

    /// <summary>
    /// Re-raises the panel on every opening: a full-screen game and the taskbar are
    /// topmost too, and among topmost windows the one raised last wins.
    /// </summary>
    private void RaiseAboveEverything() => User32.SetWindowPos(
        new WindowInteropHelper(this).Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
        User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

    /// <summary>
    /// For the first half second after opening, losing focus does not close the panel; each tick it
    /// re-raises itself and retakes the foreground if a full-screen program has kept or taken it back.
    /// </summary>
    // If that fails the panel stays open anyway; any retake is logged to %LOCALAPPDATA%\Tsume\focus.log.
    private void HoldTheFront()
    {
        if (IsOpen && !ForegroundWindow.Has(this))
        {
            _retakes++;
            RaiseAboveEverything();
            ForegroundWindow.Take(this);
        }

        if (IsOpen && ++_settleTicks < SettleTicks) return;
        _settling.Stop();

        if (!IsOpen || (_tookAtOnce && _retakes == 0)) return;

        var held = ForegroundWindow.Has(this);
        FocusLog.Write(
            $"over {ForegroundWindow.Describe(_wasInFront)}: " +
            $"{(_tookAtOnce ? "in front at once" : "not in front at first")}, taken again {_retakes} times, " +
            (held ? "in front in the end" : $"NOT in front in the end - {ForegroundWindow.Describe(ForegroundWindow.Current)} is"));
    }

    /// <summary>The panel glides in; header, sections and footer fade up one after another.</summary>
    private void PlayIn()
    {
        if (!IsOpen) return;   // closed again before it ever appeared
        OpenTrace.Moving();

        Stage.Animate(OpacityProperty, 1, Motion.Quick, Motion.Settle);
        MoveIn();
    }

    private void MoveIn()
    {
        Slide.Animate(TranslateTransform.XProperty, 0, Motion.Arrive, Motion.Glide);

        var delay = TimeSpan.FromMilliseconds(70);

        foreach (var section in SectionsInOrder())
        {
            var rise = (TranslateTransform)section.RenderTransform;

            rise.Animate(TranslateTransform.YProperty, 0, Motion.Arrive, Motion.Glide, delay);
            section.Animate(OpacityProperty, 1, Motion.Smooth, Motion.Settle, delay);

            delay += Motion.Stagger;
        }
    }

    /// <summary>Puts every section where its entrance starts: a little low, and invisible.</summary>
    private void SetSectionsBack()
    {
        foreach (var section in SectionsInOrder())
        {
            if (section.RenderTransform is not TranslateTransform rise)
                section.RenderTransform = rise = new TranslateTransform();

            rise.Snap(TranslateTransform.YProperty, SectionRise);
            section.Snap(OpacityProperty, 0);
        }
    }

    private IEnumerable<UIElement> SectionsInOrder()
    {
        yield return Header;
        foreach (UIElement section in Sections.Children) yield return section;
        yield return Footer;
    }

    private void FocusFirstControl() => Sections.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

    /// <summary>Slides the panel away. The task completes once it is fully hidden.</summary>
    public Task CloseAsync()
    {
        if (!IsOpen) return _closing?.Task ?? Task.CompletedTask;
        IsOpen = false;

        var closing = _closing = new TaskCompletionSource();
        OpenChanged?.Invoke(false);

        // A layer left up would still be folding away during the next opening.
        _panel.CloseLayers();

        Stage.Animate(OpacityProperty, 0, Motion.Smooth, Motion.Depart);
        Slide.Animate(TranslateTransform.XProperty, -DesignWidth / 2, Motion.Smooth, Motion.Depart, then: () =>
        {
            // Windows flashes a hidden window's last frame when it is next shown, so wait
            // until the fully transparent frame is on screen before hiding.
            Motion.AfterFrames(2, () =>
            {
                if (!IsOpen)   // not reopened mid-flight
                {
                    ForgetFocus();
                    Hide();
                    DisplayClock.KeepAwake(false);
                    RightOfWay.Yield();
                    _panel.OnClosed();
                }

                closing.TrySetResult();
            });
        });

        return closing.Task;
    }

    /// <summary>
    /// Clears focus so the next opening does not start with the ring on the last-used control.
    /// </summary>
    private void ForgetFocus()
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
    }

    /// <summary>Back / Escape closes the top layer if there is one, otherwise the panel.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_panel.IsCovered) _panel.CloseTopLayer();
        else _ = CloseAsync();

        e.Handled = true;
    }

    /// <summary>
    /// Feeds a controller button into the panel as the equivalent key press, so controls only handle the keyboard.
    /// </summary>
    public void Press(PadButton button)
    {
        var key = button switch
        {
            PadButton.Up    => Key.Up,
            PadButton.Down  => Key.Down,
            PadButton.Left  => Key.Left,
            PadButton.Right => Key.Right,
            PadButton.A     => Key.Space,
            PadButton.B     => Key.Escape,
            _               => Key.None,
        };

        if (!IsOpen || key == Key.None || PresentationSource.FromVisual(this) is not { } source) return;

        Send(Keyboard.PreviewKeyDownEvent);
        Send(Keyboard.PreviewKeyUpEvent);

        void Send(RoutedEvent stroke) => InputManager.Current.ProcessInput(
            new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = stroke });
    }

    /// <summary>
    /// Disables the controls under an open layer, and returns focus to the button that opened it when it closes.
    /// </summary>
    private void OnLayerMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LayerViewModel.IsOpen) || sender is not LayerViewModel layer) return;

        if (layer.IsOpen) _focusBeneath.Push(Keyboard.FocusedElement);

        Main.IsEnabled = !_panel.IsCovered;
        var onAPage = _panel.Settings.Customization.IsOpen || _panel.Settings.PerformancePresets.IsOpen;
        SettingsScreen.IsCovered = onAPage;
        SettingsScreen.IsEnabled = !onAPage && !_panel.Quit.IsOpen;
        UpscalingScreen.IsCovered = _panel.Upscaling.HasPageOpen;
        UpscalingScreen.IsEnabled = !_panel.Upscaling.HasPageOpen && !_panel.Upscaling.RestoreAll.IsOpen;
        StepAside(_panel.Settings.IsOpen || _panel.Lighting.IsOpen || _panel.Upscaling.IsOpen);

        if (layer.IsOpen || !_focusBeneath.TryPop(out var beneath)) return;
        if (!IsOpen) return;

        if (beneath is not null) beneath.Focus();
        else if (!_panel.IsCovered) FocusFirstControl();   // opened straight into a layer: nothing to go back to
    }

    /// <summary>The first screen slips left and fades as a second screen comes in from the right.</summary>
    private void StepAside(bool aside)
    {
        var easing = aside ? Motion.Depart : Motion.Glide;

        Main.Animate(OpacityProperty, aside ? 0 : 1, aside ? Motion.Quick : Motion.Smooth, easing);
        MainAside.Animate(TranslateTransform.XProperty, aside ? -Controls.Screen.StartsAside : 0,
            aside ? Motion.Smooth : Motion.Arrive, easing);
    }

    private void HideFromAltTab()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = User32.GetWindowLongPtr(handle, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtr(handle, User32.GWL_EXSTYLE, style | User32.WS_EX_TOOLWINDOW);
    }
}
