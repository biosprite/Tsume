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

    private static readonly TimeSpan ReleaseGrace = TimeSpan.FromMilliseconds(50);
    private readonly DispatcherTimer _awaitingRelease = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
    private DateTime _releaseDeadline;
    private DateTime? _releasedAt;

    private nint _wasInFront;
    private bool _tookAtOnce;
    private int _retakes;
    private int _settleTicks;

    /// <summary>Watched while the panel is open, to put it back over the taskbar (see StayOverTaskbar).</summary>
    private readonly ZOrderChanges _zOrderChanges = new();

    /// <summary>What had focus as each layer opened, so focus can go back there as each one closes.</summary>
    private readonly Stack<IInputElement?> _focusBeneath = new();
    private bool _resettingLayers;

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
        _awaitingRelease.Tick += (_, _) => OnAwaitingReleaseTick();
        _zOrderChanges.Changed += StayOverTaskbar;

        foreach (var layer in panel.Layers) layer.PropertyChanged += OnLayerMoved;
        panel.Settings.Controls.PropertyChanged += OnControlsChanged;
        Controls.StepOut.LetFocusLeave(Sections);   // down to Settings and Power
        Controls.SmoothScroll.Follow((System.Windows.Controls.ScrollViewer)Sections.Parent);
    }

    private bool IsRightSide => _panel.Settings.Controls.OpenOnRight;
    private bool RememberLastPage => _panel.Settings.Controls.RememberLastPage;

    /// <summary>Starting/ending horizontal offset in design units: off the left or right edge.</summary>
    private double OffscreenX => IsRightSide ? DesignWidth : -DesignWidth;

    private void OnControlsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ControlsViewModel.OpenOnRight) || !IsOpen) return;

        PlaceOnScreen();
        Slide.Snap(TranslateTransform.XProperty, OffscreenX / 2);
        Slide.Animate(TranslateTransform.XProperty, 0, Motion.Arrive, Motion.Glide);
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
        _closing = null;
        RightOfWay.Claim();   // first, so everything below already runs ahead of the game
        OpenTrace.Begin();

        // Notify isolation before tile refreshes start so Mode 1 -> Mode 4 capture wins the vendor HID channel.
        OpenChanged?.Invoke(true);

        Stage.IsHitTestVisible = true;
        if (RememberLastPage) CloseSheetsSilently();
        else ResetLayersSilently();
        PlaceOnScreen();
        _panel.OnOpening(keepPages: RememberLastPage);

        // Start off screen and invisible. Slide is inside Fit, so it moves in design units.
        Slide.Snap(TranslateTransform.XProperty, OffscreenX);
        Stage.Snap(OpacityProperty, 0);
        if (_panel.IsCovered) RestoreSections();
        else SetSectionsBack();

        // Until the panel has settled in front, losing focus does not close it (see HoldTheFront).
        ForegroundWindow.BehindPanel = _wasInFront = ForegroundWindow.Current;
        _retakes = _settleTicks = 0;

        // If the stick or a button is still held, leave the focus where it is until it is let go: many apps
        // (Steam Big Picture among them) stop reading the controller once they lose focus and keep repeating
        // the held direction, since they never see the release.
        var awaitRelease = Input.PadIsolation.IsBuiltInPadHeld();
        if (!awaitRelease) _settling.Start();

        // Activate on a real open, even if an early-boot WarmUp() has not finished hiding yet.
        ShowActivated = !awaitRelease;
        Show();
        RaiseAboveEverything();
        if (awaitRelease) AwaitRelease();
        else _tookAtOnce = ForegroundWindow.Take(this);
        _zOrderChanges.Start();

        // After Show: started alongside it, this delays the window by a frame or three.
        DisplayClock.KeepAwake(OpenTrace.PaceThisTime);

        // Deliberately not waiting for the window to settle: the pause is felt
        // more than one late first frame is seen.
        PlayIn();

        if (!awaitRelease) Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusActiveControl);
    }

    /// <summary>Polls the built-in pad until it has been released for a moment (or the cap passes), then takes the front.</summary>
    private void AwaitRelease()
    {
        _releaseDeadline = DateTime.UtcNow + Input.PadIsolation.HeldInputWaitCap;
        _releasedAt = null;
        _awaitingRelease.Start();
    }

    private void OnAwaitingReleaseTick()
    {
        if (!IsOpen)
        {
            _awaitingRelease.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        if (Input.PadIsolation.IsBuiltInPadHeld()) _releasedAt = null;
        else _releasedAt ??= now;

        // After the release, give the app in front a moment to read the neutral report before it loses focus.
        var settled = _releasedAt is { } released && now - released >= ReleaseGrace;
        if (!settled && now < _releaseDeadline) return;

        _awaitingRelease.Stop();
        _settling.Start();
        _tookAtOnce = ForegroundWindow.Take(this);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusActiveControl);
    }

    /// <summary>
    /// Shows the panel once, invisibly, at start-up, so the few hundred milliseconds of
    /// first-time window construction are not spent on the first opening.
    /// </summary>
    public void WarmUp()
    {
        if (IsOpen || IsVisible) return;
        if (ScreenHeight() <= 0) return;   // display not ready yet (e.g. early logon)

        PlaceOnScreen();
        _panel.OnOpening();

        Slide.Snap(TranslateTransform.XProperty, OffscreenX);
        Stage.Snap(OpacityProperty, 0);
        SetSectionsBack();

        ShowActivated = false;   // whatever is in front keeps the focus
        Show();

        // Run the entrance animation too, with the stage left transparent.
        Motion.AfterFrames(2, () => { if (!IsOpen) MoveIn(); });

        var finished = false;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        void Finish()
        {
            if (finished) return;
            finished = true;
            fallback.Stop();

            ShowActivated = true;
            if (IsOpen) return;   // opened for real in the meantime

            Hide();
            _panel.OnClosed();
        }

        // During early FSE/logon transitions CompositionTarget.Rendering may not tick yet; the fallback guarantees cleanup.
        fallback.Tick += (_, _) => Finish();
        fallback.Start();
        Motion.AfterFrames(8, Finish);
    }

    /// <summary>
    /// Spans the whole screen height, taskbar included, so Stage's margin leaves equal gaps above and below,
    /// pinned to either the left or right edge of the primary display.
    /// </summary>
    private void PlaceOnScreen()
    {
        var height = ScreenHeight();
        var width = ScreenWidth();
        if (height <= 0 || width <= 0) return;

        Height = height;
        FitTo(Height);

        // Leave 24 design units on the inner edge for the drop shadow and 12 on the screen edge.
        Stage.Margin = IsRightSide ? new Thickness(24, 12, 12, 12) : new Thickness(12, 12, 24, 12);
        Left = IsRightSide ? Math.Max(0, width - Width) : 0;
        Top = 0;
    }

    /// <summary>
    /// The primary screen's width in this window's units, reflecting the current DPI scaling.
    /// </summary>
    private double ScreenWidth() =>
        User32.GetSystemMetrics(User32.SM_CXSCREEN) / VisualTreeHelper.GetDpi(this).DpiScaleX;

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
        if (height <= 0) return;

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
    /// Puts the panel back over the taskbar whenever the taskbar has landed on top of it. While Start or
    /// Search is open, Windows lifts the taskbar above every ordinary window; when it lets it down again, at
    /// once or minutes after the flyout has closed, the taskbar lands on top of the topmost windows.
    /// </summary>
    // Nothing a program without uiAccess makes can go above Start or Search themselves, or take the foreground from them.
    private void StayOverTaskbar()
    {
        if (IsOpen && TaskbarIsAbove()) RaiseAboveEverything();
    }

    /// <summary>Whether the taskbar sits above the panel in the stacking order. Never true while it is lifted out of it.</summary>
    private bool TaskbarIsAbove()
    {
        var taskbar = User32.FindWindow("Shell_TrayWnd", null);

        for (var above = User32.GetWindow(new WindowInteropHelper(this).Handle, User32.GW_HWNDPREV);
             above != 0;
             above = User32.GetWindow(above, User32.GW_HWNDPREV))
        {
            if (above == taskbar) return true;
        }

        return false;
    }

    /// <summary>
    /// For the first half second after opening, losing focus does not close the panel; each tick it
    /// re-raises itself, and retakes the foreground if a full-screen program has kept or taken it back.
    /// </summary>
    // If that fails the panel stays open anyway; any retake is logged to %LOCALAPPDATA%\Tsume\focus.log.
    private void HoldTheFront()
    {
        // Every tick, in front or not: the taskbar can land on top of a panel that has the focus (see StayOverTaskbar).
        if (IsOpen) RaiseAboveEverything();

        if (IsOpen && !ForegroundWindow.Has(this))
        {
            _retakes++;
            ForegroundWindow.Take(this);
        }

        if (IsOpen && ++_settleTicks < SettleTicks) return;
        _settling.Stop();

        if (!IsOpen || (_tookAtOnce && _retakes == 0)) return;

        var held = ForegroundWindow.Has(this);
        var band = User32.GetWindowBand(new WindowInteropHelper(this).Handle, out var zbid) ? zbid.ToString() : "?";
        FocusLog.Write(
            $"over {ForegroundWindow.Describe(_wasInFront)}: " +
            $"{(_tookAtOnce ? "in front at once" : "not in front at first")}, taken again {_retakes} times, " +
            (held ? "in front in the end" : $"NOT in front in the end - {ForegroundWindow.Describe(ForegroundWindow.Current)} is") +
            $"; uiaccess {(Platform.UiAccess.IsGranted ? "yes" : "no")}, band {band}, fse {(Platform.FullScreenExperience.IsActive ? "yes" : "no")}");
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

        if (_panel.IsCovered) return;

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

    /// <summary>Ensures main sections sit at resting opacity and position while a remembered page is open.</summary>
    private void RestoreSections()
    {
        foreach (var section in SectionsInOrder())
        {
            if (section.RenderTransform is TranslateTransform rise)
                rise.Snap(TranslateTransform.YProperty, 0);
            section.Snap(OpacityProperty, 1);
        }
    }

    private IEnumerable<UIElement> SectionsInOrder()
    {
        yield return Header;
        foreach (UIElement section in Sections.Children) yield return section;
        yield return Footer;
    }

    private void FocusFirstControl() => Sections.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

    /// <summary>Focuses the topmost open screen when reopening a remembered page, otherwise the main screen.</summary>
    private void FocusActiveControl()
    {
        if (!IsOpen) return;

        if (_panel.IsCovered)
        {
            for (var i = LayerHost.Children.Count - 1; i >= 0; i--)
            {
                if (LayerHost.Children[i] is Controls.Screen { IsOpen: true } top)
                {
                    top.FocusInitial();
                    return;
                }
            }
        }

        FocusFirstControl();
    }

    /// <summary>Slides the panel away. The task completes once it is fully hidden.</summary>
    public Task CloseAsync()
    {
        if (!IsOpen) return _closing?.Task ?? Task.CompletedTask;
        IsOpen = false;
        _settling.Stop();
        _awaitingRelease.Stop();
        Stage.IsHitTestVisible = false;

        var closing = _closing = new TaskCompletionSource();
        OpenChanged?.Invoke(false);

        // Close any floating popup immediately; layers stay visually frozen until the panel is hidden
        // so dismissing never flashes the main screen mid-slide.
        Controls.KeyDropdown.CloseIfOpen();

        var finished = false;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };

        void Finish()
        {
            if (finished) return;
            finished = true;
            fallback.Stop();

            if (!IsOpen && _closing == closing)   // not reopened mid-flight
            {
                ForgetFocus();
                _zOrderChanges.Stop();
                if (IsVisible) Hide();
                DisplayClock.KeepAwake(false);
                RightOfWay.Yield();
                SettleLayersAfterHide();
                _panel.OnClosed();
            }

            closing.TrySetResult();
        }

        // If DWM stops ticking frames (e.g. entering sleep/hibernate), the fallback still hides and completes CloseAsync.
        fallback.Tick += (_, _) => Finish();
        fallback.Start();

        Stage.Animate(OpacityProperty, 0, Motion.Smooth, Motion.Depart);
        Slide.Animate(TranslateTransform.XProperty, OffscreenX / 2, Motion.Smooth, Motion.Depart, then: () =>
        {
            // Windows flashes a hidden window's last frame when it is next shown, so wait
            // until the fully transparent frame is on screen before hiding.
            Motion.AfterFrames(2, Finish);
        });

        return closing.Task;
    }

    /// <summary>Immediately hides the panel and stops background presentation helpers before system suspend.</summary>
    public void ForceCloseForSuspend()
    {
        var wasOpen = IsOpen;
        IsOpen = false;
        _settling.Stop();
        _zOrderChanges.Stop();
        DisplayClock.KeepAwake(false);
        RightOfWay.Yield();

        if (wasOpen) OpenChanged?.Invoke(false);

        if (wasOpen || IsVisible)
        {
            Controls.KeyDropdown.CloseIfOpen();
            ForgetFocus();
            if (IsVisible) Hide();
            SettleLayersAfterHide();
            _panel.OnClosed();
        }

        _closing?.TrySetResult();
        _closing = null;
    }

    /// <summary>
    /// Clears focus so the next opening does not start with the ring on the last-used control.
    /// </summary>
    private void ForgetFocus()
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
    }

    /// <summary>Back / Escape closes an open dropdown, or the top layer, otherwise the panel.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsOpen)
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        if (Controls.KeyDropdown.CloseIfOpen())
        {
            e.Handled = true;
            return;
        }

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

        if (!IsOpen || key == Key.None) return;
        if (Keyboard.FocusedElement is null) FocusActiveControl();

        var source = Keyboard.FocusedElement is Visual focused
            ? PresentationSource.FromVisual(focused) ?? PresentationSource.FromVisual(this)
            : PresentationSource.FromVisual(this);
        if (source is null) return;

        Send(Keyboard.PreviewKeyDownEvent);
        Send(Keyboard.PreviewKeyUpEvent);

        void Send(RoutedEvent stroke) => InputManager.Current.ProcessInput(
            new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = stroke });
    }

    private void SettleLayersAfterHide()
    {
        if (RememberLastPage)
        {
            CloseSheetsSilently();
            _panel.FlushOpenLayers();
        }
        else
        {
            ResetLayersSilently();
        }
    }

    /// <summary>
    /// Closes every open layer and snaps the panel back to the main screen without running layer
    /// transitions, so closing never reveals the main screen and the next opening starts clean.
    /// </summary>
    private void ResetLayersSilently()
    {
        _resettingLayers = true;
        try
        {
            _focusBeneath.Clear();
            _panel.CloseLayers();

            foreach (var child in LayerHost.Children)
            {
                if (child is Controls.Screen screen) screen.SnapClosed();
                else if (child is Controls.Sheet sheet) sheet.SnapClosed();
            }

            Main.IsEnabled = true;
            Main.Snap(OpacityProperty, 1);
            MainAside.Snap(TranslateTransform.XProperty, 0);
        }
        finally
        {
            _resettingLayers = false;
        }
    }

    /// <summary>
    /// Silently closes transient confirmation/action sheets while keeping any open Screen pages
    /// intact when Remember last open page is enabled.
    /// </summary>
    private void CloseSheetsSilently()
    {
        _resettingLayers = true;
        try
        {
            foreach (var sheet in _panel.Sheets)
            {
                if (!sheet.IsOpen) continue;
                sheet.IsOpen = false;
                _focusBeneath.TryPop(out _);
            }

            foreach (var child in LayerHost.Children)
            {
                if (child is Controls.Sheet sheet) sheet.SnapClosed();
                else if (child is Controls.Screen { IsOpen: false } screen) screen.SnapClosed();
            }

            SyncLayerInteractivity();
        }
        finally
        {
            _resettingLayers = false;
        }
    }

    private void SyncLayerInteractivity()
    {
        Main.IsEnabled = !_panel.IsCovered;
        var onControlsSubpage = _panel.Settings.Controls.MainTilesPage.IsOpen
            || _panel.Settings.Controls.DedicatedActionsPage.IsOpen
            || _panel.Settings.Controls.SystemButtonsPage.IsOpen;
        var onAPage = _panel.Settings.Customization.IsOpen
            || _panel.Settings.PerformancePresets.IsOpen
            || _panel.Settings.Controls.IsOpen
            || onControlsSubpage
            || _panel.Settings.Developer.IsOpen;
        SettingsScreen.IsEnabled = !onAPage && !_panel.Quit.IsOpen;
        ControlsScreen.IsEnabled = !onControlsSubpage;
        UpscalingScreen.IsEnabled = !_panel.Upscaling.HasPageOpen && !_panel.Upscaling.RestoreAll.IsOpen;
        RtssScreen.IsEnabled = !_panel.Rtss.Download.IsOpen;
    }

    /// <summary>
    /// Disables the controls under an open layer, and returns focus to the button that opened it when it closes.
    /// </summary>
    private void OnLayerMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (_resettingLayers) return;
        if (e.PropertyName != nameof(LayerViewModel.IsOpen) || sender is not LayerViewModel layer) return;

        if (layer.IsOpen) _focusBeneath.Push(Keyboard.FocusedElement);

        var onControlsSubpage = _panel.Settings.Controls.MainTilesPage.IsOpen
            || _panel.Settings.Controls.DedicatedActionsPage.IsOpen
            || _panel.Settings.Controls.SystemButtonsPage.IsOpen;
        var onAPage = _panel.Settings.Customization.IsOpen
            || _panel.Settings.PerformancePresets.IsOpen
            || _panel.Settings.Controls.IsOpen
            || onControlsSubpage
            || _panel.Settings.Developer.IsOpen;
        SettingsScreen.IsCovered = onAPage;
        ControlsScreen.IsCovered = onControlsSubpage;
        UpscalingScreen.IsCovered = _panel.Upscaling.HasPageOpen;
        RtssScreen.IsCovered = _panel.Rtss.Download.IsOpen;
        SyncLayerInteractivity();
        StepAside(_panel.Settings.IsOpen || _panel.Fan.IsOpen || _panel.Lighting.IsOpen || _panel.Upscaling.IsOpen || _panel.Rtss.IsOpen);

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
