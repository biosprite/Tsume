using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tsume.Upscaling.Games;

/// <summary>
/// Finds installed games from each store's registry values and library files, read-only.
/// A store that is missing or unreadable contributes nothing.
/// </summary>
public static partial class GameLibrary
{
    public static IReadOnlyList<Game> Find()
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);

        foreach (var store in new Func<IEnumerable<Game>>[] { Steam, Epic, Gog, Ubisoft, Xbox })
        {
            try
            {
                foreach (var game in store())
                    if (Directory.Exists(game.Folder)) games.TryAdd(game.Folder, game);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                              or System.Security.SecurityException or JsonException)
            {
                // That store's games can still be added by hand.
            }
        }

        return [.. games.Values.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Name prefixes of Steam runtimes and redistributables, which are listed like games.</summary>
    private static readonly string[] SteamTools = ["Steamworks Common", "Steam Linux Runtime", "Proton", "SteamVR"];

    private static IEnumerable<Game> Steam()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam)
            yield break;

        steam = Path.GetFullPath(steam);

        // libraryfolders.vdf goes first: it has the proper path casing, where the registry's SteamPath is all lower case.
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = Path.Combine(steam, "steamapps", "libraryfolders.vdf");

        if (File.Exists(index))
            foreach (var path in Values(File.ReadAllText(index), "path"))
                libraries.Add(Path.GetFullPath(path));

        libraries.Add(steam);

        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;

            foreach (var record in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(record);
                var folder = Values(text, "installdir").FirstOrDefault();
                var name = Values(text, "name").FirstOrDefault() ?? folder;

                if (folder is null || name is null) continue;
                if (SteamTools.Any(tool => name.StartsWith(tool, StringComparison.OrdinalIgnoreCase))) continue;

                yield return Game.At(Path.Combine(apps, "common", folder), name, GameStore.Steam);
            }
        }
    }

    /// <summary>Every value of <paramref name="key"/> in one of Steam's text files: <c>"key"   "value"</c>.</summary>
    private static IEnumerable<string> Values(string text, string key) => SteamPair().Matches(text)
        .Where(pair => pair.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
        .Select(pair => pair.Groups[2].Value.Replace(@"\\", @"\"));

    [GeneratedRegex("\"([^\"]+)\"\\s+\"([^\"]*)\"")]
    private static partial Regex SteamPair();

    private static IEnumerable<Game> Epic()
    {
        var records = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");

        if (!Directory.Exists(records)) yield break;

        foreach (var record in Directory.EnumerateFiles(records, "*.item"))
        {
            using var item = JsonDocument.Parse(File.ReadAllText(record));
            var root = item.RootElement;

            if (Text(root, "InstallLocation") is not { } folder || Text(root, "DisplayName") is not { } name) continue;
            if (root.TryGetProperty("bIsIncompleteInstall", out var partial) && partial.ValueKind == JsonValueKind.True) continue;

            // Add-ons are listed like games; only what has a program of its own is one.
            if (Text(root, "LaunchExecutable") is not { Length: > 0 } program) continue;

            yield return Game.At(folder, name, GameStore.Epic, Path.Combine(folder, program));
        }

        static string? Text(JsonElement record, string name) =>
            record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static IEnumerable<Game> Gog()
    {
        using var all = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (all is null) yield break;

        foreach (var id in all.GetSubKeyNames())
        {
            using var entry = all.OpenSubKey(id);

            if (entry?.GetValue("path") is not string folder || entry.GetValue("gameName") is not string name) continue;
            if (entry.GetValue("dependsOn") is string { Length: > 0 }) continue;   // an add-on to another game

            yield return Game.At(folder, name, GameStore.Gog, entry.GetValue("exe") as string);
        }
    }

    private static IEnumerable<Game> Ubisoft()
    {
        using var all = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (all is null) yield break;

        foreach (var id in all.GetSubKeyNames())
        {
            using var entry = all.OpenSubKey(id);
            if (entry?.GetValue("InstallDir") is not string { Length: > 0 } folder) continue;

            // Ubisoft records no title here; the folder carries the game's name.
            folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            yield return Game.At(folder, Path.GetFileName(folder), GameStore.Ubisoft);
        }
    }

    private static IEnumerable<Game> Xbox()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive is { IsReady: true, DriveType: DriveType.Fixed }))
        {
            var games = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(games)) continue;

            foreach (var folder in Directory.EnumerateDirectories(games))
            {
                // The game itself is in Content; a folder without one holds saves or nothing.
                var content = Path.Combine(folder, "Content");
                if (Directory.Exists(content)) yield return Game.At(content, Path.GetFileName(folder), GameStore.Xbox);
            }
        }
    }
}
