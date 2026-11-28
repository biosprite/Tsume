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
    public static IReadOnlyList<Game> Find(IEnumerable<string>? customFolders = null)
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);

        // Stores go first: a game a store knows keeps the store's name even when a saved folder holds it too.
        var sources = new Func<IEnumerable<Game>>[] { Steam, Epic, Gog, Ubisoft, Ea, Xbox }
            .Concat((customFolders ?? []).Select(folder => (Func<IEnumerable<Game>>)(() => FromFolder(folder))));

        foreach (var source in sources)
        {
            try
            {
                foreach (var game in source())
                    if (Directory.Exists(game.Folder)) games.TryAdd(game.Folder, game);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                              or System.Security.SecurityException or JsonException)
            {
                // That store's or folder's games can still be added by hand.
            }
        }

        return [.. games.Values.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Builds a hand-added <see cref="Game"/> for <paramref name="program"/> using the same name and root-folder
    /// detection as store and saved-folder scans: matching an installed store or saved-folder game first, then
    /// using the inferred root folder's name (or its parent for a GDK <c>Content</c> folder), and falling back to
    /// the executable's name only at a drive root.
    /// </summary>
    public static Game CustomFromProgram(string program, IEnumerable<string>? customFolders = null) =>
        CustomFromProgram(program, Find(customFolders));

    /// <summary>
    /// Builds a hand-added <see cref="Game"/> for <paramref name="program"/> against an already-discovered set of
    /// store and saved-folder games.
    /// </summary>
    public static Game CustomFromProgram(string program, IEnumerable<Game> knownGames)
    {
        var fullProgram = Path.GetFullPath(program);

        var match = knownGames
            .Where(game => IsInsideFolder(fullProgram, game.Folder))
            .OrderByDescending(game => game.Folder.Length)
            .FirstOrDefault();

        if (match is not null)
            return Game.At(match.Folder, match.Name, GameStore.Custom, fullProgram);

        var inferredFolder = GameInspector.InferRootFolder(fullProgram);
        var name = NameFromFolder(inferredFolder) ?? NameFromExecutable(fullProgram);
        return Game.At(inferredFolder, name, GameStore.Custom, fullProgram);
    }

    private static bool IsInsideFolder(string fullPath, string folder)
    {
        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NameFromFolder(string folder)
    {
        var clean = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(clean, Path.GetPathRoot(clean), StringComparison.OrdinalIgnoreCase))
            return null;

        var leaf = Path.GetFileName(clean);
        if (leaf.Equals("Content", StringComparison.OrdinalIgnoreCase)
            && IsGdkContentFolder(clean)
            && Directory.GetParent(clean) is { } parent
            && !string.Equals(parent.FullName, Path.GetPathRoot(parent.FullName), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parent.Name))
        {
            return parent.Name;
        }

        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    /// <summary>"Witchfire-Win64-Shipping.exe" → "Witchfire"; "my_game.exe" → "my game".</summary>
    private static string NameFromExecutable(string program)
    {
        var raw = Path.GetFileNameWithoutExtension(program);
        var name = raw;

        foreach (var ending in new[] { "-Win64-Shipping", "-WinGDK-Shipping", "_Win64_Shipping" })
            name = name.Replace(ending, "", StringComparison.OrdinalIgnoreCase);

        var cleaned = string.Join(' ', name.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 0 ? cleaned : raw;
    }

    /// <summary>OS folders at a drive root that never hold games directly and are expensive to walk.</summary>
    private static readonly HashSet<string> SkippedDriveRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Users", "Program Files", "Program Files (x86)", "ProgramData", "Recovery", "PerfLogs", "MSOCache",
    };

    /// <summary>Folders only a game's own folder has, never a library of games.</summary>
    private static readonly string[] GameOnlyFolders =
        [@"Engine\Binaries", "bin64", @"bin\x64", @"bin\x64_dx12", @"bin\win64", @"Binaries\Win64", @"Binaries\WinGDK"];

    /// <summary>The defaults skip hidden and system folders (the recycle bin, System Volume Information …).</summary>
    private static readonly EnumerationOptions Visible = new();

    /// <summary>
    /// Finds games inside a user-added folder: either the folder itself if it looks like a single game,
    /// or its immediate subfolders when it is a games library. Steam libraries are looked into for their games.
    /// </summary>
    public static IEnumerable<Game> FromFolder(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) yield break;

        string full;
        try { full = GameList.NormalizeFolder(root); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { yield break; }

        if (!Directory.Exists(full)) yield break;

        // A Steam library's own steamapps folder: its games are in common.
        if (SteamCommon(full, nested: false) is { } steamCommon) full = steamCommon;

        var isDriveRoot = string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase);

        if (!isDriveRoot && LooksLikeSingleGame(full))
        {
            yield return FolderGame(full, Path.GetFileName(full));
            yield break;
        }

        foreach (var child in new DirectoryInfo(full).EnumerateDirectories("*", Visible))
        {
            if (isDriveRoot && SkippedDriveRootFolders.Contains(child.Name)) continue;

            if (SteamCommon(child.FullName, nested: true) is { } common)
            {
                foreach (var steamGame in new DirectoryInfo(common).EnumerateDirectories("*", Visible))
                    if (!IsSteamTool(steamGame.Name)) yield return FolderGame(steamGame.FullName, steamGame.Name);
            }
            else if (!IsSteamTool(child.Name))
            {
                yield return FolderGame(child.FullName, child.Name);
            }
        }
    }

    /// <summary>
    /// A Steam library's common folder, given its steamapps folder or, when <paramref name="nested"/>,
    /// the library folder that holds steamapps. Null when it is neither.
    /// </summary>
    private static string? SteamCommon(string folder, bool nested)
    {
        var common = Path.GetFileName(folder).Equals("steamapps", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(folder, "common")
            : nested ? Path.Combine(folder, "steamapps", "common") : null;

        return Directory.Exists(common) ? common : null;
    }

    /// <summary>A game in a saved folder; a Microsoft Store (GDK) game's program sits in its Content folder.</summary>
    private static Game FolderGame(string folder, string name) =>
        Game.At(HasGdkContent(folder) ? Path.Combine(folder, "Content") : folder, name, GameStore.Folder);

    private static bool HasGdkContent(string folder) =>
        IsGdkContentFolder(Path.Combine(folder, "Content"));

    private static bool IsGdkContentFolder(string folder) =>
        File.Exists(Path.Combine(folder, "MicrosoftGame.config"))
        || File.Exists(Path.Combine(folder, "appxmanifest.xml"));

    private static bool LooksLikeSingleGame(string folder)
    {
        if (HasGdkContent(folder) || GameOnlyFolders.Any(inner => Directory.Exists(Path.Combine(folder, inner))))
            return true;

        try
        {
            // An upscaler beside the program, or a Unity game's "<program>_Data" folder.
            return Directory.EnumerateFiles(folder, "*.dll").Any(dll => GameInspector.IsUpscalerFile(Path.GetFileName(dll)))
                || Directory.EnumerateFiles(folder, "*.exe").Any(exe => Directory.Exists(Path.ChangeExtension(exe, null) + "_Data"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSteamTool(string name) =>
        SteamTools.Any(tool => name.StartsWith(tool, StringComparison.OrdinalIgnoreCase));

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
                if (IsSteamTool(name)) continue;

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

    private static IEnumerable<Game> Ea()
    {
        foreach (var hive in new[] { @"SOFTWARE\WOW6432Node\EA Games", @"SOFTWARE\EA Games" })
        {
            using var all = Registry.LocalMachine.OpenSubKey(hive);
            if (all is null) continue;

            foreach (var id in all.GetSubKeyNames())
            {
                using var entry = all.OpenSubKey(id);
                if (entry?.GetValue("Install Dir") is not string { Length: > 0 } folder) continue;

                folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
                var name = entry.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(folder);

                yield return Game.At(folder, name, GameStore.Ea);
            }
        }
    }

    private static IEnumerable<Game> Xbox()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include Removable drives so microSD cards on handhelds (<SD>:\XboxGames and custom .GamingRoot folders) are scanned.
        foreach (var drive in DriveInfo.GetDrives().Where(drive =>
                     drive is { IsReady: true, DriveType: DriveType.Fixed or DriveType.Removable }))
        {
            var driveRoot = drive.RootDirectory.FullName;
            roots.Add(Path.Combine(driveRoot, "XboxGames"));

            if (TryReadGamingRoot(driveRoot) is { } customRoot)
                roots.Add(customRoot);
        }

        try
        {
            using var repo = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root");
            if (repo is not null)
            {
                foreach (var subName in repo.GetSubKeyNames())
                {
                    using var sub = repo.OpenSubKey(subName);
                    foreach (var valueName in sub?.GetValueNames() ?? [])
                    {
                        if (sub?.GetValue(valueName) is string { Length: > 0 } packageRoot
                            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(packageRoot)) is { Length: > 0 } parent)
                        {
                            roots.Add(parent);
                        }
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Drive roots and .GamingRoot still cover standard Xbox libraries.
        }

        foreach (var games in roots)
        {
            if (!Directory.Exists(games)) continue;

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(games); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

            foreach (var folder in children)
            {
                // The game itself is in Content; a folder without one holds saves or nothing.
                var content = Path.Combine(folder, "Content");
                if (Directory.Exists(content)) yield return Game.At(content, Path.GetFileName(folder), GameStore.Xbox);
            }
        }
    }

    /// <summary>
    /// Reads Xbox GamingServices' <c>.GamingRoot</c> pointer at the root of a drive ("RGBX" + version + UTF-16LE relative path).
    /// </summary>
    private static string? TryReadGamingRoot(string driveRoot)
    {
        try
        {
            var marker = Path.Combine(driveRoot, ".GamingRoot");
            if (!File.Exists(marker)) return null;

            var bytes = File.ReadAllBytes(marker);
            if (bytes.Length <= 8 || bytes[0] != (byte)'R' || bytes[1] != (byte)'G' || bytes[2] != (byte)'B' || bytes[3] != (byte)'X')
                return null;

            var relative = System.Text.Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).Trim('\0', ' ', '\r', '\n');
            if (string.IsNullOrWhiteSpace(relative)) return null;

            return Files.Inside(driveRoot, relative);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }
}
