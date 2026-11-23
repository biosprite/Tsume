using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Tsume.Core.Diagnostics;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.OptiPatcher;
using Tsume.Upscaling.Xmx;

namespace Tsume.Upscaling.Install;

/// <summary>
/// Puts OptiScaler into a game's folder and takes it out again. The manifest is saved before any file is written,
/// displaced files are copied to Originals\ first, and removal deletes only files whose hash is still what Tsume placed.
/// </summary>
public sealed class Installer(UpscalingPaths paths, bool? hasIntelXmxGpu = null)
{
    private readonly XmxStore _xmx = new(paths);
    private readonly OptiPatcherStore _optiPatcher = new(paths);
    private readonly bool _supportsXmx = hasIntelXmxGpu ?? GpuVendor.HasIntelXmxGpu();

    private const string SettingsFile = "OptiScaler.ini";
    private const string Program = "OptiScaler.dll";

    /// <summary>Files OptiScaler writes while a game runs. Removal deletes them only if they were not there before the install.</summary>
    private static readonly string[] LeftBehindByOptiScaler = ["OptiScaler.log"];

    /// <summary>The build's manual-install scripts, which are not copied into games.</summary>
    private static readonly string[] NotForGames = ["setup_windows.bat", "setup_linux.sh", "Streamlined_fetcher_windows.bat"];

    /// <summary>Pre-0.9 companion DLLs that conflict with current OptiScaler builds if left beside the game's executable.</summary>
    private static readonly string[] LegacyConflictFiles = ["nvngx.dll", "nvapi64.dll"];

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

    /// <summary>True if OptiPatcher went into the game with this install.</summary>
    public static bool HasOptiPatcher(InstallManifest manifest) => OptiPatcherIn(manifest) is not null;

    /// <summary>True if OptiPatcher is installed in this game and ASI plugin loading is enabled in its OptiScaler.ini.</summary>
    public static bool IsOptiPatcherEnabled(InstallManifest manifest)
    {
        if (!HasOptiPatcher(manifest)) return false;

        try
        {
            var ini = IniFile.Load(Path.Combine(manifest.Target, SettingsFile));
            return string.Equals(ini.Get("Plugins", "LoadAsiPlugins"), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True if this install does not use OptiPatcher, Tsume holds no OptiPatcher, the user's own copy in Extras\
    /// overrides it, or this install already carries the exact OptiPatcher binary held by Tsume.
    /// </summary>
    public bool IsOptiPatcherCurrent(InstallManifest manifest, StoredOptiPatcher? patcher) =>
        patcher is null
        || !HasOptiPatcher(manifest)
        || ExtrasHaveOptiPatcher
        || OptiPatcherIn(manifest)?.Sha256 == patcher.Release.Sha256;

    /// <summary>True if the user put their own OptiPatcher.asi in Extras\plugins\, which replaces the downloaded one.</summary>
    public bool ExtrasHaveOptiPatcher => File.Exists(Path.Combine(paths.Extras, "plugins", OptiPatcherLayout.FileName));

    /// <summary>True if the install is from the build on disk and carries the XMX upscaler and (when used) OptiPatcher Tsume holds now.</summary>
    public bool IsCurrent(InstallManifest manifest, StoredBuild? build) =>
        IsCurrent(manifest, build, _xmx.Current(), _optiPatcher.Current());

    /// <summary>Same as <see cref="IsCurrent(InstallManifest, StoredBuild?)"/> using already-loaded store records.</summary>
    public bool IsCurrent(InstallManifest manifest, StoredBuild? build, XmxUpscaler? xmx, StoredOptiPatcher? patcher) =>
        (build is null || manifest.Build == build.Build.Tag)
        && manifest.Linked is not null   // installed before games shared the store's files: installing again saves the space
        && (!_supportsXmx || xmx is null || XmxIn(manifest)?.Sha256 == xmx.Sha256)
        && IsOptiPatcherCurrent(manifest, patcher);

    private static PlacedFile? XmxIn(InstallManifest manifest) =>
        manifest.Files.FirstOrDefault(file => file.Path.Equals(XmxLayout.Upscaler, StringComparison.OrdinalIgnoreCase));

    private static PlacedFile? OptiPatcherIn(InstallManifest manifest) =>
        manifest.Files.FirstOrDefault(file => file.Path.Equals(OptiPatcherLayout.Plugin, StringComparison.OrdinalIgnoreCase));

    public InstallManifest Install(Game game, GameFacts facts, StoredBuild build, UpscalerSettings settings)
    {
        if (Find(game.Id) is not null || File.Exists(ManifestFile(game.Id)))
            throw new UpscalingException(Trouble.AlreadyInstalled, game.Name);

        var target = Path.GetFullPath(facts.Target);
        if (!Directory.Exists(target)) throw new UpscalingException(Trouble.FolderMissing, target);
        if (IsRunning(facts.Executable)) throw new UpscalingException(Trouble.GameRunning, game.Name);

        var home = paths.InstallFolder(game.Id);
        var proxy = ProxyNames.Choose(settings.Proxy, facts.ExistingProxy);

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

            // XMX is installed on Intel XMX GPUs whenever Tsume holds it, chosen or not, because OptiScaler only loads it when selected.
            // OptiPatcher is placed only when enabled for this game (or overridden in Extras\), so it never loads
            // alongside the user's own ASI plugins or blocks installs when unused.
            var xmxHeld = _supportsXmx ? _xmx.Current() : null;
            var xmxFile = xmxHeld is not null ? _xmx.Shared(xmxHeld) : null;

            var extrasHasOptiPatcher = ExtrasHaveOptiPatcher;
            StoredOptiPatcher? patcher = null;
            if (settings.OptiPatcher && !extrasHasOptiPatcher)
            {
                patcher = _optiPatcher.Current();
                if (patcher is not null) OptiPatcherStore.Verified(patcher);
            }

            var hasOptiPatcher = patcher is not null || extrasHasOptiPatcher;
            var keepOtherAsiPlugins = HasOtherAsiPlugins(target);
            var ueIntelAtomics = GameInspector.IsUnrealTarget(target) && GpuVendor.HasIntelGpu();

            var staged = StageSettings(
                home,
                build,
                settings.Fitting(xmxFile is not null, hasOptiPatcher, hasIntelXmxGpu: _supportsXmx),
                ueIntelAtomics,
                hasOptiPatcher,
                keepOtherAsiPlugins);
            var plan = Plan(build, proxy, staged, xmxHeld, xmxFile, patcher);
            var retired = ConflictingFilesToRetire(target, proxy, plan);

            // Record the store's OptiPatcher tag only when the placed binary actually came from the store (not overridden by Extras\).
            var placedPatcher = plan.FirstOrDefault(step => step.Path.Equals(OptiPatcherLayout.Plugin, StringComparison.OrdinalIgnoreCase));
            manifest.OptiPatcher = patcher is not null && placedPatcher?.Sha256 == patcher.Release.Sha256
                ? patcher.Release.Tag
                : null;

            // Where the disk allows, the big libraries are not copied: the game gets a second name for Tsume's own file.
            var linking = Files.CanLink(Files.Inside(build.Folder, Program), target);
            manifest.Linked = linking;

            MakeSureOfRoom(target, plan.Where(step => !(linking && step.Shared) && step.SameAs is null).Sum(step => step.Length));

            manifest.Files =
            [
                .. plan.Select(step => new PlacedFile
                {
                    Path = step.Path,
                    Sha256 = step.Sha256,
                    Length = step.Length,
                    IsSettings = step.Path.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase),
                }),
                .. retired.Select(name => new PlacedFile { Path = name }),
            ];
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
                if (linking && step.Shared && LinkFromStore(step, build, patcher, destination)) continue;

                Files.Place(step.Source, destination);
            }

            foreach (var placed in manifest.Files.Skip(plan.Count))
            {
                var conflicting = Files.Inside(target, placed.Path);
                if (!File.Exists(conflicting)) continue;

                MoveAside(conflicting, Files.Inside(Path.Combine(home, "Originals"), placed.Path), placed);
                Save(manifest);
                Files.Delete(conflicting);
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

            // Undo what was done. Keep any pre-existing Saved\ settings from an update removal so a retry keeps tuning.
            // If undo fails too, the manifest stays and a later removal finishes the job.
            try
            {
                if (File.Exists(ManifestFile(game.Id))) Undo(manifest, keepSettings: false, discardSaved: false);
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
    /// settings file), the XMX upscaler with AMD's beside it, OptiPatcher when enabled, then the Extras\ files laid
    /// over OptiScaler\, replacing same-named files.
    /// </summary>
    private List<Step> Plan(StoredBuild build, string proxy, string settingsFile, XmxUpscaler? xmxHeld, string? xmxFile, StoredOptiPatcher? optiPatcher)
    {
        var plan = new Dictionary<string, Step>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in build.Files)
        {
            var name = Path.GetFileName(file.Path);
            if (NotForGames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("!!", StringComparison.Ordinal)) continue;

            var source = Files.Inside(build.Folder, file.Path);

            if (file.Path.Equals(Program, StringComparison.OrdinalIgnoreCase))
                plan[proxy] = new Step(source, proxy, file.Length, file.Sha256);

            else if (file.Path.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase))
                plan[SettingsFile] = new Step(settingsFile, SettingsFile, new FileInfo(settingsFile).Length, Files.Hash(settingsFile));

            else
                plan[file.Path] = new Step(source, file.Path, file.Length, file.Sha256) { Shared = true };
        }

        if (xmxHeld is not null && xmxFile is not null)
        {
            plan[XmxLayout.Upscaler] = new Step(xmxFile, XmxLayout.Upscaler, xmxHeld.Length, xmxHeld.Sha256) { Shared = true };

            // The XMX upscaler reads FSR 4.1.2's weights out of AMD's own upscaler (in the nightly), under this other name.
            if (plan.TryGetValue(XmxLayout.WeightsInBuild, out var amd))
                plan[XmxLayout.Weights] = amd with { Path = XmxLayout.Weights, SameAs = XmxLayout.WeightsInBuild };
        }

        if (optiPatcher is not null)
        {
            plan[OptiPatcherLayout.Plugin] = new Step(
                optiPatcher.File,
                OptiPatcherLayout.Plugin,
                new FileInfo(optiPatcher.File).Length,
                optiPatcher.Release.Sha256) { Shared = true };
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
    private static bool LinkFromStore(Step step, StoredBuild build, StoredOptiPatcher? patcher, string destination)
    {
        if (Files.Hash(step.Source) != step.Sha256)
        {
            Log.Write($"{step.Source} is no longer the file that was downloaded");
            if (patcher is not null && step.Path.Equals(OptiPatcherLayout.Plugin, StringComparison.OrdinalIgnoreCase))
            {
                OptiPatcherStore.Distrust(patcher);
                throw new UpscalingException(Trouble.OptiPatcherDamaged, step.Path);
            }

            if (step.Path.Equals(XmxLayout.Upscaler, StringComparison.OrdinalIgnoreCase))
                throw new UpscalingException(Trouble.NotXmx, step.Path);

            BuildStore.Distrust(build);
            throw new UpscalingException(Trouble.BuildDamaged, step.Path);
        }

        return Files.LetEveryoneRead(step.Source) && Files.TryLink(step.Source, destination);
    }

    /// <summary>
    /// The settings file to install: the one saved from an update (so overlay tuning survives),
    /// else the build's clean one, with Tsume's settings written into it.
    /// </summary>
    private static string StageSettings(string home, StoredBuild build, UpscalerSettings settings,
        bool ueIntelAtomics, bool hasOptiPatcher, bool keepOtherAsiPlugins)
    {
        var saved = Path.Combine(home, "Saved", SettingsFile);
        var ini = IniFile.Load(File.Exists(saved) ? saved : Path.Combine(build.Folder, SettingsFile));

        settings.WriteTo(ini, ueIntelAtomics, hasOptiPatcher, keepOtherAsiPlugins);

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
            var report = Undo(manifest, keepSettings: keepSettings && manifest.Complete, discardSaved: !keepSettings);
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

    private RemovalReport Undo(InstallManifest manifest, bool keepSettings, bool discardSaved)
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
        if (discardSaved) Files.Discard(Path.Combine(home, "Saved"));

        return new RemovalReport(leftAlone, kept);
    }

    /// <summary>The game's settings as its OptiScaler.ini has them now, keeping the remembered proxy preference.</summary>
    public static UpscalerSettings ReadSettings(InstallManifest manifest, UpscalerSettings remembered)
    {
        try
        {
            var ini = IniFile.Load(Path.Combine(manifest.Target, SettingsFile));
            return remembered.CorrectedBy(ini, hasOptiPatcher: HasOptiPatcher(manifest));
        }
        catch (IOException)
        {
            return remembered;
        }
    }

    /// <summary>
    /// Writes the settings into the game's OptiScaler.ini, and places or removes <c>OptiPatcher.asi</c> when its
    /// toggle changes. Settings take effect the next time the game starts.
    /// </summary>
    public void WriteSettings(InstallManifest manifest, UpscalerSettings settings)
    {
        if (!Directory.Exists(manifest.Target)) throw new UpscalingException(Trouble.FolderMissing, manifest.Target);

        try
        {
            var hadOptiPatcher = HasOptiPatcher(manifest);
            var manageAsiPlugins = hadOptiPatcher;

            if (settings.OptiPatcher && !hadOptiPatcher)
            {
                if (AddOptiPatcher(manifest)) manageAsiPlugins = true;
            }
            else if (!settings.OptiPatcher && hadOptiPatcher)
            {
                RemoveOptiPatcher(manifest);
                manageAsiPlugins = true;
            }

            var hasOptiPatcher = HasOptiPatcher(manifest);
            var keepOtherAsiPlugins = HasOtherAsiPlugins(manifest.Target);

            var file = Path.Combine(manifest.Target, SettingsFile);
            var ini = IniFile.Load(file);
            var ueIntelAtomics = GameInspector.IsUnrealTarget(manifest.Target) && GpuVendor.HasIntelGpu();

            settings.Fitting(HasXmx(manifest), hasOptiPatcher, hasIntelXmxGpu: _supportsXmx)
                .WriteTo(ini, ueIntelAtomics, manageAsiPlugins, keepOtherAsiPlugins);
            ini.Save(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Explain(error, manifest.Target);
        }
    }

    /// <summary>
    /// Places <c>OptiPatcher.asi</c> into an already-installed game and updates its manifest.
    /// Returns false when neither Extras\ nor Tsume's store holds OptiPatcher.
    /// </summary>
    private bool AddOptiPatcher(InstallManifest manifest)
    {
        string source;
        string sha256;
        long length;
        string? tag;
        var fromStore = false;

        if (ExtrasHaveOptiPatcher)
        {
            source = Path.Combine(paths.Extras, "plugins", OptiPatcherLayout.FileName);
            sha256 = Files.Hash(source);
            length = new FileInfo(source).Length;
            tag = null;
        }
        else if (_optiPatcher.Current() is { } patcher)
        {
            source = OptiPatcherStore.Verified(patcher);
            sha256 = patcher.Release.Sha256;
            length = new FileInfo(source).Length;
            tag = patcher.Release.Tag;
            fromStore = true;
        }
        else
        {
            return false;
        }

        if (IsRunning(manifest.Executable)) throw new UpscalingException(Trouble.GameRunning, manifest.GameName);

        var home = paths.InstallFolder(manifest.GameId);
        var destination = Files.Inside(manifest.Target, OptiPatcherLayout.Plugin);
        var newFolders = FoldersToMake(manifest.Target, [OptiPatcherLayout.Plugin]);
        var addedFolders = newFolders.Where(folder => !manifest.NewFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)).ToList();

        var placed = new PlacedFile
        {
            Path = OptiPatcherLayout.Plugin,
            Sha256 = sha256,
            Length = length,
        };

        var previousTag = manifest.OptiPatcher;
        manifest.Files.Add(placed);
        manifest.NewFolders.AddRange(addedFolders);
        manifest.OptiPatcher = tag;

        var original = Files.Inside(Path.Combine(home, "Originals"), placed.Path);

        try
        {
            // Record the file already there before anything in the game's folder changes, as Install does:
            // an add cut short then still finds it, in place or in Originals\, and puts it back.
            placed.OriginalSha256 = File.Exists(destination) ? Files.Hash(destination) : null;
            Save(manifest);

            if (placed.OriginalSha256 is not null) MoveAside(destination, original, placed);

            var linked = fromStore && manifest.Linked == true && Files.LetEveryoneRead(source) && Files.TryLink(source, destination);
            if (!linked) Files.Place(source, destination);

            return true;
        }
        catch
        {
            try
            {
                // Delete only what Tsume put there: an original that never got moved aside stays.
                var originalMoved = placed.OriginalSha256 is not null && File.Exists(original);
                if (File.Exists(destination) && Files.Hash(destination) == placed.Sha256
                    && (placed.OriginalSha256 is null || originalMoved))
                {
                    Files.Delete(destination);
                }

                if (originalMoved)
                {
                    Files.Place(original, destination);
                    Files.Delete(original);
                }

                foreach (var folder in addedFolders.OrderByDescending(folder => folder.Length))
                {
                    var path = Files.Inside(manifest.Target, folder);
                    if (Directory.Exists(path) && Files.IsEmpty(path)) Directory.Delete(path);
                }

                manifest.Files.Remove(placed);
                foreach (var folder in addedFolders) manifest.NewFolders.Remove(folder);
                manifest.OptiPatcher = previousTag;
                Save(manifest);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                Log.Write($"could not roll back OptiPatcher placement in {manifest.Target}: {rollback.Message}");
            }

            throw;
        }
    }

    /// <summary>
    /// Removes <c>OptiPatcher.asi</c> from an already-installed game, restores any displaced original,
    /// cleans up an empty plugins folder if Tsume created it, and updates the manifest.
    /// </summary>
    private void RemoveOptiPatcher(InstallManifest manifest)
    {
        if (OptiPatcherIn(manifest) is not { } placed) return;
        if (IsRunning(manifest.Executable)) throw new UpscalingException(Trouble.GameRunning, manifest.GameName);

        var home = paths.InstallFolder(manifest.GameId);
        var file = Files.Inside(manifest.Target, placed.Path);
        var original = placed.OriginalSha256 is null ? null : Files.Inside(Path.Combine(home, "Originals"), placed.Path);

        if (File.Exists(file))
        {
            var now = Files.Hash(file);
            if (now == placed.Sha256)
            {
                Files.Delete(file);
                if (original is not null && File.Exists(original))
                {
                    Files.Place(original, file);
                    Files.Delete(original);
                }
            }
        }
        else if (original is not null && File.Exists(original))
        {
            Files.Place(original, file);
            Files.Delete(original);
        }

        var pluginsFolder = Path.GetDirectoryName(placed.Path);
        if (!string.IsNullOrEmpty(pluginsFolder)
            && manifest.NewFolders.Contains(pluginsFolder, StringComparer.OrdinalIgnoreCase))
        {
            var fullPluginsFolder = Files.Inside(manifest.Target, pluginsFolder);
            if (Directory.Exists(fullPluginsFolder) && Files.IsEmpty(fullPluginsFolder))
            {
                Directory.Delete(fullPluginsFolder);
                manifest.NewFolders.RemoveAll(folder => folder.Equals(pluginsFolder, StringComparison.OrdinalIgnoreCase));
            }
        }

        manifest.Files.Remove(placed);
        manifest.OptiPatcher = null;
        Save(manifest);
    }

    /// <summary>
    /// Secondary OptiScaler DLLs (including <c>OptiScaler.asi</c>) and pre-0.9 <c>nvngx.dll</c> / <c>nvapi64.dll</c>
    /// sitting in <paramref name="target"/> that would double-load or conflict with the installed proxy. Moved to
    /// <c>Originals\</c> on install and put back on removal.
    /// </summary>
    private static List<string> ConflictingFilesToRetire(string target, string proxy, IReadOnlyList<Step> plan)
    {
        var planned = plan.Select(step => step.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = new List<string>();

        foreach (var candidate in ProxyNames.Recognised)
        {
            if (candidate.Equals(proxy, StringComparison.OrdinalIgnoreCase) || planned.Contains(candidate))
                continue;

            var full = Path.Combine(target, candidate);
            if (!File.Exists(full)) continue;

            if (candidate.Equals(ProxyNames.AsiName, StringComparison.OrdinalIgnoreCase) || GameInspector.IsOptiScalerBinary(full))
                retired.Add(candidate);
        }

        foreach (var legacy in LegacyConflictFiles)
        {
            if (planned.Contains(legacy)) continue;
            if (File.Exists(Path.Combine(target, legacy))) retired.Add(legacy);
        }

        return retired;
    }

    /// <summary>
    /// True if any <c>*.asi</c> plugin other than OptiPatcher is in the game's OptiScaler plugins folder, or in
    /// Extras\plugins\ (laid over OptiScaler\ at every install, so it may not be in the game yet).
    /// </summary>
    private bool HasOtherAsiPlugins(string target) =>
        HasOtherAsiPluginsIn(Path.Combine(target, "OptiScaler", "plugins"))
        || HasOtherAsiPluginsIn(Path.Combine(paths.Extras, "plugins"));

    private static bool HasOtherAsiPluginsIn(string pluginsDir)
    {
        if (!Directory.Exists(pluginsDir)) return false;

        try
        {
            return Directory.EnumerateFiles(pluginsDir, "*.asi")
                .Any(file => !Path.GetFileName(file).Equals(OptiPatcherLayout.FileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
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
