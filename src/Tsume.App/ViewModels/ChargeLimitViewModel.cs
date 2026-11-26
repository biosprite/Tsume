using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.Core.Devices;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

/// <summary>
/// The charge limit card in Settings: where the battery stops charging, and what it is doing now.
/// The choices come from the device profile and the selected one from the firmware, so the card shows only a limit in force.
/// </summary>
public sealed class ChargeLimitViewModel : Observable
{
    private readonly HelperClient _helper;
    private readonly SavedState _state;
    private int? _resumeGap;

    /// <summary>Counts choices made here, so a reply requested before a choice cannot undo it.</summary>
    private int _changes;
    private int _pendingApplies;
    private bool _absorbing;

    public ChargeLimitViewModel(HelperClient helper, SavedState state)
    {
        _helper = helper;
        _state = state;
        Choices = state.Device?.ChargeLimits ?? [];
        _resumeGap = state.Device?.ChargeResumeGap;

        // Start on the last known firmware limit (or full if never read) so Settings opens already settled.
        _absorbing = true;
        Index = IndexFor(state.ChargeLimit ?? 100);
        _absorbing = false;
    }

    /// <summary>The levels this machine can stop charging at, lowest first. Empty means unsupported, and the card is hidden.</summary>
    public IReadOnlyList<int> Choices
    {
        get;
        private set
        {
            field = value;
            Notify();
            Notify(nameof(Segments));
            Notify(nameof(Card));
            NotifyStatus();
        }
    }

    public IReadOnlyList<string> Segments => [.. Choices.Select(Strings.Percent)];

    public System.Windows.Visibility Card => Choices.Count > 0
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    /// <summary>Which of <see cref="Choices"/> is in force. Setting it sets the limit.</summary>
    public int Index
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            NotifyStatus();

            if (_absorbing || value < 0 || value >= Choices.Count) return;

            _changes++;
            Apply(Choices[value]);
        }
    }

    private int Limit => Index >= 0 && Index < Choices.Count ? Choices[Index] : 100;

    /// <summary>False while the helper is not answering: the limit can be neither read nor set.</summary>
    public bool IsAvailable
    {
        get;
        private set
        {
            if (Set(ref field, value)) NotifyStatus();
        }
    }

    private Battery.Reading? _battery;

    public string Detail
    {
        get
        {
            if (!IsAvailable) return Strings.ChargeLimitUnavailable;

            return _battery switch
            {
                { PluggedIn: false }                             => Strings.ChargeOnBattery(Limit),
                { Flow: Battery.Flow.Resting }                   => Strings.ChargeResting,
                { Flow: Battery.Flow.Charging }                  => Strings.ChargeCharging(Limit),
                { Flow: Battery.Flow.Draining }                  => Strings.ChargeDraining,
                _ when Limit >= 100                              => Strings.ChargeLimitFull,
                _                                                => Strings.ChargeLimitStops(Limit),
            };
        }
    }

    public bool ShowRestingHint =>
        IsAvailable
        && _resumeGap is > 0
        && Limit < 100
        && _battery is { PluggedIn: true, Flow: Battery.Flow.Resting };

    public string RestingHint => _resumeGap is { } gap
        ? Strings.ChargeRestingHint(Math.Max(Limit - gap, 0))
        : string.Empty;

    /// <summary>Re-reads the battery. Cheap; called once a second while Settings is open.</summary>
    public void RefreshStatus()
    {
        _battery = Battery.Read();
        NotifyStatus();
    }

    private void NotifyStatus()
    {
        Notify(nameof(Detail));
        Notify(nameof(ShowRestingHint));
        Notify(nameof(RestingHint));
    }

    /// <summary>Reads the limit in force. Called each time Settings opens.</summary>
    public async Task RefreshAsync()
    {
        RefreshStatus();

        var changesBefore = _changes;
        var reply = await _helper.SendAsync(new GetSnapshot());

        // A choice made while the helper was answering wins over this older reading.
        if (_changes == changesBefore) Absorb(reply);
    }

    /// <summary>Adopts a snapshot already fetched by another view model (e.g. panel open), unless a change is in flight.</summary>
    public void AbsorbSnapshot(HelperReply reply)
    {
        if (_pendingApplies > 0 || reply.Snapshot is null) return;
        Absorb(reply);
    }

    private async void Apply(int percent)
    {
        var changesBefore = _changes;
        _pendingApplies++;
        try
        {
            var reply = await _helper.SendAsync(new SetChargeLimit(percent));

            if (_changes != changesBefore) return;   // a newer choice is on its way

            // Refused, or not kept: show what the firmware really holds, not what was asked for.
            if (reply.Snapshot is null) reply = await _helper.SendAsync(new GetSnapshot());
            if (_changes == changesBefore) Absorb(reply);

            RefreshStatus();
        }
        finally
        {
            _pendingApplies--;
        }
    }

    private void Absorb(HelperReply reply)
    {
        if (reply.Device is { } device)
        {
            if (!device.ChargeLimits.SequenceEqual(Choices)) Choices = device.ChargeLimits;
            if (_resumeGap != device.ChargeResumeGap)
            {
                _resumeGap = device.ChargeResumeGap;
                NotifyStatus();
            }
        }

        IsAvailable = reply.Snapshot is not null && Choices.Count > 0;
        if (reply.Snapshot is not { } snapshot || Choices.Count == 0) return;

        // No firmware limit means charge to full. A level set elsewhere that is
        // not among the choices shows as the nearest one.
        var inForce = snapshot.ChargeLimit ?? 100;
        if (_state.ChargeLimit != inForce)
        {
            _state.ChargeLimit = inForce;
            _state.Save();
        }

        _absorbing = true;
        Index = IndexFor(inForce);
        _absorbing = false;
    }

    private int IndexFor(int percent)
    {
        if (Choices.Count == 0) return -1;

        var nearest = Choices.MinBy(choice => Math.Abs(choice - percent));
        for (var i = 0; i < Choices.Count; i++)
            if (Choices[i] == nearest) return i;

        return Choices.Count - 1;
    }
}
