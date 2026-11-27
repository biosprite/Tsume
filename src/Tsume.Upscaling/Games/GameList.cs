using System.Text.Json;
using System.Text.Json.Serialization;
using Tsume.Core.Diagnostics;
using Tsume.Upscaling.Config;

namespace Tsume.Upscaling.Games;

public sealed class ListedGame
{
    public required Game Game { get; set; }

    /// <summary>From the last inspection of its folder. Null: nothing there to run.</summary>
    public GameFacts? Facts { get; set; }

    /// <summary>Null until the game is given settings of its own; it follows the defaults till then.</summary>
    public UpscalerSettings? Settings { get; set; }

    /// <summary>True once the user has accepted the ban risk of OptiScaler in a game with anti-cheat.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AntiCheatAccepted { get; set; }

    /// <summary>
    /// True when Auto-install may put OptiScaler into this game: it has a target executable, no third-party
    /// OptiScaler is already present, and either it has no anti-cheat or its anti-cheat warning was accepted.
    /// </summary>
    [JsonIgnore]
    public bool CanAutoInstall => Facts is { } facts
        && facts.ExistingProxy is null
        && (facts.AntiCheat is null || AntiCheatAccepted);
}

/// <summary>
/// The known games and their settings, stored in games.json. Store games come and go with each scan;
/// games added by hand stay until removed.
/// </summary>
public sealed class GameList
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Journal Log = new("optiscaler");

    /// <summary>
    /// True when games.json was there but could not be read, and no copy of it has been kept yet: the file may
    /// still hold hand-added games and settings, so it must not be written over until it has been backed up.
    /// </summary>
    private bool _fileNotBackedUp;

    /// <summary>Serialized form of a <see cref="GameList"/>, returned by <see cref="Serialize"/>.</summary>
    public sealed class Serialized : IEquatable<Serialized>
    {
        internal Serialized(GameList source, string json)
        {
            Source = source;
            Json = json;
        }

        internal GameList Source { get; }
        internal string Json { get; }

        public bool Equals(Serialized? other) => other is not null && string.Equals(Json, other.Json, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is Serialized other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Json);
        public static bool operator ==(Serialized? left, Serialized? right) => Equals(left, right);
        public static bool operator !=(Serialized? left, Serialized? right) => !Equals(left, right);
    }

    /// <summary>
    /// The user's edited defaults; null until changed. Deliberately not named "Defaults" in the JSON:
    /// old lists stored stale defaults under that name, and it must not be read.
    /// </summary>
    public UpscalerSettings? ChosenDefaults { get; set; }

    /// <summary>What a game gets until it is given settings of its own. OptiPatcher is always per game.</summary>
    [JsonIgnore]
    public UpscalerSettings Defaults
    {
        get => (ChosenDefaults ?? new()) with { OptiPatcher = false };
        set => ChosenDefaults = value with { OptiPatcher = false };
    }

    /// <summary>When true, Tsume automatically installs OptiScaler into eligible games using Global Defaults.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoInstall { get; set; }

    public List<ListedGame> Games { get; set; } = [];

    /// <summary>User-added library folders scanned alongside the game stores.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>The full path without a trailing separator, except on a drive's root: "D:\Games\" → "D:\Games".</summary>
    public static string NormalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <remarks>Saved folders are stored normalized, so only <paramref name="folder"/> needs normalizing.</remarks>
    public bool HasFolder(string folder) => Folders.Contains(NormalizeFolder(folder), StringComparer.OrdinalIgnoreCase);

    public bool AddFolder(string folder)
    {
        if (HasFolder(folder)) return false;

        Folders.Add(NormalizeFolder(folder));
        Folders.Sort(StringComparer.CurrentCultureIgnoreCase);
        return true;
    }

    public bool RemoveFolder(string folder)
    {
        // The exact text too, so a hand-edited entry in games.json can still be removed.
        var normalized = NormalizeFolder(folder);
        return Folders.RemoveAll(saved => string.Equals(saved, folder, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(saved, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public ListedGame? Find(string gameId) => Games.FirstOrDefault(listed => listed.Game.Id == gameId);

    public UpscalerSettings SettingsFor(string gameId) => Find(gameId)?.Settings ?? Defaults;

    public static GameList Load(UpscalingPaths paths)
    {
        if (!File.Exists(paths.GameList)) return new GameList();

        try
        {
            return JsonSerializer.Deserialize<GameList>(File.ReadAllText(paths.GameList), Format) ?? new GameList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Write($"games.json could not be read: {error.Message}");

            // Whether the file is damaged or only locked for a moment, it may hold hand-added games:
            // keep a copy before anything overwrites it. If no copy can be made now, Save tries again first.
            return new GameList { _fileNotBackedUp = !BackUp(paths) };
        }
    }

    /// <summary>Copies games.json to games.json.unreadable.</summary>
    /// <returns>False if the copy could not be made.</returns>
    private static bool BackUp(UpscalingPaths paths)
    {
        try
        {
            File.Copy(paths.GameList, paths.GameList + ".unreadable", overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"games.json could not be backed up: {error.Message}");
            return false;
        }
    }

    /// <summary>The list as games.json holds it.</summary>
    public Serialized Serialize() => new(this, JsonSerializer.Serialize(this, Format));

    /// <summary>
    /// Writes <paramref name="snapshot"/> (from <see cref="Serialize"/>) to games.json. A games.json that could not
    /// be read when the list was loaded is backed up first, and left alone if that still cannot be done.
    /// </summary>
    /// <returns>False if it could not be written.</returns>
    public static bool Save(UpscalingPaths paths, Serialized snapshot)
    {
        var source = snapshot.Source;

        if (source._fileNotBackedUp)
        {
            if (File.Exists(paths.GameList) && !BackUp(paths))
            {
                Log.Write("games.json not saved: it could not be read or backed up, and may hold hand-added games");
                return false;
            }

            source._fileNotBackedUp = false;
        }

        try
        {
            Files.WriteWhole(paths.GameList, snapshot.Json);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing a choice is better than taking the panel down.
            Log.Write($"games.json could not be saved: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// Syncs the list with what the stores report now. Hand-added games stay; a vanished store game is dropped
    /// unless it is in <paramref name="keep"/> (games OptiScaler is still installed in, which must stay removable).
    /// </summary>
    /// <returns>Games newly added to the list by this scan.</returns>
    public IReadOnlyList<ListedGame> Absorb(IEnumerable<Game> found, IReadOnlySet<string> keep)
    {
        var now = found.ToDictionary(game => game.Id);

        Games.RemoveAll(listed =>
            listed.Game.Store != GameStore.Custom && !now.ContainsKey(listed.Game.Id) && !keep.Contains(listed.Game.Id));

        var added = new List<ListedGame>();
        foreach (var game in now.Values)
        {
            if (Find(game.Id) is { } known)
            {
                if (known.Game.Store != GameStore.Custom) known.Game = game;   // the store may have renamed it
            }
            else
            {
                var listed = new ListedGame { Game = game };
                Games.Add(listed);
                added.Add(listed);
            }
        }

        Games.Sort((a, b) => string.Compare(a.Game.Name, b.Game.Name, StringComparison.CurrentCultureIgnoreCase));
        return added;
    }
}
