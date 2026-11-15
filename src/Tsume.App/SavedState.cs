using System.IO;
using System.Text.Json;
using Tsume.Core.Devices;
using Tsume.Core.Performance;

namespace Tsume.App;

/// <summary>What Tsume remembers between runs, stored as JSON in %LOCALAPPDATA%\Tsume\state.json.</summary>
public sealed class SavedState
{
    private static readonly string File = Path.Combine(
        Core.DataFolder.Path, "state.json");

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public PerformanceMode Mode { get; set; } = PerformanceMode.Balanced;

    /// <summary>The Manual mode sliders' last limits. Null until used: the machine's default applies.</summary>
    public PowerLimits? Manual { get; set; }

    /// <summary>Per-mode limits overriding the machine's own; an unlisted mode keeps the machine's. Null: none changed.</summary>
    public Dictionary<PerformanceMode, PowerLimits>? Presets { get; set; }

    /// <summary>The helper's last device profile, so the panel can lay out before the helper answers. Null until it first has.</summary>
    public DeviceProfile? Device { get; set; }

    /// <summary>Whether the OptiScaler tile is on the main screen. Hiding it uninstalls nothing.</summary>
    public bool ShowOptiScaler { get; set; } = true;

    /// <summary>Tsume's own colours, chosen on the customization page. Null: the default scheme.</summary>
    public Theme.ColourScheme? Colours { get; set; }

    public static SavedState Load()
    {
        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<SavedState>(System.IO.File.ReadAllText(File)) ?? new SavedState()
                : new SavedState();
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return new SavedState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Format));
        }
        catch (IOException)
        {
            // Losing a preference is better than crashing the panel.
        }
    }
}
