using Tsume.Core.Ipc;
using Tsume.Core.Telemetry;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// Remembers the chosen performance mode and puts it back when the firmware has lost it (restart, sleep) —
/// the job MSI Center M does when it is not paused. Acts only on a mismatch, and never while the maker's
/// program is running: two keepers would undo each other.
/// </summary>
public sealed class ModeKeeper(IDevice device)
{
    /// <summary>Holds the request as it arrived over the pipe, so it is clamped again like any other.</summary>
    private static readonly string Memory = Path.Combine(
        Core.DataFolder.Path, "chosen-mode.json");

    private readonly Lock _gate = new();

    /// <summary>
    /// Called with the occasion after this keeper has put a mode back. Whether that makes the firmware reload its
    /// own fan tables is not known, so the fan curve keeper checks then. Must not throw.
    /// </summary>
    public Action<string>? AfterRestore { get; set; }

    private SetPerformance? _last;
    private bool _loaded;
    private bool? _lastOnBattery;

    public void Remember(SetPerformance chosen, bool? onBattery = null)
    {
        lock (_gate)
        {
            _last = chosen;
            _loaded = true;
            if (FollowsPowerSource)
                _lastOnBattery = onBattery is { } applied && PowerSource.IsOnBattery() == applied ? applied : null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Memory)!);
                var temp = Memory + ".tsume-tmp";
                File.WriteAllText(temp, HelperJson.Write(chosen));
                File.Move(temp, Memory, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not remember the chosen mode: {error.Message}");
            }
        }
    }

    /// <summary>
    /// Maps a hardware snapshot back to the user's logical preset mode when the firmware uses
    /// <see cref="Core.Performance.PerformanceMode.Manual"/> to enforce preset watt limits and
    /// the registers still hold the exact mode and limits Tsume wrote.
    /// </summary>
    public HardwareSnapshot Resolve(HardwareSnapshot snapshot)
    {
        SetPerformance? chosen;
        lock (_gate) chosen = CurrentLocked();

        if (chosen is not null && device.Profile.Offers(chosen.Mode))
        {
            var manual = device.Profile.ManualRange?.Clamp(chosen.Manual) ?? chosen.Manual;

            if (device.LimitsToApply(chosen.Mode, manual, chosen.Custom, customOnBattery: chosen.CustomOnBattery) is { } wanted)
            {
                var expectedHardwareMode = device.HardwareModeFor(chosen.Mode, wanted);

                if (snapshot.Mode == expectedHardwareMode && snapshot.Limits == wanted)
                    return expectedHardwareMode == chosen.Mode ? snapshot : snapshot with { Mode = chosen.Mode };

                // Just after plugging in or unplugging, the firmware still holds the other source's limits.
                // That is still the chosen mode.
                if (device.Profile.FollowsPowerSource)
                {
                    foreach (var either in new[]
                    {
                        device.Profile.LimitsFor(chosen.Mode, manual, chosen.Custom),
                        device.Profile.LimitsOnBattery(chosen.Mode, manual, chosen.CustomOnBattery),
                    })
                    {
                        if (either is { } limits
                            && snapshot.Mode == device.HardwareModeFor(chosen.Mode, limits) && snapshot.Limits == limits)
                            return snapshot with { Mode = chosen.Mode };
                    }
                }
            }
        }

        // On boards where Manual shares its shift code with Performance (e.g. 0x04 on MS-1T41/1T42/1T52/1T8K),
        // MsiShiftModes.Decode always returns Manual. When there is no matching remembered choice (e.g. first
        // launch or after MSI Center M switched modes), map an exact stock preset match (battery limits included
        // while on battery) back to its preset mode.
        if (snapshot.Mode == Core.Performance.PerformanceMode.Manual)
        {
            foreach (var preset in device.Profile.Modes)
            {
                if (preset.Limits is not null
                    && device.LimitsToApply(preset.Mode, default) is { } stock
                    && device.HardwareModeFor(preset.Mode, stock) == Core.Performance.PerformanceMode.Manual
                    && snapshot.Limits == stock)
                {
                    return snapshot with { Mode = preset.Mode };
                }
            }
        }

        return snapshot;
    }

    /// <param name="occasion">Why now — "start-up", "wake" — for the log.</param>
    /// <returns>True when the firmware now holds the chosen mode; false when the restore was skipped or failed.</returns>
    public bool RestoreIfForgotten(string occasion)
    {
        lock (DiscoveryWorker.KeeperGate)
        lock (_gate)
        {
            return RestoreLocked(occasion, onlyIfSourceChanged: false);
        }
    }

    /// <summary>True when the limits applied depend on the power source, so a change of it must be acted on.</summary>
    public bool FollowsPowerSource => device.Profile.FollowsPowerSource;

    /// <summary>
    /// Called on every power status notification. On a board whose limits differ on battery, puts the chosen mode
    /// back through <see cref="RestoreLocked"/> — which rewrites only when the limits changed — once per
    /// actual change of source; repeated notifications (battery level, duplicates) do nothing. Nothing otherwise.
    /// A source is counted as handled only once the firmware holds the right limits for it: if the restore was
    /// skipped (a firmware check, MSI Center M running, nothing chosen yet) or failed, the next notification tries again.
    /// </summary>
    public void OnPowerStatusChange()
    {
        if (!FollowsPowerSource) return;

        lock (DiscoveryWorker.KeeperGate)
        lock (_gate)
        {
            RestoreLocked("power source change", onlyIfSourceChanged: true);
        }
    }

    private bool RestoreLocked(string occasion, bool onlyIfSourceChanged)
    {
        bool? onBattery = FollowsPowerSource ? PowerSource.IsOnBattery() : null;
        if (onlyIfSourceChanged && _lastOnBattery == onBattery) return false;

        if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
        {
            Log.Write($"Skipped mode restore on {occasion}: a firmware check is running.");
            return false;
        }

        try
        {
            if (CurrentLocked() is not { } chosen || !device.Profile.Offers(chosen.Mode) || device.MakerToolIsRunning) return false;

            var manual = device.Profile.ManualRange?.Clamp(chosen.Manual) ?? chosen.Manual;
            var wanted = device.LimitsToApply(chosen.Mode, manual, chosen.Custom, onBattery, chosen.CustomOnBattery);
            // With no limits (unreachable for an offered mode) the comparison below fails and SetPerformance refuses.
            var expectedHardwareMode = wanted is { } target ? device.HardwareModeFor(chosen.Mode, target) : chosen.Mode;
            var (mode, limits) = device.Performance();

            if (!device.ReapplyOnResume || onlyIfSourceChanged)
            {
                if (mode == expectedHardwareMode && limits == wanted)
                {
                    if (FollowsPowerSource) _lastOnBattery = onBattery;
                    return true;
                }
            }

            device.SetPerformance(chosen.Mode, manual, chosen.Custom, onBattery, chosen.CustomOnBattery);
            if (FollowsPowerSource) _lastOnBattery = onBattery;

            var applied = expectedHardwareMode == chosen.Mode
                ? $"{chosen.Mode}"
                : $"{chosen.Mode} (shift {expectedHardwareMode})";
            if (mode == expectedHardwareMode && limits == wanted)
                Log.Write($"On {occasion} re-applied {applied}, {wanted}.");
            else
                Log.Write($"On {occasion} the firmware had {mode}, {limits}. Put back {applied}, {wanted}.");
            AfterRestore?.Invoke(occasion);
            return true;
        }
        catch (Exception error) when (error is FirmwareException or ArgumentOutOfRangeException or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Could not restore the chosen mode on {occasion}: {error.Message}");
            return false;
        }
    }

    private SetPerformance? CurrentLocked()
    {
        if (!_loaded && TryRecall(out var recalled))
        {
            _last = recalled;
            _loaded = true;
        }
        return _last;
    }

    private static bool TryRecall(out SetPerformance? chosen)
    {
        try
        {
            chosen = File.Exists(Memory) ? HelperJson.ReadRequest(File.ReadAllText(Memory)) as SetPerformance : null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            chosen = null;
            return false;   // transient I/O failure — retry next time instead of latching null
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException)
        {
            chosen = null;
            return true;    // unreadable JSON is treated as absent
        }
    }
}
