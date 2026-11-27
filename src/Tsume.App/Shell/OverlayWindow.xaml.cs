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
    private readonly DispatcherTimer _keyboardAvoidance = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private IDisposable? _inputPaneWatch;
    private bool _resettingLayers;
    private bool _yieldToKeyboard;
    private bool _keyboardClearedHeight;
    private double _targetClearedHeightDip;
    private double _lastDockedKeyboardTopDip;
    private long _preShrinkUntilTick;
    private int _heightAnimGen;
    private bool _hasWindowRegion;
    private User32.RECT _cutoutKeyboardRect;
    private User32.RECT _cutoutWindowRect;

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
        Closed += (_, _) => StopKeyboardAvoidance();
        Deactivated += (_, _) => OnWindowDeactivated();
        _settling.Tick += (_, _) => HoldTheFront();
        _awaitingRelease.Tick += (_, _) => OnAwaitingReleaseTick();
        _keyboardAvoidance.Tick += (_, _) => SyncKeyboardAvoidance();
        _zOrderChanges.Changed += StayOverTaskbar;

        foreach (var layer in panel.Layers) layer.PropertyChanged += OnLayerMoved;
        panel.Settings.Controls.PropertyChanged += OnControlsChanged;
        Controls.StepOut.LetFocusLeave(Sections);   // down to Settings and Power
        Controls.SmoothScroll.Follow((System.Windows.Controls.ScrollViewer)Sections.Parent);
    }

    private void OnWindowDeactivated()
    {
        if (_stayOpen || _settling.IsEnabled) return;

        // Keep the panel open if focus moved to Windows' on-screen / touch keyboard during text entry.
        if (Input.TextInputMode.IsActive)
        {
            var front = ForegroundWindow.Current;
            if (Platform.QuickActions.IsTouchKeyboardWindow(front)
                || (front == 0 && Platform.QuickActions.IsTouchKeyboardVisible()))
            {
                return;
            }
        }

        _ = CloseAsync();
    }

    /// <summary>
    /// Clears room for Windows' touch/on-screen keyboard during inline text entry without ever clearing
    /// <c>WS_EX_TOPMOST</c>.
    /// </summary>
    // WARNING: Never set Topmost = false or call SetWindowPos(..., HWND_NOTOPMOST) here or anywhere else on
    // OverlayWindow. Clearing WS_EX_TOPMOST demotes a UIAccess HWND from ZBID_UIACCESS (band 2) down to
    // ZBID_DESKTOP (band 1), which breaks overlays over fullscreen apps (e.g. Steam Big Picture minimizes and
    // raises the taskbar when a band-1 window takes foreground). Keep the window in band 2 and clear room for
    // the touch keyboard by adjusting Height or clipping with SetWindowRgn instead.
    public void SetYieldToKeyboard(bool yield)
    {
        if (_yieldToKeyboard == yield) return;
        _yieldToKeyboard = yield;

        if (yield)
        {
            StartKeyboardAvoidance();
        }
        else
        {
            StopKeyboardAvoidance();
            ClearKeyboardAvoidance(restoreHeight: IsOpen, animate: IsOpen);
            if (IsOpen && new WindowInteropHelper(this).Handle != 0) RaiseAboveEverything();
        }
    }

    /// <summary>
    /// Smoothly begins retracting the panel before Windows finishes raising the touch keyboard, so the panel
    /// glides out of the way proactively rather than retracting over an already-visible keyboard.
    /// </summary>
    public void PreShrinkForTouchKeyboard()
    {
        if (!IsOpen || !_yieldToKeyboard) return;

        if (Platform.QuickActions.TryGetTouchKeyboardRect(out var liveRect))
        {
            ApplyKeyboardRect(liveRect);
            return;
        }

        var fullHeight = ScreenHeight();
        if (fullHeight <= 0) return;

        var scale = Fit.ScaleY > 0 ? Fit.ScaleY : 1;
        var minHeight = Math.Min(fullHeight, 180 * scale);
        var predicted = _lastDockedKeyboardTopDip > 0 ? _lastDockedKeyboardTopDip : fullHeight * 0.52;
        var targetHeight = Math.Clamp(predicted, minHeight, fullHeight);

        _preShrinkUntilTick = Environment.TickCount64 + 900;
        AnimateClearedHeight(targetHeight);
    }

    private void StartKeyboardAvoidance()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_inputPaneWatch is null && handle != 0)
        {
            _inputPaneWatch = Platform.QuickActions.WatchTouchKeyboard(
                handle,
                onShowing: rect => Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    if (IsOpen && _yieldToKeyboard) ApplyKeyboardRect(rect);
                }),
                onHiding: () => Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    if (!IsOpen || !_yieldToKeyboard) return;
                    _preShrinkUntilTick = 0;
                    ClearKeyboardAvoidance(restoreHeight: true, animate: true);
                }));
        }

        SyncKeyboardAvoidance();
        _keyboardAvoidance.Start();
    }

    private void StopKeyboardAvoidance()
    {
        _keyboardAvoidance.Stop();
        _preShrinkUntilTick = 0;
        _inputPaneWatch?.Dispose();
        _inputPaneWatch = null;
    }

    private void SyncKeyboardAvoidance()
    {
        if (!IsOpen || !_yieldToKeyboard)
        {
            StopKeyboardAvoidance();
            ClearKeyboardAvoidance(restoreHeight: IsOpen, animate: IsOpen);
            return;
        }

        if (Platform.QuickActions.TryGetTouchKeyboardRect(out var kbRect))
        {
            ApplyKeyboardRect(kbRect);
            return;
        }

        // Give a proactive pre-shrink a short window for Windows to raise the touch keyboard before expanding back.
        if (_preShrinkUntilTick > Environment.TickCount64) return;
        _preShrinkUntilTick = 0;
        ClearKeyboardAvoidance(restoreHeight: true, animate: true);
    }

    private void ApplyKeyboardRect(User32.RECT kbRect)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || kbRect.Right <= kbRect.Left || kbRect.Bottom <= kbRect.Top) return;

        _preShrinkUntilTick = 0;
        if (!User32.GetWindowRect(handle, out var winRect)) return;

        // No overlap with the panel's horizontal span: keep full panel height.
        if (kbRect.Right <= winRect.Left || kbRect.Left >= winRect.Right)
        {
            ClearKeyboardAvoidance(restoreHeight: true, animate: true);
            return;
        }

        var screenHeightPx = User32.GetSystemMetrics(User32.SM_CYSCREEN);
        if (screenHeightPx <= 0) return;

        // Standard bottom-docked touch keyboard: shrink the window cleanly above it so rounded corners and shadow stay intact.
        if (kbRect.Bottom >= screenHeightPx - 48 && kbRect.Top > screenHeightPx / 5)
        {
            ClearWindowRegion(handle);

            var dpi = VisualTreeHelper.GetDpi(this);
            var fullHeight = ScreenHeight();
            if (fullHeight <= 0 || dpi.DpiScaleY <= 0) return;

            var scale = Fit.ScaleY > 0 ? Fit.ScaleY : 1;
            var minHeight = Math.Min(fullHeight, 180 * scale);
            var targetHeight = Math.Clamp(kbRect.Top / dpi.DpiScaleY, minHeight, fullHeight);
            _lastDockedKeyboardTopDip = targetHeight;
            AnimateClearedHeight(targetHeight);
            return;
        }

        // Top-docked or floating keyboard with a reported rect: keep full height and punch out its rect from our window region.
        if (_keyboardClearedHeight)
        {
            RestoreFullHeight(animate: false);
            if (!User32.GetWindowRect(handle, out winRect)) return;
        }

        if (_hasWindowRegion && RectsEqual(_cutoutKeyboardRect, kbRect) && RectsEqual(_cutoutWindowRect, winRect))
            return;

        var winWidth = winRect.Right - winRect.Left;
        var winHeight = winRect.Bottom - winRect.Top;
        if (winWidth <= 0 || winHeight <= 0) return;

        var fullRegion = DwmApi.CreateRectRgn(0, 0, winWidth, winHeight);
        var kbRegion = DwmApi.CreateRectRgn(
            kbRect.Left - winRect.Left,
            kbRect.Top - winRect.Top,
            kbRect.Right - winRect.Left,
            kbRect.Bottom - winRect.Top);
        var diffRegion = DwmApi.CreateRectRgn(0, 0, 0, 0);

        _ = DwmApi.CombineRgn(diffRegion, fullRegion, kbRegion, DwmApi.RGN_DIFF);
        DwmApi.DeleteObject(fullRegion);
        DwmApi.DeleteObject(kbRegion);

        if (User32.SetWindowRgn(handle, diffRegion, redraw: true) != 0)
        {
            _hasWindowRegion = true;
            _cutoutKeyboardRect = kbRect;
            _cutoutWindowRect = winRect;
        }
        else
        {
            DwmApi.DeleteObject(diffRegion);
        }
    }

    private void AnimateClearedHeight(double targetDip)
    {
        var fullHeight = ScreenHeight();
        if (fullHeight <= 0) return;

        if (targetDip >= fullHeight - 0.5)
        {
            RestoreFullHeight(animate: true);
            return;
        }

        if (_keyboardClearedHeight && Math.Abs(_targetClearedHeightDip - targetDip) <= 0.5)
            return;

        var scale = Fit.ScaleY > 0 ? Fit.ScaleY : 1;
        var targetStageH = Math.Max(120, (targetDip / scale) - 24);
        var currentStageH = Stage.ActualHeight > 0 ? Stage.ActualHeight : DesignHeight - 24;

        var gen = ++_heightAnimGen;
        _keyboardClearedHeight = true;
        _targetClearedHeightDip = targetDip;

        Stage.VerticalAlignment = VerticalAlignment.Top;
        Stage.Snap(HeightProperty, currentStageH);

        if (targetDip > Height + 0.5)
        {
            // Growing slightly (e.g. predicted height was shorter than the actual keyboard): expand HWND first so nothing clips.
            Height = targetDip;
            Stage.Animate(HeightProperty, targetStageH, Motion.Quick, Motion.Glide);
            return;
        }

        if (Math.Abs(currentStageH - targetStageH) <= 2)
        {
            Stage.Snap(HeightProperty, targetStageH);
            if (Math.Abs(Height - targetDip) > 0.5) Height = targetDip;
            return;
        }

        // Smooth GPU-composited shrink of Stage, followed by a single Win32 Window.Height resize when it lands.
        Stage.Animate(HeightProperty, targetStageH, Motion.Quick, Motion.Glide, then: () =>
        {
            if (gen != _heightAnimGen || !IsOpen || !_keyboardClearedHeight) return;
            if (Math.Abs(Height - targetDip) > 0.5) Height = targetDip;
        });
    }

    private void ClearKeyboardAvoidance(bool restoreHeight, bool animate = false)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) ClearWindowRegion(handle);
        if (restoreHeight && _keyboardClearedHeight) RestoreFullHeight(animate);
        else if (!restoreHeight) ResetStageHeight();
    }

    private void RestoreFullHeight(bool animate)
    {
        var fullHeight = ScreenHeight();
        var wasCleared = _keyboardClearedHeight;
        _keyboardClearedHeight = false;
        _targetClearedHeightDip = 0;
        _preShrinkUntilTick = 0;

        if (!animate || !wasCleared || !IsOpen || !IsVisible)
        {
            ResetStageHeight();
            if (fullHeight > 0 && Math.Abs(Height - fullHeight) > 0.5) Height = fullHeight;
            return;
        }

        var gen = ++_heightAnimGen;
        var currentStageH = Stage.ActualHeight > 0 ? Stage.ActualHeight : DesignHeight - 24;
        var fullStageH = DesignHeight - 24;

        Stage.VerticalAlignment = VerticalAlignment.Top;
        Stage.Snap(HeightProperty, currentStageH);
        if (fullHeight > 0 && Math.Abs(Height - fullHeight) > 0.5) Height = fullHeight;

        Stage.Animate(HeightProperty, fullStageH, Motion.Smooth, Motion.Glide, then: () =>
        {
            if (gen != _heightAnimGen) return;
            Stage.BeginAnimation(HeightProperty, null);
            Stage.ClearValue(HeightProperty);
            Stage.VerticalAlignment = VerticalAlignment.Stretch;
        });
    }

    private void ResetStageHeight()
    {
        _heightAnimGen++;
        _keyboardClearedHeight = false;
        _targetClearedHeightDip = 0;
        _preShrinkUntilTick = 0;
        Stage.BeginAnimation(HeightProperty, null);
        Stage.ClearValue(HeightProperty);
        Stage.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private void ClearWindowRegion(nint handle)
    {
        if (!_hasWindowRegion) return;
        _hasWindowRegion = false;
        _cutoutKeyboardRect = default;
        _cutoutWindowRect = default;
        User32.SetWindowRgn(handle, 0, redraw: true);
    }

    private static bool RectsEqual(User32.RECT a, User32.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private bool IsRightSide => _panel.Settings.Controls.OpenOnRight;
    private bool RememberLastPage => _panel.Settings.Controls.RememberLastPage;

    /// <summary>Starting/ending horizontal offset in design units: off the left or right edge.</summary>
    private double OffscreenX => IsRightSide ? DesignWidth : -DesignWidth;

    private void OnControlsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ControlsViewModel.OpenOnRight) || !IsOpen) return;

        PlaceOnScreen();
        if (_yieldToKeyboard) SyncKeyboardAvoidance();
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
        var yieldToKeyboard = Input.TextInputMode.IsActive;
        _yieldToKeyboard = yieldToKeyboard;
        if (!yieldToKeyboard)
        {
            StopKeyboardAvoidance();
            ClearKeyboardAvoidance(restoreHeight: false);

            // Promote into ZBID_UIACCESS before Show() activates the window so the very first open never
            // steals foreground from a fullscreen app while still in ZBID_DESKTOP (band 1).
            new WindowInteropHelper(this).EnsureHandle();
            RaiseAboveEverything();
        }

        ShowActivated = !awaitRelease;
        Show();
        if (!yieldToKeyboard) RaiseAboveEverything();
        else StartKeyboardAvoidance();

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
        RaiseAboveEverything();  // promote into ZBID_UIACCESS while invisible so first real open never activates in band 1

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
        if (IsOpen && !_yieldToKeyboard && TaskbarIsAbove()) RaiseAboveEverything();
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
        if (IsOpen && !_yieldToKeyboard) RaiseAboveEverything();

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

        // Close any floating popup or active inline rename immediately; layers stay visually frozen until the
        // panel is hidden so dismissing never flashes the main screen mid-slide.
        Controls.KeyDropdown.CloseIfOpen();
        if (_panel.Fan.IsRenamingProfile) _panel.Fan.CancelRename.Execute(null);

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
                StopKeyboardAvoidance();
                if (IsVisible) Hide();
                _yieldToKeyboard = false;
                ClearKeyboardAvoidance(restoreHeight: false);
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
            StopKeyboardAvoidance();
            if (IsVisible) Hide();
            _yieldToKeyboard = false;
            ClearKeyboardAvoidance(restoreHeight: false);
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
            || _panel.Settings.Fan.IsOpen
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
            || _panel.Settings.Fan.IsOpen
            || _panel.Settings.Controls.IsOpen
            || onControlsSubpage
            || _panel.Settings.Developer.IsOpen;
        SettingsScreen.IsCovered = onAPage;
        ControlsScreen.IsCovered = onControlsSubpage;
        UpscalingScreen.IsCovered = _panel.Upscaling.HasPageOpen;
        RtssScreen.IsCovered = _panel.Rtss.Download.IsOpen;
        SyncLayerInteractivity();
        StepAside(_panel.Settings.IsOpen || _panel.Settings.Fan.IsOpen || _panel.Lighting.IsOpen || _panel.Upscaling.IsOpen || _panel.Rtss.IsOpen);

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
