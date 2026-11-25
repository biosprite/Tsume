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
    private SetPerformance? _last;
    private bool _loaded;

    public void Remember(SetPerformance chosen)
    {
        lock (_gate)
        {
            _last = chosen;
            _loaded = true;
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

            if (device.LimitsToApply(chosen.Mode, manual, chosen.Custom) is { } wanted)
            {
                var expectedHardwareMode = device.HardwareModeFor(chosen.Mode, wanted);

                if (snapshot.Mode == expectedHardwareMode && snapshot.Limits == wanted)
                    return expectedHardwareMode == chosen.Mode ? snapshot : snapshot with { Mode = chosen.Mode };

                // On a board with a battery cap, the firmware holds the other power source's limits for a moment
                // after plugging in or unplugging, until the change is put back. That is still the chosen mode,
                // not something else's Manual.
                if (device.Profile.BatteryCap is not null
                    && device.Profile.LimitsFor(chosen.Mode, manual, chosen.Custom) is { } onWallPower)
                {
                    foreach (var either in new[] { onWallPower, device.Profile.CapForBattery(onWallPower) })
                    {
                        if (snapshot.Mode == device.HardwareModeFor(chosen.Mode, either) && snapshot.Limits == either)
                            return snapshot with { Mode = chosen.Mode };
                    }
                }
            }
        }

        // On boards where Manual shares its shift code with Performance (e.g. 0x04 on MS-1T41/1T42/1T52/1T8K),
        // MsiShiftModes.Decode always returns Manual. When there is no matching remembered choice (e.g. first
        // launch or after MSI Center M switched modes), map an exact stock preset match back to its preset mode.
        if (snapshot.Mode == Core.Performance.PerformanceMode.Manual)
        {
            foreach (var preset in device.Profile.Modes)
            {
                if (preset.Limits is { } stock
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
            if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
            {
                Log.Write($"Skipped mode restore on {occasion}: a firmware check is running.");
                return false;
            }

            try
            {
                if (CurrentLocked() is not { } chosen || !device.Profile.Offers(chosen.Mode) || device.MakerToolIsRunning) return false;

                var manual = device.Profile.ManualRange?.Clamp(chosen.Manual) ?? chosen.Manual;
                var wanted = device.LimitsToApply(chosen.Mode, manual, chosen.Custom);
                // With no limits (unreachable for an offered mode) the comparison below fails and SetPerformance refuses.
                var expectedHardwareMode = wanted is { } target ? device.HardwareModeFor(chosen.Mode, target) : chosen.Mode;
                var (mode, limits) = device.Performance();

                if (mode == expectedHardwareMode && limits == wanted) return true;

                device.SetPerformance(chosen.Mode, manual, chosen.Custom);
                var applied = expectedHardwareMode == chosen.Mode
                    ? $"{chosen.Mode}"
                    : $"{chosen.Mode} (shift {expectedHardwareMode})";
                Log.Write($"On {occasion} the firmware had {mode}, {limits}. Put back {applied}, {wanted}.");
                return true;
            }
            catch (Exception error) when (error is FirmwareException or ArgumentOutOfRangeException or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            {
                Log.Write($"Could not restore the chosen mode on {occasion}: {error.Message}");
                return false;
            }
        }
    }

    /// <summary>True when the limits applied depend on the power source, so a change of it must be acted on.</summary>
    public bool FollowsPowerSource => device.Profile.BatteryCap is not null;

    private readonly Lock _sourceGate = new();
    private bool? _lastOnBattery;

    /// <summary>
    /// Called on every power status notification. On a board with a battery cap, puts the chosen mode back
    /// through <see cref="RestoreIfForgotten"/> — which rewrites only when the cap went on or came off — once per
    /// actual change of source; repeated notifications (battery level, duplicates) do nothing. Nothing otherwise.
    /// A source is counted as handled only once the firmware holds the right limits for it: if the restore was
    /// skipped (a firmware check, MSI Center M running, nothing chosen yet) or failed, the next notification tries again.
    /// </summary>
    public void OnPowerStatusChange()
    {
        if (!FollowsPowerSource) return;

        bool onBattery;
        lock (_sourceGate)
        {
            onBattery = PowerSource.IsOnBattery();
            if (_lastOnBattery == onBattery) return;
        }

        if (!RestoreIfForgotten("power source change")) return;

        lock (_sourceGate) _lastOnBattery = onBattery;
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
