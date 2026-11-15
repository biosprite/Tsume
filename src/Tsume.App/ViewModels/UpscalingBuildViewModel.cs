using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.Xmx;

namespace Tsume.App.ViewModels;

/// <summary>
/// The build page: the nightly on disk, fetching a newer one, updating installed games, XMX, and the extras folder.
/// Tsume goes online only when asked: "Get the newest build", or a first install with no build on disk.
/// </summary>
public sealed class UpscalingBuildViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;
    private StoredBuild? _build;
    private XmxUpscaler? _xmx;

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public UpscalingBuildViewModel(UpscalingViewModel hub, Func<Task> dismiss)
    {
        _hub = hub;

        GetNewest = new Command(() => _ = RunAsync(GetNewestAsync));
        UpdateGames = new Command(() => _ = RunAsync(UpdateGamesAsync));
        AddXmx = new Command(() => _ = RunAsync(AddXmxAsync));
        RemoveOld = new Command(() => _ = RunAsync(RemoveOldAsync));

        OpenExtras = new Command(async () =>
        {
            Directory.CreateDirectory(_hub.Paths.Extras);

            // Started while the panel is still in front: Windows lets only the foreground program
            // bring a new window forward, so after dismissing the folder would open behind the game.
            try { Process.Start(new ProcessStartInfo(_hub.Paths.Extras) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }

            await dismiss();
        });
    }

    /// <summary>Raised after an action, when buttons may have come or gone.</summary>
    public event Action? Settled;

    public ICommand GetNewest { get; }
    public ICommand UpdateGames { get; }
    public ICommand AddXmx { get; }
    public ICommand RemoveOld { get; }
    public ICommand OpenExtras { get; }

    protected override void OnOpened()
    {
        Status = "";
        Look();
    }

    private void Look()
    {
        _build = _hub.Builds.Current();
        _xmx = _hub.Xmx.Current();
        Outdated = _build is null ? [] : [.. GamesOnAnotherBuild(_build)];
        OldSize = _hub.Builds.Old(_hub.Installer.BuildsInUse()).Sum(old => old.Size);

        var extras = Directory.Exists(_hub.Paths.Extras)
            ? Directory.EnumerateFiles(_hub.Paths.Extras, "*", SearchOption.AllDirectories).Count()
            : 0;
        ExtrasLine = extras == 0 ? Strings.ExtrasEmpty : Strings.ExtrasCount(extras);

        Notify("");
    }

    /// <summary>Healthy installs a fresh one would improve on: an older build, or no XMX upscaler where Tsume now has one.</summary>
    private IEnumerable<InstallManifest> GamesOnAnotherBuild(StoredBuild build) => _hub.Installer.InstalledGames()
        .Select(_hub.Installer.Find)
        .OfType<InstallManifest>()
        .Where(manifest => !_hub.Installer.IsCurrent(manifest, build) && Installer.Check(manifest) == InstallState.Healthy);

    private IReadOnlyList<InstallManifest> Outdated { get; set; } = [];

    public string BuildLine => _build is null
        ? Strings.NoBuildOnDisk
        : Strings.NightlyOf(UpscalingViewModel.Night(_build.Build.Tag));

    /// <summary>E.g. "OptiScaler 10.0.0-pre1".</summary>
    public string VersionLine => _build is null ? "" : $"OptiScaler {_build.Build.Version}";

    /// <summary>Always offered; says how many games when some are behind.</summary>
    public string UpdateLabel => Outdated.Count > 0 ? Strings.UpdateGames(Outdated.Count) : Strings.UpdateAllGames;

    /// <summary>Bytes held by builds that no game was installed from.</summary>
    private long OldSize { get; set; }

    public string RemoveOldLabel => OldSize > 0 ? Strings.RemoveOldBuildsSized(OldSize) : Strings.RemoveOldBuilds;

    public string XmxLabel => _xmx is null ? Strings.AddXmx : Strings.ReplaceXmx;

    public string XmxLine => _xmx is null
        ? Strings.XmxNotAdded
        : Strings.XmxHeld(_xmx.Version, _xmx.Added.ToString("d MMMM", CultureInfo.CurrentCulture));

    public string ExtrasLine { get; private set; } = "";

    public string Status { get; private set => Set(ref field, value); } = "";

    private async Task RunAsync(Func<Task> action)
    {
        if (_hub.IsBusy) return;
        _hub.IsBusy = true;

        try
        {
            await action();
        }
        catch (Exception error)
        {
            Status = Strings.Explain(error);
        }
        finally
        {
            _hub.IsBusy = false;
            Look();
            Settled?.Invoke();
        }
    }

    private async Task GetNewestAsync()
    {
        Status = Strings.CheckingBuild;
        var newest = await NightlyFeed.NewestAsync();

        if (_build?.Build.Tag == newest.Tag)
        {
            Status = Strings.HaveNewest;
            return;
        }

        await _hub.FetchAsync(newest, text => Status = text);
        Status = Strings.GotNewer(UpscalingViewModel.Night(newest.Tag));
    }

    /// <summary>
    /// Imports the XMX upscaler from the newest valid archive on the Desktop or in Downloads.
    /// The archive itself is left in place.
    /// </summary>
    private async Task AddXmxAsync()
    {
        var had = _xmx;
        var archives = XmxStore.Lying();

        if (archives.Count == 0)
        {
            Status = Strings.XmxNotFound;
            return;
        }

        Status = Strings.Unpacking;

        var added = await Task.Run(() =>
        {
            for (var i = 0; ; i++)
            {
                try { return _hub.Xmx.Add(archives[i]); }
                catch (Tsume.Upscaling.UpscalingException) when (i + 1 < archives.Count) { }   // not this one: try the next
            }
        });

        Status = added.Sha256 == had?.Sha256 ? Strings.XmxSame : Strings.XmxAdded;
    }

    /// <summary>Deletes downloaded builds that are not the current one and that no game was installed from.</summary>
    private async Task RemoveOldAsync()
    {
        if (OldSize == 0)
        {
            Status = Strings.NoOldBuilds;
            return;
        }

        var inUse = _hub.Installer.BuildsInUse();
        var freed = await Task.Run(() => _hub.Builds.RemoveOld(inUse));

        Status = freed > 0 ? Strings.RemovedOldBuilds(freed) : Strings.OldBuildsInUse;
    }

    /// <summary>
    /// Reinstalls the current build into each outdated game, keeping its proxy file name and OptiScaler.ini.
    /// One game failing (e.g. it is running) does not stop the rest.
    /// </summary>
    private async Task UpdateGamesAsync()
    {
        if (_build is not { } build) return;

        if (Outdated.Count == 0)
        {
            Status = Strings.AllGamesCurrent;
            return;
        }

        var updated = 0;
        var failures = new List<string>();

        foreach (var manifest in Outdated)
        {
            if (_hub.List.Find(manifest.GameId) is not { } listed) continue;

            Status = $"{manifest.GameName}: {Strings.Installing}";
            try
            {
                var settings = Installer.ReadSettings(manifest, _hub.List.SettingsFor(manifest.GameId));
                await Task.Run(() => _hub.PutIn(listed, build, settings));
                updated++;
            }
            catch (Exception error)
            {
                failures.Add(Strings.CouldNotUpdate(manifest.GameName, Strings.Explain(error)));
            }
        }

        Status = string.Join(Environment.NewLine, failures.Prepend(Strings.UpdatedGames(updated)));
    }
}
