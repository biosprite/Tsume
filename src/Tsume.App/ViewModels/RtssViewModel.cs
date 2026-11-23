using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform.Rtss;

namespace Tsume.App.ViewModels;

/// <summary>
/// The RTSS tile and screen: detects an existing RTSS installation (linking to Guru3D rather than
/// bundling RTSS), drives Tsume's Steam-style RTSS overlay with an option to leave the user's own
/// RTSS overlay untouched, and controls RTSS's global OSD visibility and frame-rate cap.
/// </summary>
public sealed class RtssViewModel : LayerViewModel
{
    public const double MinScale = RtssSettings.MinScale;
    public const double MaxScale = RtssSettings.MaxScale;
    public const double MinCustomFps = RtssSettings.MinCustomFps;
    public const double MaxCustomFps = RtssSettings.MaxCustomFps;

    private static readonly int[] CapPresets = [0, 30, 40, 60];

    private readonly RtssOverlayService _service;
    private readonly Func<Task> _dismiss;
    private readonly DispatcherTimer _capSettle = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private bool _absorbing;
    private bool _launching;
    private bool _closing;
    private bool _syncingExclusions;
    private bool _togglingStartup;
    private int? _excludedGamesCount;
    private string? _downloadedInstaller;

    // Minimum gap between winget update checks, so reopening the pages never spams winget.
    private const long UpdateCheckSpacing = 5000;
    private long _lastUpdateCheck = -UpdateCheckSpacing;

    public RtssViewModel(RtssOverlayService service, Func<Task> dismiss)
    {
        _service = service;
        _dismiss = dismiss;

        _capSettle.Tick += (_, _) => { _capSettle.Stop(); ApplyFrameRateLimit(CustomCap); };

        OpenDownloadPage = new Command(async () =>
        {
            // Started while the panel is still in front so Windows brings the browser forward over a game.
            if (RtssInstallation.OpenDownloadPage())
            {
                Status = Strings.RtssOpeningDownload;
                await _dismiss();
            }
        });

        InstallAutomatically = new Command(async () => await InstallAutomaticallyAsync());
        Uninstall = new Command(async () => await UninstallAsync());
        UpdateRtss = new Command(async () => await UpdateAsync());

        Download = new RtssDownloadViewModel(
            opened: () =>
            {
                ConfirmingUninstall = false;
                if (IsBusy) return;
                RefreshState();
                Status = DefaultStatus();
                _ = CheckForUpdateAsync();
            },
            closed: () => ConfirmingUninstall = false);

        RunDownloadedInstaller = new Command(async () =>
        {
            // Two installers running at once fight over the same files.
            if (IsBusy || RtssWingetInstaller.IsSetupRunning())
            {
                Status = Strings.RtssInstallAlreadyRunning;
                return;
            }

            if (_downloadedInstaller is not { } package) return;
            await _dismiss();
            if (!await RtssInstallation.RunDownloadedInstallerAsync(package))
                Status = Strings.RtssInstallerFailed;
        });

        CheckAgain = new Command(() =>
        {
            if (IsBusy) return;   // an install or uninstall reports its own result when it finishes
            RefreshState();
            if (IsInstalled) _ = RefreshStartWithWindowsAsync();
            Status = IsInstalled ? DefaultStatus() : Strings.RtssStillNotFound;
            Settled?.Invoke();
        });

        ToggleUseTsumeOverlay = new Command(() =>
        {
            var next = _service.Settings with { UseTsumeOverlay = !UseTsumeOverlay };
            _service.ApplySettings(next);
            SyncFromService();
            Status = DefaultStatus();
            Settled?.Invoke();
        });

        ToggleOsdVisible = new Command(() =>
        {
            if (!IsRunning) return;
            var target = !IsOsdVisible;
            if (_service.SetOsdVisible(target))
            {
                IsOsdVisible = target;
                NotifyTile();
            }
        });

        StartRtss = new Command(async () => await StartRtssAsync());
        OpenRtssMenu = new Command(async () => await OpenRtssMenuAsync());
        CloseRtss = new Command(async () => await CloseRtssAsync());
        ToggleStartWithWindows = new Command(async () => await ToggleStartWithWindowsAsync());
        ToggleSkipAntiCheat = new Command(async () => await ToggleSkipAntiCheatAsync());

        SyncFromService();
    }

    /// <summary>Raised when sections appear or disappear, so the screen can keep focus valid.</summary>
    public event Action? Settled;

    public ICommand OpenDownloadPage { get; }
    public ICommand InstallAutomatically { get; }
    public ICommand Uninstall { get; }
    public ICommand UpdateRtss { get; }
    public ICommand RunDownloadedInstaller { get; }

    /// <summary>The download page that opens over this screen.</summary>
    public RtssDownloadViewModel Download { get; }
    public ICommand CheckAgain { get; }
    public ICommand ToggleUseTsumeOverlay { get; }
    public ICommand ToggleOsdVisible { get; }
    public ICommand StartRtss { get; }
    public ICommand OpenRtssMenu { get; }
    public ICommand CloseRtss { get; }
    public ICommand ToggleStartWithWindows { get; }
    public ICommand ToggleSkipAntiCheat { get; }

    public IReadOnlyList<string> Levels { get; } =
    [
        Strings.RtssLevelOff,
        Strings.RtssLevelFps,
        Strings.RtssLevelBar,
        Strings.RtssLevelDetailed,
        Strings.RtssLevelFull,
    ];

    public IReadOnlyList<string> Corners { get; } =
    [
        Strings.RtssCornerTopLeft,
        Strings.RtssCornerTopRight,
        Strings.RtssCornerBottomLeft,
        Strings.RtssCornerBottomRight,
    ];

    public IReadOnlyList<string> FrameCapModes { get; } =
    [
        Strings.ChoiceOff,
        "30",
        "40",
        "60",
        Strings.RtssCapCustom,
    ];

    public bool IsInstalled
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            NotifyPresentation();
            Notify(nameof(IsNotRunning));
            Notify(nameof(RtssAppDetail));
            NotifyTile();
        }
    }

    // What the screens present is held steady while an install or uninstall is in flight, even though
    // RTSS.exe appears or vanishes on disk partway through. That way nobody starts a half-installed
    // RTSS, and the tile that was pressed stays put until the work has fully finished.
    private bool PresentsInstalled => IsUninstalling || IsUpdating || (IsInstalled && !IsInstalling);

    public Visibility InstalledVisibility => PresentsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotInstalledVisibility => PresentsInstalled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>True while Tsume's own winget install of RTSS is running.</summary>
    public bool IsInstalling
    {
        get;
        private set
        {
            if (Set(ref field, value)) NotifyPresentation();
        }
    }

    /// <summary>True while RTSS's uninstaller, started from the download page, is running.</summary>
    public bool IsUninstalling
    {
        get;
        private set
        {
            if (Set(ref field, value)) NotifyPresentation();
        }
    }

    /// <summary>True while Tsume's own winget update of RTSS is running.</summary>
    public bool IsUpdating
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            NotifyPresentation();
            Notify(nameof(IsNotRunning));
        }
    }

    /// <summary>True while an install, update or uninstall is in flight; everything else on the page waits.</summary>
    public bool IsBusy => IsInstalling || IsUninstalling || IsUpdating;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public string BusyText =>
        IsUninstalling ? Strings.RtssUninstalling
        : IsUpdating && InstallStage is RtssInstallStage.Installing ? Strings.RtssUpdateStageInstalling
        : InstallProgressText;

    /// <summary>True while winget is being asked for a newer RTSS; shows the small loading ring.</summary>
    public bool IsCheckingForUpdate
    {
        get;
        private set
        {
            if (Set(ref field, value)) NotifyUpdate();
        }
    }

    /// <summary>The newer RTSS winget offers, or "" when none is known.</summary>
    public string AvailableVersion
    {
        get;
        private set
        {
            if (Set(ref field, value)) NotifyUpdate();
        }
    } = "";

    /// <summary>Caption beside the Update heading: checking, up to date, or could not check.</summary>
    public string UpdateCheckText
    {
        get => IsCheckingForUpdate ? Strings.RtssUpdateChecking : field;
        private set
        {
            if (Set(ref field, value)) NotifyUpdate();
        }
    } = "";

    public Visibility UpdateSectionVisibility =>
        PresentsInstalled && CanInstallAutomatically ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UpdateCheckingVisibility => IsCheckingForUpdate ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UpdateTileVisibility =>
        AvailableVersion.Length > 0 || IsUpdating ? Visibility.Visible : Visibility.Collapsed;

    public string UpdateLabel => Strings.RtssUpdate(AvailableVersion);

    public string UpdateDetail => IsUpdating ? Strings.RtssUpdateBusy : Strings.RtssUpdateDetail(InstalledVersion);

    private void NotifyUpdate()
    {
        Notify(nameof(UpdateCheckText));
        Notify(nameof(UpdateSectionVisibility));
        Notify(nameof(UpdateCheckingVisibility));
        Notify(nameof(UpdateTileVisibility));
        Notify(nameof(UpdateLabel));
        Notify(nameof(UpdateDetail));
        Notify(nameof(DownloadPageDetail));
    }

    /// <summary>Set by the first press of Uninstall; the second press goes ahead.</summary>
    public bool ConfirmingUninstall
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(UninstallLabel));
            Notify(nameof(UninstallDetail));
        }
    }

    public string UninstallLabel => ConfirmingUninstall && !IsUninstalling
        ? Strings.RtssUninstallConfirm
        : Strings.RtssUninstall;

    public string UninstallDetail =>
        IsUninstalling        ? Strings.RtssUninstallBusy
        : ConfirmingUninstall ? Strings.RtssUninstallConfirmDetail
        : Strings.RtssUninstallDetail(InstalledVersion);

    public string DownloadPageLabel => PresentsInstalled ? Strings.RtssDownloadPageInstalled : Strings.RtssDownloadPage;

    public string DownloadPageDetail =>
        !PresentsInstalled ? Strings.RtssDownloadPageDetail
        : AvailableVersion.Length > 0 ? Strings.RtssDownloadPageUpdateDetail(AvailableVersion)
        : Strings.RtssDownloadPageInstalledDetail;

    private void NotifyPresentation()
    {
        Notify(nameof(InstalledVisibility));
        Notify(nameof(NotInstalledVisibility));
        Notify(nameof(AutoInstallVisibility));
        Notify(nameof(AutoInstallDetail));
        Notify(nameof(DownloadedInstallerVisibility));
        Notify(nameof(IsBusy));
        Notify(nameof(BusyVisibility));
        Notify(nameof(BusyText));
        Notify(nameof(UninstallLabel));
        Notify(nameof(UninstallDetail));
        Notify(nameof(DownloadPageLabel));
        Notify(nameof(DownloadPageDetail));
        NotifyUpdate();
    }

    /// <summary>How far the automatic install has got; only ever moves forward.</summary>
    public RtssInstallStage InstallStage
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(InstallProgressText));
            Notify(nameof(BusyText));
        }
    }

    public string InstallProgressText => InstallStage switch
    {
        RtssInstallStage.Downloading => Strings.RtssInstallStageDownloading,
        RtssInstallStage.Installing  => Strings.RtssInstallStageInstalling,
        RtssInstallStage.Finishing   => Strings.RtssInstallStageFinishing,
        _                            => Strings.RtssInstallStagePreparing,
    };

    /// <summary>False on PCs without Windows Package Manager, where only the Guru3D link is offered.</summary>
    public bool CanInstallAutomatically
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(AutoInstallVisibility));
            Notify(nameof(UpdateSectionVisibility));
        }
    }

    public Visibility AutoInstallVisibility =>
        !PresentsInstalled && CanInstallAutomatically ? Visibility.Visible : Visibility.Collapsed;

    public string AutoInstallDetail => IsInstalling ? Strings.RtssInstallAutoBusy : Strings.RtssInstallAutoDetail;

    public bool IsRunning
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(IsNotRunning));
            Notify(nameof(RtssAppDetail));
            NotifyTile();
        }
    }

    public bool IsNotRunning => IsInstalled && !IsRunning && !IsUpdating;

    public bool StartsWithWindows { get; private set => Set(ref field, value); }

    public bool SkipAntiCheatGames
    {
        get;
        private set
        {
            if (Set(ref field, value)) Notify(nameof(SkipAntiCheatDetail));
        }
    }

    public string SkipAntiCheatDetail =>
        _syncingExclusions          ? Strings.RtssSkipAntiCheatBusy
        : !SkipAntiCheatGames       ? Strings.RtssSkipAntiCheatOff
        : _excludedGamesCount > 0   ? Strings.RtssSkipAntiCheatOn(_excludedGamesCount.Value)
        : Strings.RtssSkipAntiCheatNone;

    public bool HasDownloadedInstaller
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(DownloadedInstallerVisibility));
            Notify(nameof(DownloadedInstallerDetail));
        }
    }

    public Visibility DownloadedInstallerVisibility =>
        !PresentsInstalled && HasDownloadedInstaller ? Visibility.Visible : Visibility.Collapsed;

    public string DownloadedInstallerDetail =>
        _downloadedInstaller is { } path ? Strings.RtssRunInstallerDetail(Path.GetFileName(path)) : "";

    public string InstalledVersion
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(RtssAppDetail));
            Notify(nameof(UninstallDetail));
            Notify(nameof(UpdateDetail));
        }
    } = "";

    public string RtssAppDetail =>
        _launching ? Strings.RtssStarting
        : IsRunning ? Strings.RtssStatusRunning(InstalledVersion)
        : Strings.RtssStatusStopped(InstalledVersion);

    public bool UseTsumeOverlay
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Notify(nameof(UsesCustomOverlay));
            Notify(nameof(UseTsumeOverlayDetail));
            Notify(nameof(IsTsumeOverlayShown));
            NotifyTile();
        }
    }

    public bool UsesCustomOverlay => !UseTsumeOverlay;

    public string UseTsumeOverlayDetail => UseTsumeOverlay
        ? Strings.RtssUseTsumeOverlayOn
        : Strings.RtssUseTsumeOverlayOff;

    public int LevelIndex
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 0, Levels.Count - 1);
            if (!Set(ref field, clamped)) return;

            Notify(nameof(LevelDetail));
            Notify(nameof(IsTsumeOverlayShown));
            NotifyTile();

            if (_absorbing) return;

            _service.ApplySettings(_service.Settings with { Level = (RtssOverlayLevel)clamped });
            if (clamped > 0 && IsInstalled && !IsRunning) _ = StartRtssAsync();
            Status = DefaultStatus();
        }
    }

    public bool IsTsumeOverlayShown => UseTsumeOverlay && LevelIndex > 0;

    public string LevelDetail => (RtssOverlayLevel)LevelIndex switch
    {
        RtssOverlayLevel.Fps      => Strings.RtssLevelFpsDetail,
        RtssOverlayLevel.Bar      => Strings.RtssLevelBarDetail,
        RtssOverlayLevel.Detailed => Strings.RtssLevelDetailedDetail,
        RtssOverlayLevel.Full     => Strings.RtssLevelFullDetail,
        _                         => Strings.RtssLevelOffDetail,
    };

    public int CornerIndex
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 0, Corners.Count - 1);
            if (!Set(ref field, clamped) || _absorbing) return;

            _service.ApplySettings(_service.Settings with { Corner = (RtssCorner)clamped });
        }
    }

    public double Scale
    {
        get;
        set
        {
            var rounded = Math.Clamp(Math.Round(value), MinScale, MaxScale);
            if (!Set(ref field, rounded) || _absorbing) return;

            _service.ApplySettings(_service.Settings with { Scale = (int)rounded });
        }
    } = 2;

    public bool IsOsdVisible
    {
        get;
        private set
        {
            if (Set(ref field, value)) NotifyTile();
        }
    }

    public int FrameCapIndex
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 0, FrameCapModes.Count - 1);
            if (!Set(ref field, clamped)) return;

            Notify(nameof(ShowsCustomCapSlider));

            if (_absorbing) return;

            var targetFps = clamped < CapPresets.Length ? CapPresets[clamped] : (int)CustomCap;
            ApplyFrameRateLimit(targetFps);
        }
    }

    public bool ShowsCustomCapSlider => FrameCapIndex == CapPresets.Length;

    public double CustomCap
    {
        get;
        set
        {
            var rounded = Math.Clamp(Math.Round(value), MinCustomFps, MaxCustomFps);
            if (!Set(ref field, rounded) || _absorbing) return;

            _service.ApplySettings(_service.Settings with { CustomFrameRateLimit = (int)rounded });

            if (!ShowsCustomCapSlider) return;
            _capSettle.Stop();
            _capSettle.Start();
        }
    } = 45;

    public int ActiveFrameRateLimit
    {
        get;
        private set
        {
            if (Set(ref field, value)) NotifyTile();
        }
    }

    public string Status { get; private set => Set(ref field, value); } = "";

    /// <summary>Lights the main-screen RTSS tile whenever the overlay or a frame-rate cap is active.</summary>
    public bool IsActive => IsRunning && (
        (UseTsumeOverlay && LevelIndex > 0)
        || (!UseTsumeOverlay && IsOsdVisible)
        || ActiveFrameRateLimit > 0);

    /// <summary>Short status under the main-screen RTSS tile, e.g. "Bar · 60 FPS", "60 FPS", or empty.</summary>
    public string TileDetail
    {
        get
        {
            if (!IsRunning) return "";

            string? overlayPart = UseTsumeOverlay
                ? LevelIndex > 0 ? Levels[LevelIndex] : null
                : IsOsdVisible ? Strings.RtssOsdOn : null;

            var capPart = ActiveFrameRateLimit > 0 ? Strings.RtssFpsShort(ActiveFrameRateLimit) : null;

            return (overlayPart, capPart) switch
            {
                ({ } o, { } c) => $"{o} · {c}",
                ({ } o, null)  => o,
                (null, { } c)  => c,
                _              => "",
            };
        }
    }

    /// <summary>Fast refresh for the main-screen tile whenever the panel opens.</summary>
    public void RefreshTile()
    {
        // Looking for RTSS loads its hooks DLL, which would stop the uninstaller deleting it.
        if (IsUninstalling || IsUpdating) return;

        _ = Task.Run(() =>
        {
            var install = _service.RefreshInstallation();
            var running = install is not null && _service.IsRunning;
            var osd = running ? _service.IsOsdVisible() : null;
            var limit = running ? _service.GetFrameRateLimit() : null;

            Application.Current?.Dispatcher.BeginInvoke(() => ApplySnapshot(install, running, osd, limit, installer: null, scanInstaller: false));
        });
    }

    /// <summary>Polled while the RTSS screen is open so external RTSS start/exit updates the screen live.</summary>
    public void RefreshRunningState()
    {
        if (!IsInstalled || IsBusy || _launching || _closing) return;

        var running = _service.IsRunning;
        if (running == IsRunning) return;

        RefreshState();
        Status = DefaultStatus();
        Settled?.Invoke();
    }

    protected override void OnOpened()
    {
        if (IsBusy)
        {
            // Reopened mid-install or mid-uninstall: the progress card carries the status, and
            // re-scanning now would load the hooks DLL the uninstaller is trying to delete.
            Status = "";
            return;
        }

        RefreshState();
        Status = DefaultStatus();
        if (IsInstalled)
        {
            _ = RefreshStartWithWindowsAsync();
            if (SkipAntiCheatGames && _excludedGamesCount is null)
                _ = SyncAntiCheatAsync();
            _ = CheckForUpdateAsync();
        }
    }

    private void RefreshState()
    {
        var install = _service.RefreshInstallation();
        var running = install is not null && _service.IsRunning;
        var osd = running ? _service.IsOsdVisible() : null;
        var limit = running ? _service.GetFrameRateLimit() : null;
        var installer = install is null ? RtssInstallation.FindDownloadedInstaller() : null;
        CanInstallAutomatically = RtssWingetInstaller.FindWinget() is not null;

        ApplySnapshot(install, running, osd, limit, installer, scanInstaller: true);
    }

    private void ApplySnapshot(
        RtssInstallInfo? install, bool running, bool? osdVisible, int? frameLimit, string? installer, bool scanInstaller)
    {
        IsInstalled = install is not null;
        InstalledVersion = install?.Version ?? "";
        IsRunning = running;

        if (scanInstaller)
        {
            _downloadedInstaller = installer;
            HasDownloadedInstaller = installer is not null;
        }

        _absorbing = true;
        var settings = _service.Settings;
        UseTsumeOverlay = settings.UseTsumeOverlay;
        SkipAntiCheatGames = settings.SkipAntiCheatGames;
        LevelIndex = (int)settings.Level;
        CornerIndex = (int)settings.Corner;
        Scale = Math.Clamp(settings.Scale, RtssSettings.MinScale, RtssSettings.MaxScale);
        CustomCap = Math.Clamp(settings.CustomFrameRateLimit, RtssSettings.MinCustomFps, RtssSettings.MaxCustomFps);

        if (osdVisible is { } visible) IsOsdVisible = visible;

        if (frameLimit is { } cap)
        {
            ActiveFrameRateLimit = cap;
            var presetIndex = Array.IndexOf(CapPresets, cap);
            if (presetIndex >= 0)
            {
                FrameCapIndex = presetIndex;
            }
            else
            {
                CustomCap = Math.Clamp(cap, RtssSettings.MinCustomFps, RtssSettings.MaxCustomFps);
                FrameCapIndex = CapPresets.Length;
            }
        }
        else if (!running)
        {
            ActiveFrameRateLimit = 0;
        }
        _absorbing = false;

        NotifyTile();
    }

    private void SyncFromService()
    {
        _absorbing = true;
        var settings = _service.Settings;
        UseTsumeOverlay = settings.UseTsumeOverlay;
        SkipAntiCheatGames = settings.SkipAntiCheatGames;
        LevelIndex = (int)settings.Level;
        CornerIndex = (int)settings.Corner;
        Scale = Math.Clamp(settings.Scale, RtssSettings.MinScale, RtssSettings.MaxScale);
        CustomCap = Math.Clamp(settings.CustomFrameRateLimit, RtssSettings.MinCustomFps, RtssSettings.MaxCustomFps);
        _absorbing = false;

        NotifyTile();
    }

    private async void ApplyFrameRateLimit(double fps)
    {
        var target = (int)Math.Clamp(Math.Round(fps), 0, MaxCustomFps);
        if (!IsRunning)
        {
            Status = Strings.RtssNotRunningNote;
            return;
        }

        if (await _service.SetFrameRateLimitAsync(target))
        {
            ActiveFrameRateLimit = target;
            Status = DefaultStatus();
        }
    }

    private async Task InstallAutomaticallyAsync()
    {
        if (IsBusy || RtssWingetInstaller.IsBusy)
        {
            Status = Strings.RtssInstallAlreadyRunning;
            return;
        }

        // RTSS may have been installed since this screen was drawn (by hand, or by another tool).
        // Never run the installer over a working copy: it would restart RTSS under a running game.
        RefreshState();
        if (IsInstalled)
        {
            Status = Strings.RtssAlreadyInstalled;
            Settled?.Invoke();
            return;
        }

        InstallStage = RtssInstallStage.Preparing;
        IsInstalling = true;
        Status = "";

        // Created on the UI thread, so stage reports arrive back on it.
        var progress = new Progress<RtssInstallStage>(stage =>
        {
            if (IsInstalling && stage > InstallStage) InstallStage = stage;
        });

        RtssInstallResult result;
        try
        {
            result = await RtssWingetInstaller.InstallAsync(progress);
            if (result.Outcome is RtssInstallOutcome.Finished) InstallStage = RtssInstallStage.Finishing;

            // Trust what is on disk over winget's exit code. The installer can return a moment before
            // its files and registry entries are visible, so look a few times before giving up.
            if (result.Outcome is RtssInstallOutcome.Finished)
            {
                for (var attempt = 0; attempt < 6 && _service.RefreshInstallation() is null; attempt++)
                    await Task.Delay(500);
            }
        }
        finally
        {
            IsInstalling = false;
        }

        RefreshState();

        if (IsInstalled)
        {
            _ = RefreshStartWithWindowsAsync();
            Status = result.Outcome is RtssInstallOutcome.Finished
                ? Strings.RtssInstallDone(InstalledVersion)
                : Strings.RtssAlreadyInstalled;
        }
        else
        {
            Status = result.Outcome switch
            {
                RtssInstallOutcome.WingetMissing     => Strings.RtssInstallNoWinget,
                RtssInstallOutcome.Offline           => Strings.RtssInstallOffline,
                RtssInstallOutcome.AlreadyInstalling => Strings.RtssInstallAlreadyRunning,
                RtssInstallOutcome.TimedOut          => Strings.RtssInstallTimedOut,
                RtssInstallOutcome.CouldNotStart     => Strings.RtssInstallNoWinget,
                _ when result.ExitCode == 0          => Strings.RtssInstallNotFoundAfter,
                _                                    => Strings.RtssInstallFailed(result.ExitCode),
            };
        }

        Settled?.Invoke();
    }

    /// <summary>Asks winget for a newer RTSS, at most once every few seconds so reopening pages never spams it.</summary>
    private async Task CheckForUpdateAsync()
    {
        if (!IsInstalled || IsBusy || IsCheckingForUpdate || !CanInstallAutomatically) return;
        if (Environment.TickCount64 - _lastUpdateCheck < UpdateCheckSpacing) return;

        var version = InstalledVersion;
        IsCheckingForUpdate = true;
        try
        {
            var found = await RtssWingetInstaller.FindUpgradeAsync();
            if (InstalledVersion != version || IsBusy) return;

            AvailableVersion = found ?? "";
            UpdateCheckText = found switch
            {
                null => Strings.RtssUpdateCheckFailed,
                ""   => Strings.RtssUpdateUpToDate,
                _    => "",
            };
        }
        finally
        {
            _lastUpdateCheck = Environment.TickCount64;
            IsCheckingForUpdate = false;
        }
    }

    private async Task UpdateAsync()
    {
        if (IsBusy || RtssWingetInstaller.IsBusy)
        {
            Status = Strings.RtssInstallAlreadyRunning;
            return;
        }

        if (AvailableVersion.Length == 0) return;

        var before = InstalledVersion;
        var wasRunning = _service.IsRunning;

        ConfirmingUninstall = false;
        InstallStage = RtssInstallStage.Preparing;
        IsUpdating = true;
        Status = "";

        // Created on the UI thread, so stage reports arrive back on it.
        var progress = new Progress<RtssInstallStage>(stage =>
        {
            if (IsUpdating && stage > InstallStage) InstallStage = stage;
        });

        RtssInstallResult? result = null;
        string? failure = null;
        try
        {
            // The installer cannot replace files that RTSS, or Tsume's own overlay, still has open.
            if (wasRunning && !await _service.StopRtssAsync())
            {
                failure = Strings.RtssUpdateStillRunning;
            }
            else
            {
                IsRunning = false;
                _service.ReleaseHooks();

                result = await RtssWingetInstaller.UpgradeAsync(progress);
                if (result.Value.Outcome is RtssInstallOutcome.Finished)
                {
                    InstallStage = RtssInstallStage.Finishing;

                    // The new version can take a moment to show up after the installer returns.
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        if (await Task.Run(RtssInstallation.Find) is { } found && found.Version != before) break;
                        await Task.Delay(500);
                    }
                }
            }
        }
        finally
        {
            IsUpdating = false;
        }

        RefreshState();

        var updated = IsInstalled && (InstalledVersion != before
            || result is { Outcome: RtssInstallOutcome.Finished, ExitCode: 0 });

        string message;
        if (updated)
        {
            AvailableVersion = "";
            UpdateCheckText = Strings.RtssUpdateUpToDate;
            message = Strings.RtssUpdateDone(InstalledVersion);
        }
        else
        {
            message = failure ?? result?.Outcome switch
            {
                RtssInstallOutcome.WingetMissing     => Strings.RtssInstallNoWinget,
                RtssInstallOutcome.CouldNotStart     => Strings.RtssInstallNoWinget,
                RtssInstallOutcome.Offline           => Strings.RtssInstallOffline,
                RtssInstallOutcome.AlreadyInstalling => Strings.RtssInstallAlreadyRunning,
                RtssInstallOutcome.TimedOut          => Strings.RtssUpdateTimedOut,
                _                                    => Strings.RtssUpdateFailed(result?.ExitCode ?? 0),
            };
        }

        if (IsInstalled) _ = RefreshStartWithWindowsAsync();
        Status = message;
        Settled?.Invoke();

        // Put RTSS back the way it was; a failed start reports itself instead.
        if (wasRunning && IsInstalled && !IsRunning)
        {
            await StartRtssAsync();
            if (IsRunning) Status = message;
        }
    }

    private async Task UninstallAsync()
    {
        if (IsBusy) return;

        // First press only arms it; removing RTSS takes a second, deliberate press.
        if (!ConfirmingUninstall)
        {
            ConfirmingUninstall = true;
            Status = "";
            return;
        }

        ConfirmingUninstall = false;

        var install = _service.RefreshInstallation();
        if (install is null)
        {
            RefreshState();
            Status = Strings.RtssUninstallDone;
            Settled?.Invoke();
            return;
        }

        if (RtssInstallation.FindUninstaller(install) is not { } uninstaller)
        {
            Status = Strings.RtssUninstallNotFound;
            return;
        }

        IsUninstalling = true;
        Status = "";

        string result;
        try
        {
            result = await RunUninstallAsync(uninstaller);
        }
        finally
        {
            IsUninstalling = false;
        }

        RefreshState();
        if (IsInstalled) _ = RefreshStartWithWindowsAsync();
        Status = result;
        Settled?.Invoke();
    }

    /// <summary>Stops RTSS, lets go of its files, runs its uninstaller and waits for it to be gone.</summary>
    private async Task<string> RunUninstallAsync(string uninstaller)
    {
        // A sign-in task pointing at a deleted RTSS.exe would fail quietly at every startup.
        if (StartsWithWindows) await _service.SetStartWithWindowsAsync(false);

        // The uninstaller cannot remove files that RTSS, or Tsume's own overlay, still has open.
        if (_service.IsRunning && !await _service.StopRtssAsync()) return Strings.RtssUninstallStillRunning;
        _service.ReleaseHooks();

        if (!await Task.Run(() => RtssInstallation.LaunchUninstaller(uninstaller)))
            return Strings.RtssUninstallCancelled;

        // The uninstaller hands over to a copy of itself, so watch the files instead of the process.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await Task.Delay(1000);
            if (await Task.Run(RtssInstallation.Find) is null) return Strings.RtssUninstallDone;
        }

        return Strings.RtssUninstallFailed;
    }

    private async Task StartRtssAsync()
    {
        if (_launching || IsBusy) return;
        var install = _service.Install ?? _service.RefreshInstallation();
        if (install is null) return;

        _launching = true;
        Notify(nameof(RtssAppDetail));
        Status = Strings.RtssStarting;

        try
        {
            // Launch off the UI thread so any Windows UAC prompt never freezes the panel.
            var launched = await Task.Run(() => RtssInstallation.Launch(install, minimized: true));
            if (!launched)
            {
                Status = Strings.RtssStartFailed;
                return;
            }

            // Wait for RTSS shared memory to come online so OSD and profile controls work immediately.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(250);
                if (_service.IsRunning && (RtssSharedMemory.IsAvailable() || attempt >= 8))
                {
                    RefreshState();
                    _service.ApplySettings(_service.Settings);
                    if (SkipAntiCheatGames) _ = SyncAntiCheatAsync();
                    Status = DefaultStatus();
                    Settled?.Invoke();
                    return;
                }
            }

            Status = Strings.RtssStartFailed;
        }
        finally
        {
            _launching = false;
            Notify(nameof(RtssAppDetail));
        }
    }

    private async Task OpenRtssMenuAsync()
    {
        var install = _service.Install ?? _service.RefreshInstallation();
        if (install is null) return;

        // Bring RTSS's hidden tray window forward while Tsume is still in the foreground, then dismiss Tsume.
        if (RtssInstallation.BringWindowToFront(install))
        {
            await _dismiss();
            return;
        }

        Status = Strings.RtssOpenMenuFailed;
    }

    private async Task CloseRtssAsync()
    {
        if (_closing || !IsRunning) return;
        _closing = true;
        Status = Strings.RtssClosing;

        try
        {
            var stopped = await _service.StopRtssAsync();
            RefreshState();
            Status = stopped ? Strings.RtssClosed : Strings.RtssCloseFailed;
            Settled?.Invoke();
        }
        finally
        {
            _closing = false;
        }
    }

    private async Task RefreshStartWithWindowsAsync()
    {
        if (!IsInstalled) return;
        var enabled = await _service.GetStartWithWindowsAsync();
        if (enabled is { } value && !_togglingStartup)
            StartsWithWindows = value;
    }

    private async Task ToggleStartWithWindowsAsync()
    {
        if (_togglingStartup || !IsInstalled) return;
        _togglingStartup = true;
        var target = !StartsWithWindows;

        try
        {
            var result = await _service.SetStartWithWindowsAsync(target);
            if (result is { } actual)
            {
                StartsWithWindows = actual;
                Status = DefaultStatus();
            }
            else
            {
                Status = Strings.RtssStartWithWindowsFailed;
            }
        }
        finally
        {
            _togglingStartup = false;
        }
    }

    private async Task ToggleSkipAntiCheatAsync()
    {
        var next = _service.Settings with { SkipAntiCheatGames = !SkipAntiCheatGames };
        _service.ApplySettings(next);
        SkipAntiCheatGames = next.SkipAntiCheatGames;
        await SyncAntiCheatAsync();
    }

    private async Task SyncAntiCheatAsync()
    {
        if (_syncingExclusions || !IsInstalled) return;
        _syncingExclusions = true;
        Notify(nameof(SkipAntiCheatDetail));

        try
        {
            _excludedGamesCount = await _service.SyncAntiCheatExclusionsAsync();
        }
        finally
        {
            _syncingExclusions = false;
            Notify(nameof(SkipAntiCheatDetail));
        }
    }

    private string DefaultStatus() =>
        !IsInstalled ? ""
        : !IsRunning ? Strings.RtssNotRunningNote
        : Strings.RtssRunningSavedNote(InstalledVersion);

    private void NotifyTile()
    {
        Notify(nameof(IsActive));
        Notify(nameof(TileDetail));
    }

    /// <summary>Development aid for <see cref="Shell.ScreenShots"/>: displays an installed state without touching disk or RTSS.</summary>
    internal void PreviewForScreenShot(bool useTsumeOverlay, RtssOverlayLevel level, int frameRateLimit, bool isRunning = true)
    {
        _absorbing = true;
        IsInstalled = true;
        InstalledVersion = "7.3.7";
        IsRunning = isRunning;
        StartsWithWindows = isRunning;
        UseTsumeOverlay = useTsumeOverlay;
        SkipAntiCheatGames = true;
        _excludedGamesCount = 2;
        Notify(nameof(SkipAntiCheatDetail));
        LevelIndex = (int)level;
        CornerIndex = (int)RtssCorner.TopLeft;
        Scale = 2;
        IsOsdVisible = true;
        ActiveFrameRateLimit = isRunning ? frameRateLimit : 0;

        var presetIndex = Array.IndexOf(CapPresets, frameRateLimit);
        if (presetIndex >= 0)
        {
            FrameCapIndex = presetIndex;
        }
        else
        {
            CustomCap = Math.Clamp(frameRateLimit, RtssSettings.MinCustomFps, RtssSettings.MaxCustomFps);
            FrameCapIndex = CapPresets.Length;
        }

        Status = DefaultStatus();
        _absorbing = false;
        NotifyTile();
    }
}
