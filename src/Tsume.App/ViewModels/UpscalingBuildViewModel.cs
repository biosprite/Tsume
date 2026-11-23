using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Theme;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.OptiPatcher;
using Tsume.Upscaling.Xmx;

namespace Tsume.App.ViewModels;

/// <summary>
/// The build page: the nightly on disk, checking for and downloading a newer one, updating installed games, OptiPatcher, XMX, and the extras folder.
/// Tsume goes online only when asked: "Check for a newer build", downloading OptiPatcher, or a first install with no build on disk.
/// </summary>
public sealed class UpscalingBuildViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;
    private StoredBuild? _build;
    private NightlyBuild? _available;
    private StoredOptiPatcher? _optiPatcher;
    private OptiPatcherRelease? _availableOptiPatcher;
    private XmxUpscaler? _xmx;
    private CancellationTokenSource? _buildCancel;
    private CancellationTokenSource? _optiPatcherCancel;
    private bool _busyHere;

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public UpscalingBuildViewModel(UpscalingViewModel hub, Func<Task> dismiss)
    {
        _hub = hub;
        _hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpscalingViewModel.IsBusy)) Notify(nameof(CanAct));
        };

        CheckForNewer = new Command(() => _ = RunCancellableAsync(CheckForNewerAsync, Strings.CheckCancelled));
        DownloadNewer = new Command(() => _ = RunCancellableAsync(DownloadNewerAsync, Strings.DownloadCancelled, onCancelled: () => _available = null));
        CancelUpdate = new Command(OnCancelUpdate);
        UpdateGames = new Command(() => _ = RunAsync(UpdateGamesAsync));
        CheckOrDownloadOptiPatcher = new Command(() => _ = RunCancellableAsync(
            CheckOrDownloadOptiPatcherAsync,
            _optiPatcher is null ? Strings.DownloadCancelled : Strings.CheckCancelled,
            isOptiPatcher: true));
        DownloadOptiPatcher = new Command(() => _ = RunCancellableAsync(
            DownloadOptiPatcherAsync,
            Strings.DownloadCancelled,
            onCancelled: () => _availableOptiPatcher = null,
            isOptiPatcher: true));
        CancelOptiPatcher = new Command(OnCancelOptiPatcher);
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

    /// <summary>Raised when an OptiPatcher check, download, or cancel settles so focus stays in the OptiPatcher section.</summary>
    public event Action? OptiPatcherSettled;

    public ICommand CheckForNewer { get; }
    public ICommand DownloadNewer { get; }
    public ICommand CancelUpdate { get; }
    public ICommand UpdateGames { get; }
    public ICommand CheckOrDownloadOptiPatcher { get; }
    public ICommand DownloadOptiPatcher { get; }
    public ICommand CancelOptiPatcher { get; }
    public ICommand AddXmx { get; }
    public ICommand RemoveOld { get; }
    public ICommand OpenExtras { get; }
    public ICommand OpenOptiScalerCompatibility => _hub.OpenOptiScalerCompatibility;
    public ICommand OpenOptiPatcherCompatibility => _hub.OpenOptiPatcherCompatibility;

    protected override void OnOpened()
    {
        if (!IsDownloading) _available = null;
        if (!IsDownloadingOptiPatcher) _availableOptiPatcher = null;
        if (!_busyHere) Status = "";

        Look();
    }

    private void Look()
    {
        _build = _hub.Builds.Current();
        if (_available is not null && _build?.Build.Tag == _available.Tag) _available = null;

        _optiPatcher = _hub.OptiPatcher.Current();
        if (_availableOptiPatcher is not null
            && _optiPatcher?.Release.Tag == _availableOptiPatcher.Tag
            && _optiPatcher.Release.Sha256 == _availableOptiPatcher.Sha256)
        {
            _availableOptiPatcher = null;
        }

        _xmx = Tsume.Upscaling.GpuVendor.HasIntelXmxGpu() ? _hub.Xmx.Current() : null;
        Outdated = _build is null ? [] : [.. GamesOnAnotherBuild(_build)];
        OldSize = _hub.Builds.Old(_hub.Installer.BuildsInUse()).Sum(old => old.Size);

        var extras = Directory.Exists(_hub.Paths.Extras)
            ? Directory.EnumerateFiles(_hub.Paths.Extras, "*", SearchOption.AllDirectories).Count()
            : 0;
        ExtrasLine = extras == 0 ? Strings.ExtrasEmpty : Strings.ExtrasCount(extras);

        Notify("");
    }

    /// <summary>Healthy installs a fresh one would improve on: an older build, no XMX upscaler, or an older OptiPatcher.</summary>
    private IEnumerable<InstallManifest> GamesOnAnotherBuild(StoredBuild build) => _hub.Installer.InstalledGames()
        .Select(_hub.Installer.Find)
        .OfType<InstallManifest>()
        .Where(manifest => !_hub.Installer.IsCurrent(manifest, build, _xmx, _optiPatcher) && Installer.Check(manifest) == InstallState.Healthy);

    private IReadOnlyList<InstallManifest> Outdated { get; set; } = [];

    /// <summary>False while a check, download, update, or removal is running.</summary>
    public bool CanAct => !_hub.IsBusy;

    /// <summary>True while querying GitHub for the newest nightly build.</summary>
    public bool IsChecking { get; private set => Set(ref field, value); }

    /// <summary>True while downloading and unpacking the available newer build.</summary>
    public bool IsDownloading { get; private set => Set(ref field, value); }

    /// <summary>Live progress line shown next to the spinner while downloading/unpacking.</summary>
    public string DownloadProgress { get; private set => Set(ref field, value); } = "";

    public Visibility CheckShown => !IsChecking && _available is null && !IsDownloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheckingShown => IsChecking ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AvailableShown => _available is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConfirmDownloadShown => _available is not null && !IsDownloading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownloadingShown => IsDownloading ? Visibility.Visible : Visibility.Collapsed;

    public string AvailableHeading => IsDownloading
        ? Strings.DownloadingBuildHeading
        : _build is null
            ? Strings.BuildAvailableHeading
            : Strings.NewerBuildAvailableHeading;

    public string AvailableBuildLine => _available is null
        ? ""
        : Strings.NightlyOf(UpscalingViewModel.Night(_available.Tag));

    /// <summary>E.g. "OptiScaler 10.0.0-pre1 (76 MB)".</summary>
    public string AvailableDetailLine
    {
        get
        {
            if (_available is null) return "";
            var version = string.IsNullOrEmpty(_available.Version) ? "" : $"OptiScaler {_available.Version}";
            var size = _available.Size > 0 ? Strings.FormatSize(_available.Size) : "";
            return (version.Length > 0, size.Length > 0) switch
            {
                (true, true) => $"{version} ({size})",
                (true, false) => version,
                (false, true) => size,
                _ => "",
            };
        }
    }

    public Visibility AvailableDetailShown => AvailableDetailLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The exact GitHub release tag (e.g. "nightly-20261115") when the main line displays the parsed date.</summary>
    public string AvailableTagLine => _available is { Date: not null } ? _available.Tag : "";

    public Visibility AvailableTagShown => AvailableTagLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string BuildLine => _build is null
        ? Strings.NoBuildOnDisk
        : Strings.NightlyOf(UpscalingViewModel.Night(_build.Build.Tag));

    /// <summary>E.g. "OptiScaler 10.0.0-pre1".</summary>
    public string VersionLine => _build is null || string.IsNullOrEmpty(_build.Build.Version)
        ? ""
        : $"OptiScaler {_build.Build.Version}";

    public Visibility VersionLineShown => VersionLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Always offered; says how many games when some are behind.</summary>
    public string UpdateLabel => Outdated.Count > 0 ? Strings.UpdateGames(Outdated.Count) : Strings.UpdateAllGames;

    /// <summary>Bytes held by builds that no game was installed from.</summary>
    private long OldSize { get; set; }

    public string RemoveOldLabel => OldSize > 0 ? Strings.RemoveOldBuildsSized(OldSize) : Strings.RemoveOldBuilds;

    public Visibility XmxShown => Tsume.Upscaling.GpuVendor.HasIntelXmxGpu() ? Visibility.Visible : Visibility.Collapsed;

    public string XmxLabel => _xmx is null ? Strings.AddXmx : Strings.ReplaceXmx;

    public string XmxLine => _xmx is null
        ? Strings.XmxNotAdded
        : Strings.XmxHeld(_xmx.Version, _xmx.Added.ToString("dd MMM yy", CultureInfo.CurrentCulture));

    public bool IsCheckingOptiPatcher { get; private set => Set(ref field, value); }
    public bool IsDownloadingOptiPatcher { get; private set => Set(ref field, value); }
    public bool IsOptiPatcherBusy => IsCheckingOptiPatcher || IsDownloadingOptiPatcher;
    public string OptiPatcherProgress { get; private set => Set(ref field, value); } = "";

    public string OptiPatcherLabel => _optiPatcher is null ? Strings.DownloadOptiPatcher : Strings.CheckForNewerOptiPatcher;
    public string OptiPatcherActionGlyph => _optiPatcher is null ? Glyphs.Download : Glyphs.Inspect;

    public string OptiPatcherLine => _optiPatcher is null
        ? Strings.OptiPatcherNotDownloaded
        : Strings.OptiPatcherHeld(_optiPatcher.Release.Tag, _optiPatcher.DownloadedAt.ToString("dd MMM yy", CultureInfo.CurrentCulture));

    public Visibility OptiPatcherActionShown => !IsOptiPatcherBusy && _availableOptiPatcher is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OptiPatcherBusyCardShown => IsOptiPatcherBusy && _availableOptiPatcher is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OptiPatcherAvailableShown => _availableOptiPatcher is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OptiPatcherConfirmShown => _availableOptiPatcher is not null && !IsDownloadingOptiPatcher ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OptiPatcherDownloadingShown => _availableOptiPatcher is not null && IsDownloadingOptiPatcher ? Visibility.Visible : Visibility.Collapsed;

    public string OptiPatcherAvailableHeading => IsDownloadingOptiPatcher
        ? Strings.DownloadingOptiPatcherHeading
        : Strings.NewerOptiPatcherAvailableHeading;

    public string OptiPatcherAvailableLine => _availableOptiPatcher is null ? "" : $"OptiPatcher {_availableOptiPatcher.Tag}";
    public string OptiPatcherAvailableDetail => _availableOptiPatcher is { Size: > 0 } ? Strings.FormatSize(_availableOptiPatcher.Size) : "";
    public Visibility OptiPatcherAvailableDetailShown => OptiPatcherAvailableDetail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string ExtrasLine { get; private set; } = "";

    public string Status { get; private set => Set(ref field, value); } = "";

    private async Task RunAsync(Func<Task> action)
    {
        if (_hub.IsBusy) return;
        _hub.IsBusy = true;
        _busyHere = true;

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
            _busyHere = false;
            _hub.IsBusy = false;
            Look();
            if (!IsOpen && _hub.IsOpen) _hub.ShowGames();
            Settled?.Invoke();
        }
    }

    private async Task RunCancellableAsync(Func<CancellationToken, Task> action, string cancelledStatus,
        Action? onCancelled = null, bool isOptiPatcher = false)
    {
        if (_hub.IsBusy) return;
        _hub.IsBusy = true;
        _busyHere = true;

        using var cancel = new CancellationTokenSource();
        if (isOptiPatcher) _optiPatcherCancel = cancel;
        else _buildCancel = cancel;

        try
        {
            await action(cancel.Token);
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            onCancelled?.Invoke();
            Status = cancelledStatus;
        }
        catch (Exception error)
        {
            Status = Strings.Explain(error);
        }
        finally
        {
            if (isOptiPatcher && ReferenceEquals(_optiPatcherCancel, cancel)) _optiPatcherCancel = null;
            else if (!isOptiPatcher && ReferenceEquals(_buildCancel, cancel)) _buildCancel = null;
            IsChecking = false;
            IsDownloading = false;
            DownloadProgress = "";
            IsCheckingOptiPatcher = false;
            IsDownloadingOptiPatcher = false;
            OptiPatcherProgress = "";
            _busyHere = false;
            _hub.IsBusy = false;
            Look();
            if (!IsOpen && _hub.IsOpen) _hub.ShowGames();
            if (isOptiPatcher) OptiPatcherSettled?.Invoke();
            else Settled?.Invoke();
        }
    }

    private async Task CheckForNewerAsync(CancellationToken cancel)
    {
        _available = null;
        Status = "";
        IsChecking = true;
        Notify("");
        Settled?.Invoke();

        var newest = await NightlyFeed.NewestAsync(cancel);
        cancel.ThrowIfCancellationRequested();
        var currentTag = _hub.Builds.Current()?.Build.Tag;

        if (currentTag == newest.Tag)
        {
            Status = Strings.HaveNewest;
            return;
        }

        _available = newest;
        Status = "";
    }

    private async Task DownloadNewerAsync(CancellationToken cancel)
    {
        if (_available is not { } target) return;

        Status = "";
        IsDownloading = true;
        DownloadProgress = Strings.Downloading(0);
        Notify("");
        Settled?.Invoke();

        await _hub.FetchAsync(target, text =>
        {
            if (IsDownloading && !cancel.IsCancellationRequested) DownloadProgress = text;
        }, cancel);

        _available = null;
        Status = Strings.GotNewer(UpscalingViewModel.Night(target.Tag));
    }

    private void OnCancelUpdate()
    {
        if (_buildCancel is { } cancel)
        {
            cancel.Cancel();
            return;
        }

        if (_hub.IsBusy || _available is null) return;

        _available = null;
        Status = "";
        Notify("");
        Settled?.Invoke();
    }

    private async Task CheckOrDownloadOptiPatcherAsync(CancellationToken cancel)
    {
        _availableOptiPatcher = null;
        Status = "";
        IsCheckingOptiPatcher = true;
        OptiPatcherProgress = Strings.CheckingOptiPatcher;
        Notify("");
        Settled?.Invoke();

        var newest = await OptiPatcherFeed.NewestAsync(cancel);
        cancel.ThrowIfCancellationRequested();

        var current = _hub.OptiPatcher.Current();

        // First download: fetch immediately since the user pressed "Download OptiPatcher".
        if (current is null)
        {
            IsCheckingOptiPatcher = false;
            IsDownloadingOptiPatcher = true;
            OptiPatcherProgress = Strings.DownloadingOptiPatcher(newest.Tag);
            Notify("");

            await _hub.FetchOptiPatcherAsync(newest, text =>
            {
                if (IsDownloadingOptiPatcher && !cancel.IsCancellationRequested) OptiPatcherProgress = text;
            }, cancel);

            Status = Strings.GotOptiPatcher(newest.Tag);
            return;
        }

        if (current.Release.Tag == newest.Tag && current.Release.Sha256 == newest.Sha256)
        {
            Status = Strings.HaveNewestOptiPatcher(newest.Tag);
            return;
        }

        _availableOptiPatcher = newest;
        Status = "";
    }

    private async Task DownloadOptiPatcherAsync(CancellationToken cancel)
    {
        if (_availableOptiPatcher is not { } target) return;

        Status = "";
        IsDownloadingOptiPatcher = true;
        OptiPatcherProgress = Strings.DownloadingOptiPatcher(target.Tag);
        Notify("");
        Settled?.Invoke();

        await _hub.FetchOptiPatcherAsync(target, text =>
        {
            if (IsDownloadingOptiPatcher && !cancel.IsCancellationRequested) OptiPatcherProgress = text;
        }, cancel);

        _availableOptiPatcher = null;
        Status = Strings.GotOptiPatcher(target.Tag);
    }

    private void OnCancelOptiPatcher()
    {
        if (_optiPatcherCancel is { } cancel)
        {
            cancel.Cancel();
            return;
        }

        if (_hub.IsBusy || _availableOptiPatcher is null) return;

        _availableOptiPatcher = null;
        Status = "";
        Notify("");
        OptiPatcherSettled?.Invoke();
    }

    /// <summary>
    /// Imports the XMX upscaler from the newest valid archive on the Desktop or in Downloads.
    /// The archive itself is left in place.
    /// </summary>
    private async Task AddXmxAsync()
    {
        if (!Tsume.Upscaling.GpuVendor.HasIntelXmxGpu()) return;

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

        var plan = Outdated
            .Select(manifest =>
            {
                if (_hub.List.Find(manifest.GameId) is { } known)
                    return (Manifest: manifest, Listed: known, KnownInList: true, Settings: _hub.SettingsForUpdate(manifest, known));

                var fallback = new Tsume.Upscaling.Games.ListedGame
                {
                    Game = new Tsume.Upscaling.Games.Game
                    {
                        Id = manifest.GameId,
                        Name = manifest.GameName,
                        Folder = Tsume.Upscaling.Games.GameInspector.InferRootFolder(manifest.Executable),
                        Executable = manifest.Executable,
                        Store = Tsume.Upscaling.Games.GameStore.Custom,
                    },
                };
                var settings = Installer.ReadSettings(manifest, _hub.List.Defaults);
                return (Manifest: manifest, Listed: fallback, KnownInList: false, Settings: settings);
            })
            .ToList();

        // When at least one game in the batch needs stored OptiPatcher, verify it up front so a damaged file
        // aborts cleanly instead of uninstalling the first game and stripping OptiPatcher from the rest.
        if (plan.Any(item => item.Settings.OptiPatcher)
            && !_hub.Installer.ExtrasHaveOptiPatcher
            && _hub.OptiPatcher.Current() is { } patcher)
        {
            await Task.Run(() => OptiPatcherStore.Verified(patcher));
        }

        var updated = 0;
        var failures = new List<string>();

        foreach (var (manifest, listed, knownInList, settings) in plan)
        {
            Status = $"{manifest.GameName}: {Strings.Installing}";
            try
            {
                await Task.Run(() => _hub.PutIn(listed, build, settings));
                if (knownInList) listed.Settings = settings;
                updated++;
            }
            catch (Exception error)
            {
                failures.Add(Strings.CouldNotUpdate(manifest.GameName, Strings.Explain(error)));
            }
        }

        if (updated > 0) _hub.SaveList();

        Status = string.Join(Environment.NewLine, failures.Prepend(Strings.UpdatedGames(updated)));
    }
}
