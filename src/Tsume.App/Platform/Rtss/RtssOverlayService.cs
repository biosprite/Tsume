using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;
using Tsume.Upscaling.Games;

namespace Tsume.App.Platform.Rtss;

/// <summary>
/// Drives Tsume's RTSS overlay through RTSS's own OverlayEditor plugin using one combined Essential layout
/// (realHfde, MIT) per theme. OverlayEditor stays enabled while <see cref="RtssSettings.UseTsumeOverlay"/> is on;
/// switching levels (including Off) updates <c>AIDA64_SensorValues</c> live with no helper call and no RTSS restart.
/// Only toggling Tsume's overlay on/off or changing theme updates <c>OverlayEditor.cfg</c> and restarts RTSS (without UAC).
/// When Tsume exits, the shared-memory handle closes and the overlay disappears automatically.
/// </summary>
public sealed class RtssOverlayService : IDisposable
{
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConflictRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ThemeDebounceInterval = TimeSpan.FromMilliseconds(450);
    private static readonly Journal Log = new("rtss");

    private readonly HelperClient _helper;
    private readonly SavedState _state;
    private readonly RtssHooks _hooks = new();
    private readonly AidaSensorBridge _aida = new();
    private readonly EnergyMeterTelemetry _energy = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _sync = new(1, 1);

    private RtssSettings _settings;
    private RtssInstallInfo? _install;
    private CancellationTokenSource? _telemetryCts;
    private CancellationTokenSource? _themeDebounceCts;
    private AidaConflictKind _lastReportedConflict;
    private int? _lastCpuTemp;
    private double? _lastCpuWatts;
    private double? _lastGpuWatts;
    private double? _lastCpuClockMhz;
    private bool _disposed;

    public RtssOverlayService(HelperClient helper, SavedState state)
    {
        _helper = helper;
        _state = state;
        _settings = state.Rtss ?? new RtssSettings();
    }

    /// <summary>Raised (off the UI thread) after Tsume restarted RTSS so it loads a new overlay theme.</summary>
    public event Action? Restarted;

    /// <summary>Raised (off the UI thread) when <see cref="SharedMemoryConflict"/> changes.</summary>
    public event Action? ConflictChanged;

    public RtssSettings Settings => _settings;

    public RtssInstallInfo? Install => _install;

    /// <summary>Non-None when AIDA64, Winhanced, or another app already owns <c>AIDA64_SensorValues</c>.</summary>
    public AidaConflictKind SharedMemoryConflict => _aida.Conflict;

    private bool IsOverlayPluginWanted => _state.ShowRtss && _settings.UseTsumeOverlay;

    private bool IsOverlayVisibleWanted => IsOverlayPluginWanted && _settings.Level != RtssOverlayLevel.Off;

    /// <summary>Checks the overlay setup once at start-up (and clears any slot left by older Tsume versions).</summary>
    public void StartIfEnabled()
    {
        var install = RefreshInstallation();
        RtssSharedMemory.ClearSlot();

        if (_state.ShowRtss && install is not null && _settings.SkipAntiCheatGames)
            _ = Task.Run(() => SyncAntiCheatExclusionsAsync());

        ApplyLiveLevelState();

        if (install is not null) _ = Task.Run(SyncOverlayPluginAsync);
    }

    /// <summary>Hands the overlay back to the user's own setup, or takes it again, when the RTSS tile is hidden or shown in Settings.</summary>
    public void SetFeatureEnabled(bool enabled)
    {
        CancelThemeDebounce();
        ApplyLiveLevelState();
        _ = Task.Run(SyncOverlayPluginAsync);
    }

    /// <summary>Re-checks <c>AIDA64_SensorValues</c> if a previous conflict prevented Tsume from claiming it.</summary>
    public void RetrySharedMemoryIfConflicted()
    {
        if (IsOverlayVisibleWanted && !_aida.IsOwned)
            ApplyLiveLevelState();
    }

    /// <summary>
    /// Ensures OverlayEditor is configured for the current theme before starting RTSS (so RTSS loads the right
    /// layout on its very first boot without needing a second restart), and refreshes the live AIDA64 state.
    /// </summary>
    public async Task EnsureSyncedAsync()
    {
        ApplyLiveLevelState(ensureOsdVisible: IsOverlayVisibleWanted);
        await SyncOverlayPluginAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Lets go of <c>RTSSHooks64.dll</c> so RTSS's uninstaller can delete it. The next
    /// <see cref="RefreshInstallation"/> loads it again if RTSS is still there.
    /// </summary>
    public void ReleaseHooks() => _hooks.Dispose();

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
    /// Starts RTSS, which demands elevation. The helper starts it in this session with the signed-in administrator's
    /// full token, so there is no UAC prompt; only when the helper cannot be reached does Windows ask instead.
    /// </summary>
    public async Task<bool> LaunchRtssAsync(RtssInstallInfo install)
    {
        var reply = await _helper.SendAsync(new StartRtss()).ConfigureAwait(false);
        if (reply.Ok) return true;
        if (!reply.Unreachable)
        {
            Log.Write($"helper rtss.start failed ({reply.Error})");
            return false;
        }

        Log.Write("helper offline; starting RTSS through Windows (UAC prompt)");
        return RtssInstallation.Launch(install, minimized: true);
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string RtssRunValueName = "RTSS";

    /// <summary>Checks whether RTSS is configured to start automatically at sign-in.</summary>
    public async Task<bool?> GetStartWithWindowsAsync()
    {
        var reply = await _helper.SendAsync(new GetRtssStartup()).ConfigureAwait(false);
        if (reply.Ok && reply.RtssStartsWithWindows is { } enabled)
            return enabled || HasCurrentUserRunEntry();

        Log.Write($"helper rtss.startup.get failed ({reply.Error ?? "offline"})");
        return HasCurrentUserRunEntry();
    }

    /// <summary>Enables or disables RTSS starting automatically at sign-in via the elevated helper.</summary>
    public async Task<bool?> SetStartWithWindowsAsync(bool enabled)
    {
        var reply = await _helper.SendAsync(new SetRtssStartup(enabled)).ConfigureAwait(false);
        if (!reply.Ok)
        {
            Log.Write($"helper rtss.startup.set({enabled}) failed ({reply.Error ?? "offline"})");
            return null;
        }

        // Clean up any legacy per-user Run-key entry in our own HKCU hive as well.
        RemoveCurrentUserRunEntry();
        return reply.RtssStartsWithWindows ?? enabled;
    }

    private static bool HasCurrentUserRunEntry()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (run?.GetValue(RtssRunValueName) is not string { Length: > 0 }) return false;

            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath);
            return approved?.GetValue(RtssRunValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RemoveCurrentUserRunEntry()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            run?.DeleteValue(RtssRunValueName, throwOnMissingValue: false);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
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
    /// Saves new overlay settings and applies them:
    /// - Level changes (including Off) only update <c>AIDA64_SensorValues</c> live, with no helper call and no RTSS restart.
    /// - Toggling <see cref="RtssSettings.UseTsumeOverlay"/> updates OverlayEditor immediately and restarts RTSS if changed.
    /// - Theme changes are debounced so stepping across themes only restarts RTSS once on the final choice.
    /// </summary>
    public void ApplySettings(RtssSettings next)
    {
        var previous = _settings;
        _settings = next;
        SaveSettings();

        var pluginChanged = previous.UseTsumeOverlay != next.UseTsumeOverlay;
        var themeChanged = previous.Theme != next.Theme;
        var levelChanged = previous.Level != next.Level;

        if (pluginChanged || levelChanged)
            ApplyLiveLevelState(ensureOsdVisible: levelChanged && next.Level != RtssOverlayLevel.Off);

        if (pluginChanged)
        {
            CancelThemeDebounce();
            _ = Task.Run(SyncOverlayPluginAsync);
        }
        else if (themeChanged && IsOverlayPluginWanted)
        {
            ScheduleDebouncedPluginSync();
        }
    }

    private void ApplyLiveLevelState(bool ensureOsdVisible = false)
    {
        if (_disposed) return;

        if (!IsOverlayVisibleWanted)
        {
            StopTelemetryLoop();
            _energy.Reset();
            _aida.Release();
            CheckConflictChanged();
            return;
        }

        var level = _settings.Level;
        int? cpuTemp;
        double? cpuWatts;
        double? gpuWatts;
        double? cpuClockMhz;
        lock (_gate)
        {
            cpuTemp = _lastCpuTemp;
            cpuWatts = _lastCpuWatts;
            gpuWatts = _lastGpuWatts;
            cpuClockMhz = _lastCpuClockMhz;
        }

        _aida.TryPublish(new AidaSensorSnapshot((int)level, cpuTemp, cpuWatts, gpuWatts, cpuClockMhz), _install?.Folder);
        CheckConflictChanged();

        if (ensureOsdVisible && IsRunning)
        {
            EnsureHooks();
            _hooks.SetOsdVisible(true);
        }

        if (level >= RtssOverlayLevel.Bar)
        {
            StartTelemetryLoop();
        }
        else
        {
            StopTelemetryLoop();
            _energy.Reset();
        }
    }

    private void StartTelemetryLoop()
    {
        lock (_gate)
        {
            if (_disposed || _telemetryCts is not null) return;
            _telemetryCts = new CancellationTokenSource();
            var token = _telemetryCts.Token;
            _ = Task.Run(() => RunTelemetryLoopAsync(token), token);
        }
    }

    private void StopTelemetryLoop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _telemetryCts;
            _telemetryCts = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private async Task RunTelemetryLoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested && IsOverlayVisibleWanted && _settings.Level >= RtssOverlayLevel.Bar)
        {
            try
            {
                var (cpuWatts, gpuWatts, cpuClockMhz) = _energy.Sample();

                int? cpuTemp = null;
                try
                {
                    var reply = await _helper.SendAsync(new GetReadings(), cancel).ConfigureAwait(false);
                    if (reply.Ok) cpuTemp = reply.Readings?.CpuTemperature;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Helper offline: omit temperature cleanly.
                }

                if (cancel.IsCancellationRequested || !IsOverlayVisibleWanted) break;

                lock (_gate)
                {
                    _lastCpuTemp = cpuTemp;
                    _lastCpuWatts = cpuWatts;
                    _lastGpuWatts = gpuWatts;
                    _lastCpuClockMhz = cpuClockMhz;
                }

                var level = _settings.Level;
                if (level == RtssOverlayLevel.Off) break;

                var ok = _aida.TryPublish(
                    new AidaSensorSnapshot((int)level, cpuTemp, cpuWatts, gpuWatts, cpuClockMhz),
                    _install?.Folder);
                CheckConflictChanged();

                await Task.Delay(ok ? TelemetryInterval : ConflictRetryInterval, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                Log.Write($"overlay telemetry tick failed: {error.Message}");
                try { await Task.Delay(ConflictRetryInterval, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void ScheduleDebouncedPluginSync()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return;
            _themeDebounceCts?.Cancel();
            _themeDebounceCts?.Dispose();
            _themeDebounceCts = new CancellationTokenSource();
            token = _themeDebounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ThemeDebounceInterval, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    await SyncOverlayPluginAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void CancelThemeDebounce()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _themeDebounceCts;
            _themeDebounceCts = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    private void CheckConflictChanged()
    {
        var current = _aida.Conflict;
        var changed = false;
        lock (_gate)
        {
            if (_lastReportedConflict != current)
            {
                _lastReportedConflict = current;
                changed = true;
            }
        }

        if (changed) ConflictChanged?.Invoke();
    }

    /// <summary>
    /// Points OverlayEditor at the combined layout for the current theme through the helper (or restores the
    /// user's original setup when Tsume's overlay is turned off) and restarts RTSS only when that changed anything.
    /// Runs one at a time, always with the latest settings.
    /// </summary>
    private async Task SyncOverlayPluginAsync()
    {
        if (_disposed) return;
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_install is null) return;

            await RestoreLegacyAppearanceAsync().ConfigureAwait(false);

            var layout = IsOverlayPluginWanted ? "tsume" : null;
            var theme = layout is null ? null : _settings.Theme.ToString().ToLowerInvariant();
            var reply = await _helper.SendAsync(new SetRtssOverlay(layout, theme)).ConfigureAwait(false);
            if (!reply.Ok)
            {
                Log.Write($"helper rtss.overlay.set failed ({reply.Error ?? "offline"})");
                return;
            }

            if (reply.RtssOverlayChanged == true && IsRunning)
                await RestartRtssAsync().ConfigureAwait(false);

            if (IsOverlayVisibleWanted && IsRunning)
            {
                EnsureHooks();
                _hooks.SetOsdVisible(true);
            }
        }
        catch (Exception error)
        {
            Log.Write($"overlay sync failed: {error.Message}");
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task RestartRtssAsync()
    {
        if (_install is not { } install) return;

        Log.Write("restarting RTSS to load the new overlay theme");
        if (!await StopRtssAsync().ConfigureAwait(false)) return;
        if (!await LaunchRtssAsync(install).ConfigureAwait(false)) return;

        for (var attempt = 0; attempt < 30 && !RtssSharedMemory.IsAvailable(); attempt++)
            await Task.Delay(250).ConfigureAwait(false);

        Restarted?.Invoke();
    }

    /// <summary>
    /// Older Tsume versions restyled RTSS's Global OSD (position, zoom, fill) for their Steam-style overlay and kept a
    /// backup of the user's values. The Essential layouts position themselves, so put those values back once.
    /// </summary>
    private async Task RestoreLegacyAppearanceAsync()
    {
        if (_settings.SavedAppearance is not { } backup) return;

        var request = new SetRtssProfile(
            PositionX: backup.PositionX,
            PositionY: backup.PositionY,
            ZoomRatio: backup.ZoomRatio,
            EnableFill: backup.EnableFill,
            FillColor: backup.FillColor);

        if (!await WriteGlobalProfileAsync(request).ConfigureAwait(false)) return;

        _hooks.NotifyProfilesUpdated();
        _settings = _settings with { SavedAppearance = null };
        SaveSettings();
        Log.Write("restored the user's original RTSS Global profile appearance");
    }

    private void EnsureHooks()
    {
        if (_install is null) RefreshInstallation();
        else if (_install.HooksDll is { } hooks) _hooks.EnsureLoaded(hooks);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelThemeDebounce();
        StopTelemetryLoop();
        _energy.Dispose();
        _aida.Dispose();
        // _sync is left to the GC: a sync still in flight releases it after this returns.
        _hooks.Dispose();
    }
}
