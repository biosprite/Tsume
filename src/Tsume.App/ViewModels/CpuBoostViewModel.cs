using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Core.Ipc;

namespace Tsume.App.ViewModels;

/// <summary>
/// The CPU Boost quick tile: toggles Windows Processor Performance Boost Mode on or off via the helper.
/// Rapid taps update the tile immediately, coalesce behind a short settle window, and run strictly one
/// helper request at a time so spamming never races or submits duplicate requests.
/// </summary>
public sealed class CpuBoostViewModel : Observable
{
    private readonly HelperClient _helper;

    /// <summary>Lets rapid tile taps settle before sending a single write to the helper.</summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Counts user toggles so an older refresh or in-flight write reply never overwrites a newer choice.</summary>
    private int _changes;

    /// <summary>Last state confirmed by the helper (null until read). Used to skip redundant writes and roll back on error.</summary>
    private bool? _applied;

    private bool _applying;
    private bool _applyAgain;
    private bool _refreshing;

    public CpuBoostViewModel(HelperClient helper)
    {
        _helper = helper;
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = ApplyAsync();
        };
        Toggle = new Command(OnToggle);
    }

    public ICommand Toggle { get; }

    public bool IsEnabled
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(IsOn));
            Notify(nameof(Detail));
        }
    }

    public bool IsAvailable
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(IsOn));
            Notify(nameof(Detail));
        }
    }

    /// <summary>Lights up the tile only when boost is known to be active.</summary>
    public bool IsOn => IsAvailable && IsEnabled;

    public string Detail =>
        !IsAvailable ? Strings.TileCpuBoostUnavailable
        : IsEnabled ? Strings.TileCpuBoostOn
        : Strings.TileCpuBoostOff;

    /// <summary>Reads the real Windows boost state. Called when the panel opens (and on helper reconnect).</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing || _applying || _settle.IsEnabled) return;
        _refreshing = true;

        var changesBefore = _changes;
        try
        {
            var reply = await _helper.SendAsync(new GetCpuBoost());

            // A user toggle started while reading: the user's choice wins.
            if (_changes != changesBefore || _applying || _settle.IsEnabled) return;

            if (reply.Ok && reply.CpuBoostEnabled is { } enabled)
            {
                _applied = enabled;
                IsEnabled = enabled;
                IsAvailable = true;
            }
            else
            {
                IsAvailable = false;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Triggers a guarded background refresh without throwing unhandled exceptions.</summary>
    public void RefreshInBackground() => _ = RefreshAsync();

    /// <summary>Called when the panel closes so a just-tapped change commits immediately instead of waiting on the settle timer.</summary>
    public void FlushPending()
    {
        if (!_settle.IsEnabled) return;
        _settle.Stop();
        _ = ApplyAsync();
    }

    private void OnToggle()
    {
        if (!IsAvailable)
        {
            // While unavailable (e.g. helper was offline), a tap only re-reads; it never toggles blindly.
            _ = RefreshAsync();
            return;
        }

        // Flip UI immediately and coalesce any rapid taps before submitting a single helper request.
        IsEnabled = !IsEnabled;
        _changes++;

        _settle.Stop();
        _settle.Start();
    }

    private async Task ApplyAsync()
    {
        _settle.Stop();

        if (_applying)
        {
            _applyAgain = true;
            return;
        }

        _applying = true;
        try
        {
            while (true)
            {
                _applyAgain = false;

                var target = IsEnabled;
                var sentChanges = _changes;

                // If rapid taps landed back on the state already in force, submit nothing at all.
                if (_applied == target) break;

                var reply = await _helper.SendAsync(new SetCpuBoost(target));

                if (reply.CpuBoostEnabled is { } confirmed)
                    _applied = confirmed;

                // If another tap happened while this request was in flight, wait for the settle timer
                // (or run once more if FlushPending already stopped it).
                if (_changes != sentChanges)
                {
                    if (_settle.IsEnabled) break;
                    continue;
                }

                if (reply.Ok && reply.CpuBoostEnabled is { } actual)
                {
                    IsEnabled = actual;
                    IsAvailable = true;
                }
                else if (reply.Unreachable)
                {
                    IsAvailable = false;
                }
                else if (reply.CpuBoostEnabled is { } actualOnError)
                {
                    // Reachable failure (e.g. Group Policy refused): revert tile to real Windows state.
                    IsEnabled = actualOnError;
                    IsAvailable = true;
                }
                else if (_applied is { } previous)
                {
                    IsEnabled = previous;
                }

                if (!_applyAgain) break;
            }
        }
        finally
        {
            _applying = false;
        }
    }
}
