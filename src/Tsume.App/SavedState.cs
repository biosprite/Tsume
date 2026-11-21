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

    /// <summary>Whether the RTSS tile is on the main screen. Hiding it uninstalls nothing.</summary>
    public bool ShowRtss { get; set; } = true;

    /// <summary>RTSS Steam-style overlay and frame-rate limiter preferences. Null: defaults apply.</summary>
    public Platform.Rtss.RtssSettings? Rtss { get; set; }

    /// <summary>Tsume's own colours, chosen on the customization page. Null: the default scheme.</summary>
    public Theme.ColourScheme? Colours { get; set; }

    /// <summary>True to slide the panel in from the right edge of the screen instead of the left.</summary>
    public bool OpenOnRight { get; set; }

    /// <summary>True to embed read-only hardware discovery data inside Share Logs bundles even on supported devices.</summary>
    public bool IncludeHardwareInLogs { get; set; }

    public Platform.HardwareButtonAction GameBarButton { get; set; } = Platform.HardwareButtonAction.XboxGameBar;
    public Platform.HardwareButtonAction? OemButton { get; set; }

    /// <summary>Legacy fields kept for migrating earlier state.json files.</summary>
    public int? MButton { get; set; }
    public bool MButtonOpensTsume { get; set; } = true;

    public Platform.HardwareButtonAction ResolveOemButton()
    {
        if (OemButton is { } chosen) return chosen;

        if (MButton is { } legacy)
        {
            return legacy switch
            {
                0 => Platform.HardwareButtonAction.OpenTsume,
                1 => Platform.HardwareButtonAction.XboxGameBar,
                >= 2 and <= 7 => (Platform.HardwareButtonAction)legacy,
                _ => Platform.HardwareButtonAction.OpenTsume,
            };
        }

        return MButtonOpensTsume
            ? Platform.HardwareButtonAction.OpenTsume
            : Platform.HardwareButtonAction.Disabled;
    }

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
