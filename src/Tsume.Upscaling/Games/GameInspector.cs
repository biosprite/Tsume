using System.Diagnostics;
using Tsume.Upscaling.Install;

namespace Tsume.Upscaling.Games;

/// <summary>
/// Read-only scan of a game's folder: which program really runs (OptiScaler must sit beside it),
/// which upscalers the game ships, and whether an anti-cheat is present.
/// </summary>
public static class GameInspector
{
    /// <summary>Deep enough for Unreal's …\Engine\Plugins\Runtime\Nvidia\DLSS\Binaries\ThirdParty\Win64.</summary>
    private const int Deepest = 9;

    /// <summary>A game with more files than this is not searched to the end.</summary>
    private const int MostFiles = 60_000;

    /// <summary>File name → the upscaler it belongs to.</summary>
    private static readonly Dictionary<string, string> UpscalerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"] = "DLSS",
        ["sl.dlss.dll"] = "DLSS",
        ["nvngx_dlssg.dll"] = "DLSS-FG",
        ["sl.dlss_g.dll"] = "DLSS-FG",
        ["libxess.dll"] = "XeSS",
        ["libxess_dx11.dll"] = "XeSS",
        ["libxess_fg.dll"] = "XeSS-FG",
        ["ffx_fsr2_api_x64.dll"] = "FSR 2",
        ["ffx_fsr2_api_dx11_x64.dll"] = "FSR 2",
        ["ffx_fsr2_api_dx12_x64.dll"] = "FSR 2",
        ["ffx_fsr2_api_vk_x64.dll"] = "FSR 2",
        ["ffx_fsr3_x64.dll"] = "FSR 3",
        ["ffx_fsr3upscaler_x64.dll"] = "FSR 3",
        ["amd_fidelityfx_dx12.dll"] = "FSR 3",
        ["amd_fidelityfx_vk.dll"] = "FSR 3",
        ["amd_fidelityfx_loader_dx12.dll"] = "FSR 3",
        ["amd_fidelityfx_loader_vk.dll"] = "FSR 3",
        ["amd_fidelityfx_upscaler_dx12.dll"] = "FSR 3",
        ["amd_fidelityfx_upscaler_vk.dll"] = "FSR 3",
        ["amd_fidelityfx_framegeneration_dx12.dll"] = "FSR-FG",
        ["amd_fidelityfx_framegeneration_vk.dll"] = "FSR-FG",
    };

    /// <summary>Folder or file name → the anti-cheat it belongs to.</summary>
    private static readonly Dictionary<string, string> AntiCheatMarks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EasyAntiCheat"] = "Easy Anti-Cheat",
        ["EasyAntiCheat_EOS"] = "Easy Anti-Cheat",
        ["EasyAntiCheat.exe"] = "Easy Anti-Cheat",
        ["start_protected_game.exe"] = "Easy Anti-Cheat",
        ["BattlEye"] = "BattlEye",
        ["BEService.exe"] = "BattlEye",
        ["BEService_x64.exe"] = "BattlEye",
        ["BEClient_x64.dll"] = "BattlEye",
        ["AntiCheatExpert"] = "Anti-Cheat Expert",
        ["GameGuard"] = "nProtect GameGuard",
        ["XIGNCODE"] = "XIGNCODE",
        ["EAAntiCheat.GameServiceLauncher.exe"] = "EA AntiCheat",
        ["EAAntiCheat.GameServiceLauncher.dll"] = "EA AntiCheat",
        ["EAAntiCheat.Installer.exe"] = "EA AntiCheat",
        ["randgrid.sys"] = "RICOCHET Anti-Cheat",
        ["vgk.sys"] = "Riot Vanguard",
    };

    /// <summary>Folders not worth opening: installers for other things, and OptiScaler's own.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", ".git", "_CommonRedist", "Redist", "Redistributables", "__Installer", "DirectX", "VCRedist",
        "OptiScaler",   // its libxess.dll and amd_fidelityfx_*.dll are not the game's
    };

    /// <summary>Words in a program's name that say it is not the game itself.</summary>
    private static readonly string[] HelperWords =
    [
        "unins", "crash", "report", "setup", "install", "launcher", "launchhelper", "bootstrap", "redist",
        "dxsetup", "dotnet", "helper", "subprocess", "webengine", "anticheat", "beservice", "updater",
        "patcher", "benchmark", "protected_game",
    ];

    /// <summary>True when <paramref name="fileName"/> is an upscaler library OptiScaler can take over.</summary>
    public static bool IsUpscalerFile(string fileName) => UpscalerFiles.ContainsKey(fileName);

    /// <summary>Null when the folder is gone or holds no program at all.</summary>
    public static GameFacts? Inspect(Game game)
    {
        if (!Directory.Exists(game.Folder)) return null;

        var programs = new List<string>();
        var upscalers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var upscalerFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? antiCheat = null;

        foreach (var entry in Walk(game.Folder))
        {
            var name = Path.GetFileName(entry);

            if (AntiCheatMarks.TryGetValue(name, out var found)) antiCheat ??= found;

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                programs.Add(entry);
            }
            else if (UpscalerFiles.TryGetValue(name, out var upscaler))
            {
                upscalers.Add(upscaler);
                upscalerFolders.Add(Path.GetDirectoryName(entry)!);
            }
        }

        // The hinted program counts even where the walk did not reach.
        if (game.Executable is { } hint && File.Exists(hint) &&
            !programs.Contains(hint, StringComparer.OrdinalIgnoreCase)) programs.Add(hint);

        if (ChooseProgram(game, programs, upscalerFolders) is not { } program) return null;

        var target = Path.GetDirectoryName(program)!;
        var existing = ExistingProxy(target);
        return new GameFacts(target, program, [.. upscalers], antiCheat, existing, PresentProxies(target, existing));
    }

    /// <summary>
    /// Infers the game's root folder when a user picks an executable inside a binary subfolder
    /// (such as <c>&lt;Root&gt;\&lt;Project&gt;\Binaries\Win64\*.exe</c> or <c>&lt;Root&gt;\bin\x64\*.exe</c>),
    /// so root-level anti-cheat folders and <c>Engine\Plugins</c> upscaler DLLs are still scanned.
    /// </summary>
    public static string InferRootFolder(string program)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(program))!;
        var clean = Path.TrimEndingDirectorySeparator(folder);

        // Unreal Engine: <Root>\<Project>\Binaries\Win64 (3 levels deep) or <Root>\Binaries\Win64 (2 levels deep).
        if (IsUnrealTarget(clean))
        {
            var twoUp = Directory.GetParent(clean)?.Parent?.FullName;
            var threeUp = twoUp is null ? null : Directory.GetParent(twoUp)?.FullName;

            if (IsPlausibleGameRoot(threeUp) && HasRootMarkers(threeUp!))
                return threeUp!;

            if (IsPlausibleGameRoot(twoUp) && HasRootMarkers(twoUp!))
                return twoUp!;
        }

        // Common multi-level binary layouts: <Root>\bin\x64, <Root>\bin\x64_dx12, <Root>\bin\win64.
        var leaf = Path.GetFileName(clean);
        var parentDir = Directory.GetParent(clean);
        if (parentDir is not null
            && parentDir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            && (leaf.StartsWith("x64", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("win64", StringComparison.OrdinalIgnoreCase)))
        {
            var twoUp = parentDir.Parent?.FullName;
            if (IsPlausibleGameRoot(twoUp)) return twoUp!;
        }

        if ((leaf.Equals("bin64", StringComparison.OrdinalIgnoreCase)
             || leaf.Equals("bin", StringComparison.OrdinalIgnoreCase))
            && parentDir?.FullName is { } oneUp
            && IsPlausibleGameRoot(oneUp))
        {
            return oneUp;
        }

        return folder;

        static bool IsPlausibleGameRoot(string? candidate) =>
            !string.IsNullOrEmpty(candidate)
            && !string.Equals(candidate, Path.GetPathRoot(candidate), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(candidate);

        static bool HasRootMarkers(string candidate)
        {
            if (Directory.Exists(Path.Combine(candidate, "Engine"))) return true;

            foreach (var mark in AntiCheatMarks.Keys)
                if (Path.Exists(Path.Combine(candidate, mark))) return true;

            try { return Directory.EnumerateFiles(candidate, "*.exe").Any(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// The proxy file name OptiScaler already sits under in this folder (including <c>OptiScaler.asi</c>), or null.
    /// Recognised by its OriginalFilename version field, whatever the file is called — the same test its own setup uses.
    /// </summary>
    public static string? ExistingProxy(string folder)
    {
        foreach (var name in ProxyNames.Recognised)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;

            if (name.Equals(ProxyNames.AsiName, StringComparison.OrdinalIgnoreCase) || IsOptiScalerBinary(path))
                return name;
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> carries OptiScaler's version resource.</summary>
    public static bool IsOptiScalerBinary(string path)
    {
        try
        {
            return string.Equals(FileVersionInfo.GetVersionInfo(path).OriginalFilename, "OptiScaler.dll",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Selectable proxy DLL names already occupied in <paramref name="folder"/> by non-OptiScaler files.</summary>
    private static IReadOnlyList<string> PresentProxies(string folder, string? existingOptiScaler) =>
    [
        .. ProxyNames.All.Where(name =>
            !string.Equals(name, existingOptiScaler, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(folder, name))
            && !IsOptiScalerBinary(Path.Combine(folder, name))),
    ];

    private static string? ChooseProgram(Game game, List<string> programs, HashSet<string> upscalerFolders)
    {
        if (programs.Count == 0) return null;

        // Unreal: the top-level exe is a stub; the real game sits in …\Binaries\Win64\ or …\Binaries\WinGDK\
        // (with or without "-Shipping" in its filename), and that is the folder Windows loads OptiScaler from.
        var shipping = programs.Where(IsUnrealGame).ToList();
        if (shipping.Count > 0)
        {
            if (game.Store == GameStore.Custom
                && game.Executable is { } customUnreal
                && shipping.Contains(customUnreal, StringComparer.OrdinalIgnoreCase))
            {
                return customUnreal;
            }

            return Best(shipping);
        }

        if (game.Executable is { } hint && File.Exists(hint))
        {
            // Always honour a program the user picked by hand. For store manifests (Epic/GOG), skip launchers
            // like REDprelauncher.exe when a real game executable (especially next to upscaler DLLs) was found.
            var hintFolder = Path.GetDirectoryName(hint)!;
            if (game.Store == GameStore.Custom
                || (!IsHelper(hint) && (upscalerFolders.Count == 0 || upscalerFolders.Contains(hintFolder))))
                return hint;
        }

        var games = programs.Where(program => !IsHelper(program)).ToList();
        return Best(games.Count > 0 ? games : programs);

        string Best(List<string> among) => among
            .OrderByDescending(program => upscalerFolders.Contains(Path.GetDirectoryName(program)!))
            .ThenByDescending(IsUnrealShipping)
            .ThenByDescending(program => string.Equals(program, game.Executable, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(program => NamedLike(program, game))
            .ThenByDescending(Size)
            .ThenBy(program => program, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>True when <paramref name="target"/> looks like an Unreal Engine binary folder.</summary>
    public static bool IsUnrealTarget(string target)
    {
        var clean = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return clean.EndsWith(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(@"\Binaries\WinGDK", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnrealGame(string program)
    {
        var folder = Path.GetDirectoryName(program)!;
        return IsUnrealTarget(folder) && !IsHelper(program);
    }

    private static bool IsUnrealShipping(string program)
    {
        var name = Path.GetFileNameWithoutExtension(program);
        return name.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_Shipping", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-Retail", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelper(string program)
    {
        var name = Path.GetFileNameWithoutExtension(program);

        return HelperWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
            || program.Contains(@"\Engine\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Witchfire.exe" in a game called "Witchfire": letters and digits only, either inside the other.</summary>
    private static bool NamedLike(string program, Game game)
    {
        var file = Plain(Path.GetFileNameWithoutExtension(program));
        var title = Plain(game.Name);
        var folder = Plain(Path.GetFileName(game.Folder));

        return file.Length > 2 && (title.Contains(file) || file.Contains(title) || folder.Contains(file));

        static string Plain(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static long Size(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (IOException) { return 0; }
    }

    /// <summary>Every file and folder under <paramref name="root"/>, nearest first, skipping what cannot be read.</summary>
    private static IEnumerable<string> Walk(string root)
    {
        var waiting = new Queue<(string Folder, int Depth)>();
        waiting.Enqueue((root, 0));

        var seen = 0;

        while (waiting.Count > 0 && seen < MostFiles)
        {
            var (folder, depth) = waiting.Dequeue();

            string[] files, folders;
            try
            {
                files = Directory.GetFiles(folder);
                folders = Directory.GetDirectories(folder);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            seen += files.Length;
            foreach (var file in files) yield return file;

            foreach (var inner in folders)
            {
                yield return inner;   // a folder's name can be an anti-cheat's mark

                var name = Path.GetFileName(inner);
                var isLink = new DirectoryInfo(inner).Attributes.HasFlag(FileAttributes.ReparsePoint);

                if (depth < Deepest && !isLink && !SkippedFolders.Contains(name) && !AntiCheatMarks.ContainsKey(name))
                    waiting.Enqueue((inner, depth + 1));
            }
        }
    }
}
