using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.App.Theme;
using Tsume.Core.Diagnostics;
using Tsume.Upscaling;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.OptiPatcher;
using Tsume.Upscaling.Xmx;

namespace Tsume.App.ViewModels;

/// <summary>
/// The OptiScaler games list, and the state shared by its sub-pages (Game, Build, Defaults, Browse).
/// The list is read in the background when the panel first opens (<see cref="PrepareAsync"/>);
/// the work itself is in Tsume.Upscaling.
/// </summary>
public sealed class UpscalingViewModel : LayerViewModel
{
    private const string OptiScalerCompatibilityUrl = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";
    private const string OptiPatcherCompatibilityUrl = "https://github.com/optiscaler/OptiPatcher/blob/main/GameSupport.md";
    private static readonly Journal Log = new("optiscaler");

    private readonly Lock _listLoadLock = new();
    private (GameList List, GameList.Serialized Snapshot)? _loadedFromDisk;
    private GameList? _list;
    private GameList.Serialized? _savedSnapshot;
    private int _rowsVersion;
    private CancellationTokenSource? _rowsCancel;
    private Task _rowsTask = Task.CompletedTask;
    private Task? _preparing;
    private bool _rescanRequested;
    private bool _manualScan;

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public UpscalingViewModel(Func<Task> dismiss)
    {
        Builds = new BuildStore(Paths);
        Installer = new Installer(Paths);
        Xmx = new XmxStore(Paths);
        OptiPatcher = new OptiPatcherStore(Paths);

        Game = new UpscalingGameViewModel(this, dismiss);
        Build = new UpscalingBuildViewModel(this, dismiss);
        Defaults = new UpscalingDefaultsViewModel(this);
        Browse = new UpscalingBrowseViewModel(this);
        Pages = [Game, Build, Defaults, Browse];
        RestoreAll = new RestoreAllViewModel(this);

        // Refresh the list when a page closes.
        foreach (var page in Pages)
            page.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsOpen) && !page.IsOpen && IsOpen) ShowGames(); };

        // The panel stays open: the key press is posted straight to the game behind it.
        OpenGameMenu = new Command(QuickActions.OpenOptiScalerMenu);
        Rescan = new Command(() => { if (!IsBusy) _ = LookForGamesAsync(manual: true); });

        OpenOptiScalerCompatibility = new Command(() => _ = OpenUrlAsync(OptiScalerCompatibilityUrl, dismiss));
        OpenOptiPatcherCompatibility = new Command(() => _ = OpenUrlAsync(OptiPatcherCompatibilityUrl, dismiss));
    }

    private static async Task OpenUrlAsync(string url, Func<Task> dismiss)
    {
        // Delegate through explorer.exe first so UIAccess integrity does not block ShellExecute
        // protocol activation (e.g. Microsoft Edge / MSEdgeHTM).
        var opened = false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { url }, UseShellExecute = false });
            opened = true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"explorer.exe could not open {url}: {error.Message}");
        }

        if (!opened)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                opened = true;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Write($"ShellExecute could not open {url}: {error.Message}");
            }
        }

        if (opened) await dismiss();
    }

    public UpscalingPaths Paths { get; } = new();
    public BuildStore Builds { get; }
    public Installer Installer { get; }

    /// <summary>The XMX build of FSR 4, if Tsume has been given it.</summary>
    public XmxStore Xmx { get; }

    /// <summary>The OptiPatcher ASI plugin, if downloaded from GitHub.</summary>
    public OptiPatcherStore OptiPatcher { get; }

    /// <summary>The games and their settings, loaded from disk on first use.</summary>
    public GameList List => _list ??= LoadList();

    /// <summary>
    /// Reads the list and makes its initial rows in the background, once, so the first opening of the screen
    /// does not wait on the file, the first use of the JSON serializer, or local store/manifest reads.
    /// Does not touch game folders, so sleeping drives are not woken until the screen actually opens.
    /// </summary>
    /// <returns>The same task on every call; done once the initial rows are made.</returns>
    public Task PrepareAsync() => _preparing ??= LoadAheadAsync();

    private async Task LoadAheadAsync()
    {
        try
        {
            if (_list is not null)
            {
                await _rowsTask;
                return;
            }

            var (list, snapshot) = await Task.Run(LoadListFromDisk);

            if (_list is not null)
            {
                // The screen opened meanwhile and took the list on the spot.
                await _rowsTask;
                return;
            }

            _list = list;
            _savedSnapshot = snapshot;

            var refresh = ShowGamesAsync(verifyGameFolders: false);
            await refresh;

            // If the screen opened while preload rows were being read and superseded them, wait for its rows too.
            if (!ReferenceEquals(_rowsTask, refresh)) await _rowsTask;
        }
        catch (Exception error)
        {
            Log.Write($"games list could not be preloaded: {error.Message}");
            if (_list is null) _preparing = null;
        }
    }

    private GameList LoadList()
    {
        var (list, snapshot) = LoadListFromDisk();
        _savedSnapshot = snapshot;
        return list;
    }

    /// <summary>
    /// Loads games.json at most once across the preload thread and the UI thread so the two never read or back up
    /// the file concurrently, nor race a subsequent save. Whichever asks second gets the same list.
    /// </summary>
    private (GameList List, GameList.Serialized Snapshot) LoadListFromDisk()
    {
        lock (_listLoadLock)
        {
            if (_loadedFromDisk is { } loaded) return loaded;

            var list = GameList.Load(Paths);
            var result = (list, list.Serialize());
            _loadedFromDisk = result;
            return result;
        }
    }

    /// <summary>Writes the list to disk unless it is unchanged since it was last read or written.</summary>
    /// <returns>True if the list had changed.</returns>
    public bool SaveList()
    {
        var snapshot = List.Serialize();
        if (snapshot == _savedSnapshot) return false;

        if (GameList.Save(Paths, snapshot)) _savedSnapshot = snapshot;
        return true;
    }

    /// <summary>True while files are being downloaded or moved; other actions are refused meanwhile.</summary>
    public bool IsBusy
    {
        get;
        set { if (Set(ref field, value)) Notify(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsBusy;

    public UpscalingGameViewModel Game { get; }
    public UpscalingBuildViewModel Build { get; }
    public UpscalingDefaultsViewModel Defaults { get; }
    public UpscalingBrowseViewModel Browse { get; }

    /// <summary>The sheet that asks before OptiScaler is taken out of every game.</summary>
    public RestoreAllViewModel RestoreAll { get; }

    /// <summary>The screens that open over this one.</summary>
    public IReadOnlyList<LayerViewModel> Pages { get; }

    public bool HasPageOpen => Pages.Any(page => page.IsOpen);

    /// <summary>A build tag's date in DD MMM YY format (e.g. "22 Nov 26"); the tag itself if it has no date.</summary>
    public static string Night(string buildTag) =>
        new NightlyBuild(buildTag, "", "", 0, "").Date is { } date
            ? date.ToString("dd MMM yy", CultureInfo.CurrentCulture)
            : buildTag;

    /// <summary>
    /// The build to install from: the one on disk, or the newest, downloaded now, if there is none.
    /// Never goes online when a build is already on disk. When downloading for the first time, also fetches
    /// OptiPatcher best-effort so games can enable it right away.
    /// </summary>
    public async Task<StoredBuild> NeedBuildAsync(Action<string> say)
    {
        if (await Task.Run(Builds.Current) is { } here) return here;

        say(Strings.CheckingBuild);
        var newest = await NightlyFeed.NewestAsync();

        var stored = await FetchAsync(newest, say);

        if (await Task.Run(OptiPatcher.Current) is null)
        {
            try
            {
                say(Strings.CheckingOptiPatcher);
                var patcher = await OptiPatcherFeed.NewestAsync();
                await FetchOptiPatcherAsync(patcher, say);
            }
            catch (Exception error) when (error is UpscalingException or System.IO.IOException or UnauthorizedAccessException)
            {
                // OptiPatcher is optional: do not block OptiScaler installation if its fetch fails.
            }
        }

        return stored;
    }

    public async Task<StoredOptiPatcher> FetchOptiPatcherAsync(OptiPatcherRelease release, Action<string> say,
        CancellationToken cancel = default)
    {
        say(Strings.DownloadingOptiPatcher(release.Tag));
        var progress = new Progress<double>(done =>
        {
            if (cancel.IsCancellationRequested) return;
            say(Strings.DownloadingOptiPatcherProgress(release.Tag, (int)(done * 100)));
        });

        return await Task.Run(() => OptiPatcher.FetchAsync(release, progress, cancel), cancel);
    }

    public async Task<StoredBuild> FetchAsync(NightlyBuild build, Action<string> say, CancellationToken cancel = default)
    {
        // Progress<T> marshals the download thread's reports back to this thread.
        var progress = new Progress<double>(done =>
        {
            if (cancel.IsCancellationRequested) return;
            say(done < 1 ? Strings.Downloading((int)(done * 100)) : Strings.Unpacking);
        });

        return await Task.Run(() => Builds.FetchAsync(build, progress, cancel), cancel);
    }

    /// <summary>
    /// Settings to apply when updating an installed game: reads live overlay changes from OptiScaler.ini,
    /// and if the game was installed without XMX (falling back to plain FSR 4) while Tsume now has XMX
    /// and the user's remembered preference was XMX, restores XMX so the update activates it.
    /// </summary>
    public UpscalerSettings SettingsForUpdate(InstallManifest manifest, ListedGame listed)
    {
        var remembered = List.SettingsFor(listed.Game.Id);
        var current = Installer.ReadSettings(manifest, remembered);

        if (!Installer.HasXmx(manifest) && Xmx.Current() is not null
            && remembered.Upscaler == UpscalerChoice.Fsr4Xmx
            && current.Upscaler == UpscalerChoice.Fsr4)
        {
            current = current with { Upscaler = UpscalerChoice.Fsr4Xmx, Model = remembered.Model };
        }

        if (!Installer.HasOptiPatcher(manifest) && OptiPatcher.Current() is not null
            && remembered.OptiPatcher
            && !current.OptiPatcher)
        {
            current = current with { OptiPatcher = true };
        }

        return current;
    }

    /// <summary>
    /// Installs <paramref name="build"/> into a game, first removing any OptiScaler Tsume had put there.
    /// Call on a background thread.
    /// </summary>
    /// <returns>What the removal, if there was one, left alone.</returns>
    public RemovalReport? PutIn(ListedGame listed, StoredBuild build, UpscalerSettings settings)
    {
        // When this install needs the stored OptiPatcher, verify it before removing an existing healthy install
        // so a damaged file on disk fails cleanly instead of leaving the game uninstalled.
        if (settings.OptiPatcher && !Installer.ExtrasHaveOptiPatcher && OptiPatcher.Current() is { } patcher)
            OptiPatcherStore.Verified(patcher);

        RemovalReport? removed = null;

        if (Installer.Find(listed.Game.Id) is { } old)
        {
            var healthy = Installer.Check(old) == InstallState.Healthy;

            // A healthy install keeps its proxy name and overlay tuning. If the game replaced that file,
            // do not reuse the replaced name; a repair also starts from a clean OptiScaler.ini.
            var proxy = healthy ? old.Proxy
                : settings.Proxy.Equals(old.Proxy, StringComparison.OrdinalIgnoreCase) ? "" : settings.Proxy;
            settings = settings with { Proxy = proxy };
            removed = Installer.Remove(old.GameId, keepSettings: healthy);
        }

        var facts = GameInspector.Inspect(listed.Game) ?? throw new UpscalingException(Trouble.FolderMissing, listed.Game.Folder);
        if (facts.AntiCheat is not null) throw new UpscalingException(Trouble.AntiCheat, facts.AntiCheat);

        listed.Facts = facts;
        Installer.Install(listed.Game, facts, build, settings);
        return removed;
    }

    /// <summary>Counts the games with OptiScaler, for the main-screen tile. Costs one folder listing.</summary>
    public void RefreshTile()
    {
        InstalledCount = Installer.InstalledGames().Count;
    }

    public int InstalledCount
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(TileDetail));
            Notify(nameof(IsInUse));
        }
    }

    public string TileDetail => InstalledCount > 0 ? Strings.GamesWithOptiScaler(InstalledCount) : "";
    public bool IsInUse => InstalledCount > 0;

    public ObservableCollection<UpscalingGameRow> Games { get; } = [];

    public ICommand OpenGameMenu { get; }
    public ICommand Rescan { get; }
    public ICommand OpenOptiScalerCompatibility { get; }
    public ICommand OpenOptiPatcherCompatibility { get; }

    /// <summary>The line under the build's row: which nightly is here.</summary>
    public string BuildLine { get; private set => Set(ref field, value); } = "";

    /// <summary>The line under Add folder: how many folders are saved, or empty when none.</summary>
    public string FoldersLine { get; private set => Set(ref field, value); } = "";

    /// <summary>True while stores and custom folders are being scanned.</summary>
    public bool IsLooking
    {
        get;
        private set { if (Set(ref field, value)) Notify(nameof(IsLookingShown)); }
    }

    public Visibility IsLookingShown => IsLooking ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown beside the GAMES heading while scanning or right after a manual rescan.</summary>
    public string ScanStatus
    {
        get;
        private set { if (Set(ref field, value)) Notify(nameof(ScanStatusShown)); }
    } = "";

    public Visibility ScanStatusShown => ScanStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shown in place of the list while it is empty: still looking, or nothing found.</summary>
    public string EmptyNote { get; private set => Set(ref field, value); } = "";

    /// <summary>Raised when rows were added or removed, so the screen can fix up focus.</summary>
    public event Action? ListChanged;

    protected override void OnOpened()
    {
        RestoreAll.Reset();
        _manualScan = false;
        if (!IsLooking) ScanStatus = "";   // the last rescan's count; a scan still running keeps "Scanning…"
        _ = LookForGamesAsync();   // runs to its first await, setting IsLooking before ShowGames reads it
        ShowGames();
    }

    /// <summary>Opens a game's page.</summary>
    public void Show(ListedGame listed) => Game.Show(listed);

    /// <summary>Adds a game by its program and opens its page. The program's folder is taken as the game's folder.</summary>
    public void AddByProgram(string program)
    {
        var folder = System.IO.Path.GetDirectoryName(program)!;
        var game = Upscaling.Games.Game.At(folder, NameFrom(program), GameStore.Custom, program);

        if (List.Find(game.Id) is not { } listed)
        {
            listed = new ListedGame { Game = game, Facts = GameInspector.Inspect(game) };
            List.Games.Add(listed);
            SaveList();
        }

        ShowGames();
        Show(listed);
    }

    /// <summary>Remembers a library folder and scans it for games.</summary>
    public void AddFolder(string folder)
    {
        if (!List.AddFolder(folder)) return;
        SaveList();
        ShowGames();
        _ = LookForGamesAsync(manual: true);
    }

    /// <summary>Stops scanning a saved library folder and updates the games list.</summary>
    public void RemoveFolder(string folder)
    {
        if (!List.RemoveFolder(folder)) return;
        SaveList();
        ShowGames();
        _ = LookForGamesAsync();
    }

    /// <summary>"Witchfire-Win64-Shipping.exe" → "Witchfire"; "my_game.exe" → "my game".</summary>
    private static string NameFrom(string program)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(program);

        foreach (var ending in new[] { "-Win64-Shipping", "-WinGDK-Shipping", "_Win64_Shipping" })
            name = name.Replace(ending, "", StringComparison.OrdinalIgnoreCase);

        return string.Join(' ', name.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Finds the stores' and saved folders' installed games and inspects each one in the background,
    /// while the last known list is already on screen.
    /// </summary>
    private async Task LookForGamesAsync(bool manual = false)
    {
        _manualScan |= manual;

        // Already looking: go round once more when this pass ends, so the latest folders are read.
        if (IsLooking)
        {
            _rescanRequested = true;
            return;
        }

        IsLooking = true;
        ScanStatus = Strings.ScanningGames;
        var started = Stopwatch.StartNew();
        var listChanged = false;

        try
        {
            do
            {
                _rescanRequested = false;

                try { listChanged |= await ScanOnceAsync(); }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                {
                    // Keep the list as it was.
                }

                // Give manual rescans a brief visible spin even when disk reads finish in a few milliseconds.
                // Checked inside the loop, so a request made during the wait is not lost.
                var remaining = TimeSpan.FromMilliseconds(300) - started.Elapsed;
                if (_manualScan && !_rescanRequested && remaining > TimeSpan.Zero) await Task.Delay(remaining);

                // The rows were refreshed as the screen opened; read them again only if the scan changed the list,
                // the user asked for the rescan, or an earlier refresh left the visible rows empty.
                // Awaited before ScanStatus reports the count so newly found rows are already on screen.
                if (!_rescanRequested && IsOpen
                    && (listChanged || _manualScan || (_rowsTask.IsCompleted && Games.Count == 0 && List.Games.Count > 0)))
                {
                    listChanged = false;
                    var refresh = ShowGamesAsync(verifyGameFolders: true);
                    await refresh;
                    if (!ReferenceEquals(_rowsTask, refresh)) await _rowsTask;
                }
            }
            while (_rescanRequested);
        }
        finally
        {
            var wasManual = _manualScan;
            _manualScan = false;
            IsLooking = false;

            ShowEmptyNote();

            ScanStatus = wasManual && IsOpen ? Strings.ScanFoundGames(Games.Count) : "";
        }
    }

    /// <returns>True if the list changed.</returns>
    private async Task<bool> ScanOnceAsync()
    {
        var list = List;
        var folders = list.Folders.ToList();
        var byHand = list.Games.Where(listed => listed.Game.Store == GameStore.Custom).Select(listed => listed.Game).ToList();

        var (installed, found, facts) = await Task.Run(() =>
        {
            var withOptiScaler = Installer.InstalledGames();
            var games = GameLibrary.Find(folders);

            // Re-inspect hand-added games too: they also get updated.
            var all = games.Concat(byHand).DistinctBy(game => game.Id);
            return (withOptiScaler, games, all.ToDictionary(game => game.Id, GameInspector.Inspect));
        });

        // A program already listed (added by hand, or by a store) is not listed again from a saved folder.
        var targets = new HashSet<string>(
            byHand.Select(game => facts[game.Id]?.Target).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        // Store and folder games get a row only if they ship an upscaler or already have OptiScaler;
        // the rest can still be added by hand.
        var eligible = found
            .OrderBy(game => game.Store == GameStore.Folder)
            .Where(game =>
            {
                var seen = facts[game.Id];
                var isNew = seen is null || targets.Add(seen.Target);
                return installed.Contains(game.Id) || (isNew && seen is { Upscalers.Count: > 0 });
            })
            .ToList();

        list.Absorb(eligible, installed);

        foreach (var listed in list.Games)
            if (facts.TryGetValue(listed.Game.Id, out var seen)) listed.Facts = seen;

        return SaveList();
    }

    /// <summary>
    /// Syncs the rows with the list, reusing existing rows. Rows for games taken off the list go at once; the
    /// local store records and then each game's manifest/folder are read in the background, and the rows (new ones
    /// included) are brought up to date when that is done, so a slow or sleeping drive does not hold up the screen.
    /// </summary>
    public void ShowGames() => _ = ShowGamesAsync(verifyGameFolders: true);

    private Task ShowGamesAsync(bool verifyGameFolders)
    {
        FoldersLine = List.Folders.Count > 0 ? Strings.FolderCount(List.Folders.Count) : "";

        if (DropUnlistedRows()) ListChanged?.Invoke();
        ShowEmptyNote();

        var cancel = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _rowsCancel, cancel);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }

        return _rowsTask = RefreshRowsAsync(++_rowsVersion, [.. List.Games], verifyGameFolders, cancel);
    }

    /// <summary>Store records and install count read from Tsume's own local data folder.</summary>
    private sealed record LocalState(StoredBuild? Build, XmxUpscaler? Xmx, StoredOptiPatcher? Patcher, int? InstalledCount);

    /// <summary>What was read from disk for one game.</summary>
    private sealed record RowRead(ListedGame Listed, InstallManifest? Manifest, InstallState State, bool IsCurrent, bool OptiPatcherOn);

    private async Task RefreshRowsAsync(int version, IReadOnlyList<ListedGame> games, bool verifyGameFolders, CancellationTokenSource cancel)
    {
        var token = cancel.Token;

        try
        {
            var local = await Task.Run(() => ReadLocalState(token), token);
            if (version != _rowsVersion || token.IsCancellationRequested) return;

            ApplyLocalState(local);

            var rows = await Task.Run(() => ReadGameRows(games, local, verifyGameFolders, token), token);
            if (version != _rowsVersion || token.IsCancellationRequested) return;

            ApplyRows(rows);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception error)
        {
            Log.Write($"games list could not be read: {error.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _rowsCancel, null, cancel);
            cancel.Dispose();
        }
    }

    /// <summary>Reads local store metadata and install count. Runs on a background thread.</summary>
    private LocalState ReadLocalState(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var build = TryRead("current build", Builds.Current);
        var xmx = TryRead("XMX upscaler", Xmx.Current);
        var patcher = TryRead("OptiPatcher release", OptiPatcher.Current);
        var installedCount = TryRead<int?>("installed games count", () => Installer.InstalledGames().Count);
        return new LocalState(build, xmx, patcher, installedCount);
    }

    private static T? TryRead<T>(string what, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Write($"{what} could not be read: {error.Message}");
            return default;
        }
    }

    private void ApplyLocalState(LocalState local)
    {
        var baseBuildLine = local.Build is null ? Strings.NoBuildOnDisk : Strings.NightlyOf(Night(local.Build.Build.Tag));
        BuildLine = local.Patcher is null
            ? baseBuildLine
            : Strings.BuildLineWithOptiPatcher(baseBuildLine, local.Patcher.Release.Tag);

        if (local.InstalledCount is { } count) InstalledCount = count;
    }

    /// <summary>Reads each game's install from disk. Runs on a background thread, and touches no rows.</summary>
    private List<RowRead> ReadGameRows(IReadOnlyList<ListedGame> games, LocalState local, bool verifyGameFolders, CancellationToken cancel)
    {
        var rows = new List<RowRead>(games.Count);

        foreach (var listed in games)
        {
            cancel.ThrowIfCancellationRequested();
            rows.Add(ReadRow(listed, local, verifyGameFolders));
        }

        return rows;
    }

    private RowRead ReadRow(ListedGame listed, LocalState local, bool verifyGameFolders)
    {
        try
        {
            // A hand-edited or damaged manifest can hold nulls where none are expected: such a game is shown as
            // without OptiScaler rather than stopping the rest of the list.
            var manifest = Installer.Find(listed.Game.Id);
            if (manifest is null || manifest.Build is null || manifest.Files is null || manifest.Files.Any(file => file?.Path is null))
                return new RowRead(listed, null, InstallState.None, IsCurrent: true, OptiPatcherOn: false);

            InstallState state;
            bool optiPatcherOn;
            if (verifyGameFolders)
            {
                state = Installer.Check(manifest);
                optiPatcherOn = state == InstallState.Healthy && Installer.IsOptiPatcherEnabled(manifest);
            }
            else
            {
                // Preload stays inside Tsume's own local data folder so sleeping game drives are not woken.
                // Opening the screen runs a full verification against each game's folder.
                state = manifest.Complete ? InstallState.Healthy : InstallState.CutShort;
                optiPatcherOn = state == InstallState.Healthy && Installer.HasOptiPatcher(manifest);
            }

            var isCurrent = Installer.IsCurrent(manifest, local.Build, local.Xmx, local.Patcher);
            return new RowRead(listed, manifest, state, isCurrent, optiPatcherOn);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Write($"could not read install state for {listed.Game.Name}: {error.Message}");
            return new RowRead(listed, null, InstallState.None, IsCurrent: true, OptiPatcherOn: false);
        }
    }

    private void ApplyRows(IReadOnlyList<RowRead> rows)
    {
        var readByListed = new Dictionary<ListedGame, RowRead>(rows.Count, ReferenceEqualityComparer.Instance);
        foreach (var row in rows) readByListed.TryAdd(row.Listed, row);

        // The list may have changed while the reads were under way.
        var changed = DropUnlistedRows();

        var existingByListed = new Dictionary<ListedGame, UpscalingGameRow>(Games.Count, ReferenceEqualityComparer.Instance);
        foreach (var row in Games) existingByListed.TryAdd(row.Listed, row);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var targetIndex = 0;

        foreach (var listed in List.Games)
        {
            // First entry per ID wins, matching DropUnlistedRows and GameList.Find.
            if (!seenIds.Add(listed.Game.Id)) continue;

            var hasRead = readByListed.TryGetValue(listed, out var game);

            if (existingByListed.TryGetValue(listed, out var row))
            {
                if (targetIndex < Games.Count && !ReferenceEquals(Games[targetIndex], row))
                {
                    var currentIndex = Games.IndexOf(row);
                    if (currentIndex >= 0 && currentIndex != targetIndex)
                    {
                        Games.Move(currentIndex, targetIndex);
                        changed = true;
                    }
                }

                if (hasRead) row.Describe(game!.Manifest, game.State, game.IsCurrent, game.OptiPatcherOn);
                targetIndex++;
            }
            else if (hasRead)
            {
                row = new UpscalingGameRow(listed, () => Show(listed));
                row.Describe(game!.Manifest, game.State, game.IsCurrent, game.OptiPatcherOn);
                Games.Insert(Math.Min(targetIndex, Games.Count), row);
                existingByListed[listed] = row;
                changed = true;
                targetIndex++;
            }
        }

        ShowEmptyNote();

        if (changed) ListChanged?.Invoke();
    }

    /// <summary>First <see cref="ListedGame"/> for each game ID, matching <see cref="GameList.Find"/>.</summary>
    private Dictionary<string, ListedGame> CanonicalListedById()
    {
        var byId = new Dictionary<string, ListedGame>(List.Games.Count, StringComparer.Ordinal);
        foreach (var listed in List.Games) byId.TryAdd(listed.Game.Id, listed);
        return byId;
    }

    /// <summary>Removes the rows of games no longer on the list.</summary>
    /// <returns>True if any row was removed.</returns>
    private bool DropUnlistedRows()
    {
        var byId = CanonicalListedById();
        var changed = false;

        for (var i = Games.Count - 1; i >= 0; i--)
        {
            var listed = Games[i].Listed;
            if (byId.TryGetValue(listed.Game.Id, out var canonical) && ReferenceEquals(canonical, listed)) continue;

            Games.RemoveAt(i);
            changed = true;
        }

        return changed;
    }

    /// <remarks>Goes by the list rather than the rows, which may not have been made yet.</remarks>
    private void ShowEmptyNote() =>
        EmptyNote = List.Games.Count > 0 ? "" : IsLooking ? Strings.LookingForGames : Strings.NoGamesYet;
}

public enum GameTagTone
{
    Neutral,
    Success,
    Accent,
    Warning,
    Danger,
}

/// <summary>One slightly rounded rectangular tag shown beneath a game's title so each item is cleanly separated.</summary>
public sealed record GameTag(string Text, GameTagTone Tone = GameTagTone.Neutral, string? Glyph = null)
{
    public Visibility GlyphShown => string.IsNullOrEmpty(Glyph) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SuccessShown => Tone == GameTagTone.Success ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AccentShown => Tone == GameTagTone.Accent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeutralShown => Tone == GameTagTone.Neutral ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningShown => Tone == GameTagTone.Warning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DangerShown => Tone == GameTagTone.Danger ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One game in the list: its name and separated slightly-rounded status/upscaler tags below it.</summary>
public sealed class UpscalingGameRow(ListedGame listed, Action open) : Observable
{
    public ListedGame Listed { get; } = listed;

    public ICommand Open { get; } = new Command(open);

    public string Name => Listed.Game.Name;
    public string Glyph => Glyphs.Game;

    public string Detail { get; private set => Set(ref field, value); } = "";

    public IReadOnlyList<GameTag> Tags { get; private set => Set(ref field, value); } = [];

    /// <summary>True when OptiScaler is in this game; lights the row's icon.</summary>
    public bool IsInstalled { get; private set => Set(ref field, value); }

    public void Describe(InstallManifest? manifest, InstallState state, bool isCurrent, bool optiPatcherOn)
    {
        var facts = Listed.Facts;

        IsInstalled = state is InstallState.Healthy;
        Detail = state switch
        {
            InstallState.Healthy when !isCurrent && optiPatcherOn => Strings.GameUpdateReadyWithOptiPatcher,
            InstallState.Healthy when !isCurrent                  => Strings.GameUpdateReady,
            InstallState.Healthy when optiPatcherOn               => Strings.GameInstalledWithOptiPatcher(UpscalingViewModel.Night(manifest!.Build)),
            InstallState.Healthy                                  => Strings.GameInstalled(UpscalingViewModel.Night(manifest!.Build)),
            InstallState.Replaced                                 => Strings.GameNeedsRepair,
            InstallState.CutShort                                 => Strings.GameCutShort,
            InstallState.FolderGone                               => Strings.GameFolderGone,

            _ when facts is null                    => Strings.GameNothingToRun,
            _ when facts.AntiCheat is { } antiCheat => Strings.GameBlocked(antiCheat),
            _ when facts.Upscalers.Count == 0       => Strings.GameNoUpscaler,
            _                                       => string.Join(", ", facts.Upscalers),
        };

        var tags = new List<GameTag>();
        switch (state)
        {
            case InstallState.Healthy:
                tags.Add(new GameTag(Strings.GameInstalled(UpscalingViewModel.Night(manifest!.Build)), GameTagTone.Success, Glyphs.Tick));
                if (optiPatcherOn)
                    tags.Add(new GameTag(Strings.OptiPatcher, GameTagTone.Accent, Glyphs.Plugin));
                if (!isCurrent)
                    tags.Add(new GameTag(Strings.TagUpdateReady, GameTagTone.Warning, Glyphs.Update));
                break;

            case InstallState.Replaced:
                tags.Add(new GameTag(Strings.GameNeedsRepair, GameTagTone.Warning, Glyphs.Warning));
                break;

            case InstallState.CutShort:
                tags.Add(new GameTag(Strings.GameCutShort, GameTagTone.Warning, Glyphs.Warning));
                break;

            case InstallState.FolderGone:
                tags.Add(new GameTag(Strings.GameFolderGone, GameTagTone.Warning, Glyphs.Warning));
                break;

            default:
                if (facts is null)
                    tags.Add(new GameTag(Strings.GameNothingToRun));
                else if (facts.AntiCheat is { } antiCheat)
                {
                    tags.Add(new GameTag(Strings.TagBlocked, GameTagTone.Danger, Glyphs.Shield));
                    tags.Add(new GameTag(antiCheat));
                }
                else if (facts.Upscalers.Count == 0)
                    tags.Add(new GameTag(Strings.GameNoUpscaler));
                else
                    foreach (var upscaler in facts.Upscalers)
                        tags.Add(new GameTag(upscaler));
                break;
        }

        if (!Tags.SequenceEqual(tags)) Tags = tags;

        Notify(nameof(Name));   // a store may have renamed it since the row was made
    }
}
