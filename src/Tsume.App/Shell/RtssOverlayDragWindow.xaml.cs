using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Tsume.App.Language;
using Tsume.App.Platform.Interop;
using Tsume.App.Platform.Rtss;
using Tsume.App.Theme;
using Tsume.App.ViewModels;
using static Tsume.App.Platform.Interop.User32;

namespace Tsume.App.Shell;

/// <summary>
/// Lightweight, no-activate UIAccess overlay window that makes Tsume's RTSS FPS overlays smoothly draggable
/// on touchscreens (and pointers). Operates in two seamless modes:
/// 1. Standby hit-target: invisible window tightly bounded to the active FPS overlay; dragging it expands
///    to full screen without stealing focus from the game, shows a 60–120 FPS glass frame + alignment snap
///    guides, and commits the new position to RTSS on release.
/// 2. Interactive "Drag on screen" mode: full-screen positioning with clickable 9-point anchors, Reset &amp; Done.
/// </summary>
public sealed partial class RtssOverlayDragWindow : Window
{
    private const double DragThresholdDip = 6.0;
    private const double MinStandbyTouchDip = 44.0;
    private const long HandoffTimeoutMs = 1400;

    private readonly RtssOverlayService _service;
    private readonly Border[] _snapDots = new Border[9];
    private readonly DispatcherTimer _handoffTimer;

    private RtssOverlayLevel _level = RtssOverlayLevel.Off;
    private int _screenPhysW = 1920;
    private int _screenPhysH = 1200;
    private int _viewportX;
    private int _viewportY;
    private int _viewportW = 1920;
    private int _viewportH = 1200;
    private int _overlayPhysW;
    private int _overlayPhysH;
    private int _currentPhysX;
    private int _currentPhysY;

    private double _rawDpiX = 1.0;
    private double _rawDpiY = 1.0;
    private double _viewLeftDip;
    private double _viewTopDip;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    private bool _interactive;
    private bool _expanded;
    private bool _showing;
    private bool _pointerDown;
    private bool _dragging;
    private bool _awaitingHandoff;
    private int _handoffConfirmedTicks;
    private long _handoffStartedMs;
    private int _handoffX;
    private int _handoffY;
    private int _handoffW;
    private int _handoffH;
    private int? _activeTouchId;
    private Point _downScreenDip;
    private int _startPhysX;
    private int _startPhysY;
    private string? _lastBadgeText;
    private double _cachedBadgeW;
    private double _cachedBadgeH;

    public RtssOverlayDragWindow(RtssOverlayService service)
    {
        InitializeComponent();
        _service = service;

        _handoffTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _handoffTimer.Tick += OnHandoffTick;

        SourceInitialized += (_, _) => ApplyWindowStyles();
        Loaded += (_, _) => RefreshDpi();
        Closed += (_, _) => CancelHandoff();

        // Touch handlers (primary path on PC handhelds).
        OverlayBox.PreviewTouchDown += OnOverlayTouchDown;
        PreviewTouchMove += OnWindowTouchMove;
        PreviewTouchUp += OnWindowTouchUp;
        LostTouchCapture += (_, _) => EndDrag(commit: true);

        // Mouse handlers (ignoring touch-promoted mouse events so touch is never double-handled).
        OverlayBox.PreviewMouseLeftButtonDown += OnOverlayMouseDown;
        PreviewMouseMove += OnWindowMouseMove;
        PreviewMouseLeftButtonUp += OnWindowMouseUp;
        LostMouseCapture += (_, _) => EndDrag(commit: true);

        Scrim.MouseLeftButtonDown += (_, _) => { if (_interactive) ExitInteractiveMode(); };
        Scrim.TouchDown += (_, e) => { if (_interactive) { e.Handled = true; ExitInteractiveMode(); } };

        DoneButton.Click += (_, _) => ExitInteractiveMode();
        ResetButton.Click += (_, _) => ResetCurrentLevel();
        KeyDown += (_, e) => { if (_interactive && e.Key == Key.Escape) ExitInteractiveMode(); };

        BuildSnapDots();
    }

    /// <summary>True while in explicit "Drag on screen" mode, mid-touch-drag, or handing off to RTSS after a drop.</summary>
    public bool IsActivelyPositioning => _interactive || _dragging || _awaitingHandoff;

    /// <summary>True while in explicit full-screen "Drag on screen" mode.</summary>
    public bool IsInteractive => _interactive;

    /// <summary>Raised when interactive "Drag on screen" mode exits.</summary>
    public event Action? InteractiveClosed;

    /// <summary>Raised when a standby touch/mouse drag finishes.</summary>
    public event Action? DragEnded;

    /// <summary>Updates the invisible standby hit-target to match the active FPS overlay's current bounds.</summary>
    public void SyncStandby(
        RtssOverlayLevel level, int x, int y, int width, int height,
        int frameW, int frameH, int viewportX, int viewportY, int viewportW, int viewportH)
    {
        if (_interactive || _dragging || _awaitingHandoff || _showing) return;

        if (IsVisible && !_expanded)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rawDpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            var rawDpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            if (_level == level
                && _currentPhysX == x
                && _currentPhysY == y
                && _overlayPhysW == width
                && _overlayPhysH == height
                && _screenPhysW == frameW
                && _screenPhysH == frameH
                && _viewportX == viewportX
                && _viewportY == viewportY
                && _viewportW == viewportW
                && _viewportH == viewportH
                && _rawDpiX == rawDpiX
                && _rawDpiY == rawDpiY)
            {
                return;
            }
        }

        _level = level;
        _overlayPhysW = width;
        _overlayPhysH = height;
        _currentPhysX = x;
        _currentPhysY = y;
        _screenPhysW = frameW;
        _screenPhysH = frameH;
        _viewportX = viewportX;
        _viewportY = viewportY;
        _viewportW = viewportW;
        _viewportH = viewportH;

        if (!EnsureShown()) return;
        RefreshDpi();
        ApplyStandbyWindowBounds();
        RaiseTopmostNoActivate();
    }

    /// <summary>Enters full-screen interactive positioning mode (works with touch, stylus, and mouse).</summary>
    public void EnterInteractiveMode(
        RtssOverlayLevel level, int x, int y, int width, int height,
        int frameW, int frameH, int viewportX, int viewportY, int viewportW, int viewportH)
    {
        if (_showing) return;

        CancelHandoff();
        _level = level;
        _overlayPhysW = width;
        _overlayPhysH = height;
        _currentPhysX = x;
        _currentPhysY = y;
        _screenPhysW = frameW;
        _screenPhysH = frameH;
        _viewportX = viewportX;
        _viewportY = viewportY;
        _viewportW = viewportW;
        _viewportH = viewportH;
        _interactive = true;

        if (!EnsureShown())
        {
            _interactive = false;
            return;
        }
        RefreshDpi();

        ExpandToFullScreen();
        UpdateSnapDotsLayout();
        PositionOverlayBox(_currentPhysX, _currentPhysY, snapped: null);

        FadeInElement(Scrim, 1.0, 140);
        FadeInElement(ActionBar, 1.0, 140);
        FadeInElement(SnapDotsLayer, 1.0, 140);
        ShowDragChrome(visible: true);
        UpdateBadge(_currentPhysX, _currentPhysY, _service.Settings.GetPlacement(_level).Alignment);
        FadeInElement(PositionBadge, 1.0, 140);

        RaiseTopmostNoActivate();
    }

    private bool EnsureShown()
    {
        if (IsVisible) return true;
        if (_showing) return false;

        _showing = true;
        try
        {
            Show();
            return IsVisible;
        }
        finally
        {
            _showing = false;
        }
    }

    /// <summary>Leaves interactive mode cleanly and notifies the controller to return to standby or hide.</summary>
    public void ExitInteractiveMode()
    {
        if (!_interactive) return;
        EndDrag(commit: true);
        _interactive = false;
        CancelHandoff();

        HideElementImmediate(Scrim);
        HideElementImmediate(ActionBar);
        HideElementImmediate(SnapDotsLayer);
        HideElementImmediate(PositionBadge);
        ShowDragChrome(visible: false, immediate: true);

        InteractiveClosed?.Invoke();
    }

    public void HideQuietly()
    {
        CancelHandoff();
        _interactive = false;
        EndDrag(commit: false);
        HideElementImmediate(Scrim);
        HideElementImmediate(ActionBar);
        HideElementImmediate(SnapDotsLayer);
        HideElementImmediate(PositionBadge);
        ShowDragChrome(visible: false, immediate: true);
        if (IsVisible) Hide();
    }

    private void ResetCurrentLevel()
    {
        if (_level == RtssOverlayLevel.Off) return;

        var defaultPlacement = RtssOverlayMetrics.DefaultPlacement(_level);
        var (ax, ay) = RtssOverlayMetrics.ResolveAnchor(_level, defaultPlacement.Alignment, _screenPhysW, _screenPhysH);
        _currentPhysX = ax;
        _currentPhysY = ay;
        _service.CommitDragPlacement(_level, defaultPlacement);
        PositionOverlayBox(ax, ay, defaultPlacement.Alignment);
        UpdateBadge(ax, ay, defaultPlacement.Alignment);
    }

    private void OnOverlayTouchDown(object? sender, TouchEventArgs e)
    {
        if (_level == RtssOverlayLevel.Off || _pointerDown) return;

        e.Handled = true;
        _activeTouchId = e.TouchDevice.Id;
        var pt = PointToScreenDip(e.GetTouchPoint(this).Position);
        BeginPointer(pt);
        CaptureTouch(e.TouchDevice);
    }

    private void OnWindowTouchMove(object? sender, TouchEventArgs e)
    {
        if (!_pointerDown || _activeTouchId != e.TouchDevice.Id) return;

        e.Handled = true;
        var pt = PointToScreenDip(e.GetTouchPoint(this).Position);
        MovePointer(pt);
    }

    private void OnWindowTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_pointerDown || _activeTouchId != e.TouchDevice.Id) return;

        e.Handled = true;
        ReleaseTouchCapture(e.TouchDevice);
        _activeTouchId = null;
        EndDrag(commit: true);
    }

    private void OnOverlayMouseDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null || _level == RtssOverlayLevel.Off || _pointerDown) return;

        e.Handled = true;
        var pt = PointToScreenDip(e.GetPosition(this));
        BeginPointer(pt);
        CaptureMouse();
    }

    private void OnWindowMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null || !_pointerDown || _activeTouchId is not null) return;

        var pt = PointToScreenDip(e.GetPosition(this));
        MovePointer(pt);
    }

    private void OnWindowMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null || !_pointerDown || _activeTouchId is not null) return;

        e.Handled = true;
        ReleaseMouseCapture();
        EndDrag(commit: true);
    }

    private void BeginPointer(Point screenDip)
    {
        if (_awaitingHandoff)
        {
            CancelHandoff();
            if (!_interactive)
            {
                ShowDragChrome(visible: false, immediate: true);
                ApplyStandbyWindowBounds();
            }
        }

        RefreshDpi();
        _pointerDown = true;
        _dragging = _interactive;
        _downScreenDip = screenDip;
        _startPhysX = _currentPhysX;
        _startPhysY = _currentPhysY;

        if (_interactive)
            ShowDragChrome(visible: true);
    }

    private void MovePointer(Point screenDip)
    {
        var dxDip = screenDip.X - _downScreenDip.X;
        var dyDip = screenDip.Y - _downScreenDip.Y;

        if (!_dragging)
        {
            if (Math.Sqrt(dxDip * dxDip + dyDip * dyDip) < DragThresholdDip) return;

            _dragging = true;
            if (!_expanded)
            {
                ExpandToFullScreen();
                UpdateSnapDotsLayout();
                ShowDragChrome(visible: true);
                FadeInElement(SnapDotsLayer, 0.9, 120);
                FadeInElement(PositionBadge, 1.0, 120);
            }
        }

        var rawPhysX = _startPhysX + (int)Math.Round(dxDip * _dpiX);
        var rawPhysY = _startPhysY + (int)Math.Round(dyDip * _dpiY);
        var maxX = Math.Max(0, _screenPhysW - _overlayPhysW);
        var maxY = Math.Max(0, _screenPhysH - _overlayPhysH);
        var clampedX = Math.Clamp(rawPhysX, 0, maxX);
        var clampedY = Math.Clamp(rawPhysY, 0, maxY);

        RtssOverlayAlignment? snapped = null;
        if (RtssOverlayMetrics.TrySnapToAlignment(
            _level, clampedX, clampedY, _screenPhysW, _screenPhysH,
            out var snapAlign, out var snapX, out var snapY))
        {
            clampedX = snapX;
            clampedY = snapY;
            snapped = snapAlign;
        }

        _currentPhysX = clampedX;
        _currentPhysY = clampedY;

        PositionOverlayBox(clampedX, clampedY, snapped);
        UpdateBadge(clampedX, clampedY, snapped ?? RtssOverlayAlignment.Custom);
    }

    private void EndDrag(bool commit)
    {
        var wasDragging = _dragging;
        _pointerDown = false;
        _dragging = false;
        _activeTouchId = null;

        if (!wasDragging) return;

        if (commit && _level != RtssOverlayLevel.Off)
        {
            RtssOverlayPlacement placement;
            if (RtssOverlayMetrics.TrySnapToAlignment(
                _level, _currentPhysX, _currentPhysY, _screenPhysW, _screenPhysH,
                out var snapAlign, out var snapX, out var snapY))
            {
                _currentPhysX = snapX;
                _currentPhysY = snapY;
                placement = new RtssOverlayPlacement(snapAlign);
            }
            else
            {
                placement = RtssOverlayMetrics.CreateCustomPlacement(
                    _level, _currentPhysX, _currentPhysY, _screenPhysW, _screenPhysH);
            }

            _service.CommitDragPlacement(_level, placement);
            PositionOverlayBox(_currentPhysX, _currentPhysY, placement.Alignment == RtssOverlayAlignment.Custom ? null : placement.Alignment);
            UpdateBadge(_currentPhysX, _currentPhysY, placement.Alignment);
        }
        else
        {
            _service.CancelDrag();
        }

        if (!_interactive)
        {
            HideElementImmediate(SnapDotsLayer);
            HideElementImmediate(PositionBadge);

            if (commit && _level != RtssOverlayLevel.Off
                && (_currentPhysX != _startPhysX || _currentPhysY != _startPhysY))
            {
                BeginHandoff(_currentPhysX, _currentPhysY, _overlayPhysW, _overlayPhysH);
                return;
            }

            ShowDragChrome(visible: false, immediate: true);
            ApplyStandbyWindowBounds();
            DragEnded?.Invoke();
        }
    }

    private void BeginHandoff(int x, int y, int w, int h)
    {
        CancelHandoff();
        _awaitingHandoff = true;
        _handoffConfirmedTicks = 0;
        _handoffStartedMs = Environment.TickCount64;
        _handoffX = x;
        _handoffY = y;
        _handoffW = w;
        _handoffH = h;

        // Make the window click-through right away so the game receives input immediately, without
        // moving/resizing the HWND while BoxChrome is still visible (which would glitch for a frame).
        SetClickThrough(true);
        _handoffTimer.Start();
    }

    private void OnHandoffTick(object? sender, EventArgs e)
    {
        if (!_awaitingHandoff)
        {
            _handoffTimer.Stop();
            return;
        }

        if (Environment.TickCount64 - _handoffStartedMs >= HandoffTimeoutMs)
        {
            CompleteHandoff();
            return;
        }

        // Wait until OverlayEditor's 1 s AIDA64 poll publishes the new <P=x,y> into shared memory,
        // plus two 16 ms frames (~32 ms) so the game has presented the moved overlay underneath.
        if (RtssSharedMemory.IsOverlayEditorAtPosition(_handoffX, _handoffY, _handoffW, _handoffH))
        {
            _handoffConfirmedTicks++;
            if (_handoffConfirmedTicks >= 2)
                CompleteHandoff();
        }
        else
        {
            _handoffConfirmedTicks = 0;
        }
    }

    private void CompleteHandoff()
    {
        CancelHandoff();
        if (_interactive) return;

        ShowDragChrome(visible: false, immediate: true);
        ApplyStandbyWindowBounds();
        DragEnded?.Invoke();
    }

    private void CancelHandoff()
    {
        _handoffTimer.Stop();
        if (_awaitingHandoff)
        {
            _awaitingHandoff = false;
            SetClickThrough(false);
        }
        _handoffConfirmedTicks = 0;
    }

    private void ApplyStandbyWindowBounds()
    {
        _expanded = false;

        var boxW = _overlayPhysW / _dpiX;
        var boxH = _overlayPhysH / _dpiY;
        var hitW = Math.Max(boxW, MinStandbyTouchDip);
        var hitH = Math.Max(boxH, MinStandbyTouchDip);
        var padX = (hitW - boxW) / 2.0;
        var padY = (hitH - boxH) / 2.0;

        Left = Math.Max(_viewLeftDip, _viewLeftDip + _currentPhysX / _dpiX - padX);
        Top = Math.Max(_viewTopDip, _viewTopDip + _currentPhysY / _dpiY - padY);
        Width = hitW;
        Height = hitH;

        OverlayBox.Width = hitW;
        OverlayBox.Height = hitH;
        BoxTranslate.X = 0;
        BoxTranslate.Y = 0;
    }

    private void ExpandToFullScreen()
    {
        _expanded = true;

        Left = _viewLeftDip;
        Top = _viewTopDip;
        Width = _screenPhysW / _dpiX;
        Height = _screenPhysH / _dpiY;

        OverlayBox.Width = _overlayPhysW / _dpiX;
        OverlayBox.Height = _overlayPhysH / _dpiY;
        BoxTranslate.X = _currentPhysX / _dpiX;
        BoxTranslate.Y = _currentPhysY / _dpiY;
    }

    private void PositionOverlayBox(int physX, int physY, RtssOverlayAlignment? snapped)
    {
        BoxTranslate.X = physX / _dpiX;
        BoxTranslate.Y = physY / _dpiY;

        for (var i = 0; i < _snapDots.Length; i++)
        {
            var isHit = snapped is { } s && (int)s == i;
            _snapDots[i].Background = isHit ? Accent.Brush : Brushes.White;
            _snapDots[i].Opacity = isHit ? 1.0 : 0.35;
        }
    }

    private void UpdateBadge(int physX, int physY, RtssOverlayAlignment alignment)
    {
        var text = alignment == RtssOverlayAlignment.Custom
            ? $"{Strings.RtssAlignCustom} · {physX}, {physY}"
            : RtssViewModel.FormatAlignment(alignment);

        if (!string.Equals(_lastBadgeText, text, StringComparison.Ordinal))
        {
            _lastBadgeText = text;
            BadgeText.Text = text;
            PositionBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _cachedBadgeW = PositionBadge.DesiredSize.Width;
            _cachedBadgeH = PositionBadge.DesiredSize.Height;
        }

        var badgeW = _cachedBadgeW;
        var badgeH = _cachedBadgeH;

        var boxX = physX / _dpiX;
        var boxY = physY / _dpiY;
        var boxW = _overlayPhysW / _dpiX;
        var boxH = _overlayPhysH / _dpiY;
        var screenW = _screenPhysW / _dpiX;
        var screenH = _screenPhysH / _dpiY;

        var bx = Math.Clamp(boxX + (boxW - badgeW) / 2.0, 12, Math.Max(12, screenW - badgeW - 12));
        var by = boxY + boxH + 10 + badgeH <= screenH - 72
            ? boxY + boxH + 10
            : Math.Max(12, boxY - badgeH - 10);

        BadgeTranslate.X = bx;
        BadgeTranslate.Y = by;
    }

    private void ShowDragChrome(bool visible, bool immediate = false)
    {
        OverlayBox.BorderBrush = visible ? Accent.Brush : Brushes.Transparent;
        if (visible)
            FadeInElement(BoxChrome, 1.0, 110);
        else if (immediate)
            HideElementImmediate(BoxChrome);
        else
            FadeOutAndCollapse(BoxChrome, 110);
    }

    private void BuildSnapDots()
    {
        SnapDotsLayer.Children.Clear();
        for (var i = 0; i < 9; i++)
        {
            var alignment = (RtssOverlayAlignment)i;
            var dot = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.White,
                Opacity = 0.35,
                Cursor = Cursors.Hand,
                Tag = alignment,
            };

            dot.MouseLeftButtonDown += (_, e) =>
            {
                if (!_interactive) return;
                e.Handled = true;
                SnapToAnchor(alignment);
            };
            dot.TouchDown += (_, e) =>
            {
                if (!_interactive) return;
                e.Handled = true;
                SnapToAnchor(alignment);
            };

            _snapDots[i] = dot;
            SnapDotsLayer.Children.Add(dot);
        }
    }

    private void SnapToAnchor(RtssOverlayAlignment alignment)
    {
        if (_level == RtssOverlayLevel.Off) return;
        var (ax, ay) = RtssOverlayMetrics.ResolveAnchor(_level, alignment, _screenPhysW, _screenPhysH);
        _currentPhysX = ax;
        _currentPhysY = ay;
        _service.CommitDragPlacement(_level, new RtssOverlayPlacement(alignment));
        PositionOverlayBox(ax, ay, alignment);
        UpdateBadge(ax, ay, alignment);
    }

    private void UpdateSnapDotsLayout()
    {
        if (_level == RtssOverlayLevel.Off) return;

        for (var i = 0; i < 9; i++)
        {
            var alignment = (RtssOverlayAlignment)i;
            var (ax, ay) = RtssOverlayMetrics.ResolveAnchor(_level, alignment, _screenPhysW, _screenPhysH);
            var centerX = (ax + _overlayPhysW / 2.0) / _dpiX;
            var centerY = (ay + _overlayPhysH / 2.0) / _dpiY;

            Canvas.SetLeft(_snapDots[i], centerX - 7);
            Canvas.SetTop(_snapDots[i], centerY - 7);
        }
    }

    private Point PointToScreenDip(Point clientDip) => new(Left + clientDip.X, Top + clientDip.Y);

    private void RefreshDpi()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _rawDpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        _rawDpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        _viewLeftDip = _viewportX / _rawDpiX;
        _viewTopDip = _viewportY / _rawDpiY;

        // Overlay coordinates are in the game's frame pixels, presented into the game's viewport rectangle:
        // a borderless game rendering 1280 wide into a 1920-wide screen puts 1.5 screen pixels under each frame pixel,
        // while a 1280-wide windowed game maps 1:1 into its own client area. Folding frame/viewport into the scale
        // keeps this window over the overlay as it is seen, with every position still in frame pixels.
        if (_screenPhysW > 0 && _screenPhysH > 0 && _viewportW > 0 && _viewportH > 0)
        {
            _dpiX = _rawDpiX * ((double)_screenPhysW / _viewportW);
            _dpiY = _rawDpiY * ((double)_screenPhysH / _viewportH);
        }
        else
        {
            _dpiX = _rawDpiX;
            _dpiY = _rawDpiY;
        }
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
    }

    private void SetClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var next = clickThrough ? (style | WS_EX_TRANSPARENT) : (style & ~WS_EX_TRANSPARENT);
        if (next != style)
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, next);
    }

    private void RaiseTopmostNoActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static void FadeInElement(UIElement element, double targetOpacity, int durationMs)
    {
        element.Visibility = Visibility.Visible;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static void FadeOutAndCollapse(UIElement element, int durationMs)
    {
        var animation = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 0;
            element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(OpacityProperty, animation);
    }

    private static void HideElementImmediate(UIElement element)
    {
        // Clear any HoldEnd animation clock first; in WPF, an active animation overrides local Opacity assignments.
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
        element.Visibility = Visibility.Collapsed;
    }
}
