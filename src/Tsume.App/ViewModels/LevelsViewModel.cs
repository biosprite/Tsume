using System.Windows.Input;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.App.Theme;

namespace Tsume.App.ViewModels;

/// <summary>Screen brightness and speaker volume.</summary>
public sealed class LevelsViewModel : Observable
{
    // Both calls can take tens of milliseconds, so they run off the UI thread
    // and only ever for the most recent slider position.
    private readonly LatestOnly<int> _brightnessWriter = new(DisplayBrightness.Set);
    private readonly LatestOnly<int> _volumeWriter = new(AudioVolume.Set);

    private bool _absorbing;

    /// <summary>Counts what the person has changed, so a reading that was asked for earlier does not undo it.</summary>
    private int _changes;

    public LevelsViewModel() => ToggleMute = new Command(() => IsMuted = !IsMuted);

    public double Brightness
    {
        get;
        set
        {
            if (!Set(ref field, Math.Round(value)) || _absorbing) return;

            _changes++;
            _brightnessWriter.Post((int)field);
        }
    }

    public double Volume
    {
        get;
        set
        {
            if (!Set(ref field, Math.Round(value)) || _absorbing) return;

            _changes++;
            _volumeWriter.Post((int)field);
            if (IsMuted && field > 0) IsMuted = false;   // turning it up means "I want sound"
        }
    }

    public bool IsMuted
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;

            Notify(nameof(VolumeGlyph));
            if (_absorbing) return;

            _changes++;
            Task.Run(() => AudioVolume.SetMuted(value));
        }
    }

    public string VolumeGlyph => IsMuted ? Glyphs.Muted : Glyphs.Volume;

    public ICommand ToggleMute { get; }

    /// <summary>Reads the real levels. Called each time the panel opens.</summary>
    public async Task RefreshAsync()
    {
        var changesBefore = _changes;
        var (brightness, volume) = await Task.Run(() => (DisplayBrightness.Read(), AudioVolume.Read()));

        // A slider moved while the levels were being read: what the person set wins.
        if (_changes != changesBefore) return;

        _absorbing = true;
        if (brightness is { } percent) Brightness = percent;
        if (volume is { } reading)
        {
            Volume = reading.Percent;
            IsMuted = reading.Muted;
        }
        _absorbing = false;
    }
}
