using Tsume.Core.Cooling;
using Tsume.Core.Ipc;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// Turns one request into one reply. This is Tsume's whole privileged surface: an unelevated process can do
/// nothing that is not listed here. Anything outside the device's profile is refused before the device is called.
/// </summary>
public sealed class RequestHandler
{
    private sealed record HardwareState(Board Machine, IDevice? Device, ModeKeeper? Keeper, ChargeKeeper? Charge, FanCurveKeeper? Fan);

    private volatile HardwareState? _hardware;

    /// <summary>Creates a handler whose hardware is not yet ready; <see cref="SetHardware"/> populates it once WMI answers.</summary>
    public RequestHandler() { }

    /// <param name="machine">The board found at start-up.</param>
    /// <param name="device">Null on a machine Tsume does not know.</param>
    /// <param name="keeper">Null exactly when <paramref name="device"/> is.</param>
    /// <param name="charge">Null exactly when <paramref name="device"/> is.</param>
    /// <param name="fan">Null exactly when <paramref name="device"/> is.</param>
    public RequestHandler(Board machine, IDevice? device, ModeKeeper? keeper, ChargeKeeper? charge, FanCurveKeeper? fan) =>
        _hardware = new HardwareState(machine, device, keeper, charge, fan);

    public void SetHardware(Board machine, IDevice? device, ModeKeeper? keeper, ChargeKeeper? charge, FanCurveKeeper? fan) =>
        _hardware = new HardwareState(machine, device, keeper, charge, fan);

    public HelperReply Handle(HelperRequest request)
    {
        if (_hardware is not { } hardware)
            return HelperReply.Failure("The helper is still starting up.");

        var (machine, device, keeper, charge, fan) = hardware;

        // On an unknown machine even a snapshot would read the wrong registers, so everything is refused.
        if (device is null)
            return HelperReply.NotSupported($"{machine}");

        var profile = device.Profile;
        var takesCurve = profile.FanCurve is not null && profile.Offers(FanMode.Custom);

        try
        {
            switch (request)
            {
                case GetSnapshot:
                    // The only reply that also carries the device profile.
                    return HelperReply.Success(SnapshotFor(device, keeper)) with { Device = profile };

                case GetReadings:
                    return HelperReply.Success(device.Readings());

                case GetFanCurve:
                    if (!takesCurve)
                        return HelperReply.Failure("This device takes no custom fan curve.");

                    return new HelperReply { Ok = true, FanCurve = device.FanCurve() };

                case SetPerformance or SetFan or SetFanCurve or SetChargeLimit when DiscoveryWorker.IsMutatingFirmwareCheckRunning:
                    return HelperReply.Failure("A firmware check is running. Wait for it to finish before changing settings.");

                case SetPerformance(var mode, var manual, var custom, var customOnBattery) chosen:
                    if (!profile.Offers(mode))
                        return HelperReply.Failure($"This device has no {mode} mode.");

                    lock (DiscoveryWorker.KeeperGate)
                    {
                        if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
                            return HelperReply.Failure("A firmware check is running. Wait for it to finish before changing settings.");

                        bool? onBattery = profile.FollowsPowerSource ? PowerSource.IsOnBattery() : null;
                        // A preset's own limits, if sent, are clamped where they are used: DeviceProfile.LimitsFor.
                        device.SetPerformance(mode, profile.ManualRange?.Clamp(manual) ?? manual, custom, onBattery, customOnBattery);
                        keeper?.Remember(chosen, onBattery);
                    }

                    // Whether a mode change makes the firmware reload its own fan tables is not known, so check.
                    // The keeper never throws, and does nothing unless a curve is remembered and was lost.
                    fan?.RestoreIfForgotten("mode change");
                    break;

                case SetFan(FanMode.Custom):
                    return HelperReply.Failure("Custom fans need a curve: set one instead of choosing Custom.");

                case SetFan(var mode):
                    if (!profile.Offers(mode))
                        return HelperReply.Failure($"This device has no {mode} fan setting.");

                    lock (DiscoveryWorker.KeeperGate)
                    {
                        if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
                            return HelperReply.Failure("A firmware check is running. Wait for it to finish before changing settings.");

                        try
                        {
                            device.SetFan(mode);
                        }
                        finally
                        {
                            // Max is a boost on top of the curve, so the curve stays remembered. Auto drops it even
                            // if the firmware write failed part-way: the user asked for Auto, not for the curve back.
                            if (mode == FanMode.Auto) fan?.Forget();
                        }
                    }
                    break;

                case SetFanCurve(var speeds):
                    if (!takesCurve)
                        return HelperReply.Failure("This device takes no custom fan curve.");
                    if (speeds is null)
                        return HelperReply.Failure("A fan curve needs its speeds.");

                    lock (DiscoveryWorker.KeeperGate)
                    {
                        if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
                            return HelperReply.Failure("A firmware check is running. Wait for it to finish before changing settings.");

                        // Clamped where it is written: FanCurveLimits.Clamp, against the firmware's own temperatures.
                        // Remembered as written, so the keeper later compares like with like.
                        var written = device.SetFanCurve(speeds);
                        fan?.Remember(written);
                    }
                    break;

                case SetChargeLimit(var percent) chosen:
                    if (!profile.ChargeLimits.Contains(percent))
                        return HelperReply.Failure($"This device has no {percent} % charge limit.");

                    lock (DiscoveryWorker.KeeperGate)
                    {
                        if (DiscoveryWorker.IsMutatingFirmwareCheckRunning)
                            return HelperReply.Failure("A firmware check is running. Wait for it to finish before changing settings.");

                        device.SetChargeLimit(percent);
                        charge?.Remember(chosen);
                    }
                    break;

                default:
                    return HelperReply.Failure($"Unknown request: {request.GetType().Name}");
            }

            var reply = HelperReply.Success(SnapshotFor(device, keeper));
            return request is SetFan or SetFanCurve && takesCurve ? reply with { FanCurve = TryFanCurve(device) } : reply;
        }
        catch (Exception error)
        {
            Log.Write($"{request.GetType().Name} failed: {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }

    private static Core.Telemetry.HardwareSnapshot SnapshotFor(IDevice device, ModeKeeper? keeper)
    {
        var snapshot = device.Snapshot();
        return keeper is null ? snapshot : keeper.Resolve(snapshot);
    }

    /// <summary>The curve to send with a change already made. A failed read leaves the curve out rather than failing the reply.</summary>
    private static FanCurveState? TryFanCurve(IDevice device)
    {
        try
        {
            return device.FanCurve();
        }
        catch (Exception error) when (error is FirmwareException or System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Reading the fan curve failed: {error.Message}");
            return null;
        }
    }
}
