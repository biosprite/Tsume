using Tsume.Core.Ipc;
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

    public void Remember(SetPerformance chosen)
    {
        lock (_gate)
        {
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
                if (Recall() is not { } chosen || !device.Profile.Offers(chosen.Mode) || device.MakerToolIsRunning) return;

                var manual = device.Profile.ManualRange?.Clamp(chosen.Manual) ?? chosen.Manual;
                var wanted = device.Profile.LimitsFor(chosen.Mode, manual, chosen.Custom);
                var (mode, limits) = device.Performance();

                if (mode == chosen.Mode && limits == wanted) return;

                device.SetPerformance(chosen.Mode, manual, chosen.Custom);
                Log.Write($"On {occasion} the firmware had {mode}, {limits}. Put back {chosen.Mode}, {wanted}.");
            }
            catch (Exception error) when (error is FirmwareException or ArgumentOutOfRangeException or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
            {
                Log.Write($"Could not restore the chosen mode on {occasion}: {error.Message}");
            }
        }
    }

    private static SetPerformance? Recall()
    {
        try
        {
            return File.Exists(Memory) ? HelperJson.ReadRequest(File.ReadAllText(Memory)) as SetPerformance : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;   // unreadable is treated as absent
        }
    }
}
