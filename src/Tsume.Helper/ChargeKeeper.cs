using Tsume.Core.Ipc;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// Remembers the chosen charge limit and puts it back when the firmware has lost it (restart, sleep).
/// Like <see cref="ModeKeeper"/>, it acts only on a mismatch and not while the maker's program is running.
/// </summary>
public sealed class ChargeKeeper(IDevice device)
{
    /// <summary>Holds the request as it arrived over the pipe, so it is re-checked like any other.</summary>
    private static readonly string Memory = Path.Combine(
        Core.DataFolder.Path, "chosen-charge-limit.json");

    private readonly Lock _gate = new();

    public void Remember(SetChargeLimit chosen)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Memory)!);
                File.WriteAllText(Memory, HelperJson.Write(chosen));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not remember the chosen charge limit: {error.Message}");
            }
        }
    }

    /// <param name="occasion">Why now — "start-up", "wake" — for the log.</param>
    public void RestoreIfForgotten(string occasion)
    {
        lock (_gate)
        {
            try
            {
                if (Recall() is not { } chosen || !device.Profile.ChargeLimits.Contains(chosen.Percent) || device.MakerToolIsRunning) return;

                var found = device.ChargeLimit();
                if (found == chosen.Percent) return;

                device.SetChargeLimit(chosen.Percent);
                Log.Write($"On {occasion} the firmware's charge limit was {(found is { } percent ? $"{percent} %" : "off")}. Put back {chosen.Percent} %.");
            }
            catch (Exception error) when (error is FirmwareException or ArgumentOutOfRangeException or System.Management.ManagementException)
            {
                Log.Write($"Could not restore the charge limit on {occasion}: {error.Message}");
            }
        }
    }

    private static SetChargeLimit? Recall()
    {
        try
        {
            return File.Exists(Memory) ? HelperJson.ReadRequest(File.ReadAllText(Memory)) as SetChargeLimit : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;   // unreadable is treated as absent
        }
    }
}
