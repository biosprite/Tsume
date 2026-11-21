using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.App.Theme;
using Tsume.Core.Controller;

namespace Tsume.App.ViewModels;

/// <summary>
/// The Controller tile: toggles the built-in pad between gamepad and cursor (mouse) mode.
/// The mode is stored only in the pad, so it is re-read each time the panel opens.
/// </summary>
public sealed class PadModeViewModel : Observable
{
    private readonly PadIsolation? _isolation;
    private bool _switching;

    public PadModeViewModel(PadIsolation? isolation = null)
    {
        _isolation = isolation;
        Toggle = new Command(() => _ = ToggleAsync());
    }

    public ICommand Toggle { get; }

    /// <summary>Gamepad until read from the pad: that is the pad's power-on mode.</summary>
    public PadMode Mode
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(IsCursor));
            Notify(nameof(Detail));
            Notify(nameof(Glyph));
        }
    } = PadMode.Gamepad;

    /// <summary>False when the pad could not be found or did not answer.</summary>
    public bool IsAvailable
    {
        get;
        private set { if (Set(ref field, value)) Notify(nameof(Detail)); }
    } = true;

    /// <summary>The tile glows in cursor mode.</summary>
    public bool IsCursor => Mode == PadMode.Cursor;

    public string Detail =>
        !IsAvailable ? Strings.PadUnavailable
        : IsCursor ? Strings.PadCursor
        : Strings.PadGamepad;

    public string Glyph => IsCursor ? Glyphs.Cursor : Glyphs.Gamepad;

    /// <summary>Reads the mode from the pad. Called as the panel opens.</summary>
    public async Task RefreshAsync()
    {
        if (_switching) return;

        var mode = await ReadModeAsync();
        if (_switching) return;   // a toggle started meanwhile; its result wins

        Show(mode);
    }

    private async Task ToggleAsync()
    {
        if (_switching) return;
        _switching = true;

        try
        {
            if (!IsAvailable)
            {
                // While unavailable, a press only re-reads the pad; it does not toggle.
                Show(await ReadModeAsync());
                return;
            }

            var before = Mode;
            var wanted = IsCursor ? PadMode.Gamepad : PadMode.Cursor;

            Mode = wanted;   // update the tile at once; the pad follows in ~30 ms

            if (!await SetModeAsync(wanted))
            {
                // The set failed: show what the pad reports now, or the previous mode if it does not answer.
                Mode = before;
                Show(await ReadModeAsync());
            }

            // Stay deaf until the press that asked for this is let go, or it toggles straight back.
            await ControllerMode.ReleasedAsync();

            // Let the click of that release arrive while still deaf.
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        finally
        {
            _switching = false;
        }
    }

    private Task<PadMode?> ReadModeAsync() =>
        _isolation is not null ? _isolation.ReadLogicalModeAsync() : ControllerMode.ReadAsync();

    private Task<bool> SetModeAsync(PadMode wanted) =>
        _isolation is not null ? _isolation.SetLogicalModeAsync(wanted) : ControllerMode.SetAsync(wanted);

    private void Show(PadMode? mode)
    {
        IsAvailable = mode is not null;
        if (mode is { } known) Mode = known;
    }
}
