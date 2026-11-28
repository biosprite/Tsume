using Tsume.Core.Cooling;
using Tsume.Core.Ipc;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// Remembers the chosen custom fan curve and puts it back when the firmware has lost it (restart, sleep, perhaps a
/// mode change): the fans are no longer on Custom, or a table no longer holds the curve. Like
/// <see cref="ModeKeeper"/>, it acts only on a mismatch and not while the maker's program is running. It also
/// stands back while Cooler Boost is on: that is a temporary boost on top of the curve, and turning it off is the
/// user's call. It remembers the speeds as written, after clamping, so it compares like with like.
/// </summary>
public sealed class FanCurveKeeper
{
    /// <summary>Holds the speeds as written, in the form of the request.</summary>
    private static readonly string Memory = Path.Combine(
        Core.DataFolder.Path, "chosen-fan-curve.json");

    private readonly IDevice _device;
    private readonly Lock _gate = new();

    public FanCurveKeeper(IDevice device)
    {
        _device = device;
        try
        {
            _device.NoteChosenFanCurve(Recall()?.Speeds);
        }
        catch (Exception error)
        {
            Log.Write($"Could not recall the chosen fan curve: {error.Message}");
        }
    }

    /// <param name="written">The speeds <see cref="IDevice.SetFanCurve"/> returned.</param>
    public void Remember(IReadOnlyList<int> written)
    {
        lock (_gate)
        {
            _device.NoteChosenFanCurve(written);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Memory)!);
                var temp = Memory + ".tsume-tmp";
                File.WriteAllText(temp, HelperJson.Write(new SetFanCurve([.. written])));
                File.Move(temp, Memory, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not remember the chosen fan curve: {error.Message}");
            }
        }
    }

    /// <summary>Drops the remembered curve: the fans were set to Auto, or it no longer fits the firmware's curve.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _device.NoteChosenFanCurve(null);
            try
            {
                File.Delete(Memory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Could not forget the chosen fan curve: {error.Message}");
            }
        }
    }

    /// <param name="occasion">Why now, for the log: "start-up", "wake", "mode change"…</param>
    /// <remarks>Never throws: it runs at start-up and after other changes, which must not fail because of it.</remarks>
    public void RestoreIfForgotten(string occasion)
    {
        try
        {
            lock (DiscoveryWorker.KeeperGate)
            lock (_gate)
            {
                RestoreLocked(occasion);
            }
        }
        catch (Exception error)
        {
            Log.Write($"Could not restore the fan curve on {occasion}: {error.Message}");
        }
    }

    private void RestoreLocked(string occasion)
    {
        if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
        {
            Log.Write($"Skipped fan curve restore on {occasion}: a firmware check is running.");
            return;
        }

        if (Recall() is not { } chosen || !_device.Profile.Offers(FanMode.Custom) || _device.MakerToolIsRunning) return;

        var found = _device.Fan();
        if (found == FanMode.Max || (found == FanMode.Custom && _device.HoldsFanCurve(chosen.Speeds))) return;

        IReadOnlyList<int> written;
        try
        {
            written = _device.SetFanCurve(chosen.Speeds);
        }
        catch (ArgumentException error)
        {
            // Speeds that no longer fit the firmware's curve (e.g. another number of points) are not a curve to keep.
            Log.Write($"Dropped the remembered fan curve on {occasion}: {error.Message}");
            Forget();
            return;
        }

        if (!written.SequenceEqual(chosen.Speeds)) Remember(written);
        Log.Write($"On {occasion} the fans were on {found}{(found == FanMode.Custom ? " with another curve" : "")}. Put back the custom curve.");
    }

    /// <summary>The remembered curve, or null if there is none or it is unusable (no speeds).</summary>
    private static SetFanCurve? Recall()
    {
        try
        {
            return File.Exists(Memory)
                && HelperJson.ReadRequest(File.ReadAllText(Memory)) is SetFanCurve { Speeds.Count: > 0 } chosen
                ? chosen
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;   // unreadable is treated as absent
        }
    }
}
