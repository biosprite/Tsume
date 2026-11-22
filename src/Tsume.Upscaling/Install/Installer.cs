using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Tsume.Core.Diagnostics;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.Xmx;

namespace Tsume.Upscaling.Install;

/// <summary>
/// Puts OptiScaler into a game's folder and takes it out again. The manifest is saved before any file is written,
/// displaced files are copied to Originals\ first, and removal deletes only files whose hash is still what Tsume placed.
/// </summary>
public sealed class Installer(UpscalingPaths paths)
{
    private readonly XmxStore _xmx = new(paths);

    private const string SettingsFile = "OptiScaler.ini";
    private const string Program = "OptiScaler.dll";

    /// <summary>Files OptiScaler writes while a game runs. Removal deletes them only if they were not there before the install.</summary>
    private static readonly string[] LeftBehindByOptiScaler = ["OptiScaler.log"];

    /// <summary>The build's manual-install scripts, which are not copied into games.</summary>
    private static readonly string[] NotForGames = ["setup_windows.bat", "setup_linux.sh"];

    /// <summary>Free disk space required beyond the files' own size (64 MB).</summary>
    private const long Headroom = 64L << 20;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    private static readonly Journal Log = new("optiscaler");

    /// <summary>The game's install manifest; null when there is none or it cannot be read.</summary>
    public InstallManifest? Find(string gameId)
    {
        var file = ManifestFile(gameId);
        if (!File.Exists(file)) return null;

        try
        {
            return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(file), Format);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            Log.Write($"manifest for {gameId} cannot be read: {error.Message}");
            return null;
        }
    }

    /// <summary>The ids of every game with a manifest, readable or not.</summary>
    public IReadOnlySet<string> InstalledGames()
    {
        var installs = Path.Combine(paths.Root, "Installs");
        if (!Directory.Exists(installs)) return new HashSet<string>();

        return Directory.GetDirectories(installs)
            .Where(folder => File.Exists(Path.Combine(folder, "manifest.json")))
            .Select(folder => Path.GetFileName(folder)!)
            .ToHashSet();
    }

    /// <summary>The installs that can be taken out again: a readable manifest, and the game's folder still there.</summary>
    public IReadOnlyList<InstallManifest> Removable() => [.. InstalledGames()
        .Select(Find)
        .OfType<InstallManifest>()
        .Where(manifest => Directory.Exists(manifest.Target))];

    /// <summary>The tags of the builds that games were installed from.</summary>
    public IReadOnlySet<string> BuildsInUse() =>
        InstalledGames().Select(Find).OfType<InstallManifest>().Select(manifest => manifest.Build).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quick check, without hashing, of whether the install is still intact.</summary>
    public static InstallState Check(InstallManifest? manifest)
    {
        if (manifest is null) return InstallState.None;
        if (!Directory.Exists(manifest.Target)) return InstallState.FolderGone;
        if (!manifest.Complete) return InstallState.CutShort;

        var proxy = new FileInfo(Path.Combine(manifest.Target, manifest.Proxy));
        var placed = manifest.Files.FirstOrDefault(file => file.Path.Equals(manifest.Proxy, StringComparison.OrdinalIgnoreCase));

        return proxy.Exists && proxy.Length == placed?.Length ? InstallState.Healthy : InstallState.Replaced;
    }

    /// <summary>True if the XMX upscaler went into the game with this install.</summary>
    public static bool HasXmx(InstallManifest manifest) => XmxIn(manifest) is not null;

    /// <summary>True if the install is from the build on disk and carries the XMX upscaler Tsume holds now.</summary>
    public bool IsCurrent(InstallManifest manifest, StoredBuild? build) =>
        (build is null || manifest.Build == build.Build.Tag)
        && manifest.Linked is not null   // installed before games shared the store's files: installing again saves the space
        && (_xmx.Current() is not { } held || XmxIn(manifest)?.Sha256 == held.Sha256);

    private static PlacedFile? XmxIn(InstallManifest manifest) =>
        manifest.Files.FirstOrDefault(file => file.Path.Equals(XmxLayout.Upscaler, StringComparison.OrdinalIgnoreCase));

    public InstallManifest Install(Game game, GameFacts facts, StoredBuild build, UpscalerSettings settings)
    {
        if (Find(game.Id) is not null || File.Exists(ManifestFile(game.Id)))
            throw new UpscalingException(Trouble.AlreadyInstalled, game.Name);

        var target = Path.GetFullPath(facts.Target);
        if (!Directory.Exists(target)) throw new UpscalingException(Trouble.FolderMissing, target);
        if (IsRunning(facts.Executable)) throw new UpscalingException(Trouble.GameRunning, game.Name);

        var home = paths.InstallFolder(game.Id);
        var proxy = ProxyNames.Choose(target, settings.Proxy, facts.ExistingProxy);

        var manifest = new InstallManifest
        {
            GameId = game.Id,
            GameName = game.Name,
            Target = target,
            Executable = facts.Executable,
            Proxy = proxy,
            Build = build.Build.Tag,
            InstalledAt = DateTimeOffset.Now,
        };

        try
        {
            MakeSureWritable(target);

            // The XMX upscaler is installed whenever Tsume holds it, chosen or not, so choosing it later is only an ini change.
            var xmx = _xmx.Current() is { } held ? _xmx.Shared(held) : null;
            var ueIntelAtomics = GameInspector.IsUnrealTarget(target) && GpuVendor.HasIntelGpu();

            var plan = Plan(build, proxy, StageSettings(home, build, settings.Fitting(xmx is not null), ueIntelAtomics), xmx);

            // Where the disk allows, the big libraries are not copied: the game gets a second name for Tsume's own file.
            var linking = Files.CanLink(Files.Inside(build.Folder, Program), target);
            manifest.Linked = linking;

            MakeSureOfRoom(target, plan.Where(step => !(linking && step.Shared) && step.SameAs is null).Sum(step => step.Length));

            manifest.Files = [.. plan.Select(step => new PlacedFile
            {
                Path = step.Path,
                Sha256 = step.Sha256,
                Length = step.Length,
                IsSettings = step.Path.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase),
            })];
            manifest.NewFolders = FoldersToMake(target, plan.Select(step => step.Path));
            manifest.Leavings = [.. LeftBehindByOptiScaler.Where(name => !File.Exists(Path.Combine(target, name)))];

            Save(manifest);   // must happen before anything in the game's folder changes

            foreach (var (step, placed) in plan.Zip(manifest.Files))
            {
                var destination = Files.Inside(target, step.Path);

                if (File.Exists(destination))
                {
                    MoveAside(destination, Files.Inside(Path.Combine(home, "Originals"), step.Path), placed);
                    Save(manifest);
                }

                if (step.SameAs is { } twin && Files.TryLink(Files.Inside(target, twin), destination)) continue;
                if (linking && step.Shared && LinkFromStore(step, build, destination)) continue;

                Files.Place(step.Source, destination);
            }

            manifest.Complete = true;
            Save(manifest);

            Files.Discard(Path.Combine(home, "Staged"));
            Log.Write($"installed {build.Build.Tag} as {proxy} in {target} ({plan.Count} files)");
            return manifest;
        }
        catch (Exception error)
        {
            Log.Write($"install in {target} failed: {error.Message}");

            // Undo what was done. If that fails too, the manifest stays and a later removal finishes the job.
            try
            {
                if (File.Exists(ManifestFile(game.Id))) Undo(manifest, keepSettings: false);
                else Files.Discard(Path.Combine(home, "Staged"));   // it never got as far as the game's folder
            }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
            {
                Log.Write($"…and could not be fully undone: {undo.Message}");
            }

            throw Explain(error, target);
        }
    }

    /// <summary>One file to copy into the game's folder.</summary>
    private sealed record Step(string Source, string Path, long Length, string Sha256)
    {
        /// <summary>
        /// True for a file that is the same in every game and that nothing writes to: it may be linked from Tsume's
        /// store rather than copied. Never the proxy (a game update may overwrite it) or the settings file.
        /// </summary>
        public bool Shared { get; init; }

        /// <summary>An earlier step with the same bytes; this file is hard-linked to it where the disk allows.</summary>
        public string? SameAs { get; init; }
    }

    /// <summary>
    /// Everything that goes into the game's folder: the build (OptiScaler.dll under the proxy name, the prepared
    /// settings file), the XMX upscaler with AMD's beside it, then the Extras\ files laid over OptiScaler\, replacing same-named files.
    /// </summary>
    private List<Step> Plan(StoredBuild build, string proxy, string settingsFile, string? xmx)
    {
        var plan = new Dictionary<string, Step>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in build.Files)
        {
            var name = Path.GetFileName(file.Path);
            if (NotForGames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("!! README")) continue;

            var source = Files.Inside(build.Folder, file.Path);

            if (file.Path.Equals(Program, StringComparison.OrdinalIgnoreCase))
                plan[proxy] = new Step(source, proxy, file.Length, file.Sha256);

            else if (file.Path.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase))
                plan[SettingsFile] = new Step(settingsFile, SettingsFile, new FileInfo(settingsFile).Length, Files.Hash(settingsFile));

            else
                plan[file.Path] = new Step(source, file.Path, file.Length, file.Sha256) { Shared = true };
        }

        if (xmx is not null)
        {
            plan[XmxLayout.Upscaler] = new Step(xmx, XmxLayout.Upscaler, new FileInfo(xmx).Length, Files.Hash(xmx)) { Shared = true };

            // The XMX upscaler reads FSR 4.1.2's weights out of AMD's own upscaler (in the nightly), under this other name.
            if (plan.TryGetValue(XmxLayout.WeightsInBuild, out var amd))
                plan[XmxLayout.Weights] = amd with { Path = XmxLayout.Weights, SameAs = XmxLayout.WeightsInBuild };
        }

        if (Directory.Exists(paths.Extras))
        {
            foreach (var extra in Directory.EnumerateFiles(paths.Extras, "*", SearchOption.AllDirectories))
            {
                var path = Path.Combine("OptiScaler", Path.GetRelativePath(paths.Extras, extra));
                plan[path] = new Step(extra, path, new FileInfo(extra).Length, Files.Hash(extra));
            }
        }

        // An extra may have replaced a link's twin: drop such links. Remaining links go last, after their twin is placed.
        return [.. plan.Values
            .Select(step => step.SameAs is { } twin && plan[twin].Sha256 != step.Sha256 ? step with { SameAs = null } : step)
            .OrderBy(step => step.SameAs is not null)];
    }

    /// <summary>
    /// Gives the game a second name for the store's file instead of a copy.
    /// The store's file is checked first: one changed through some game's link must not spread to another.
    /// </summary>
    /// <returns>False if it could not be linked; the caller copies instead.</returns>
    private static bool LinkFromStore(Step step, StoredBuild build, string destination)
    {
        if (Files.Hash(step.Source) != step.Sha256)
        {
            Log.Write($"{step.Source} is no longer the file that was downloaded");
            BuildStore.Distrust(build);
            throw new UpscalingException(Trouble.BuildDamaged, step.Path);
        }

        return Files.LetEveryoneRead(step.Source) && Files.TryLink(step.Source, destination);
    }

    /// <summary>
    /// The settings file to install: the one saved from an update (so overlay tuning survives),
    /// else the build's clean one, with Tsume's settings written into it.
    /// </summary>
    private static string StageSettings(string home, StoredBuild build, UpscalerSettings settings, bool ueIntelAtomics)
    {
        var saved = Path.Combine(home, "Saved", SettingsFile);
        var ini = IniFile.Load(File.Exists(saved) ? saved : Path.Combine(build.Folder, SettingsFile));

        settings.WriteTo(ini, ueIntelAtomics);

        var staged = Path.Combine(home, "Staged", SettingsFile);
        ini.Save(staged);
        return staged;
    }

    /// <summary>Copies the file that is in the way to Originals\ and verifies the copy by hash.</summary>
    private static void MoveAside(string existing, string keep, PlacedFile placed)
    {
        var hash = Files.Hash(existing);

        Files.Place(existing, keep);
        if (Files.Hash(keep) != hash) throw new IOException($"The copy of {Path.GetFileName(existing)} did not match the file.");

        placed.OriginalSha256 = hash;
    }

    /// <summary>The folders the plan needs that are not there yet, parents before children.</summary>
    private static List<string> FoldersToMake(string target, IEnumerable<string> files)
    {
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
            for (var folder = Path.GetDirectoryName(file); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                if (!Directory.Exists(Path.Combine(target, folder))) folders.Add(folder);

        return [.. folders.OrderBy(folder => folder.Length)];
    }

    /// <summary>
    /// Takes OptiScaler out of the game and puts the game's own files back.
    /// Safe to repeat: an interrupted removal picks up where it stopped.
    /// </summary>
    /// <param name="keepSettings">
    /// True during a healthy update so in-game overlay tuning survives; false for an explicit removal or repair
    /// so a broken OptiScaler.ini does not carry over into a fresh install.
    /// </param>
    public RemovalReport Remove(string gameId, bool keepSettings = false)
    {
        var manifest = Find(gameId) ?? throw new UpscalingException(Trouble.NotInstalled, gameId);

        if (!Directory.Exists(manifest.Target)) throw new UpscalingException(Trouble.FolderMissing, manifest.Target);
        if (IsRunning(manifest.Executable)) throw new UpscalingException(Trouble.GameRunning, manifest.GameName);

        try
        {
            var report = Undo(manifest, keepSettings: keepSettings && manifest.Complete);
            Log.Write($"removed from {manifest.Target}; left alone: {report.LeftAlone.Count}");
            return report;
        }
        catch (Exception error)
        {
            Log.Write($"removal from {manifest.Target} failed: {error.Message}");
            throw Explain(error, manifest.Target);
        }
    }

    /// <summary>
    /// For a game whose folder has gone: drops the manifest and the kept originals.
    /// The saved settings file stays, in case the game returns.
    /// </summary>
    public void Forget(string gameId)
    {
        var home = paths.InstallFolder(gameId);

        Files.Delete(ManifestFile(gameId));
        Files.Discard(Path.Combine(home, "Originals"));
        Files.Discard(Path.Combine(home, "Staged"));
    }

    private RemovalReport Undo(InstallManifest manifest, bool keepSettings)
    {
        var home = paths.InstallFolder(manifest.GameId);
        var originals = Path.Combine(home, "Originals");
        var leftAlone = new List<string>();
        var orphans = new List<string>();   // originals whose place has been taken by something newer

        foreach (var placed in Enumerable.Reverse(manifest.Files))
        {
            var file = Files.Inside(manifest.Target, placed.Path);
            var original = placed.OriginalSha256 is null ? null : Files.Inside(originals, placed.Path);

            if (File.Exists(file))
            {
                var now = Files.Hash(file);

                if (now == placed.OriginalSha256) continue;   // the original, never replaced or already back

                // The settings file is Tsume's by name once the install is whole (OptiScaler rewrites it). In one cut
                // short it may be an older file not yet moved aside, so there it must match like any other.
                if (!(placed.IsSettings && manifest.Complete) && now != placed.Sha256)
                {
                    // Changed since Tsume placed it (a game update, usually), so not Tsume's to delete.
                    leftAlone.Add(placed.Path);
                    if (original is not null && File.Exists(original)) orphans.Add(placed.Path);
                    continue;
                }

                if (placed.IsSettings && keepSettings) Files.Place(file, Path.Combine(home, "Saved", SettingsFile));

                Files.Delete(file);
            }

            if (original is not null && File.Exists(original)) Files.Place(original, file);
        }

        foreach (var name in manifest.Leavings) Files.Delete(Files.Inside(manifest.Target, name));

        // A folder Tsume made is removed only if empty: hand-added files (plugins, Streamline) stay.
        foreach (var folder in manifest.NewFolders.OrderByDescending(folder => folder.Length))
        {
            var path = Files.Inside(manifest.Target, folder);
            if (Directory.Exists(path) && Files.IsEmpty(path)) Directory.Delete(path);
        }

        // Originals that could not go back without overwriting something newer are moved to Kept\.
        string? kept = null;
        if (orphans.Count > 0)
        {
            kept = Path.Combine(home, "Kept");
            foreach (var path in orphans) Files.Place(Files.Inside(originals, path), Files.Inside(kept, path));
        }

        Files.Delete(ManifestFile(manifest.GameId));
        Files.Discard(originals);
        Files.Discard(Path.Combine(home, "Staged"));
        if (!keepSettings) Files.Discard(Path.Combine(home, "Saved"));

        return new RemovalReport(leftAlone, kept);
    }

    /// <summary>The game's settings as its OptiScaler.ini has them now, keeping the remembered proxy preference.</summary>
    public static UpscalerSettings ReadSettings(InstallManifest manifest, UpscalerSettings remembered)
    {
        try
        {
            var ini = IniFile.Load(Path.Combine(manifest.Target, SettingsFile));
            return remembered.CorrectedBy(ini);
        }
        catch (IOException)
        {
            return remembered;
        }
    }

    /// <summary>Writes the settings into the game's OptiScaler.ini. They take effect the next time the game starts.</summary>
    public static void WriteSettings(InstallManifest manifest, UpscalerSettings settings)
    {
        if (!Directory.Exists(manifest.Target)) throw new UpscalingException(Trouble.FolderMissing, manifest.Target);

        try
        {
            var file = Path.Combine(manifest.Target, SettingsFile);
            var ini = IniFile.Load(file);
            var ueIntelAtomics = GameInspector.IsUnrealTarget(manifest.Target) && GpuVendor.HasIntelGpu();

            settings.Fitting(HasXmx(manifest)).WriteTo(ini, ueIntelAtomics);
            ini.Save(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Explain(error, manifest.Target);
        }
    }

    /// <summary>True if the program is running, or might be (Windows would not show the process's path).</summary>
    public static bool IsRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    return true;   // same name, and Windows will not show its path: assume it is the game
                }
            }
        }

        return false;
    }

    /// <summary>Throws NotWritable for folders that need administrator rights (Program Files).</summary>
    private static void MakeSureWritable(string folder)
    {
        var probe = Path.Combine(folder, ".tsume-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UpscalingException(Trouble.NotWritable, folder, error);
        }
    }

    private static void MakeSureOfRoom(string folder, long needed)
    {
        var drive = new DriveInfo(Path.GetPathRoot(folder)!);
        if (drive.AvailableFreeSpace < needed + Headroom) throw new UpscalingException(Trouble.DiskFull, drive.Name);
    }

    /// <summary>Maps a system error to an <see cref="UpscalingException"/> where one fits.</summary>
    private static Exception Explain(Exception error, string folder) => error switch
    {
        UpscalingException                              => error,
        UnauthorizedAccessException                     => new UpscalingException(Trouble.NotWritable, folder, error),
        IOException io when BuildStore.IsDiskFull(io)   => new UpscalingException(Trouble.DiskFull, folder, error),
        IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21   // sharing or lock violation
                                                        => new UpscalingException(Trouble.FileInUse, io.Message, error),
        _                                               => error,
    };

    private string ManifestFile(string gameId) => Path.Combine(paths.InstallFolder(gameId), "manifest.json");

    private void Save(InstallManifest manifest) =>
        Files.WriteWhole(ManifestFile(manifest.GameId), JsonSerializer.Serialize(manifest, Format));
}
