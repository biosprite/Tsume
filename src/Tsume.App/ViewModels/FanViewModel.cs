using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Core.Cooling;

namespace Tsume.App.ViewModels;

/// <summary>
/// The fan screen, opened from the Fans tile: the fan mode, and a custom curve drawn on a chart. The user's own mode is
/// Auto or Custom; Max is a boost on top of it. The fan state itself lives in <see cref="PerformanceViewModel"/>,
/// which sees every reply from the helper; this screen only holds the curve while it is being changed.
/// </summary>
public sealed class FanViewModel : LayerViewModel
{
    private readonly PerformanceViewModel _performance;

    /// <summary>Waits for the controller to stop stepping a point before sending the curve.</summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>The mode just picked, shown until the helper has answered; null to show the one in force.</summary>
    private FanMode? _chosen;

    /// <summary>The modes in the selector, in its order.</summary>
    private IReadOnlyList<FanMode> _modes = [];

    /// <summary>True while the curve is being put into the chart, so the change is not taken for the user's.</summary>
    private bool _showing;

    /// <summary>True when the user has changed the curve since it was last sent.</summary>
    private bool _changed;

    /// <summary>The profile the unsent change was made on, so it is never saved into another one.</summary>
    private int _editProfile;

    public FanViewModel(PerformanceViewModel performance)
    {
        _performance = performance;

        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (!IsDragging) Keep();   // a finger still on it sends when it lifts
        };
        Reset = new Command(BackToStock);
        ReselectMode = new Command(() => { if (Mode == FanMode.Custom) Resend(); });
        ReselectProfile = new Command(Resend);

        performance.PropertyChanged += OnPerformanceChanged;
        LayOut();
    }

    private void OnPerformanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PerformanceViewModel.Device):
            case nameof(PerformanceViewModel.IsHelperOnline):
                LayOut();
                Sync();
                AskIfUnknown();
                break;

            case nameof(PerformanceViewModel.IsFanBusy) when !_performance.IsFanBusy:
                _chosen = null;   // answered: show what the fans are really doing
                Sync();
                break;

            case nameof(PerformanceViewModel.Fan):
                if (ListsCustom != _modes.Contains(FanMode.Custom)) LayOut();
                Sync();
                break;

            case nameof(PerformanceViewModel.FanCurve):
            case nameof(PerformanceViewModel.FanCurveAsked):
                Sync();
                break;

            case nameof(PerformanceViewModel.FanProfile):
                Notify(nameof(ProfileIndex));
                break;

            case nameof(PerformanceViewModel.CpuCelsius):
                Notify(nameof(CpuCelsius));
                break;

            case nameof(PerformanceViewModel.FanRpm):
                Notify(nameof(FanRpm));
                break;
        }
    }

    /// <summary>False on a machine whose fans cannot be controlled, hiding the empty mode selector.</summary>
    public bool ShowsModes => Modes.Count > 0;

    /// <summary>Segment labels: the fan modes this machine offers.</summary>
    public IReadOnlyList<string> Modes { get; private set => Set(ref field, value); } = [];

    /// <summary>The mode shown as chosen: the one just picked, or the one in force.</summary>
    public FanMode Mode => _chosen ?? _performance.Fan;

    /// <summary>The mode's place in the selector; bound to the segmented control.</summary>
    public int ModeIndex
    {
        get => Math.Max(0, IndexOf(Mode));
        set
        {
            if (value >= 0 && value < _modes.Count) Choose(_modes[value]);
        }
    }

    /// <summary>Custom, while there is no curve to start it from.</summary>
    public IReadOnlySet<int>? Unavailable => !CurveShown && IndexOf(FanMode.Custom) is >= 0 and var custom ? new HashSet<int> { custom } : null;

    /// <summary>Segment labels for the custom profiles: "Profile 1"…</summary>
    public IReadOnlyList<string> Profiles { get; } =
        [.. Enumerable.Range(1, SavedState.FanProfileCount).Select(Strings.FanProfile)];

    /// <summary>The active profile; bound to the profile selector.</summary>
    public int ProfileIndex
    {
        get => _performance.FanProfile;
        set => PickProfile(value);
    }

    /// <summary>The profile selector shows on Custom, while there is a curve to edit.</summary>
    public bool ShowsProfiles => CurveShown && Mode == FanMode.Custom;

    /// <summary>The selector works only while the helper does and this machine offers fan modes.</summary>
    public bool CanChoose => _performance.IsHelperOnline && ShowsModes;

    // The chart.
    public FanCurveLimits? Limits => _performance.FanLimits;
    public IReadOnlyList<int> Temperatures { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<int>? Stock { get; private set => Set(ref field, value); }
    public int WarnCelsius => _performance.Device.TempWarnC;
    public int HotCelsius => _performance.Device.TempHotC;
    public int? CpuCelsius => _performance.CpuCelsius;
    public int? FanRpm => _performance.FanRpm;

    /// <summary>One speed per point: what the chart shows, and on Custom what the user is shaping.</summary>
    public IReadOnlyList<int> Speeds
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(CanReset));
            if (_showing) return;

            if (!_changed) _editProfile = _performance.FanProfile;
            _changed = true;
            _settle.Stop();
            _settle.Start();
        }
    } = [];

    /// <summary>Only a custom curve can be changed; Auto shows the machine's own and Max full speed.</summary>
    public bool IsReadOnly => Mode != FanMode.Custom || !_performance.IsHelperOnline;

    /// <summary>True while the controller is editing the curve; Back then stops editing instead of leaving the page.</summary>
    public bool IsEditing { get; set => Set(ref field, value); }

    /// <summary>True while a finger or the mouse holds a point; the curve is sent as it lets go.</summary>
    public bool IsDragging
    {
        get;
        set
        {
            if (Set(ref field, value) && !value) Keep();
        }
    }

    /// <summary>The chart shows only when there is a curve to show: this machine takes one, and the helper sent it.</summary>
    public bool CurveShown => _performance.IsHelperOnline && Limits is not null && _performance.FanCurve is not null;

    /// <summary>E.g. "MSI Claw 8 AI+ (MS-1T52)".</summary>
    public string DeviceName => _performance.Device.Name;

    /// <summary>E.g. "Up to ~8,000 rpm".</summary>
    public string MaxRpmNote => Limits is { } limits ? Strings.FanMaxRpm(limits.MaxRpm) : "";

    /// <summary>
    /// Why there is no chart, or what the chart shows on Auto and Max. On Custom, only a warning: the fans are
    /// off the active profile, or the machine's own curve is unknown.
    /// </summary>
    public string Note =>
        !_performance.IsHelperOnline ? _performance.HelperNote
        : Limits is null ? (_performance.Fan == FanMode.Custom ? Strings.FanCustomElsewhere : Strings.FanCurveUnavailable)
        : _performance.FanCurve is null ? (_performance.FanCurveAsked ? Strings.FanCurveUnreadable : Strings.FanCurveReading)
        : Mode switch
        {
            FanMode.Auto => Strings.FanAutoNote,
            FanMode.Max  => Strings.FanMaxNote,
            _ when Adrift => Strings.FanProfileAdrift(_performance.FanProfile + 1),
            _ when _performance.FanCurve is { StockKnown: false } => Strings.FanStockUnknown,
            _ => "",
        };

    public bool ShowsNote => Note.Length > 0;

    /// <summary>
    /// The fans are on Custom, but not on the active profile's curve, with nothing on its way to change that:
    /// another tool set it, or the last send failed. Picking the profile (or Custom) again sends it.
    /// </summary>
    private bool Adrift =>
        Mode == FanMode.Custom && _performance.Fan == FanMode.Custom && !_changed && !_performance.IsFanBusy
        && !_performance.ActiveProfileInForce;

    /// <summary>Picking the mode already shown: on Custom, sends the active profile if the fans have drifted off it.</summary>
    public ICommand ReselectMode { get; }

    /// <summary>Picking the profile already active: sends it if the fans have drifted off it.</summary>
    public ICommand ReselectProfile { get; }

    /// <summary>The note about the safety floors, on a custom curve that has them.</summary>
    public bool ShowsFloorHint => CurveShown && Mode == FanMode.Custom && Limits?.Floors.Count > 0;

    /// <summary>Puts the custom curve back to the machine's own.</summary>
    public ICommand Reset { get; }

    public bool CanReset => CurveShown && !IsReadOnly && Stock is { } stock && !stock.SequenceEqual(Speeds);

    private int IndexOf(FanMode mode)
    {
        for (var i = 0; i < _modes.Count; i++)
            if (_modes[i] == mode) return i;
        return -1;
    }

    /// <summary>
    /// Custom is offered where the machine takes a curve, and also shown (though it cannot be picked) while the fans
    /// are on a curve set elsewhere, so the selector never claims Auto when they are not.
    /// </summary>
    private bool ListsCustom => _performance.FanLimits is not null || _performance.Fan == FanMode.Custom;

    private void LayOut()
    {
        var device = _performance.Device;
        FanMode[] order = [FanMode.Auto, FanMode.Custom, FanMode.Max];

        _modes = [.. order.Where(mode => mode == FanMode.Custom ? ListsCustom : device.Offers(mode))];
        Modes = [.. _modes.Select(Label)];

        Notify(nameof(ShowsModes));
        Notify(nameof(Limits));
        Notify(nameof(DeviceName));
        Notify(nameof(MaxRpmNote));
        Notify(nameof(WarnCelsius));
        Notify(nameof(HotCelsius));
    }

    private static string Label(FanMode mode) => mode switch
    {
        FanMode.Custom => Strings.FanCustom,
        FanMode.Max    => Strings.FanMax,
        _              => Strings.FanAuto,
    };

    private void Choose(FanMode mode)
    {
        if (mode != Mode && _performance.IsHelperOnline)
        {
            if (mode != FanMode.Custom)
            {
                Keep(send: false);   // an unsent edit is kept for later, but must not follow this choice to the fans
                _chosen = mode;
                ShowAxisFor(mode);
                Show(SpeedsFor(mode));
                _performance.ChooseFan(mode);
            }
            else if (_performance.FanCurve is { } curve && Limits is not null)
            {
                // The active profile; one never edited starts from the firmware's curve sampled onto the custom scale.
                Keep(send: false);
                _chosen = mode;
                ShowAxisFor(mode);
                Show(_performance.ProfileCurve(_performance.FanProfile, curve));
                _performance.ChooseFanProfile(_performance.FanProfile, send: true);
            }
        }

        NotifyMode();   // also puts the selector back if the choice could not be made
    }

    /// <summary>Brings the chart and everything around it up to date, unless the user is in the middle of a change.</summary>
    private void Sync()
    {
        ShowAxisFor(Mode);

        // While the user is shaping the curve, or it is on its way, newer readings would only pull it back.
        var holding = IsEditing || IsDragging || _settle.IsEnabled || _changed || _performance.IsFanBusy;
        if (!holding) Show(SpeedsFor(Mode));

        NotifyMode();
    }

    /// <summary>Auto shows the firmware's own scale; Custom and Max show the chipset's custom temperature scale.</summary>
    private void ShowAxisFor(FanMode mode)
    {
        var curve = _performance.FanCurve;
        IReadOnlyList<int> temperatures = curve is null ? []
            : mode == FanMode.Auto ? curve.EffectiveStockTemperatures
            : _performance.CustomTemperaturesOf(curve);
        if (!temperatures.SequenceEqual(Temperatures)) Temperatures = temperatures;

        IReadOnlyList<int>? known = curve is null ? null
            : mode == FanMode.Auto ? (curve.StockKnown ? curve.Stock : null)
            : _performance.StockForCustom(curve);
        if (known is null || Stock is null || !known.SequenceEqual(Stock)) Stock = known;
    }

    private IReadOnlyList<int> SpeedsFor(FanMode mode)
    {
        if (_performance.FanCurve is not { } curve || Limits is not { } limits) return [];

        return mode switch
        {
            FanMode.Max    => [.. curve.Temperatures.Select(_ => limits.MaxSpeed)],
            // On Custom, always the active profile: an edit must start from it, never from a curve in force that
            // is not its own (set elsewhere, or left by a send that failed). The note says when they differ.
            FanMode.Custom => _performance.ProfileCurve(_performance.FanProfile, curve),
            _              => curve.Stock,
        };
    }

    /// <summary>Puts speeds into the chart that are not the user's change.</summary>
    private void Show(IReadOnlyList<int> speeds)
    {
        if (speeds.SequenceEqual(Speeds)) return;

        _showing = true;
        Speeds = speeds;
        _showing = false;
    }

    private void NotifyMode()
    {
        Notify(nameof(Mode));
        Notify(nameof(ModeIndex));
        Notify(nameof(Unavailable));
        Notify(nameof(CanChoose));
        Notify(nameof(IsReadOnly));
        Notify(nameof(CurveShown));
        Notify(nameof(Note));
        Notify(nameof(ShowsNote));
        Notify(nameof(ShowsFloorHint));
        Notify(nameof(ShowsProfiles));
        Notify(nameof(CanReset));
        if (IsReadOnly) IsEditing = false;
    }

    private void BackToStock()
    {
        if (IsReadOnly || Stock is not { } stock) return;

        IsEditing = false;
        Speeds = stock;
        Keep();
    }

    /// <summary>
    /// Saves the user's change into the profile it was made on. It is sent only while that profile is still the
    /// active one and the fans are on, or heading for, Custom: if Max or Auto was chosen meanwhile (here or with
    /// Toggle fan boost), it is kept for later instead.
    /// </summary>
    private void Keep(bool send = true)
    {
        _settle.Stop();
        if (!_changed) return;
        _changed = false;

        _performance.KeepFanCurve(_editProfile, Speeds,
            send && _performance.WantedFan == FanMode.Custom && _editProfile == _performance.FanProfile);
    }

    /// <summary>
    /// Makes a profile active, shows its curve and sends it. A change still waiting is first saved into the profile
    /// it was made on, without sending it.
    /// </summary>
    private void PickProfile(int profile)
    {
        if (profile != _performance.FanProfile && profile >= 0 && profile < SavedState.FanProfileCount)
        {
            Keep(send: false);
            IsEditing = false;

            var custom = Mode == FanMode.Custom && _performance.IsHelperOnline && _performance.FanCurve is not null;
            _performance.ChooseFanProfile(profile, send: custom);
            if (_performance.FanCurve is { } curve && Mode == FanMode.Custom) Show(_performance.ProfileCurve(profile, curve));
        }

        Notify(nameof(ProfileIndex));
        NotifyMode();
    }

    /// <summary>Sends the active profile again when the fans are not on it.</summary>
    private void Resend()
    {
        if (!Adrift || _performance.FanCurve is not { } curve) return;

        IsEditing = false;
        Show(_performance.ProfileCurve(_performance.FanProfile, curve));
        _performance.ChooseFanProfile(_performance.FanProfile, send: true);
        NotifyMode();
    }

    /// <summary>Reads the fan curve again; called as the page opens, and as the panel reopens on it.</summary>
    public void Refresh() => _performance.RefreshFanCurve();

    /// <summary>
    /// Asks for the curve again if the page is open without one: the first request may have gone out while the
    /// helper was away, and reconnecting reads only a snapshot.
    /// </summary>
    private void AskIfUnknown()
    {
        if (IsOpen && _performance.FanCurve is null && _performance.IsHelperOnline) Refresh();
    }

    protected override void OnOpened()
    {
        IsEditing = false;
        LayOut();
        Sync();
        Refresh();
    }

    protected override void OnClosed()
    {
        IsEditing = false;
        Keep();
    }

    public override void FlushPending() => Keep();

    /// <summary>Back while editing with the controller only stops editing.</summary>
    public override bool GoBack()
    {
        if (!IsEditing) return false;

        IsEditing = false;
        return true;
    }
}
