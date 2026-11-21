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
    /// <summary>MSI's EC resumes charging ten points below the chosen charge limit.</summary>
    private const int ResumeGap = 10;

    private readonly HelperClient _helper;

    /// <summary>Counts choices made here, so a reply requested before a choice cannot undo it.</summary>
    private int _changes;
    private bool _absorbing;

    public ChargeLimitViewModel(HelperClient helper, SavedState state)
    {
        _helper = helper;
        Choices = state.Device?.ChargeLimits ?? [];

        // Until the firmware is read, show the last choice: charge to full.
        _absorbing = true;
        Index = Choices.Count - 1;
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

    /// <summary>True while plugged in with the battery resting, to explain the firmware's 10-point resume rule.</summary>
    public bool ShowRestingHint => IsAvailable && _battery is { PluggedIn: true, Flow: Battery.Flow.Resting };

    public string RestingHint => Strings.ChargeRestingHint(Math.Max(Limit - ResumeGap, 0));

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

    private async void Apply(int percent)
    {
        var changesBefore = _changes;
        var reply = await _helper.SendAsync(new SetChargeLimit(percent));

        if (_changes != changesBefore) return;   // a newer choice is on its way

        // Refused, or not kept: show what the firmware really holds, not what was asked for.
        if (reply.Snapshot is null) reply = await _helper.SendAsync(new GetSnapshot());
        if (_changes == changesBefore) Absorb(reply);

        RefreshStatus();
    }

    private void Absorb(HelperReply reply)
    {
        if (reply.Device is { } device && !device.ChargeLimits.SequenceEqual(Choices)) Choices = device.ChargeLimits;

        IsAvailable = reply.Snapshot is not null && Choices.Count > 0;
        if (reply.Snapshot is not { } snapshot || Choices.Count == 0) return;

        // No firmware limit means charge to full. A level set elsewhere that is
        // not among the choices shows as the nearest one.
        var inForce = snapshot.ChargeLimit ?? 100;
        var nearest = Choices.MinBy(choice => Math.Abs(choice - inForce));

        _absorbing = true;
        Index = Choices.ToList().IndexOf(nearest);
        _absorbing = false;
    }
}
