using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Theme;
using Tsume.Core.Lighting;

namespace Tsume.App.ViewModels;

/// <summary>
/// The customization page, opened from Settings: preset schemes, then accent, background and text colours.
/// Changes apply at once and are written to state.json shortly after the controls stop moving.
/// </summary>
public sealed class CustomizationViewModel : LayerViewModel
{
    private readonly SavedState _state;

    /// <summary>Waits for a slider to stop before writing the file.</summary>
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(400) };

    /// <summary>True while a scheme is being copied into the controls, so their change callbacks are ignored.</summary>
    private bool _absorbing;

    public CustomizationViewModel(SavedState state)
    {
        _state = state;

        Accent = new ColourChoice(Strings.ColourAccent, Strings.AccentBrightness, dimmest: 30, Mixed);
        Background = new ColourChoice(Strings.ColourBackground, Strings.BackgroundBrightness, dimmest: 0, Mixed);
        Text = new ColourChoice(Strings.ColourText, Strings.TextBrightness, dimmest: 0, Mixed);
        Choices = [Accent, Background, Text];

        Presets = [.. ColourPresets.All.Select(preset => new PresetChoice(preset, () => Wear(preset.Scheme)))];

        _settle.Tick += (_, _) => Save();

        Scheme = state.Colours ?? ColourScheme.Default;
        Show(Scheme);
        Colours.Apply(Scheme);
    }

    public IReadOnlyList<PresetChoice> Presets { get; }

    public ColourChoice Accent { get; }
    public ColourChoice Background { get; }
    public ColourChoice Text { get; }

    /// <summary>The three colours, in page order.</summary>
    public IReadOnlyList<ColourChoice> Choices { get; }

    /// <summary>
    /// The scheme in use. Stored exactly rather than read back from the sliders: whole-percent
    /// brightness cannot represent every colour, and a preset must still compare equal to itself.
    /// </summary>
    public ColourScheme Scheme { get; private set; }

    /// <summary>Applies a preset scheme and saves it.</summary>
    private void Wear(ColourScheme scheme)
    {
        Scheme = scheme;
        Show(scheme);
        Colours.Apply(scheme, animate: true);
        Save();
    }

    /// <summary>A colour's controls moved. Only that colour is re-read; the other two keep their exact values.</summary>
    private void Mixed(ColourChoice moved)
    {
        if (_absorbing) return;

        var mixed = ColourScheme.Written(moved.Colour);
        Scheme = moved == Accent ? Scheme with { Accent = mixed }
            : moved == Background ? Scheme with { Background = mixed }
            : Scheme with { Text = mixed };
        MarkPreset();
        Colours.Apply(Scheme);

        _settle.Stop();
        _settle.Start();
    }

    private void Show(ColourScheme scheme)
    {
        _absorbing = true;
        Accent.Show(scheme.AccentColour);
        Background.Show(scheme.BackgroundColour);
        Text.Show(scheme.TextColour);
        _absorbing = false;

        MarkPreset();
    }

    private void MarkPreset()
    {
        foreach (var preset in Presets) preset.IsCurrent = preset.Scheme == Scheme;
    }

    private void Save()
    {
        _settle.Stop();

        // The default is stored as null, so a later version with a different default takes effect.
        _state.Colours = Scheme == ColourScheme.Default ? null : Scheme;
        _state.Save();
    }

    /// <summary>Saves any pending change at once instead of waiting for the timer.</summary>
    protected override void OnClosed()
    {
        if (_settle.IsEnabled) Save();
    }

    public override void FlushPending() => OnClosed();
}

/// <summary>One ready-made scheme on the customization page, drawn in its own colours.</summary>
public sealed class PresetChoice : Observable
{
    public PresetChoice(ColourPreset preset, Action wear)
    {
        Name = preset.Name;
        Scheme = preset.Scheme;
        Background = Frozen(Scheme.BackgroundColour);
        Accent = Frozen(Scheme.AccentColour);
        Text = Frozen(Scheme.TextColour);
        Choose = new Command(wear);
    }

    public string Name { get; }
    public ColourScheme Scheme { get; }

    public Brush Background { get; }
    public Brush Accent { get; }
    public Brush Text { get; }

    public ICommand Choose { get; }

    /// <summary>True while exactly this scheme is in use.</summary>
    public bool IsCurrent { get; set => Set(ref field, value); }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// One colour being chosen, held as the controls hold it: a full-strength tint
/// (from the dots and strips) and a brightness, 0–100.
/// </summary>
public sealed class ColourChoice : Observable
{
    private readonly Action<ColourChoice> _changed;

    public ColourChoice(string name, string brightnessName, double dimmest, Action<ColourChoice> changed)
    {
        Name = name;
        BrightnessName = brightnessName;
        Dimmest = dimmest;
        _changed = changed;
    }

    /// <summary>What this colour is for, e.g. "Background".</summary>
    public string Name { get; }

    /// <summary>The slider's name for a screen reader, e.g. "Background brightness".</summary>
    public string BrightnessName { get; }

    /// <summary>The slider's minimum brightness. Non-zero for the accent, which must stay visible.</summary>
    public double Dimmest { get; }

    /// <summary>The same swatches as the lighting screen.</summary>
    public IReadOnlyList<Color> Swatches { get; } =
        [.. LightingPalette.Swatches.Select(swatch => Color.FromRgb(swatch.R, swatch.G, swatch.B))];

    public Color Tint
    {
        get;
        set { if (Set(ref field, value)) _changed(this); }
    } = Colors.White;

    public double Brightness
    {
        get;
        set { if (Set(ref field, Math.Round(Math.Clamp(value, Dimmest, 100)))) _changed(this); }
    } = 100;

    /// <summary>The tint at its brightness: the colour actually used.</summary>
    public Color Colour
    {
        get
        {
            byte Channel(byte full) => (byte)Math.Round(full * Brightness / 100);
            return Color.FromRgb(Channel(Tint.R), Channel(Tint.G), Channel(Tint.B));
        }
    }

    /// <summary>Splits a colour into tint and brightness, as near as whole per cent allows.</summary>
    public void Show(Color colour)
    {
        var strongest = Math.Max(colour.R, Math.Max(colour.G, colour.B));

        // Black has no tint; white stands in, so the strips rest at their ends.
        Tint = strongest == 0
            ? Colors.White
            : Color.FromRgb(Full(colour.R), Full(colour.G), Full(colour.B));
        Brightness = strongest / 2.55;

        byte Full(byte channel) => (byte)Math.Round(channel * 255.0 / strongest);
    }
}
