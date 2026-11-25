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
            var expectedHardwareMode = device.HardwareModeFor(chosen.Mode);
            var manual = device.Profile.ManualRange?.Clamp(chosen.Manual) ?? chosen.Manual;
            var wanted = device.Profile.LimitsFor(chosen.Mode, manual, chosen.Custom);

            if (snapshot.Mode == expectedHardwareMode && snapshot.Limits == wanted)
                return expectedHardwareMode == chosen.Mode ? snapshot : snapshot with { Mode = chosen.Mode };
        }

        // On boards where Manual shares its shift code with Performance (e.g. 0x04 on MS-1T41/1T42/1T52/1T8K),
        // MsiShiftModes.Decode always returns Manual. When there is no matching remembered choice (e.g. first
        // launch or after MSI Center M switched modes), map an exact stock preset match back to its preset mode.
        if (snapshot.Mode == Core.Performance.PerformanceMode.Manual)
        {
            foreach (var preset in device.Profile.Modes)
            {
                if (preset.Limits is { } stock
                    && device.HardwareModeFor(preset.Mode) == Core.Performance.PerformanceMode.Manual
                    && snapshot.Limits == stock)
                {
                    return snapshot with { Mode = preset.Mode };
                }
            }
        }

        return snapshot;
    }

    /// <param name="occasion">Why now — "start-up", "wake" — for the log.</param>
    public void RestoreIfForgotten(string occasion)
    {
        lock (DiscoveryWorker.KeeperGate)
        lock (_gate)
        {
            if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
            {
                Log.Write($"Skipped mode restore on {occasion}: a firmware check is running.");
                return;
            }

            try
            {
                if (CurrentLocked() is not { } chosen || !device.Profile.Offers(chosen.Mode) || device.MakerToolIsRunning) return;

                var manual = device.Profile.ManualRange?.Clamp(chosen.Manual) ?? chosen.Manual;
                var wanted = device.Profile.LimitsFor(chosen.Mode, manual, chosen.Custom);
                var expectedHardwareMode = device.HardwareModeFor(chosen.Mode);
                var (mode, limits) = device.Performance();

                if (mode == expectedHardwareMode && limits == wanted) return;

                device.SetPerformance(chosen.Mode, manual, chosen.Custom);
                var applied = expectedHardwareMode == chosen.Mode
                    ? $"{chosen.Mode}"
                    : $"{chosen.Mode} (shift {expectedHardwareMode})";
                Log.Write($"On {occasion} the firmware had {mode}, {limits}. Put back {applied}, {wanted}.");
            }
            catch (Exception error) when (error is FirmwareException or ArgumentOutOfRangeException or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            {
                Log.Write($"Could not restore the chosen mode on {occasion}: {error.Message}");
            }
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
