using System.Security.Cryptography;
using System.Text;

namespace Tsume.Upscaling.Games;

public enum GameStore
{
    /// <summary>Added by hand, by pointing at its program.</summary>
    Custom,
    /// <summary>Found inside a user-added games folder.</summary>
    Folder,
    Steam,
    Epic,
    Gog,
    Xbox,
    Ubisoft,
}

public sealed record Game
{
    /// <summary>Hash of the folder path, so it is stable across scans; settings and the install record are keyed by it.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Folder { get; init; }

    /// <summary>The program the store starts, or the one the user picked. Only a hint; see <see cref="GameInspector"/>.</summary>
    public string? Executable { get; init; }

    public GameStore Store { get; init; }

    public static Game At(string folder, string name, GameStore store, string? executable = null)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

        return new Game
        {
            Id = IdFor(full),
            Name = name.Trim(),
            Folder = full,
            Executable = executable,
            Store = store,
        };
    }

    private static string IdFor(string folder) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..16];
}

/// <summary>What <see cref="GameInspector"/> found in a game's folder.</summary>
/// <param name="Target">The folder holding the program that really runs — where OptiScaler has to go.</param>
/// <param name="Executable">That program.</param>
/// <param name="Upscalers">The upscalers the game ships that OptiScaler can take over: "DLSS", "FSR 3", "XeSS" …</param>
/// <param name="AntiCheat">The anti-cheat found, if any. OptiScaler is not offered for such a game.</param>
/// <param name="ExistingProxy">The file name of an OptiScaler already in <paramref name="Target"/>, if there is one.</param>
public sealed record GameFacts(
    string Target,
    string Executable,
    IReadOnlyList<string> Upscalers,
    string? AntiCheat,
    string? ExistingProxy);
