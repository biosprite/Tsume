using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Core.Devices;
using Tsume.Core.Performance;

namespace Tsume.App.ViewModels;

/// <summary>
/// The performance presets page, opened from Settings: sustained and boost watts for each mode that has its own limits.
/// Changes are saved to state.json shortly after the sliders stop, and applied if the mode is in force.
/// </summary>
public sealed class PerformancePresetsViewModel : LayerViewModel
{
    // The helper clamps whatever is sent to Manual's range, so these sliders cannot exceed what Manual allows.
    private readonly PerformanceViewModel _performance;
    private readonly SavedState _state;

    /// <summary>Waits for a slider to stop before writing the file and the firmware.</summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>The modes whose sliders have moved since the last save.</summary>
    private readonly HashSet<PerformanceMode> _moved = [];

    /// <summary>True while values are being put into the sliders, so their change callbacks are ignored.</summary>
    private bool _absorbing;

    public PerformancePresetsViewModel(PerformanceViewModel performance, SavedState state)
    {
        _performance = performance;
        _state = state;

        _settle.Tick += (_, _) => Keep();
        Reset = new Command(BackToTheMachines);

        // Rebuild the page when the device profile's modes change.
        performance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceViewModel.Modes)) LayOut();
        };

        LayOut();
    }

    /// <summary>One per mode whose limits can be changed, quietest first.</summary>
    public IReadOnlyList<PresetLimits> Presets { get; private set => Set(ref field, value); } = [];

    /// <summary>Puts every mode back to the machine's own limits.</summary>
    public ICommand Reset { get; }

    /// <summary>The card in Settings; hidden on a machine whose limits cannot be set.</summary>
    public Visibility Card => Presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void LayOut()
    {
        var device = _performance.Device;

        Presets = device.ManualRange is not { } range ? [] :
        [
            .. device.Modes
                .Where(preset => device.CanCustomize(preset.Mode))
                .Select(preset => new PresetLimits(
                    preset.Mode, preset.Limits!.Value, range, _performance.CustomFor(preset.Mode), Moved)),
        ];

        Notify(nameof(Card));
    }

    private void Moved(PresetLimits preset)
    {
        if (_absorbing) return;

        _moved.Add(preset.Mode);
        _settle.Stop();
        _settle.Start();
    }

    private void BackToTheMachines()
    {
        _absorbing = true;
        foreach (var preset in Presets)
        {
            if (preset.IsStock) continue;

            preset.Show(preset.Stock);
            _moved.Add(preset.Mode);
        }
        _absorbing = false;

        Keep();
    }

    /// <summary>Saves the choices and tells the performance view model which modes changed.</summary>
    private void Keep()
    {
        _settle.Stop();
        if (_moved.Count == 0) return;

        // Stock limits are not stored, so an unchanged mode follows the device profile
        // if a later version corrects it.
        var chosen = Presets.Where(preset => !preset.IsStock).ToDictionary(preset => preset.Mode, preset => preset.Limits);
        _state.Presets = chosen.Count > 0 ? chosen : null;
        _state.Save();

        PerformanceMode[] moved = [.. _moved];
        _moved.Clear();
        _performance.OnPresetsChanged(moved);
    }

    /// <summary>Saves any pending change at once instead of waiting for the timer.</summary>
    protected override void OnClosed() => Keep();

    public override void FlushPending() => Keep();
}

/// <summary>One mode on the performance presets page: its two limits, and the machine's own (stock) ones.</summary>
public sealed class PresetLimits : Observable
{
    private readonly Action<PresetLimits> _changed;
    private readonly PowerRange _range;

    public PresetLimits(PerformanceMode mode, PowerLimits stock, PowerRange range, PowerLimits? custom, Action<PresetLimits> changed)
    {
        Mode = mode;
        Stock = stock;
        _range = range;
        _changed = _ => { };

        var name = PerformanceViewModel.Label(mode);
        Heading = name.ToUpper(CultureInfo.CurrentCulture);
        SustainedName = Strings.PresetLimit(name, Strings.Sustained);
        BoostName = Strings.PresetLimit(name, Strings.Boost);
        FastBoostName = Strings.PresetLimit(name, Strings.FastBoost);
        StockLine = stock.FastBoost is { } fast
            ? Strings.PresetStock3(stock.Sustained, stock.Boost, fast)
            : Strings.PresetStock(stock.Sustained, stock.Boost);

        Show(range.Clamp(custom ?? stock));
        _changed = changed;
    }

    public PerformanceMode Mode { get; }

    /// <summary>The mode's name as a section heading, e.g. "BALANCED".</summary>
    public string Heading { get; }

    /// <summary>The sliders' names for a screen reader, e.g. "Balanced: Sustained".</summary>
    public string SustainedName { get; }
    public string BoostName { get; }
    public string FastBoostName { get; }

    /// <summary>True on 3-tier AMD devices that expose an independent Fast Boost (FPPT) slider.</summary>
    public bool HasFastBoost => _range.HasFastBoost;

    /// <summary>The machine's own limits for this mode.</summary>
    public PowerLimits Stock { get; }

    /// <summary>E.g. "Default 25 W / 37 W" or "Default 28 W / 45 W / 55 W".</summary>
    public string StockLine { get; }

    // Slider ranges, the same as Manual's; doubles to bind to Slider.Minimum/Maximum.
    public double MinWatts => _range.Min;
    public double MinBoostWatts => _range.MinBoost;
    public double MinFastBoostWatts => _range.MinFastBoost;
    public double MaxSustainedWatts => _range.EffectiveMaxSustained;
    public double MaxBoostWatts => _range.EffectiveMaxBoost;
    public double MaxFastBoostWatts => _range.MaxFastBoost ?? 1;

    public double Sustained
    {
        get;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), _range.Min, _range.EffectiveMaxSustained);
            if (!Set(ref field, clamped))
            {
                if (clamped != value) Notify();
                return;
            }

            var minBoost = _range.MinBoostFor((int)field);
            if (Boost < minBoost) Boost = minBoost;
            OnChanged();
        }
    }

    public double Boost
    {
        get;
        set
        {
            var clamped = Math.Clamp(Math.Round(value), _range.MinBoostFor((int)Sustained), _range.EffectiveMaxBoost);
            if (!Set(ref field, clamped))
            {
                if (clamped != value) Notify();
                return;
            }

            if (HasFastBoost)
            {
                var minFast = _range.MinFastBoostFor((int)field);
                if (FastBoost < minFast) FastBoost = minFast;
            }
            OnChanged();
        }
    }

    public double FastBoost
    {
        get;
        set
        {
            var rounded = Math.Round(value);
            var clamped = _range.MaxFastBoost is { } maxFast
                ? Math.Clamp(rounded, _range.MinFastBoostFor((int)Boost), maxFast)
                : rounded;

            if (Set(ref field, clamped))
                OnChanged();
            else if (clamped != value)
                Notify();
        }
    }

    public PowerLimits Limits => HasFastBoost
        ? new((int)Sustained, (int)Boost, (int)FastBoost)
        : new((int)Sustained, (int)Boost);

    /// <summary>True while the sliders are at the machine's own limits.</summary>
    public bool IsStock => Limits == Stock;

    /// <summary>The stock-limits note, shown only when the sliders differ from stock.</summary>
    public Visibility StockNote => IsStock ? Visibility.Hidden : Visibility.Visible;

    public void Show(PowerLimits limits)
    {
        Sustained = limits.Sustained;
        Boost = limits.Boost;
        if (HasFastBoost) FastBoost = limits.FastBoost ?? _range.MinFastBoostFor(limits.Boost);
    }

    private void OnChanged()
    {
        Notify(nameof(StockNote));
        _changed(this);
    }
}
