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
    private int? _excludedGamesCount;
    private string? _downloadedInstaller;

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

        RunDownloadedInstaller = new Command(async () =>
        {
            if (_downloadedInstaller is not { } package) return;
            await _dismiss();
            if (!await RtssInstallation.RunDownloadedInstallerAsync(package))
                Status = Strings.RtssInstallerFailed;
        });

        CheckAgain = new Command(() =>
        {
            RefreshState();
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
        ToggleSkipAntiCheat = new Command(async () => await ToggleSkipAntiCheatAsync());

        SyncFromService();
    }

    /// <summary>Raised when sections appear or disappear, so the screen can keep focus valid.</summary>
    public event Action? Settled;

    public ICommand OpenDownloadPage { get; }
    public ICommand RunDownloadedInstaller { get; }
    public ICommand CheckAgain { get; }
    public ICommand ToggleUseTsumeOverlay { get; }
    public ICommand ToggleOsdVisible { get; }
    public ICommand StartRtss { get; }
    public ICommand OpenRtssMenu { get; }
    public ICommand CloseRtss { get; }
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
            Notify(nameof(InstalledVisibility));
            Notify(nameof(NotInstalledVisibility));
            Notify(nameof(IsNotRunning));
            Notify(nameof(RtssAppDetail));
            NotifyTile();
        }
    }

    public Visibility InstalledVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NotInstalledVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;

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

    public bool IsNotRunning => IsInstalled && !IsRunning;

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
        !IsInstalled && HasDownloadedInstaller ? Visibility.Visible : Visibility.Collapsed;

    public string DownloadedInstallerDetail =>
        _downloadedInstaller is { } path ? Strings.RtssRunInstallerDetail(Path.GetFileName(path)) : "";

    public string InstalledVersion { get; private set => Set(ref field, value); } = "";

    public string RtssAppDetail => IsRunning
        ? Strings.RtssStatusRunning(InstalledVersion)
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
        _ = Task.Run(() =>
        {
            var install = _service.RefreshInstallation();
            var running = install is not null && _service.IsRunning;
            var osd = running ? _service.IsOsdVisible() : null;
            var limit = running ? _service.GetFrameRateLimit() : null;

            Application.Current?.Dispatcher.BeginInvoke(() => ApplySnapshot(install, running, osd, limit, installer: null, scanInstaller: false));
        });
    }

    protected override void OnOpened()
    {
        RefreshState();
        Status = DefaultStatus();
        if (IsInstalled && SkipAntiCheatGames && _excludedGamesCount is null)
            _ = SyncAntiCheatAsync();
    }

    private void RefreshState()
    {
        var install = _service.RefreshInstallation();
        var running = install is not null && _service.IsRunning;
        var osd = running ? _service.IsOsdVisible() : null;
        var limit = running ? _service.GetFrameRateLimit() : null;
        var installer = install is null ? RtssInstallation.FindDownloadedInstaller() : null;

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

    private async Task StartRtssAsync()
    {
        if (_launching) return;
        var install = _service.Install ?? _service.RefreshInstallation();
        if (install is null) return;

        _launching = true;
        Status = Strings.RtssStarting;

        try
        {
            if (!RtssInstallation.Launch(install, minimized: true))
            {
                Status = Strings.RtssStartFailed;
                return;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250);
                if (_service.IsRunning)
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
        : Strings.RtssSavedNote;

    private void NotifyTile()
    {
        Notify(nameof(IsActive));
        Notify(nameof(TileDetail));
    }

    /// <summary>Development aid for <see cref="Shell.ScreenShots"/>: displays an installed state without touching disk or RTSS.</summary>
    internal void PreviewForScreenShot(bool useTsumeOverlay, RtssOverlayLevel level, int frameRateLimit)
    {
        _absorbing = true;
        IsInstalled = true;
        InstalledVersion = "7.3.7";
        IsRunning = true;
        UseTsumeOverlay = useTsumeOverlay;
        SkipAntiCheatGames = true;
        _excludedGamesCount = 2;
        Notify(nameof(SkipAntiCheatDetail));
        LevelIndex = (int)level;
        CornerIndex = (int)RtssCorner.TopLeft;
        Scale = 2;
        IsOsdVisible = true;
        ActiveFrameRateLimit = frameRateLimit;

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

        Status = Strings.RtssSavedNote;
        _absorbing = false;
        NotifyTile();
    }
}
