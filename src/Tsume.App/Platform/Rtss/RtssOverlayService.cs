using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Tsume.App.Platform.Interop;
using Tsume.Core.Devices;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;
using Tsume.Upscaling.Games;

namespace Tsume.App.Platform.Rtss;

/// <summary>
/// Drives Tsume's RTSS overlay through RTSS's own OverlayEditor plugin using one combined Essential Dark layout
/// (realHfde, MIT) covering all four levels. OverlayEditor stays enabled while
/// <see cref="RtssSettings.UseTsumeOverlay"/> is on; switching levels (including Off) and repositioning both
/// update <c>AIDA64_SensorValues</c> live with no helper call and no RTSS restart.
/// Only toggling Tsume's overlay on/off (or an install upgrade) updates <c>OverlayEditor.cfg</c>, and any
/// RTSS restart is deferred while a 3D game is hooked so running games are never crashed mid-session.
/// When Tsume exits, the shared-memory handle closes and the overlay disappears automatically.
/// </summary>
public sealed class RtssOverlayService : IDisposable
{
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleTelemetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConflictRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly Journal Log = new("rtss");

    private readonly HelperClient _helper;
    private readonly SavedState _state;
    private readonly RtssHooks _hooks = new();
    private readonly AidaSensorBridge _aida = new();
    private readonly EnergyMeterTelemetry _energy = new();
    private readonly Lock _gate = new();
    private readonly Lock _publishGate = new();
    private readonly SemaphoreSlim _sync = new(1, 1);

    private RtssSettings _settings;
    private RtssInstallInfo? _install;
    private CancellationTokenSource? _telemetryCts;
    private bool _rtssRestartPending;
    private bool _deferredRestartPolling;
    private AidaConflictKind _lastReportedConflict;
    private int? _lastCpuTemp;
    private double? _lastCpuWatts;
    private double? _lastGpuWatts;
    private double? _lastCpuClockMhz;
    private double? _lastGpuVramMb;
    private (int X, int Y)? _dragOverridePos;
    private (int Width, int Height)? _frame;
    private uint _framePid;
    private nint _frameHwnd;
    private uint _frameHwndPid;
    private (int Width, int Height) _publishedFrame;
    private bool _disposed;

    public RtssOverlayService(HelperClient helper, SavedState state)
    {
        _helper = helper;
        _state = state;
        _settings = state.Rtss ?? new RtssSettings();

        // Settings saved before OverlayClaimed existed: a level already in use counts as having been picked.
        if (_settings.Level != RtssOverlayLevel.Off && !_settings.OverlayClaimed)
            _settings = _settings with { OverlayClaimed = true };
    }

    /// <summary>Raised (off the UI thread) after Tsume restarted RTSS so it loads a new overlay theme.</summary>
    public event Action? Restarted;

    /// <summary>Raised (off the UI thread) when <see cref="SharedMemoryConflict"/> changes.</summary>
    public event Action? ConflictChanged;

    /// <summary>Raised whenever overlay visibility, level, placement, or touch-drag preference changes.</summary>
    public event Action? OverlayStateChanged;

    public RtssSettings Settings => _settings;

    public RtssInstallInfo? Install => _install;

    /// <summary>Non-None when AIDA64, Winhanced, or another app already owns <c>AIDA64_SensorValues</c>.</summary>
    public AidaConflictKind SharedMemoryConflict => _aida.Conflict;

    /// <summary>True when the FPS Overlay feature is enabled in Settings.</summary>
    public bool IsFeatureEnabled => _state.ShowRtss;

    private bool IsOverlayPluginWanted => _state.ShowRtss && _settings.UseTsumeOverlay && _settings.OverlayClaimed;

    private bool IsOverlayVisibleWanted => IsOverlayPluginWanted && _settings.Level != RtssOverlayLevel.Off;

    /// <summary>True when Tsume's own overlay is enabled and a non-Off level is selected.</summary>
    public bool IsTsumeOverlayActive => IsOverlayVisibleWanted;

    /// <summary>Primary display dimensions in physical pixels (matching RTSS's 1:1 coordinate space).</summary>
    public static (int Width, int Height) GetScreenPixels()
    {
        var w = User32.GetSystemMetrics(User32.SM_CXSCREEN);
        var h = User32.GetSystemMetrics(User32.SM_CYSCREEN);
        return w > 0 && h > 0 ? (w, h) : (1920, 1200);
    }

    /// <summary>
    /// Size in pixels of the frame RTSS positions the overlay in: the hooked foreground game's own frame, which is
    /// smaller than the screen when a borderless or windowed game renders below native resolution. Keeps the last
    /// size seen while that game is still hooked (for example while Tsume's panel has focus), clears it once the
    /// game exits or another hooked game takes focus, and falls back to the screen size otherwise.
    /// </summary>
    private (int Width, int Height) GetFramePixels()
    {
        uint cachedPid;
        lock (_gate) cachedPid = _framePid;

        var fg = User32.GetForegroundWindow();
        var fgPid = 0u;
        if (fg != 0 && !User32.IsIconic(fg) && User32.IsWindowVisible(fg))
        {
            User32.GetWindowThreadProcessId(fg, out var pid);
            if (pid != 0 && pid != (uint)Environment.ProcessId)
                fgPid = pid;
        }

        var query = RtssSharedMemory.QueryFrameState(fgPid != 0 ? [fgPid] : [], cachedPid);
        if (!query.ForegroundIsHooked && fgPid != 0 && IsApplicationFrameWindow(fg))
        {
            var childPids = CollectUwpChildPids(fg, fgPid);
            if (childPids.Count > 0)
                query = RtssSharedMemory.QueryFrameState(CollectionsMarshal.AsSpan(childPids), cachedPid);
        }

        lock (_gate)
        {
            if (query.HasFrame)
            {
                _frame = (query.FrameWidth, query.FrameHeight);
                _framePid = query.ForegroundPid;
                _frameHwnd = fg;
                _frameHwndPid = fgPid;
            }
            else if (query.ForegroundIsHooked && query.ForegroundPid == _framePid && fgPid != 0)
            {
                _frameHwnd = fg;
                _frameHwndPid = fgPid;
            }
            else if ((query.ForegroundIsHooked && query.ForegroundPid != _framePid)
                || (cachedPid != 0 && _framePid == cachedPid && !query.CachedPidStillHooked))
            {
                ClearCachedFrameLocked();
            }

            return _frame ?? GetScreenPixels();
        }
    }

    private void ClearCachedFrameLocked()
    {
        _frame = null;
        _framePid = 0;
        _frameHwnd = 0;
        _frameHwndPid = 0;
    }

    private static bool IsApplicationFrameWindow(nint hwnd)
    {
        var className = new char[64];
        var len = User32.GetClassName(hwnd, className, className.Length);
        return len > 0 && new ReadOnlySpan<char>(className, 0, len).SequenceEqual("ApplicationFrameWindow");
    }

    private static List<uint> CollectUwpChildPids(nint frameHwnd, uint framePid)
    {
        var pids = new List<uint>(2);
        User32.EnumChildWindows(frameHwnd, (child, _) =>
        {
            User32.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != 0 && childPid != framePid && !pids.Contains(childPid))
                pids.Add(childPid);
            return pids.Count < 8;
        }, 0);
        return pids;
    }

    private static (int X, int Y, int Width, int Height)? TryGetClientViewport(nint hwnd, uint expectedHwndPid)
    {
        if (hwnd == 0 || User32.IsIconic(hwnd) || !User32.IsWindowVisible(hwnd))
            return null;

        User32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || (expectedHwndPid != 0 && pid != expectedHwndPid))
            return null;

        if (!User32.GetClientRect(hwnd, out var rect))
            return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < RtssSharedMemory.MinFrameSide || height < RtssSharedMemory.MinFrameSide)
            return null;

        var origin = new User32.POINT { X = rect.Left, Y = rect.Top };
        if (!User32.ClientToScreen(hwnd, ref origin))
            return null;

        var vx = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN);
        var vy = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        var vw = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN);
        var vh = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);
        if (vw > 0 && vh > 0
            && (origin.X + width <= vx || origin.X >= vx + vw || origin.Y + height <= vy || origin.Y >= vy + vh))
        {
            return null;
        }

        return (origin.X, origin.Y, width, height);
    }

    /// <summary>
    /// Publishes the overlay position again when the foreground game's frame is no longer the size it was last
    /// worked out for (another game took focus, this one changed resolution, or the last game exited).
    /// </summary>
    public void SyncFrameSize()
    {
        if (_disposed) return;
        TryApplyDeferredRestartIfIdle();
        if (!IsOverlayVisibleWanted) return;

        var frame = GetFramePixels();
        bool changed;
        lock (_gate) changed = frame != _publishedFrame;

        if (changed) ApplyLiveLevelState(shiftOverlayNow: true);
    }

    /// <summary>Publishes the overlay position again, worked out afresh for the current frame or screen size.</summary>
    public void RepublishPosition() => ApplyLiveLevelState(shiftOverlayNow: true);

    /// <summary>
    /// Bounding box of the active Tsume overlay in the game's frame pixels, together with that frame's size and
    /// the physical screen rectangle the frame is presented into, or null when no Tsume overlay is shown.
    /// </summary>
    public (RtssOverlayLevel Level, int X, int Y, int Width, int Height, int FrameWidth, int FrameHeight, int ViewportX, int ViewportY, int ViewportWidth, int ViewportHeight)? GetActiveOverlayRect()
    {
        if (!IsOverlayVisibleWanted) return null;
        var level = _settings.Level;
        var (w, h) = RtssOverlayMetrics.GetSize(level);
        if (w <= 0 || h <= 0) return null;

        var (fw, fh) = GetFramePixels();
        var (x, y) = ResolveActivePosition(level, fw, fh);

        nint frameHwnd;
        uint frameHwndPid;
        lock (_gate)
        {
            frameHwnd = _frameHwnd;
            frameHwndPid = _frameHwndPid;
        }

        var (screenW, screenH) = GetScreenPixels();
        var (vx, vy, vw, vh) = TryGetClientViewport(frameHwnd, frameHwndPid) ?? (0, 0, screenW, screenH);
        return (level, x, y, w, h, fw, fh, vx, vy, vw, vh);
    }

    /// <summary>Streams a live pixel position to RTSS while the user is actively dragging the overlay.</summary>
    public void SetLiveDragPosition(int x, int y)
    {
        if (_disposed || !IsOverlayVisibleWanted) return;

        var (w, h) = RtssOverlayMetrics.GetSize(_settings.Level);
        var (sw, sh) = GetFramePixels();
        var cx = Math.Clamp(x, 0, Math.Max(0, sw - w));
        var cy = Math.Clamp(y, 0, Math.Max(0, sh - h));

        lock (_gate) _dragOverridePos = (cx, cy);
        ApplyLiveLevelState(shiftOverlayNow: true);
    }

    /// <summary>Saves the final dragged placement for <paramref name="level"/> and publishes it immediately.</summary>
    public void CommitDragPlacement(RtssOverlayLevel level, RtssOverlayPlacement placement)
    {
        lock (_gate) _dragOverridePos = null;
        if (level == RtssOverlayLevel.Off) return;

        ApplySettings(_settings.WithPlacement(level, placement));
    }

    /// <summary>Clears any in-flight live drag override and restores the saved placement.</summary>
    public void CancelDrag()
    {
        bool hadOverride;
        lock (_gate)
        {
            hadOverride = _dragOverridePos is not null;
            _dragOverridePos = null;
        }

        if (hadOverride) ApplyLiveLevelState(shiftOverlayNow: true);
    }

    private (int X, int Y) ResolveActivePosition(RtssOverlayLevel level, int screenWidth, int screenHeight)
    {
        lock (_gate)
        {
            if (_dragOverridePos is { } live)
                return live;
        }

        var placement = _settings.GetPlacement(level);
        return RtssOverlayMetrics.ResolvePosition(level, placement, screenWidth, screenHeight);
    }

    /// <summary>Checks the overlay setup once at start-up (and clears any slot left by older Tsume versions).</summary>
    public void StartIfEnabled()
    {
        var install = RefreshInstallation();
        RtssSharedMemory.ClearSlot();

        if (_state.ShowRtss && install is not null && _settings.SkipAntiCheatGames)
            _ = Task.Run(() => SyncAntiCheatExclusionsAsync());

        ApplyLiveLevelState();
        OverlayStateChanged?.Invoke();

        if (install is not null) _ = Task.Run(() => SyncOverlayPluginAsync(autoStartIfWanted: true));
    }

    /// <summary>Hands the overlay back to the user's own setup, or takes it again, when the RTSS tile is hidden or shown in Settings.</summary>
    public void SetFeatureEnabled(bool enabled)
    {
        ApplyLiveLevelState();
        OverlayStateChanged?.Invoke();
        _ = Task.Run(() => SyncOverlayPluginAsync());
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
    /// - Level and placement changes update <c>AIDA64_SensorValues</c> live, with no helper call and no RTSS restart.
    /// - Toggling <see cref="RtssSettings.UseTsumeOverlay"/> updates OverlayEditor immediately (deferring any RTSS restart while a game is hooked).
    /// </summary>
    public void ApplySettings(RtssSettings next)
    {
        // The first non-Off level is what hands OverlayEditor to Tsume; it stays claimed from then on.
        // Also remember the active level so a global toggle shortcut can restore it.
        if (next.Level != RtssOverlayLevel.Off)
            next = next with { OverlayClaimed = true, LastActiveLevel = next.Level };

        var previous = _settings;
        _settings = next;

        var pluginChanged = previous.UseTsumeOverlay != next.UseTsumeOverlay
            || previous.OverlayClaimed != next.OverlayClaimed;
        var levelChanged = previous.Level != next.Level;
        var touchDragChanged = previous.TouchDragEnabled != next.TouchDragEnabled;
        var placementChanged = !Equals(previous.GetPlacement(next.Level), next.GetPlacement(next.Level));

        // Publish to AIDA64_SensorValues before writing state.json so RTSS sees new level and position immediately.
        if (pluginChanged || levelChanged || placementChanged)
        {
            ApplyLiveLevelState(
                ensureOsdVisible: levelChanged && next.Level != RtssOverlayLevel.Off,
                shiftOverlayNow: placementChanged && !levelChanged && !pluginChanged);
        }

        SaveSettings();

        if (pluginChanged)
        {
            _ = Task.Run(() => SyncOverlayPluginAsync());
        }

        if (pluginChanged || levelChanged || placementChanged || touchDragChanged)
            OverlayStateChanged?.Invoke();
    }

    private void ApplyLiveLevelState(bool ensureOsdVisible = false, bool shiftOverlayNow = false)
    {
        if (_disposed) return;

        if (!IsOverlayVisibleWanted)
        {
            StopTelemetryLoop();
            _energy.Reset();
            lock (_publishGate) _aida.Release();
            CheckConflictChanged();
            return;
        }

        int? cpuTemp;
        double? cpuWatts;
        double? gpuWatts;
        double? cpuClockMhz;
        double? gpuVramMb;
        lock (_gate)
        {
            cpuTemp = _lastCpuTemp;
            cpuWatts = _lastCpuWatts;
            gpuWatts = _lastGpuWatts;
            cpuClockMhz = _lastCpuClockMhz;
            gpuVramMb = _lastGpuVramMb;
        }

        var (tempWarnC, tempHotC) = CurrentTempThresholds();
        var (sw, sh) = GetFramePixels();

        RtssOverlayLevel level;
        bool published;
        lock (_publishGate)
        {
            if (!IsOverlayVisibleWanted) return;
            level = _settings.Level;
            var (posX, posY) = ResolveActivePosition(level, sw, sh);
            published = _aida.TryPublish(
                new AidaSensorSnapshot((int)level, cpuTemp, cpuWatts, gpuWatts, cpuClockMhz, gpuVramMb, tempWarnC, tempHotC, posX, posY),
                _install?.Folder);
            if (published)
            {
                lock (_gate) _publishedFrame = (sw, sh);
            }
        }
        CheckConflictChanged();

        if (ensureOsdVisible && IsRunning)
        {
            EnsureHooks();
            _hooks.SetOsdVisible(true);
        }

        if (level >= RtssOverlayLevel.Bar || !published)
        {
            StartTelemetryLoop();
        }
        else
        {
            StopTelemetryLoop();
            _energy.Reset();
        }
    }

    private (int WarnC, int HotC) CurrentTempThresholds()
    {
        var device = _state.Device;
        return device is not null
            ? (device.TempWarnC, device.TempHotC)
            : (DeviceProfile.DefaultTempWarnC, DeviceProfile.DefaultTempHotC);
    }

    private void StartTelemetryLoop()
    {
        lock (_gate)
        {
            if (_disposed || _telemetryCts is not null) return;
            var cts = new CancellationTokenSource();
            _telemetryCts = cts;
            _ = Task.Run(() => RunTelemetryLoopAsync(cts), cts.Token);
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
        }
    }

    private async Task RunTelemetryLoopAsync(CancellationTokenSource cts)
    {
        var cancel = cts.Token;
        try
        {
            while (!cancel.IsCancellationRequested && IsOverlayVisibleWanted
                && (_settings.Level >= RtssOverlayLevel.Bar || !_aida.IsOwned))
            {
                try
                {
                    var level = _settings.Level;
                    if (level == RtssOverlayLevel.Off) break;

                    if (level < RtssOverlayLevel.Bar)
                    {
                        // FPS-only mode needs no sensor polling; just retry claiming AIDA64_SensorValues until owned.
                        var (fpsSw, fpsSh) = GetFramePixels();
                        bool claimed;
                        lock (_publishGate)
                        {
                            if (!IsOverlayVisibleWanted) break;
                            var currentLevel = _settings.Level;
                            var (fpsPosX, fpsPosY) = ResolveActivePosition(currentLevel, fpsSw, fpsSh);
                            claimed = _aida.TryPublish(
                                new AidaSensorSnapshot((int)currentLevel, PosX: fpsPosX, PosY: fpsPosY),
                                _install?.Folder);
                            if (claimed) lock (_gate) _publishedFrame = (fpsSw, fpsSh);
                        }
                        CheckConflictChanged();
                        if (claimed) break;

                        await Task.Delay(ConflictRetryInterval, cancel).ConfigureAwait(false);
                        continue;
                    }

                    // When RTSS is stopped or no 3D app is hooked, release PDH handles and skip helper IPC until a game starts.
                    if (!RtssSharedMemory.HasHookedApps())
                    {
                        _energy.Reset();
                        lock (_gate) ClearCachedFrameLocked();
                        TryApplyDeferredRestartIfIdle();
                        if (!_aida.IsOwned)
                        {
                            var (idleSw, idleSh) = GetScreenPixels();
                            lock (_publishGate)
                            {
                                if (IsOverlayVisibleWanted)
                                {
                                    var currentLevel = _settings.Level;
                                    var (idlePosX, idlePosY) = ResolveActivePosition(currentLevel, idleSw, idleSh);
                                    if (_aida.TryPublish(
                                        new AidaSensorSnapshot((int)currentLevel, PosX: idlePosX, PosY: idlePosY),
                                        _install?.Folder))
                                    {
                                        lock (_gate) _publishedFrame = (idleSw, idleSh);
                                    }
                                }
                            }
                            CheckConflictChanged();
                        }

                        await Task.Delay(IdleTelemetryInterval, cancel).ConfigureAwait(false);
                        continue;
                    }

                    var (cpuWatts, gpuWatts, cpuClockMhz, gpuVramMb) = _energy.Sample();

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
                        _lastGpuVramMb = gpuVramMb;
                    }

                    var (tempWarnC, tempHotC) = CurrentTempThresholds();
                    var (sw, sh) = GetFramePixels();
                    bool ok;
                    lock (_publishGate)
                    {
                        if (cancel.IsCancellationRequested || !IsOverlayVisibleWanted) break;
                        level = _settings.Level;
                        if (level == RtssOverlayLevel.Off) break;
                        var (posX, posY) = ResolveActivePosition(level, sw, sh);
                        ok = _aida.TryPublish(
                            new AidaSensorSnapshot((int)level, cpuTemp, cpuWatts, gpuWatts, cpuClockMhz, gpuVramMb, tempWarnC, tempHotC, posX, posY),
                            _install?.Folder);
                        if (ok) lock (_gate) _publishedFrame = (sw, sh);
                    }
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
        finally
        {
            var endedItself = false;
            lock (_gate)
            {
                if (ReferenceEquals(_telemetryCts, cts))
                {
                    _telemetryCts = null;
                    endedItself = true;
                }

                // A sample still in flight when the loop was stopped can reopen the counters after the caller's
                // reset; with no loop left to use them, close them here.
                if (_telemetryCts is null) _energy.Reset();
            }
            cts.Dispose();

            // A level change that arrived while this loop was on its way out found it still registered and
            // started nothing, so start the loop it wanted.
            if (endedItself && IsOverlayVisibleWanted
                && (_settings.Level >= RtssOverlayLevel.Bar || !_aida.IsOwned))
            {
                StartTelemetryLoop();
            }
        }
    }

    private void MarkRestartPendingAndWatchIdle()
    {
        lock (_gate)
        {
            _rtssRestartPending = true;
            if (_disposed || _deferredRestartPolling) return;
            _deferredRestartPolling = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed)
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    bool pending;
                    lock (_gate) pending = _rtssRestartPending;
                    if (!pending) break;
                    if (!RtssSharedMemory.HasHookedApps())
                    {
                        await FlushDeferredRestartAsync().ConfigureAwait(false);
                        break;
                    }
                }
            }
            finally
            {
                lock (_gate) _deferredRestartPolling = false;
            }
        });
    }

    private void TryApplyDeferredRestartIfIdle()
    {
        if (_disposed) return;

        bool pending;
        lock (_gate) pending = _rtssRestartPending;
        if (!pending || RtssSharedMemory.HasHookedApps()) return;

        _ = Task.Run(() => FlushDeferredRestartAsync());
    }

    private async Task FlushDeferredRestartAsync()
    {
        if (_disposed) return;
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            bool pending;
            lock (_gate) pending = _rtssRestartPending;
            if (!pending || !IsRunning || RtssSharedMemory.HasHookedApps()) return;

            lock (_gate) _rtssRestartPending = false;
            await RestartRtssAsync().ConfigureAwait(false);

            if (IsOverlayVisibleWanted && IsRunning)
            {
                EnsureHooks();
                _hooks.SetOsdVisible(true);
            }
        }
        catch (Exception error)
        {
            Log.Write($"deferred RTSS restart failed: {error.Message}");
        }
        finally
        {
            _sync.Release();
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
    /// Points OverlayEditor at Tsume's unified layout through the helper (or restores the user's original
    /// setup when Tsume's overlay is turned off) and restarts RTSS only when that changed anything AND no
    /// 3D game is currently hooked (deferring the restart until idle so running games are never crashed).
    /// On start-up (<paramref name="autoStartIfWanted"/>), also starts RTSS silently via the helper if the overlay
    /// is enabled and RTSS is not running yet so overlays appear without needing to open Tsume first.
    /// Runs one at a time, always with the latest settings.
    /// </summary>
    private async Task SyncOverlayPluginAsync(bool autoStartIfWanted = false)
    {
        if (_disposed) return;
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_install is null) return;

            await RestoreLegacyAppearanceAsync().ConfigureAwait(false);

            var layout = IsOverlayPluginWanted ? "tsume" : null;
            HelperReply reply = HelperReply.Failure("offline");
            var attempts = autoStartIfWanted ? 5 : 1;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                reply = await _helper.SendAsync(new SetRtssOverlay(layout)).ConfigureAwait(false);
                if (reply.Ok || !reply.Unreachable || attempt == attempts || _disposed) break;
                await Task.Delay(1000).ConfigureAwait(false);
            }

            if (!reply.Ok)
            {
                Log.Write($"helper rtss.overlay.set failed ({reply.Error ?? "offline"})");
                return;
            }

            bool needRestart;
            lock (_gate)
            {
                if (reply.RtssOverlayChanged == true) _rtssRestartPending = true;
                needRestart = _rtssRestartPending;
            }

            if (needRestart && IsRunning)
            {
                if (RtssSharedMemory.HasHookedApps())
                {
                    Log.Write("deferring RTSS restart while a 3D application is hooked");
                    MarkRestartPendingAndWatchIdle();
                }
                else
                {
                    lock (_gate) _rtssRestartPending = false;
                    await RestartRtssAsync().ConfigureAwait(false);
                }
            }
            else if (autoStartIfWanted && IsOverlayVisibleWanted && !IsRunning)
            {
                lock (_gate) _rtssRestartPending = false;
                // Auto-start silently through the elevated helper only (never show a UAC prompt in the background).
                var started = await _helper.SendAsync(new StartRtss()).ConfigureAwait(false);
                if (started.Ok)
                {
                    Log.Write("started RTSS automatically in the background for Tsume's overlay");
                    for (var attempt = 0; attempt < 30 && !RtssSharedMemory.IsAvailable(); attempt++)
                        await Task.Delay(250).ConfigureAwait(false);
                    Restarted?.Invoke();
                }
                else
                {
                    Log.Write($"background rtss.start failed ({started.Error ?? "offline"})");
                }
            }

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

        // Defense in depth: never kill RTSS while a 3D app is hooked (tearing down RTSSHooks64 mid-game crashes DX12/Vulkan titles).
        if (RtssSharedMemory.HasHookedApps())
        {
            MarkRestartPendingAndWatchIdle();
            Log.Write("skipping RTSS restart because a 3D application is currently hooked");
            return;
        }

        Log.Write("restarting RTSS to apply updated OverlayEditor configuration");
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

        StopTelemetryLoop();
        _energy.Dispose();
        _aida.Dispose();
        // _sync is left to the GC: a sync still in flight releases it after this returns.
        _hooks.Dispose();
    }
}
