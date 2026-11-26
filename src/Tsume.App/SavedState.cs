using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tsume.Core.Devices;
using Tsume.Core.Performance;

namespace Tsume.App;

/// <summary>What Tsume remembers between runs, stored as JSON in %LOCALAPPDATA%\Tsume\state.json.</summary>
public sealed class SavedState
{
    private static readonly string File = Path.Combine(
        Core.DataFolder.Path, "state.json");

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    private static readonly Lock SaveGate = new();

    public PerformanceMode Mode { get; set; } = PerformanceMode.Balanced;

    /// <summary>The Manual mode sliders' last limits. Null until used: the machine's default applies.</summary>
    public PowerLimits? Manual { get; set; }

    /// <summary>Per-mode limits overriding the machine's own; an unlisted mode keeps the machine's. Null: none changed.</summary>
    public Dictionary<PerformanceMode, PowerLimits>? Presets { get; set; }

    /// <summary>The same on battery, on a machine whose limits differ there. Null: none changed.</summary>
    public Dictionary<PerformanceMode, PowerLimits>? BatteryPresets { get; set; }

    /// <summary>How many custom fan profiles there are.</summary>
    public const int FanProfileCount = 3;

    /// <summary>
    /// The custom fan profiles: per profile, one speed per temperature point of the machine's curve, coolest first.
    /// A null slot was never edited and starts as the machine's own curve. May be short or long in an older or
    /// hand-edited file; read it through <see cref="FanCurveIn"/>.
    /// </summary>
    public List<List<int>?>? FanCurves { get; set; }

    /// <summary>The active custom fan profile, 0-based. Read it through <see cref="ActiveFanProfile"/>, which keeps it in range.</summary>
    public int FanProfile { get; set; }

    /// <summary>Before profiles there was one custom curve. Moved into the first profile and then left null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? FanCurve { get; set; }

    /// <summary>True when the user's own fan mode, the one Max returns to, is Custom (the active profile) rather than Auto.</summary>
    public bool FanCurveOn { get; set; }

    [JsonIgnore]
    public int ActiveFanProfile => Math.Clamp(FanProfile, 0, FanProfileCount - 1);

    /// <summary>A profile's saved curve; null if it was never edited.</summary>
    public List<int>? FanCurveIn(int profile)
    {
        MoveOldFanCurve();
        return FanCurves is { } curves && profile >= 0 && profile < curves.Count ? curves[profile] : null;
    }

    /// <summary>Puts a curve into a profile, or empties it with null. Not saved until <see cref="Save"/>.</summary>
    public void KeepFanCurve(int profile, List<int>? curve)
    {
        if (profile < 0 || profile >= FanProfileCount) return;

        MoveOldFanCurve();
        var curves = FanCurves ??= [];
        while (curves.Count < FanProfileCount) curves.Add(null);
        curves[profile] = curve;
    }

    /// <summary>A curve made on a build before profiles becomes the first profile, so nobody loses it.</summary>
    private void MoveOldFanCurve()
    {
        if (FanCurve is not { } old) return;

        FanCurves ??= [];
        while (FanCurves.Count < FanProfileCount) FanCurves.Add(null);
        FanCurves[0] ??= old;
        FanCurve = null;
    }

    /// <summary>The helper's last device profile, so the panel can lay out before the helper answers. Null until it first has.</summary>
    public DeviceProfile? Device { get; set; }

    /// <summary>Whether the OptiScaler tile is on the main screen. Hiding it uninstalls nothing.</summary>
    public bool ShowOptiScaler { get; set; } = true;

    /// <summary>Whether the RTSS tile is on the main screen. Hiding it uninstalls nothing.</summary>
    public bool ShowRtss { get; set; } = true;

    /// <summary>RTSS overlay and frame-rate limiter preferences. Null: defaults apply.</summary>
    public Platform.Rtss.RtssSettings? Rtss { get; set; }

    /// <summary>Tsume's own colours, chosen on the customization page. Null: the default scheme.</summary>
    public Theme.ColourScheme? Colours { get; set; }

    /// <summary>True to slide the panel in from the right edge of the screen instead of the left.</summary>
    public bool OpenOnRight { get; set; }

    /// <summary>True to reopen Tsume on the last open page instead of returning to the main screen.</summary>
    public bool RememberLastPage { get; set; }

    /// <summary>Ordered main-screen tile IDs (see <see cref="ViewModels.MainTileId"/>). Null uses the default layout.</summary>
    public List<string>? MainTiles { get; set; }

    /// <summary>Number of columns (2, 3, or 4) in the main-screen quick-tile grid.</summary>
    public int MainTileColumns { get; set; } = 4;

    /// <summary>True to embed read-only hardware discovery data inside Share Logs bundles even on supported devices.</summary>
    public bool IncludeHardwareInLogs { get; set; }

    public Platform.HardwareButtonAction GameBarButton { get; set; } = Platform.HardwareButtonAction.XboxGameBar;
    public Platform.HardwareButtonAction? OemButton { get; set; }

    /// <summary>Global key/button chords (up to 3 simultaneous <see cref="Core.Controller.BackButtonKey.Code"/> values) for Dedicated Actions.</summary>
    public Dictionary<string, List<byte>>? DedicatedActionHotkeys { get; set; }

    /// <summary>Legacy fields kept for backwards compatibility with earlier state.json files.</summary>
    public List<byte>? FpsOverlayHotkey { get; set; }
    public List<byte>? OpenTsumeHotkey { get; set; }

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
        lock (SaveGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                var temp = File + ".tmp";
                System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, Format));
                System.IO.File.Move(temp, File, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Losing a preference is better than crashing the panel.
            }
        }
    }
}
