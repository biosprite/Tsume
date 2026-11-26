using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tsume.App.Language;
using Tsume.App.Theme;
using Tsume.Core.Cooling;

namespace Tsume.App.Controls;

/// <summary>
/// A fan curve on an L-shaped chart: fan speed up the left, labeled in rough RPM, and the curve's temperature
/// points along the bottom, one handle each. Touch or click anywhere in a point's column and drag up or down;
/// with a controller, A starts editing, left and right pick a point, up and down set its speed, and A or B stop.
/// Every change goes through <see cref="FanCurveLimits.Clamp"/>, so what is drawn is what the helper writes.
/// </summary>
public sealed class FanCurveEditor : Control
{
    // Room around the plot for the axis labels, and above it for a handle at full speed.
    private const double LabelWidth = 30;
    private const double LabelHeight = 20;
    private const double Headroom = 12;
    private const double RightEdge = 4;

    private const double HandleRadius = 7;
    private const double ChosenRadius = 9;

    /// <summary>A finger must travel this far before a touch becomes a drag, so a tap only picks the point.</summary>
    private const double DragThreshold = 4;

    /// <summary>A direction pressed again within this many milliseconds is being held, not tapped.</summary>
    private const int HeldWithin = 160;

    private readonly Plot _plot;

    /// <summary>
    /// The speeds when the chosen point was picked up. A move starts from these, so lowering a point again lets
    /// the hotter points it pushed up fall back.
    /// </summary>
    private int[]? _base;

    private bool _dragging;
    private TouchDevice? _finger;

    /// <summary>True while the control itself puts a new curve in, so it is not taken for one replaced from outside.</summary>
    private bool _moving;
    private double _grabY;
    private int _grabSpeed;
    private bool _moved;

    /// <summary>True after a touch or a click, so the "Press A to edit" prompt is only shown to the controller.</summary>
    private bool _pointed;

    private Key _lastKey;
    private long _lastKeyAt;
    private int _streak;

    public FanCurveEditor()
    {
        Focusable = true;
        _plot = new Plot(this);

        // Without this the page's touch-scrolling list would take a vertical drag for a scroll.
        IsManipulationEnabled = true;

        // The plot draws with the scheme's brushes, which are replaced (not recoloured) when the scheme changes.
        Loaded += (_, _) => { Colours.Changed += Redraw; Redraw(); };
        Unloaded += (_, _) => Colours.Changed -= Redraw;
    }

    public static readonly DependencyProperty LimitsProperty = Register(nameof(Limits), typeof(FanCurveLimits), null);
    public static readonly DependencyProperty TemperaturesProperty = Register(nameof(Temperatures), typeof(IReadOnlyList<int>), Array.Empty<int>());
    public static readonly DependencyProperty StockProperty = Register(nameof(Stock), typeof(IReadOnlyList<int>), null);
    public static readonly DependencyProperty IsReadOnlyProperty = Register(nameof(IsReadOnly), typeof(bool), false);
    public static readonly DependencyProperty CurrentCelsiusProperty = Register(nameof(CurrentCelsius), typeof(int?), null);
    public static readonly DependencyProperty CurrentRpmProperty = Register(nameof(CurrentRpm), typeof(int?), null);
    public static readonly DependencyProperty WarnCelsiusProperty = Register(nameof(WarnCelsius), typeof(int), 75);
    public static readonly DependencyProperty HotCelsiusProperty = Register(nameof(HotCelsius), typeof(int), 85);

    public static readonly DependencyProperty SpeedsProperty = DependencyProperty.Register(
        nameof(Speeds), typeof(IReadOnlyList<int>), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(Array.Empty<int>(), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((FanCurveEditor)d).OnPicked()));

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((FanCurveEditor)d).OnPicked()));

    public static readonly DependencyProperty IsDraggingProperty = DependencyProperty.Register(
        nameof(IsDragging), typeof(bool), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private static readonly IReadOnlyList<string> EmptyHints = [];
    private static readonly IReadOnlyList<string> PromptHints = [Strings.FanEditPrompt];
    private static readonly IReadOnlyList<string> EditHints = [Strings.FanEditSpeed, Strings.FanEditPoint, Strings.FanEditDone];

    private static readonly DependencyPropertyKey PointTemperatureKey = DependencyProperty.RegisterReadOnly(
        nameof(PointTemperature), typeof(string), typeof(FanCurveEditor), new PropertyMetadata(""));

    private static readonly DependencyPropertyKey PointPercentKey = DependencyProperty.RegisterReadOnly(
        nameof(PointPercent), typeof(string), typeof(FanCurveEditor), new PropertyMetadata(""));

    private static readonly DependencyPropertyKey PointRpmKey = DependencyProperty.RegisterReadOnly(
        nameof(PointRpm), typeof(string), typeof(FanCurveEditor), new PropertyMetadata(""));

    private static readonly DependencyPropertyKey LiveTemperatureKey = DependencyProperty.RegisterReadOnly(
        nameof(LiveTemperature), typeof(string), typeof(FanCurveEditor), new PropertyMetadata(""));

    private static readonly DependencyPropertyKey LiveRpmKey = DependencyProperty.RegisterReadOnly(
        nameof(LiveRpm), typeof(string), typeof(FanCurveEditor), new PropertyMetadata(""));

    private static readonly DependencyPropertyKey HintItemsKey = DependencyProperty.RegisterReadOnly(
        nameof(HintItems), typeof(IReadOnlyList<string>), typeof(FanCurveEditor), new PropertyMetadata(EmptyHints));

    private static readonly DependencyPropertyKey HasHintKey = DependencyProperty.RegisterReadOnly(
        nameof(HasHint), typeof(bool), typeof(FanCurveEditor), new PropertyMetadata(false));

    public static readonly DependencyProperty PointTemperatureProperty = PointTemperatureKey.DependencyProperty;
    public static readonly DependencyProperty PointPercentProperty = PointPercentKey.DependencyProperty;
    public static readonly DependencyProperty PointRpmProperty = PointRpmKey.DependencyProperty;
    public static readonly DependencyProperty LiveTemperatureProperty = LiveTemperatureKey.DependencyProperty;
    public static readonly DependencyProperty LiveRpmProperty = LiveRpmKey.DependencyProperty;
    public static readonly DependencyProperty HintItemsProperty = HintItemsKey.DependencyProperty;
    public static readonly DependencyProperty HasHintProperty = HasHintKey.DependencyProperty;

    /// <summary>What the curve may hold: the top of the graph, the RPM scale, and the safety floors.</summary>
    public FanCurveLimits? Limits { get => (FanCurveLimits?)GetValue(LimitsProperty); set => SetValue(LimitsProperty, value); }

    /// <summary>The curve's temperature points in °C, coolest first: the firmware's own on Auto, or the chipset's custom scale on Custom and Max.</summary>
    public IReadOnlyList<int> Temperatures { get => (IReadOnlyList<int>)GetValue(TemperaturesProperty); set => SetValue(TemperaturesProperty, value); }

    /// <summary>One speed per temperature point, in <see cref="FanCurveLimits.MaxSpeed"/> units. Replaced, never changed in place.</summary>
    public IReadOnlyList<int> Speeds { get => (IReadOnlyList<int>)GetValue(SpeedsProperty); set => SetValue(SpeedsProperty, value); }

    /// <summary>The firmware's own speeds, drawn faintly for reference; also lowers the floors as <see cref="FanCurveLimits.Clamp"/> does.</summary>
    public IReadOnlyList<int>? Stock { get => (IReadOnlyList<int>?)GetValue(StockProperty); set => SetValue(StockProperty, value); }

    /// <summary>True to show a curve without letting it be changed: the firmware's own on Auto, full speed on Max.</summary>
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    /// <summary>The processor's temperature now, marked on the temperature axis; null when there is no reading.</summary>
    public int? CurrentCelsius { get => (int?)GetValue(CurrentCelsiusProperty); set => SetValue(CurrentCelsiusProperty, value); }

    /// <summary>The fan's speed now, a dot on the current temperature's line; null when there is no reading.</summary>
    public int? CurrentRpm { get => (int?)GetValue(CurrentRpmProperty); set => SetValue(CurrentRpmProperty, value); }

    /// <summary>From here the temperature axis is tinted amber.</summary>
    public int WarnCelsius { get => (int)GetValue(WarnCelsiusProperty); set => SetValue(WarnCelsiusProperty, value); }

    /// <summary>From here the temperature axis is tinted red.</summary>
    public int HotCelsius { get => (int)GetValue(HotCelsiusProperty); set => SetValue(HotCelsiusProperty, value); }

    /// <summary>The point whose value is read out, and which the controller moves.</summary>
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    /// <summary>True while the controller is editing: up and down set the chosen point's speed instead of moving focus.</summary>
    public bool IsEditing { get => (bool)GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    /// <summary>True while a finger or the mouse holds a point. A curve is worth sending once this goes false.</summary>
    public bool IsDragging { get => (bool)GetValue(IsDraggingProperty); set => SetValue(IsDraggingProperty, value); }

    /// <summary>The chosen point's temperature, e.g. "70 °C".</summary>
    public string PointTemperature => (string)GetValue(PointTemperatureProperty);

    /// <summary>The chosen point's speed percentage, e.g. "62%".</summary>
    public string PointPercent => (string)GetValue(PointPercentProperty);

    /// <summary>The chosen point's rough speed, e.g. "~3,400 rpm".</summary>
    public string PointRpm => (string)GetValue(PointRpmProperty);

    /// <summary>The live CPU temperature, e.g. "61 °C".</summary>
    public string LiveTemperature => (string)GetValue(LiveTemperatureProperty);

    /// <summary>The live fan speed, e.g. "3,200 rpm".</summary>
    public string LiveRpm => (string)GetValue(LiveRpmProperty);

    /// <summary>Controller help chips; empty unless the controller is on the curve.</summary>
    public IReadOnlyList<string> HintItems => (IReadOnlyList<string>)GetValue(HintItemsProperty);

    /// <summary>True when at least one controller help chip is shown.</summary>
    public bool HasHint => (bool)GetValue(HasHintProperty);

    private static DependencyProperty Register(string name, Type type, object? initial) => DependencyProperty.Register(
        name, type, typeof(FanCurveEditor), new PropertyMetadata(initial, OnChanged));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (FanCurveEditor)d;

        // A different set of points: start on the one nearest the temperature now, which is the one that matters.
        if (e.Property == TemperaturesProperty
            && (((IReadOnlyList<int>?)e.OldValue)?.Count != editor.Temperatures.Count || editor.SelectedIndex < 0))
            editor.SetCurrentValue(SelectedIndexProperty, editor.NearestNow());
        if (e.Property == IsReadOnlyProperty && (bool)e.NewValue) editor.SetCurrentValue(IsEditingProperty, false);
        if (e.Property == SpeedsProperty && !editor._moving) editor.Rebase();

        editor.Redraw();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Plot") is Border host) host.Child = _plot;
        Redraw();
    }

    /// <summary>The curve can be shown: there are limits and a speed for every point.</summary>
    private bool HasCurve => Limits is { MaxSpeed: > 0 } && Temperatures.Count > 0 && Speeds.Count == Temperatures.Count;

    private bool CanEdit => HasCurve && !IsReadOnly && IsEnabled;

    private int Chosen => HasCurve ? Math.Clamp(SelectedIndex, 0, Temperatures.Count - 1) : -1;

    private void OnPicked()
    {
        _base = null;   // a new point, or a new edit: the next move starts from the curve as it is now
        Redraw();
    }

    private void Redraw()
    {
        if (Chosen >= 0 && Limits is { } limits)
        {
            var speed = Speeds[Chosen];
            var rpm = (int)Math.Round(limits.RpmAt(speed) / 100.0) * 100;
            SetValue(PointTemperatureKey, Strings.Temperature(Temperatures[Chosen]));
            SetValue(PointPercentKey, Strings.FanPercent(limits.PercentAt(speed)));
            SetValue(PointRpmKey, Strings.FanApproxRpm(rpm));
        }
        else
        {
            SetValue(PointTemperatureKey, "");
            SetValue(PointPercentKey, "");
            SetValue(PointRpmKey, "");
        }

        if (CurrentCelsius is { } celsius)
        {
            SetValue(LiveTemperatureKey, Strings.Temperature(celsius));
            SetValue(LiveRpmKey, CurrentRpm is { } rpm ? Strings.FanSpeed(rpm) : "");
        }
        else
        {
            SetValue(LiveTemperatureKey, "");
            SetValue(LiveRpmKey, "");
        }

        var hints = IsEditing ? EditHints
            : IsKeyboardFocused && !_pointed && CanEdit ? PromptHints
            : EmptyHints;
        if (!ReferenceEquals(HintItems, hints)) SetValue(HintItemsKey, hints);
        if (HasHint != (hints.Count > 0)) SetValue(HasHintKey, hints.Count > 0);

        _plot.InvalidateVisual();
    }

    private int NearestNow()
    {
        var temperatures = Temperatures;
        if (temperatures.Count == 0) return -1;
        if (CurrentCelsius is not { } now) return 0;

        var nearest = 0;
        for (var i = 1; i < temperatures.Count; i++)
            if (Math.Abs(temperatures[i] - now) < Math.Abs(temperatures[nearest] - now)) nearest = i;
        return nearest;
    }

    // ---- Changing the curve -------------------------------------------------------------------------------

    /// <summary>
    /// Puts one point at <paramref name="speed"/> and makes the curve legal again: hotter points are pushed up
    /// with it, and it cannot go below its floor or below the cooler point before it (it stops there).
    /// </summary>
    private void Move(int point, int speed)
    {
        if (!CanEdit || Limits is not { } limits || point < 0 || point >= Speeds.Count) return;

        // The curve may have been replaced by one with a different number of points since it was picked up.
        if (_base is not null && _base.Length != Speeds.Count) _base = null;
        _base ??= [.. Speeds];
        var wanted = (int[])_base.Clone();
        wanted[point] = Math.Clamp(speed, 0, limits.MaxSpeed);

        var legal = limits.Clamp(Temperatures, wanted, Stock);
        if (legal.SequenceEqual(Speeds)) return;

        _moving = true;
        try { SetCurrentValue(SpeedsProperty, legal); }
        finally { _moving = false; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!HasCurve) return;

        _pointed = false;

        switch (e.Key)
        {
            case Key.Space or Key.Enter when CanEdit:
                SetCurrentValue(IsEditingProperty, !IsEditing);
                break;

            case Key.Left or Key.Right:
                var next = Math.Clamp(Chosen + (e.Key == Key.Left ? -1 : +1), 0, Temperatures.Count - 1);
                SetCurrentValue(SelectedIndexProperty, next);
                break;

            case Key.Up or Key.Down when IsEditing:
                Step(e.Key);
                break;

            default:
                return;   // up and down move focus on, as anywhere else on the page
        }

        Redraw();
        e.Handled = true;
    }

    /// <summary>A tap moves about 2 %; while the direction is held the steps grow, as on the level slider.</summary>
    private void Step(Key key)
    {
        if (Limits is not { } limits) return;

        var now = Environment.TickCount64;
        _streak = key == _lastKey && now - _lastKeyAt < HeldWithin ? _streak + 1 : 0;
        _lastKey = key;
        _lastKeyAt = now;

        var unit = Math.Max(1, (int)Math.Round(limits.MaxSpeed / 50.0));
        var step = unit * Math.Min(1 + _streak / 2, 5);
        Move(Chosen, Speeds[Chosen] + (key == Key.Up ? step : -step));
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetCurrentValue(IsEditingProperty, false);
        Redraw();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Redraw();
    }

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        Redraw();
    }

    // ---- Touch and mouse ---------------------------------------------------------------------------------

    // The list's panning would otherwise claim the touch; cancelling here keeps it for the curve.
    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        e.Cancel();
        e.Handled = true;
    }

    // Touch is handled directly, as in LevelSlider: promoted mouse events arrive late inside a touch-scrolling list.
    protected override void OnTouchDown(TouchEventArgs e)
    {
        if (_finger is not null) return;

        _finger = e.TouchDevice;
        CaptureTouch(_finger);
        Grab(e.GetTouchPoint(_plot).Position);
        e.Handled = true;
    }

    protected override void OnTouchMove(TouchEventArgs e)
    {
        if (e.TouchDevice != _finger) return;

        Drag(e.GetTouchPoint(_plot).Position);
        e.Handled = true;
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        if (e.TouchDevice != _finger) return;

        // Let go here too: if the capture never took, no lost-capture event follows and the drag would stay on.
        _finger = null;
        ReleaseTouchCapture(e.TouchDevice);
        LetGo();
        e.Handled = true;
    }

    protected override void OnLostTouchCapture(TouchEventArgs e)
    {
        if (e.TouchDevice != _finger) return;

        _finger = null;
        LetGo();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_finger is not null || e.StylusDevice is not null) return;   // a touch, already handled as one

        CaptureMouse();
        Grab(e.GetPosition(_plot));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging && _finger is null && e.StylusDevice is null) Drag(e.GetPosition(_plot));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_finger is null)
        {
            ReleaseMouseCapture();
            LetGo();   // also when the capture never took
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_finger is null) LetGo();
    }

    /// <summary>Anywhere in a point's column picks that point up, so the targets are as wide as the columns.</summary>
    private void Grab(Point at)
    {
        Focus();
        _pointed = true;
        if (!HasCurve) return;

        var layout = new Layout(this, _plot.RenderSize);
        var point = layout.ColumnAt(at.X);
        SetCurrentValue(SelectedIndexProperty, point);
        SetCurrentValue(IsEditingProperty, false);

        _dragging = true;
        _base = null;
        _grabY = at.Y;
        _grabSpeed = Speeds[point];
        _moved = false;
        if (CanEdit) SetCurrentValue(IsDraggingProperty, true);
        Redraw();
    }

    /// <summary>The point follows the finger's movement, not its position, so grabbing it never makes it jump.</summary>
    private void Drag(Point at)
    {
        if (!HasCurve)
        {
            LetGo();   // the curve went away under the finger
            return;
        }
        if (!_dragging || !CanEdit || Limits is not { } limits) return;

        var rise = _grabY - at.Y;
        if (!_moved && Math.Abs(rise) < DragThreshold) return;
        _moved = true;

        var layout = new Layout(this, _plot.RenderSize);
        if (layout.Height <= 0) return;

        Move(Chosen, _grabSpeed + (int)Math.Round(rise / layout.Height * limits.MaxSpeed));
    }

    /// <summary>
    /// The curve was replaced from outside (another profile picked, say, perhaps by a second finger): what was picked
    /// up belongs to the old curve, so a drag ends and the next move starts from the new one.
    /// </summary>
    private void Rebase()
    {
        _base = null;
        if (!_dragging) return;

        if (_finger is { } finger)
        {
            _finger = null;
            ReleaseTouchCapture(finger);
        }
        else ReleaseMouseCapture();
        LetGo();
    }

    private void LetGo()
    {
        if (!_dragging) return;

        _dragging = false;
        _base = null;
        SetCurrentValue(IsDraggingProperty, false);
        Redraw();
    }

    // ---- Drawing -----------------------------------------------------------------------------------------

    /// <summary>Where everything sits inside the plot: columns across, speed up.</summary>
    private readonly struct Layout(FanCurveEditor editor, Size size)
    {
        public readonly double Left = LabelWidth;
        public readonly double Right = Math.Max(LabelWidth + 1, size.Width - RightEdge);
        public readonly double Top = Headroom;
        public readonly double Bottom = Math.Max(Headroom + 1, size.Height - LabelHeight);

        private readonly IReadOnlyList<int> _temperatures = editor.Temperatures;
        private readonly int _maxSpeed = editor.Limits?.MaxSpeed ?? 1;

        public double Height => Bottom - Top;
        public double Column => (Right - Left) / Math.Max(1, _temperatures.Count);

        public double X(int point) => Left + (point + 0.5) * Column;

        public double Y(double speed) => Bottom - Math.Clamp(speed / _maxSpeed, 0, 1) * Height;

        public int ColumnAt(double x) => Math.Clamp((int)Math.Floor((x - Left) / Column), 0, _temperatures.Count - 1);

        /// <summary>A temperature's place across, between the two points either side of it; the columns are evenly spaced.</summary>
        public double XAt(double celsius)
        {
            var t = _temperatures;
            if (t.Count == 1) return X(0);

            for (var i = 0; i < t.Count - 1; i++)
            {
                if (celsius > t[i + 1] && i < t.Count - 2) continue;

                var span = Math.Max(1, t[i + 1] - t[i]);
                return Math.Clamp(X(i) + (celsius - t[i]) / span * Column, Left, Right);
            }

            return Right;
        }
    }

    private void Draw(DrawingContext dc, Size size)
    {
        // Hit-testable everywhere, so a touch between lines still lands on the curve.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (!HasCurve || Limits is not { } limits) return;

        var layout = new Layout(this, size);
        var temperatures = Temperatures;
        var speeds = Speeds;
        var count = temperatures.Count;
        var editable = CanEdit;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var text = Find("Text");
        var muted = Find("Text.Muted");
        var faint = Find("Text.Faint");
        var divider = Find("Surface.Divider");
        var surface = Find("Surface.Hover");
        var panel = Find("Panel.Fill");

        // The chosen column, while the curve has the focus or a finger.
        if ((IsKeyboardFocused || _dragging) && Chosen >= 0)
        {
            var x = layout.X(Chosen);
            dc.DrawRoundedRectangle(surface, null,
                new Rect(x - layout.Column / 2 + 2, layout.Top - Headroom + 2, layout.Column - 4, layout.Bottom - layout.Top + Headroom - 2), 8, 8);
        }

        // Speed: grid lines at round RPM figures, labeled "2k", "4k"…
        var tick = RpmTick(limits.MaxRpm);
        var gridPen = new Pen(divider, 1);
        for (var rpm = tick; rpm <= limits.MaxRpm; rpm += tick)
        {
            var y = layout.Bottom - (double)rpm / limits.MaxRpm * layout.Height;
            dc.DrawLine(gridPen, new Point(layout.Left, y), new Point(layout.Right, y));

            var label = Words(Strings.FanRpmTick((rpm / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)), 11, muted, dip);
            dc.DrawText(label, new Point(layout.Left - 6 - label.Width, y - label.Height / 2));
        }

        // The floors: speeds the helper will not go below, shaded under each point.
        if (editable)
        {
            var hatch = Hatch(muted);
            var edge = new Pen(faint, 1) { DashStyle = new DashStyle([3, 3], 0) };
            for (var point = 0; point < count; point++)
            {
                var floor = limits.MinSpeedAt(point, temperatures, Stock);
                if (floor <= 0) continue;

                var x = layout.X(point) - layout.Column / 2;
                var y = layout.Y(floor);
                dc.DrawRectangle(hatch, null, new Rect(x, y, layout.Column, layout.Bottom - y));
                dc.DrawLine(edge, new Point(x, y), new Point(x + layout.Column, y));
            }
        }

        // The firmware's own curve, faintly, for reference.
        if (editable && Stock is { } stock && stock.Count == count && !stock.SequenceEqual(speeds))
        {
            var stockPen = new Pen(faint, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
            dc.DrawGeometry(null, stockPen, Line(layout, stock, closed: false));
        }

        // The curve: shaded beneath, then the line itself.
        var accent = Accent.Brush;
        dc.DrawGeometry(editable ? Accent.Tint : Find("Surface"), null, Line(layout, speeds, closed: true));
        dc.DrawGeometry(null, new Pen(editable ? accent : muted, editable ? 2.5 : 2) { LineJoin = PenLineJoin.Round }, Line(layout, speeds, closed: false));

        // The L: speed up the left, temperature along the bottom, tinted where the chip runs warm and hot.
        var axisPen = new Pen(faint, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawLine(axisPen, new Point(layout.Left, layout.Top - Headroom / 2), new Point(layout.Left, layout.Bottom));
        dc.DrawLine(axisPen, new Point(layout.Left, layout.Bottom), new Point(layout.Right, layout.Bottom));

        var warm = Find("Signal.Warn");
        var hot = Find("Signal.Danger");
        var warmFrom = Math.Max(layout.Left, layout.XAt(WarnCelsius));
        var hotFrom = Math.Max(layout.Left, layout.XAt(HotCelsius));
        if (hotFrom > warmFrom) dc.DrawLine(new Pen(warm, 2.5), new Point(warmFrom, layout.Bottom), new Point(hotFrom, layout.Bottom));
        if (layout.Right > hotFrom) dc.DrawLine(new Pen(hot, 2.5), new Point(hotFrom, layout.Bottom), new Point(layout.Right, layout.Bottom));

        for (var point = 0; point < count; point++)
        {
            var celsius = temperatures[point];
            var colour = celsius >= HotCelsius ? hot : celsius >= WarnCelsius ? warm : point == Chosen ? text : muted;
            var label = Words(Strings.FanDegreesTick(celsius), 11, colour, dip, bold: point == Chosen);
            dc.DrawText(label, new Point(layout.X(point) - label.Width / 2, layout.Bottom + 4));
        }

        // Now: a line up from the temperature, and a dot at the fan's speed on it.
        if (CurrentCelsius is { } now)
        {
            var x = layout.XAt(now);
            var nowPen = new Pen(text, 1) { DashStyle = new DashStyle([2, 3], 0) };
            dc.PushOpacity(0.55);
            dc.DrawLine(nowPen, new Point(x, layout.Top), new Point(x, layout.Bottom));
            dc.Pop();

            if (CurrentRpm is { } rpm && limits.MaxRpm > 0)
            {
                var y = Math.Max(layout.Top, layout.Bottom - (double)rpm / limits.MaxRpm * layout.Height);
                dc.DrawEllipse(text, new Pen(panel, 2), new Point(x, y), 4.5, 4.5);
            }
        }

        // The handles: solid on a curve that can be changed, small dots on one that cannot.
        var ring = new Pen(panel, 2.5);
        for (var point = 0; point < count; point++)
        {
            var centre = new Point(layout.X(point), layout.Y(speeds[point]));
            var chosen = point == Chosen;

            if (!editable)
            {
                dc.DrawEllipse(chosen ? text : muted, null, centre, chosen ? 4.5 : 3.5, chosen ? 4.5 : 3.5);
                continue;
            }

            if (chosen && (IsEditing || _dragging)) dc.DrawEllipse(Accent.Wash, null, centre, 17, 17);
            var radius = chosen ? ChosenRadius : HandleRadius;
            dc.DrawEllipse(accent, ring, centre, radius, radius);
            if (chosen) dc.DrawEllipse(Colours.TextBrush, null, centre, 3, 3);
        }
    }

    /// <summary>The curve through every point, run flat out to both edges; closed down to the axis for shading.</summary>
    private static StreamGeometry Line(Layout layout, IReadOnlyList<int> speeds, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var first = new Point(layout.Left, layout.Y(speeds[0]));
            g.BeginFigure(closed ? new Point(layout.Left, layout.Bottom) : first, closed, closed);
            if (closed) g.LineTo(first, false, false);

            for (var point = 0; point < speeds.Count; point++)
                g.LineTo(new Point(layout.X(point), layout.Y(speeds[point])), true, true);

            g.LineTo(new Point(layout.Right, layout.Y(speeds[^1])), true, true);
            if (closed) g.LineTo(new Point(layout.Right, layout.Bottom), false, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The gap between RPM grid lines: a round figure giving two to four lines.</summary>
    private static int RpmTick(int maxRpm)
    {
        foreach (var step in (int[])[500, 1000, 2000, 2500, 5000])
            if (maxRpm / step <= 4) return step;
        return 10000;
    }

    /// <summary>Thin diagonal lines: the floors read as out of bounds without hiding the grid beneath.</summary>
    private static DrawingBrush Hatch(Brush colour)
    {
        var line = new GeometryDrawing(null, new Pen(colour, 1),
            new LineGeometry(new Point(0, 6), new Point(6, 0)));

        return new DrawingBrush(line)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 6),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6),
            ViewboxUnits = BrushMappingMode.Absolute,
            Opacity = 0.35,
        };
    }

    private FormattedText Words(string words, double size, Brush colour, double dip, bool bold = false) => new(
        words, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface((FontFamily)FindResource("Font.Words"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
        size, colour, dip);

    private Brush Find(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>The surface the chart is drawn on; sits inside the template so the readout and hint can lay out around it.</summary>
    private sealed class Plot(FanCurveEditor editor) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => editor.Draw(dc, RenderSize);
    }
}
