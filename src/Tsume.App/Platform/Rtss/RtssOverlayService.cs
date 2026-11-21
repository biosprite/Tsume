using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;
using Tsume.Upscaling.Games;

namespace Tsume.App.Platform.Rtss;

/// <summary>
/// Coordinates Tsume's Steam-style RTSS overlay loop, helper-backed Global profile updates
/// (position, scale, fill, frame-rate cap), and instant in-game reload signalling.
/// Runs zero background work when Tsume's Steam-style overlay is turned off or its level is Off.
/// </summary>
public sealed class RtssOverlayService : IDisposable
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WaitingInterval = TimeSpan.FromSeconds(3);
    private static readonly Journal Log = new("rtss");

    private readonly HelperClient _helper;
    private readonly SavedState _state;
    private readonly Func<(string ModeLabel, int? Sustained, int? Boost)> _readPowerInfo;
    private readonly RtssHooks _hooks = new();
    private readonly SystemTelemetry _telemetry = new();
    private readonly Lock _gate = new();

    private RtssSettings _settings;
    private RtssInstallInfo? _install;
    private CancellationTokenSource? _loopCts;
    private bool _appearanceApplied;
    private bool _disposed;

    public RtssOverlayService(
        HelperClient helper,
        SavedState state,
        Func<(string ModeLabel, int? Sustained, int? Boost)> readPowerInfo)
    {
        _helper = helper;
        _state = state;
        _readPowerInfo = readPowerInfo;
        _settings = state.Rtss ?? new RtssSettings();
    }

    public RtssSettings Settings => _settings;

    public RtssInstallInfo? Install => _install;

    /// <summary>Starts the background overlay updater if the tile is enabled and Tsume's overlay is turned on.</summary>
    public void StartIfEnabled()
    {
        var install = RefreshInstallation();
        if (_state.ShowRtss && install is not null && _settings.SkipAntiCheatGames)
            _ = Task.Run(() => SyncAntiCheatExclusionsAsync());

        if (_state.ShowRtss && ShouldRenderOverlay) StartLoop();
    }

    /// <summary>Pauses or resumes Tsume's background overlay when the RTSS tile itself is hidden or shown in Settings.</summary>
    public void SetFeatureEnabled(bool enabled)
    {
        if (!enabled)
        {
            StopLoop();
            RtssSharedMemory.ClearSlot();
            _telemetry.Reset();
            return;
        }

        if (ShouldRenderOverlay) StartLoop();
    }

    /// <summary>Re-scans for RTSS and loads <c>RTSSHooks64.dll</c> directly from its install folder if found.</summary>
    public RtssInstallInfo? RefreshInstallation()
    {
        var found = RtssInstallation.Find();
        _install = found;

        if (found?.HooksDll is { } hooks)
            _hooks.EnsureLoaded(hooks);

        return found;
    }

    public bool IsRunning => RtssInstallation.IsRunning();

    public bool? IsOsdVisible()
    {
        EnsureHooks();
        return _hooks.IsOsdVisible();
    }

    public bool SetOsdVisible(bool visible)
    {
        EnsureHooks();
        return _hooks.SetOsdVisible(visible);
    }

    public int? GetFrameRateLimit()
    {
        EnsureHooks();
        return _hooks.GetFrameRateLimit(_install?.Folder);
    }

    /// <summary>
    /// Updates RTSS's Global frame-rate limit via the elevated helper (since <c>Profiles\Global</c>
    /// sits under Program Files) and immediately notifies running games to reload it.
    /// </summary>
    public async Task<bool> SetFrameRateLimitAsync(int fps)
    {
        EnsureHooks();
        fps = Math.Clamp(fps, 0, RtssSettings.MaxCustomFps);

        var ok = await WriteGlobalProfileAsync(new SetRtssProfile(FramerateLimit: fps)).ConfigureAwait(false);
        if (!ok) return false;

        if (fps > 0) _hooks.EnsureLimiterEnabled();
        _hooks.NotifyProfilesUpdated();
        return true;
    }

    /// <summary>Stops RTSS completely via the elevated helper (falling back to direct kill if unelevated).</summary>
    public async Task<bool> StopRtssAsync()
    {
        StopLoop();
        RtssSharedMemory.ClearSlot();
        _telemetry.Reset();
        _appearanceApplied = false;

        var reply = await _helper.SendAsync(new StopRtss()).ConfigureAwait(false);
        if (!reply.Ok)
        {
            Log.Write($"helper rtss.stop failed ({reply.Error ?? "offline"}); trying direct kill");
            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                using (process)
                {
                    try { process.Kill(); process.WaitForExit(2000); }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                }
            }
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!IsRunning) return true;
            await Task.Delay(150).ConfigureAwait(false);
        }

        return !IsRunning;
    }

    /// <summary>
    /// Scans installed games for anti-cheat systems (reusing Tsume's game inspector) and writes RTSS
    /// <c>EnableHooking=0</c> exclusion profiles via the helper so RTSS never injects into them.
    /// When <see cref="RtssSettings.SkipAntiCheatGames"/> is false, removes only Tsume-created exclusions.
    /// </summary>
    /// <returns>Number of anti-cheat games excluded, or null if the helper could not update profiles.</returns>
    public async Task<int?> SyncAntiCheatExclusionsAsync()
    {
        EnsureHooks();
        if (_install is null) return null;

        if (!_settings.SkipAntiCheatGames)
        {
            var clear = await _helper.SendAsync(new SetRtssExclusions([])).ConfigureAwait(false);
            if (!clear.Ok) return null;
            _hooks.NotifyProfilesUpdated();
            return 0;
        }

        var (games, exes) = await Task.Run(FindAntiCheatExecutables).ConfigureAwait(false);
        var reply = await _helper.SendAsync(new SetRtssExclusions(exes)).ConfigureAwait(false);
        if (!reply.Ok)
        {
            Log.Write($"helper rtss.exclusions.set failed ({reply.Error ?? "offline"})");
            return null;
        }

        _hooks.NotifyProfilesUpdated();
        Log.Write($"excluded {games} anti-cheat game(s) ({exes.Count} exe profiles) from RTSS hooking");
        return games;
    }

    private static (int GameCount, List<string> ExeNames) FindAntiCheatExecutables()
    {
        var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gameCount = 0;

        try
        {
            foreach (var game in GameLibrary.Find())
            {
                var facts = GameInspector.Inspect(game);
                if (facts?.AntiCheat is null) continue;

                gameCount++;
                var mainExe = Path.GetFileName(facts.Executable);
                if (!string.IsNullOrWhiteSpace(mainExe)) exes.Add(mainExe);

                // Also exclude any shipping / top-level game executables in the same target folder.
                try
                {
                    foreach (var file in Directory.EnumerateFiles(facts.Target, "*.exe"))
                    {
                        var name = Path.GetFileName(file);
                        if (!string.IsNullOrWhiteSpace(name)) exes.Add(name);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        return (gameCount, [.. exes.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Saves and applies new RTSS overlay settings.
    /// Turning <see cref="RtssSettings.UseTsumeOverlay"/> off immediately clears Tsume's OSD slot
    /// and restores the user's original RTSS Global appearance so their custom overlay is untouched.
    /// </summary>
    public void ApplySettings(RtssSettings next)
    {
        var previous = _settings;
        _settings = next;

        EnsureHooks();

        if (!next.UseTsumeOverlay)
        {
            StopLoop();
            RtssSharedMemory.ClearSlot();
            _telemetry.Reset();

            var backup = next.SavedAppearance;
            if (backup is not null)
            {
                _settings = next with { SavedAppearance = null };
                _appearanceApplied = false;
                SaveSettings();
                _ = Task.Run(() => RestoreAppearanceAsync(backup));
                return;
            }

            SaveSettings();
            return;
        }

        if (next.Level == RtssOverlayLevel.Off)
        {
            StopLoop();
            RtssSharedMemory.ClearSlot();
            _telemetry.Reset();
            SaveSettings();
            return;
        }

        var appearanceChanged = !_appearanceApplied
            || previous.Corner != next.Corner
            || previous.Scale != next.Scale
            || !previous.UseTsumeOverlay;

        if (appearanceChanged) _appearanceApplied = false;

        SaveSettings();
        StartLoop();

        // Apply profile + push a fresh OSD frame immediately without waiting for the next 1 s tick.
        _ = Task.Run(async () =>
        {
            if (appearanceChanged && IsRunning)
            {
                await ApplySteamAppearanceAsync().ConfigureAwait(false);
                _hooks.SetOsdVisible(true);
            }

            await PushOneFrameAsync(CancellationToken.None).ConfigureAwait(false);
        });
    }

    private bool ShouldRenderOverlay => _settings.UseTsumeOverlay && _settings.Level != RtssOverlayLevel.Off;

    private void EnsureHooks()
    {
        if (_install is null) RefreshInstallation();
        else if (_install.HooksDll is { } hooks) _hooks.EnsureLoaded(hooks);
    }

    private async Task ApplySteamAppearanceAsync()
    {
        EnsureHooks();
        var current = _settings;
        if (!current.UseTsumeOverlay || current.Level == RtssOverlayLevel.Off) return;

        if (current.SavedAppearance is null && _hooks.ReadAppearance(_install?.Folder) is { } backup)
        {
            _settings = _settings with { SavedAppearance = backup };
            SaveSettings();
        }

        var (x, y) = RtssHooks.CoordinatesFor(current.Corner);
        var scale = Math.Clamp(current.Scale, RtssSettings.MinScale, RtssSettings.MaxScale);

        var request = new SetRtssProfile(
            PositionX: x,
            PositionY: y,
            ZoomRatio: scale,
            EnableFill: 1,
            FillColor: RtssHooks.SteamFillColor);

        if (await WriteGlobalProfileAsync(request).ConfigureAwait(false))
        {
            _appearanceApplied = true;
            _hooks.NotifyProfilesUpdated();
        }
    }

    private async Task RestoreAppearanceAsync(RtssAppearanceBackup backup)
    {
        EnsureHooks();
        var request = new SetRtssProfile(
            PositionX: backup.PositionX,
            PositionY: backup.PositionY,
            ZoomRatio: backup.ZoomRatio,
            EnableFill: backup.EnableFill,
            FillColor: backup.FillColor);

        if (await WriteGlobalProfileAsync(request).ConfigureAwait(false))
        {
            _hooks.NotifyProfilesUpdated();
            Log.Write("restored user's original RTSS Global profile appearance");
        }
    }

    private async Task<bool> WriteGlobalProfileAsync(SetRtssProfile request)
    {
        var reply = await _helper.SendAsync(request).ConfigureAwait(false);
        if (reply.Ok) return true;

        Log.Write($"helper rtss.profile.set failed ({reply.Error ?? "offline"}); trying direct write");
        return _hooks.TryWriteProfileDirect(
            request.FramerateLimit,
            request.PositionX,
            request.PositionY,
            request.ZoomRatio,
            request.EnableFill,
            request.FillColor);
    }

    private void SaveSettings()
    {
        lock (_gate)
        {
            _state.Rtss = _settings;
            _state.Save();
        }
    }

    private void StartLoop()
    {
        lock (_gate)
        {
            if (_disposed || _loopCts is not null) return;
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _ = Task.Run(() => RunLoopAsync(token), token);
        }
    }

    private void StopLoop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _loopCts;
            _loopCts = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && ShouldRenderOverlay)
        {
            try
            {
                if (!IsRunning || !RtssSharedMemory.IsAvailable())
                {
                    _appearanceApplied = false;
                    await Task.Delay(WaitingInterval, cancel).ConfigureAwait(false);
                    continue;
                }

                if (!_appearanceApplied)
                {
                    await ApplySteamAppearanceAsync().ConfigureAwait(false);
                    _hooks.SetOsdVisible(true);
                }

                await PushOneFrameAsync(cancel).ConfigureAwait(false);
                await Task.Delay(ActiveInterval, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                Log.Write($"overlay loop tick failed: {error.Message}");
                try { await Task.Delay(WaitingInterval, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task PushOneFrameAsync(CancellationToken cancel)
    {
        var settings = _settings;
        if (!settings.UseTsumeOverlay || settings.Level == RtssOverlayLevel.Off) return;
        if (!RtssSharedMemory.IsAvailable()) return;

        TelemetryFrame frame;
        if (settings.Level == RtssOverlayLevel.Fps)
        {
            frame = new TelemetryFrame();
        }
        else
        {
            var app = RtssSharedMemory.ReadActiveApp();
            var includeGpu = settings.Level >= RtssOverlayLevel.Bar;
            var (cpu, gpu, vram, ramUsed, ramTotal, bat) = _telemetry.Sample(includeGpu);

            int? cpuTemp = null;
            int? fanRpm = null;
            try
            {
                var readings = await _helper.SendAsync(new GetReadings(), cancel).ConfigureAwait(false);
                cpuTemp = readings.Readings?.CpuTemperature;
                fanRpm = readings.Readings?.FanRpm;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Helper offline: omit temp/fan cleanly.
            }

            var (modeLabel, sustained, boost) = _readPowerInfo();

            frame = new TelemetryFrame
            {
                CpuPercent = cpu,
                CpuTempC = cpuTemp,
                FanRpm = fanRpm,
                GpuPercent = gpu,
                VramUsedGb = vram,
                RamUsedGb = ramUsed,
                RamTotalGb = ramTotal,
                Battery = bat,
                PowerModeLabel = modeLabel,
                SustainedWatts = sustained,
                BoostWatts = boost,
                LiveFps = app.Fps,
                LiveFrameTimeMs = app.FrameTimeMs,
            };
        }

        var embedGraph = settings.Level == RtssOverlayLevel.Full;
        var text = SteamOverlayFormatter.Format(settings.Level, frame, hasGraph: embedGraph);
        RtssSharedMemory.UpdateSlot(text, embedNativeFrametimeGraph: embedGraph);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopLoop();
        RtssSharedMemory.ClearSlot();
        _telemetry.Dispose();
        _hooks.Dispose();
    }
}
