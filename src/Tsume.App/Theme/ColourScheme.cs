using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Tsume.App.Language;

namespace Tsume.App.Theme;

/// <summary>
/// The three user-chosen colours, stored as "#RRGGBB" so state.json stays readable.
/// <see cref="Colours"/> derives every other colour from them.
/// </summary>
public sealed record ColourScheme(string Accent, string Background, string Text)
{
    public static ColourScheme Default { get; } = new("#4CC2FF", "#13151A", "#F4F6FA");

    [JsonIgnore] public Color AccentColour => Read(Accent, Default.Accent);
    [JsonIgnore] public Color BackgroundColour => Read(Background, Default.Background);
    [JsonIgnore] public Color TextColour => Read(Text, Default.Text);

    /// <summary>Formats a colour as "#RRGGBB".</summary>
    public static string Written(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>Parses "#RRGGBB"; anything invalid reads as <paramref name="otherwise"/>.</summary>
    private static Color Read(string? written, string otherwise)
    {
        if (written is not ['#', .. { Length: 6 } digits]
            || !int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return Read(otherwise, otherwise);

        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

/// <summary>A ready-made <see cref="ColourScheme"/> with a name.</summary>
public sealed record ColourPreset(string Name, ColourScheme Scheme);

/// <summary>The schemes offered on the customization page. The first is the default.</summary>
public static class ColourPresets
{
    public static IReadOnlyList<ColourPreset> All { get; } =
    [
        new(Strings.PresetDefault,  ColourScheme.Default),
        new(Strings.PresetEmber,    new("#FF6B4A", "#1A1412", "#FAF4F1")),
        new(Strings.PresetLeaf,     new("#3DDC97", "#111A16", "#F1FAF5")),
        new(Strings.PresetViolet,   new("#B394FF", "#16131F", "#F5F2FC")),
        new(Strings.PresetGold,     new("#FFC53D", "#17140D", "#FAF6EA")),
        new(Strings.PresetRose,     new("#FF5C9A", "#1B1217", "#FCF1F6")),
        new(Strings.PresetMono,     new("#FFFFFF", "#000000", "#F2F2F2")),
        new(Strings.PresetDaylight, new("#0A6CFF", "#F2F4F8", "#14171C")),
    ];
}
