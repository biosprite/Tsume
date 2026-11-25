using Tsume.Core.Ipc;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// Turns one request into one reply. This is Tsume's whole privileged surface: an unelevated process can do
/// nothing that is not listed here. Anything outside the device's profile is refused before the device is called.
/// </summary>
public sealed class RequestHandler
{
    private sealed record HardwareState(Board Machine, IDevice? Device, ModeKeeper? Keeper, ChargeKeeper? Charge);

    private volatile HardwareState? _hardware;

    /// <summary>Creates a handler whose hardware is not yet ready; <see cref="SetHardware"/> populates it once WMI answers.</summary>
    public RequestHandler() { }

    /// <param name="machine">The board found at start-up.</param>
    /// <param name="device">Null on a machine Tsume does not know.</param>
    /// <param name="keeper">Null exactly when <paramref name="device"/> is.</param>
    /// <param name="charge">Null exactly when <paramref name="device"/> is.</param>
    public RequestHandler(Board machine, IDevice? device, ModeKeeper? keeper, ChargeKeeper? charge) =>
        _hardware = new HardwareState(machine, device, keeper, charge);

    public void SetHardware(Board machine, IDevice? device, ModeKeeper? keeper, ChargeKeeper? charge) =>
        _hardware = new HardwareState(machine, device, keeper, charge);

    public HelperReply Handle(HelperRequest request)
    {
        if (_hardware is not { } hardware)
            return HelperReply.Failure("The helper is still starting up.");

        var (machine, device, keeper, charge) = hardware;

        // On an unknown machine even a snapshot would read the wrong registers, so everything is refused.
        if (device is null)
            return HelperReply.NotSupported($"{machine}");

        var profile = device.Profile;

        try
        {
            switch (request)
            {
                case GetSnapshot:
                    // The only reply that also carries the device profile.
                    return HelperReply.Success(SnapshotFor(device, keeper)) with { Device = profile };

                case GetReadings:
                    return HelperReply.Success(device.Readings());

                case SetPerformance or SetFan or SetChargeLimit when DiscoveryWorker.IsMutatingFirmwareCheckRunning:
                    return HelperReply.Failure("A firmware check is running. Wait for it to finish before changing settings.");

                case SetPerformance(var mode, var manual, var custom) chosen:
                    if (!profile.Offers(mode))
                        return HelperReply.Failure($"This device has no {mode} mode.");

                    bool? onBattery = profile.BatteryCap is not null ? PowerSource.IsOnBattery() : null;
                    // A preset's own limits, if sent, are clamped where they are used: DeviceProfile.LimitsFor.
                    device.SetPerformance(mode, profile.ManualRange?.Clamp(manual) ?? manual, custom, onBattery);
                    keeper?.Remember(chosen, onBattery);
                    break;

                case SetFan(var mode):
                    if (!profile.Offers(mode))
                        return HelperReply.Failure($"This device has no {mode} fan setting.");

                    device.SetFan(mode);
                    break;

                case SetChargeLimit(var percent) chosen:
                    if (!profile.ChargeLimits.Contains(percent))
                        return HelperReply.Failure($"This device has no {percent} % charge limit.");

                    device.SetChargeLimit(percent);
                    charge?.Remember(chosen);
                    break;

                default:
                    return HelperReply.Failure($"Unknown request: {request.GetType().Name}");
            }

            return HelperReply.Success(SnapshotFor(device, keeper));
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
}
