using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;
using Tsume.App.Theme;
using Tsume.Upscaling;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.Xmx;

namespace Tsume.App.ViewModels;

/// <summary>
/// The OptiScaler games list, and the state shared by its sub-pages (Game, Build, Defaults, Browse).
/// Reads no files until the screen is opened; the work itself is in Tsume.Upscaling.
/// </summary>
public sealed class UpscalingViewModel : LayerViewModel
{
    private GameList? _list;
    private bool _looking;

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public UpscalingViewModel(Func<Task> dismiss)
    {
        Builds = new BuildStore(Paths);
        Installer = new Installer(Paths);
        Xmx = new XmxStore(Paths);

        Game = new UpscalingGameViewModel(this);
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
    }

    public UpscalingPaths Paths { get; } = new();
    public BuildStore Builds { get; }
    public Installer Installer { get; }

    /// <summary>The XMX build of FSR 4, if Tsume has been given it.</summary>
    public XmxStore Xmx { get; }

    /// <summary>The games and their settings, loaded from disk on first use.</summary>
    public GameList List => _list ??= GameList.Load(Paths);

    public void SaveList() => List.Save(Paths);

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

    /// <summary>A build tag's date in the current culture, e.g. "11 November"; the tag itself if it has no date.</summary>
    public static string Night(string buildTag) =>
        new NightlyBuild(buildTag, "", "", 0, "").Date is { } date
            ? date.ToString("d MMMM", CultureInfo.CurrentCulture)
            : buildTag;

    /// <summary>
    /// The build to install from: the one on disk, or the newest, downloaded now, if there is none.
    /// Never goes online when a build is already on disk.
    /// </summary>
    public async Task<StoredBuild> NeedBuildAsync(Action<string> say)
    {
        if (await Task.Run(Builds.Current) is { } here) return here;

        say(Strings.CheckingBuild);
        var newest = await NightlyFeed.NewestAsync();

        return await FetchAsync(newest, say);
    }

    public async Task<StoredBuild> FetchAsync(NightlyBuild build, Action<string> say)
    {
        // Progress<T> marshals the download thread's reports back to this thread.
        var progress = new Progress<double>(done =>
            say(done < 1 ? Strings.Downloading((int)(done * 100)) : Strings.Unpacking));

        return await Task.Run(() => Builds.FetchAsync(build, progress));
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
            return current with { Upscaler = UpscalerChoice.Fsr4Xmx, Model = remembered.Model };
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

    /// <summary>The line under the build's row: which nightly is here.</summary>
    public string BuildLine { get; private set => Set(ref field, value); } = "";

    /// <summary>Shown in place of the list while it is empty: still looking, or nothing found.</summary>
    public string EmptyNote { get; private set => Set(ref field, value); } = "";

    /// <summary>Raised when rows were added or removed, so the screen can fix up focus.</summary>
    public event Action? ListChanged;

    protected override void OnOpened()
    {
        RestoreAll.Reset();
        _ = LookForGamesAsync();   // runs to its first await, setting _looking before ShowGames reads it
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

    /// <summary>"Witchfire-Win64-Shipping.exe" → "Witchfire"; "my_game.exe" → "my game".</summary>
    private static string NameFrom(string program)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(program);

        foreach (var ending in new[] { "-Win64-Shipping", "-WinGDK-Shipping", "_Win64_Shipping" })
            name = name.Replace(ending, "", StringComparison.OrdinalIgnoreCase);

        return string.Join(' ', name.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Finds the stores' installed games and inspects each one in the background,
    /// while the last known list is already on screen.
    /// </summary>
    private async Task LookForGamesAsync()
    {
        if (_looking) return;
        _looking = true;

        try
        {
            var list = List;
            var installed = Installer.InstalledGames();

            var (found, facts) = await Task.Run(() =>
            {
                var games = GameLibrary.Find();

                // Re-inspect hand-added games too: they also get updated.
                var all = games
                    .Concat(list.Games.Where(listed => listed.Game.Store == GameStore.Custom).Select(listed => listed.Game))
                    .DistinctBy(game => game.Id);
                return (games, all.ToDictionary(game => game.Id, GameInspector.Inspect));
            });

            // Store games get a row only if they ship an upscaler or already have OptiScaler;
            // the rest can still be added by hand.
            list.Absorb(
                found.Where(game => facts[game.Id] is { Upscalers.Count: > 0 } || installed.Contains(game.Id)),
                installed);

            foreach (var listed in list.Games)
                if (facts.TryGetValue(listed.Game.Id, out var seen)) listed.Facts = seen;

            SaveList();
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            // Keep the list as it was.
        }
        finally
        {
            _looking = false;
            if (IsOpen) ShowGames();
        }
    }

    /// <summary>Syncs the rows with the list, reusing existing rows.</summary>
    public void ShowGames()
    {
        var build = Builds.Current();
        BuildLine = build is null ? Strings.NoBuildOnDisk : Strings.NightlyOf(Night(build.Build.Tag));

        var changed = false;

        for (var i = Games.Count - 1; i >= 0; i--)
        {
            if (List.Find(Games[i].Listed.Game.Id) == Games[i].Listed) continue;

            Games.RemoveAt(i);
            changed = true;
        }

        for (var i = 0; i < List.Games.Count; i++)
        {
            var listed = List.Games[i];

            if (Games.FirstOrDefault(row => row.Listed == listed) is not { } row)
            {
                Games.Insert(Math.Min(i, Games.Count), row = new UpscalingGameRow(listed, () => Show(listed)));
                changed = true;
            }

            var manifest = Installer.Find(listed.Game.Id);
            row.Describe(manifest, Installer.Check(manifest), manifest is null || Installer.IsCurrent(manifest, build));
        }

        EmptyNote = Games.Count > 0 ? "" : _looking ? Strings.LookingForGames : Strings.NoGamesYet;
        RefreshTile();

        if (changed) ListChanged?.Invoke();
    }
}

/// <summary>One game in the list: its name and a status line.</summary>
public sealed class UpscalingGameRow(ListedGame listed, Action open) : Observable
{
    public ListedGame Listed { get; } = listed;

    public ICommand Open { get; } = new Command(open);

    public string Name => Listed.Game.Name;
    public string Glyph => Glyphs.Game;

    public string Detail { get; private set => Set(ref field, value); } = "";

    /// <summary>True when OptiScaler is in this game; lights the row's icon.</summary>
    public bool IsInstalled { get; private set => Set(ref field, value); }

    public void Describe(InstallManifest? manifest, InstallState state, bool isCurrent)
    {
        var facts = Listed.Facts;

        IsInstalled = state is InstallState.Healthy;
        Detail = state switch
        {
            InstallState.Healthy when !isCurrent => Strings.GameUpdateReady,
            InstallState.Healthy    => Strings.GameInstalled(UpscalingViewModel.Night(manifest!.Build)),
            InstallState.Replaced   => Strings.GameNeedsRepair,
            InstallState.CutShort   => Strings.GameCutShort,
            InstallState.FolderGone => Strings.GameFolderGone,

            _ when facts is null                    => Strings.GameNothingToRun,
            _ when facts.AntiCheat is { } antiCheat => Strings.GameBlocked(antiCheat),
            _ when facts.Upscalers.Count == 0       => Strings.GameNoUpscaler,
            _                                       => string.Join(" · ", facts.Upscalers),
        };

        Notify(nameof(Name));   // a store may have renamed it since the row was made
    }
}
