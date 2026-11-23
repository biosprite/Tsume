using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Theme;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.OptiPatcher;

namespace Tsume.App.ViewModels;

/// <summary>
/// One game's page: OptiScaler's state in it, install/remove buttons, and its settings.
/// The primary button is always present (Install, Update, Repair, else Remove) so focus never loses it.
/// </summary>
public sealed class UpscalingGameViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;

    private ListedGame? _listed;
    private InstallManifest? _manifest;
    private InstallState _state;
    private StoredBuild? _build;
    private StoredOptiPatcher? _patcher;
    private bool _hasXmx;
    private bool _hasOptiPatcher;
    private string? _existingFolder;

    /// <param name="dismiss">Closes the panel and completes once it is fully off screen.</param>
    public UpscalingGameViewModel(UpscalingViewModel hub, Func<Task> dismiss)
    {
        _hub = hub;
        _hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpscalingViewModel.IsBusy))
            {
                Notify(nameof(CanAct));
                Notify(nameof(CanTune));
            }
        };

        Tuning.Changed += OnTuned;

        Primary = new Command(() => _ = RunAsync(_state switch
        {
            InstallState.FolderGone             => ForgetAsync,
            InstallState.Healthy when IsCurrent => RemoveAsync,
            _                                   => InstallAsync,
        }));

        Remove = new Command(() => _ = RunAsync(RemoveAsync));

        TakeOffList = new Command(() =>
        {
            if (_listed is null || _hub.IsBusy) return;

            _hub.List.Games.Remove(_listed);
            _hub.SaveList();
            IsOpen = false;
        });

        OpenFolder = new Command(async () =>
        {
            if (ExistingFolder() is not { } folder)
            {
                _existingFolder = null;
                Notify(nameof(CanOpenFolder));
                Status = Strings.CannotOpenFolder;
                Settled?.Invoke();
                return;
            }

            // Started while the panel is still in front: Windows lets only the foreground program
            // bring a new window forward, so after dismissing the folder would open behind the game.
            try
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Status = Strings.CannotOpenFolder;
                return;
            }

            await dismiss();
        });
    }

    /// <summary>Raised after an action, when buttons may have come or gone.</summary>
    public event Action? Settled;

    public UpscalerTuningViewModel Tuning { get; } = new();

    public ICommand Primary { get; }
    public ICommand Remove { get; }
    public ICommand TakeOffList { get; }
    public ICommand OpenFolder { get; }

    public async void Show(ListedGame listed)
    {
        _listed = listed;
        Status = "";

        Look();          // at once, from what was last known
        IsOpen = true;

        // Then re-inspect: the folder may have changed since the list was made.
        GameFacts? facts;
        try
        {
            facts = await Task.Run(() => GameInspector.Inspect(listed.Game));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            return;   // what was last known stays on the page
        }

        if (_listed != listed || _hub.IsBusy) return;

        listed.Facts = facts;
        Look();
    }

    /// <summary>Reads the install state and settings and shows them.</summary>
    private void Look()
    {
        if (_listed is not { } listed) return;

        _manifest = _hub.Installer.Find(listed.Game.Id);
        _state = Installer.Check(_manifest);
        _build = _hub.Builds.Current();
        _patcher = _hub.OptiPatcher.Current();

        var isHealthy = _manifest is not null && _state == InstallState.Healthy;
        var storeHasXmx = _hub.Xmx.Current() is not null;
        var canUseOptiPatcher = _patcher is not null || _hub.Installer.ExtrasHaveOptiPatcher;

        // An installed game's settings come from its own file: OptiScaler's
        // in-game overlay may have changed them.
        var remembered = _hub.List.SettingsFor(listed.Game.Id);
        var settings = isHealthy
            ? Installer.ReadSettings(_manifest!, remembered)
            : remembered;

        // XMX is selectable if already in the game, or would go in with an install.
        // OptiPatcher can be toggled directly on an installed game whenever Tsume holds it (or the game already has it).
        _hasXmx = isHealthy
            ? Installer.HasXmx(_manifest!)
            : storeHasXmx;
        _hasOptiPatcher = isHealthy
            ? Installer.HasOptiPatcher(_manifest!) || canUseOptiPatcher
            : canUseOptiPatcher;

        // Lock the proxy only while cleanly installed, or when someone else's OptiScaler sits in an uninstalled game.
        // Broken installs (Replaced / CutShort) can pick a new proxy before repairing.
        var lockedProxy = isHealthy ? _manifest!.Proxy : _manifest is null ? Facts?.ExistingProxy : null;

        _existingFolder = ExistingFolder();
        Tuning.Load(settings, _hasXmx, _hasOptiPatcher, isInstalled: isHealthy, lockedProxy,
            xmxAvailableToAdd: isHealthy && storeHasXmx);
        Notify("");
    }

    private string? ExistingFolder()
    {
        foreach (var candidate in new[] { _manifest?.Target, Facts?.Target, _listed?.Game.Folder })
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate)) return candidate;
        return null;
    }

    public string Name => _listed?.Game.Name ?? "";

    private GameFacts? Facts => _listed?.Facts;

    /// <summary>True if reinstalling would change nothing: same build as on disk, and XMX included if Tsume has it.</summary>
    private bool IsCurrent => _manifest is null || _hub.Installer.IsCurrent(_manifest, _build);

    /// <summary>The nightly in the game is not the one on disk.</summary>
    private bool IsOlderBuild => _build is not null && _manifest is not null && _manifest.Build != _build.Build.Tag;

    /// <summary>True when OptiPatcher is installed in this game and enabled in OptiScaler.ini.</summary>
    public bool IsOptiPatcherActive => _state == InstallState.Healthy && _manifest is not null && Installer.HasOptiPatcher(_manifest) && Tuning.OptiPatcher;

    /// <summary>Badge shown in the status card while OptiPatcher is active on this game.</summary>
    public string OptiPatcherBadge => string.IsNullOrEmpty(_manifest?.OptiPatcher)
        ? Strings.OptiPatcherBadge
        : Strings.OptiPatcherBadgeVersion(_manifest.OptiPatcher);

    public Visibility OptiPatcherBadgeShown => IsOptiPatcherActive ? Visibility.Visible : Visibility.Collapsed;

    private bool IsOptiPatcherOutdated =>
        _patcher is not null && _manifest is not null && !_hub.Installer.IsOptiPatcherCurrent(_manifest, _patcher);

    /// <summary>OptiScaler is not offered: an anti-cheat, or nothing to run.</summary>
    private bool IsBlocked => _state == InstallState.None && Facts is not { AntiCheat: null };

    public string StateLine => _state switch
    {
        InstallState.Healthy    => Strings.StateInstalled(UpscalingViewModel.Night(_manifest!.Build), _manifest.Proxy),
        InstallState.Replaced   => Strings.StateReplaced,
        InstallState.CutShort   => Strings.StateCutShort,
        InstallState.FolderGone => Strings.StateFolderGone,

        _ when Facts is null                    => Strings.StateNothingToRun,
        _ when Facts.AntiCheat is { } antiCheat => Strings.StateBlocked(antiCheat),
        _                                       => Strings.NotInstalled,
    };

    /// <summary>What the game ships that OptiScaler can take over.</summary>
    public string FoundLine => Facts switch
    {
        null                     => "",
        { Upscalers.Count: 0 }   => Strings.FoundNoUpscalers,
        var facts                => Strings.FoundUpscalers(string.Join(", ", facts.Upscalers)),
    };

    /// <summary>Individual upscalers found in the game, shown as tags.</summary>
    public IReadOnlyList<string> FoundUpscalers => Facts?.Upscalers ?? [];

    public Visibility FoundUpscalersShown => FoundUpscalers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FoundNoUpscalersShown => Facts is { Upscalers.Count: 0 } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Where OptiScaler goes, or went.</summary>
    public string FolderLine => _manifest?.Target ?? Facts?.Target ?? _listed?.Game.Folder ?? "";

    public Visibility FolderLineShown => FolderLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>True when the game's target or root folder exists on disk.</summary>
    public bool CanOpenFolder => _existingFolder is not null;

    /// <summary>Something worth knowing before pressing a button; empty when there is nothing.</summary>
    public string Notice =>
        _state == InstallState.None && Facts is { AntiCheat: null, ExistingProxy: { } proxy } ? Strings.SomeoneElsesOptiScaler(proxy)
        : _state == InstallState.Healthy && IsOlderBuild ? Strings.NewerBuildReady(UpscalingViewModel.Night(_build!.Build.Tag))
        : _state == InstallState.Healthy && IsOptiPatcherOutdated ? Strings.OptiPatcherUpdateReady(_patcher!.Release.Tag)
        : _state == InstallState.Healthy && !IsCurrent ? Strings.XmxReady
        : "";

    public Visibility NoticeShown => Notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The ban warning shows wherever Install is offered: the anti-cheat check only knows some of them.</summary>
    public Visibility CautionShown => _state == InstallState.None && !IsBlocked ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The result of the last action, or why it failed.</summary>
    public string Status { get; private set => Set(ref field, value); } = "";

    public string PrimaryLabel => _state switch
    {
        InstallState.None                   => Strings.InstallOptiScaler,
        InstallState.Healthy when IsCurrent => Strings.RemoveOptiScaler,
        InstallState.Healthy                => Strings.UpdateOptiScaler,
        InstallState.FolderGone             => Strings.ForgetInstall,
        _                                   => Strings.RepairOptiScaler,
    };

    public string PrimaryGlyph => _state switch
    {
        InstallState.None                   => Glyphs.Download,
        InstallState.Healthy when IsCurrent => Glyphs.Remove,
        InstallState.FolderGone             => Glyphs.Quit,
        _                                   => Glyphs.Update,
    };

    /// <summary>False while files are being downloaded, installed, or removed.</summary>
    public bool CanAct => !_hub.IsBusy;

    /// <summary>False while files are being changed so settings cannot race an install or removal.</summary>
    public bool CanTune => !_hub.IsBusy;

    /// <summary>Primary action (Install / Update / Repair / Remove / Forget), hidden only for blocked games.</summary>
    public Visibility PrimaryShown => IsBlocked ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Remove gets a button of its own while the first one says Update or Repair.</summary>
    public Visibility RemoveShown =>
        _state is InstallState.Replaced or InstallState.CutShort || (_state == InstallState.Healthy && !IsCurrent)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Only a game added by hand can be taken off the list, and only once OptiScaler is out of it.</summary>
    public Visibility TakeOffListShown =>
        _listed?.Game.Store == GameStore.Custom && _state == InstallState.None ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Hidden for a game OptiScaler cannot go into.</summary>
    public Visibility TuningShown => IsBlocked || _state == InstallState.FolderGone ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Runs one action at a time, and turns a failure into a line on the page.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        if (_listed is null || _hub.IsBusy) return;
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

    /// <summary>Install, Update and Repair alike: removes what is there, then installs the build on disk.</summary>
    private async Task InstallAsync()
    {
        var listed = _listed!;
        var previous = _hub.List.SettingsFor(listed.Game.Id);
        var settings = _manifest is not null && _state == InstallState.Healthy
            ? _hub.SettingsForUpdate(_manifest, listed)
            : Tuning.Settings.PreservingUnavailable(previous, _hasXmx, _hasOptiPatcher);

        var build = await _hub.NeedBuildAsync(text => Status = text);

        Status = Strings.Installing;
        var removed = await Task.Run(() => _hub.PutIn(listed, build, settings));

        // From now on the game has its own settings, independent of the defaults.
        listed.Settings = settings;
        _hub.SaveList();

        Status = removed is { LeftAlone.Count: > 0 } ? Strings.RemovedButLeft(removed.LeftAlone.Count) : Strings.InstalledDone;
    }

    private async Task RemoveAsync()
    {
        var listed = _listed!;

        Status = Strings.Removing;
        var (report, facts) = await Task.Run(() =>
        {
            var removed = _hub.Installer.Remove(listed.Game.Id);
            try
            {
                return (removed, GameInspector.Inspect(listed.Game));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return (removed, listed.Facts is { } previous ? previous with { ExistingProxy = null } : null);
            }
        });

        listed.Facts = facts;
        _hub.SaveList();

        Status = report.LeftAlone.Count > 0 ? Strings.RemovedButLeft(report.LeftAlone.Count) : Strings.RemovedDone;
    }

    private Task ForgetAsync()
    {
        _hub.Installer.Forget(_listed!.Game.Id);
        Status = Strings.Forgotten;
        return Task.CompletedTask;
    }

    /// <summary>A setting changed: remember it, and write it to the game if OptiScaler is in it.</summary>
    private void OnTuned()
    {
        if (_listed is not { } listed || _hub.IsBusy) return;

        var isHealthy = _manifest is not null && _state == InstallState.Healthy;
        var previous = _hub.List.SettingsFor(listed.Game.Id);
        var remembered = Tuning.Settings.PreservingUnavailable(previous, _hasXmx, _hasOptiPatcher);

        // Keep the user's proxy preference untouched while cleanly installed (the proxy dropdown is locked).
        if (isHealthy) remembered = remembered with { Proxy = listed.Settings?.Proxy ?? "" };

        if (!isHealthy)
        {
            listed.Settings = remembered;
            _hub.SaveList();
            return;
        }

        try
        {
            _hub.Installer.WriteSettings(_manifest!, Tuning.Settings);

            listed.Settings = remembered;
            _hub.SaveList();

            _patcher = _hub.OptiPatcher.Current();
            _hasOptiPatcher = Installer.HasOptiPatcher(_manifest!) || _patcher is not null || _hub.Installer.ExtrasHaveOptiPatcher;
            Status = Strings.SettingsSaved;
            Notify("");

            if (!_hasOptiPatcher && Tuning.HasOptiPatcher)
            {
                Tuning.Load(Installer.ReadSettings(_manifest!, remembered), hasXmx: _hasXmx, hasOptiPatcher: false,
                    isInstalled: true, lockedProxy: _manifest!.Proxy,
                    xmxAvailableToAdd: _hub.Xmx.Current() is not null);
            }
            else if (Tuning.Settings.Upscaler == UpscalerChoice.Fsr4Xmx && !_hasXmx)
            {
                // XMX is not in this game yet: show what was actually written instead.
                Tuning.Load(Installer.ReadSettings(_manifest!, Tuning.Settings), hasXmx: false, hasOptiPatcher: _hasOptiPatcher,
                    isInstalled: true, lockedProxy: _manifest!.Proxy,
                    xmxAvailableToAdd: _hub.Xmx.Current() is not null);
                Status = Strings.XmxNotInGame;
            }
            else if (Tuning.Settings.OptiPatcher && !Installer.HasOptiPatcher(_manifest!))
            {
                Tuning.Load(Installer.ReadSettings(_manifest!, Tuning.Settings), hasXmx: _hasXmx, hasOptiPatcher: false,
                    isInstalled: true, lockedProxy: _manifest!.Proxy,
                    xmxAvailableToAdd: _hub.Xmx.Current() is not null);
                Status = Strings.OptiPatcherNotInGame;
            }
        }
        catch (Exception error)
        {
            Look();
            Status = Strings.Explain(error);
        }
    }
}
