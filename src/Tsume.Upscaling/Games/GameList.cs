using System.Text.Json;
using System.Text.Json.Serialization;
using Tsume.Upscaling.Config;

namespace Tsume.Upscaling.Games;

public sealed class ListedGame
{
    public required Game Game { get; set; }

    /// <summary>From the last inspection of its folder. Null: nothing there to run.</summary>
    public GameFacts? Facts { get; set; }

    /// <summary>Null until the game is given settings of its own; it follows the defaults till then.</summary>
    public UpscalerSettings? Settings { get; set; }
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

    /// <summary>
    /// The user's edited defaults; null until changed. Deliberately not named "Defaults" in the JSON:
    /// old lists stored stale defaults under that name, and it must not be read.
    /// </summary>
    public UpscalerSettings? ChosenDefaults { get; set; }

    /// <summary>What a game gets until it is given settings of its own.</summary>
    [JsonIgnore]
    public UpscalerSettings Defaults
    {
        get => ChosenDefaults ?? new();
        set => ChosenDefaults = value;
    }

    public List<ListedGame> Games { get; set; } = [];

    public ListedGame? Find(string gameId) => Games.FirstOrDefault(listed => listed.Game.Id == gameId);

    public UpscalerSettings SettingsFor(string gameId) => Find(gameId)?.Settings ?? Defaults;

    public static GameList Load(UpscalingPaths paths)
    {
        if (!File.Exists(paths.GameList)) return new GameList();

        try
        {
            return JsonSerializer.Deserialize<GameList>(File.ReadAllText(paths.GameList), Format) ?? new GameList();
        }
        catch (Exception error) when (error is IOException or JsonException or NotSupportedException)
        {
            // Unreadable: keep a copy rather than overwrite it, since it may hold hand-added games.
            try { File.Copy(paths.GameList, paths.GameList + ".unreadable", overwrite: true); }
            catch (IOException) { }

            return new GameList();
        }
    }

    public void Save(UpscalingPaths paths)
    {
        try
        {
            Files.WriteWhole(paths.GameList, JsonSerializer.Serialize(this, Format));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing a choice is better than taking the panel down.
        }
    }

    /// <summary>
    /// Syncs the list with what the stores report now. Hand-added games stay; a vanished store game is dropped
    /// unless it is in <paramref name="keep"/> (games OptiScaler is still installed in, which must stay removable).
    /// </summary>
    public void Absorb(IEnumerable<Game> found, IReadOnlySet<string> keep)
    {
        var now = found.ToDictionary(game => game.Id);

        Games.RemoveAll(listed =>
            listed.Game.Store != GameStore.Custom && !now.ContainsKey(listed.Game.Id) && !keep.Contains(listed.Game.Id));

        foreach (var game in now.Values)
        {
            if (Find(game.Id) is { } known) known.Game = game;   // the store may have renamed it
            else Games.Add(new ListedGame { Game = game });
        }

        Games.Sort((a, b) => string.Compare(a.Game.Name, b.Game.Name, StringComparison.CurrentCultureIgnoreCase));
    }
}
