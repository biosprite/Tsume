using System.Text.Json;
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
public sealed class CpuBoostViewModel : Observable, IDisposable
{
    private readonly HelperClient _helper;

    /// <summary>Lets rapid tile taps settle before sending a single write to the helper.</summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Counts user toggles so an older refresh or in-flight write reply never overwrites a newer choice.</summary>
    private int _changes;

    /// <summary>Last state confirmed by the helper (null until read). Used to skip redundant writes.</summary>
    private bool? _applied;

    private bool _hasRead;
    private bool _applying;
    private Task? _refreshTask;

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
        !_hasRead ? ""
        : !IsAvailable ? Strings.TileCpuBoostUnavailable
        : IsEnabled ? Strings.TileCpuBoostOn
        : Strings.TileCpuBoostOff;

    /// <summary>Reads the real Windows boost state. Called when the panel opens (if the tile is active) and on helper reconnect.</summary>
    public Task RefreshAsync()
    {
        if (_refreshTask is { IsCompleted: false } inFlight) return inFlight;
        if (_applying || _settle.IsEnabled) return Task.CompletedTask;
        return _refreshTask = RunRefreshAsync();
    }

    /// <summary>Called when the panel closes so a just-tapped change commits immediately instead of waiting on the settle timer.</summary>
    public void FlushPending()
    {
        if (!_settle.IsEnabled) return;
        _settle.Stop();
        _ = ApplyAsync();
    }

    public void Dispose() => _settle.Stop();

    private async Task RunRefreshAsync()
    {
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
            MarkRead();
        }
        catch (JsonException)
        {
            if (_changes == changesBefore && !_applying && !_settle.IsEnabled)
            {
                IsAvailable = false;
                MarkRead();
            }
        }
        finally
        {
            _refreshTask = null;
        }
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
        if (_applying) return;

        _applying = true;
        try
        {
            while (true)
            {
                var target = IsEnabled;
                var sentChanges = _changes;

                // If rapid taps landed back on the state already in force, submit nothing at all.
                if (_applied == target) break;

                HelperReply reply;
                try
                {
                    reply = await _helper.SendAsync(new SetCpuBoost(target));
                }
                catch (JsonException)
                {
                    if (_changes != sentChanges)
                    {
                        if (_settle.IsEnabled) break;
                        continue;
                    }
                    IsAvailable = false;
                    MarkRead();
                    break;
                }

                if (reply.CpuBoostEnabled is { } confirmed)
                    _applied = confirmed;

                // If another tap happened while this request was in flight, wait for the settle timer
                // (or run once more if the settle timer / FlushPending already stopped it).
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
                else if (reply.Unreachable || reply.CpuBoostEnabled is null)
                {
                    IsAvailable = false;
                }
                else
                {
                    // Reachable failure with a known real state (e.g. Group Policy refused): revert tile to Windows state.
                    IsEnabled = reply.CpuBoostEnabled.Value;
                    IsAvailable = true;
                }
                MarkRead();
                break;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void MarkRead()
    {
        if (_hasRead) return;
        _hasRead = true;
        Notify(nameof(Detail));
    }
}
