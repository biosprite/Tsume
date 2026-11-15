using Tsume.Core.Ipc;
using Tsume.Hardware;

namespace Tsume.Helper;

/// <summary>
/// Turns one request into one reply. This is Tsume's whole privileged surface: an unelevated process can do
/// nothing that is not listed here. Anything outside the device's profile is refused before the device is called.
/// </summary>
/// <param name="machine">The board found at start-up.</param>
/// <param name="device">Null on a machine Tsume does not know.</param>
/// <param name="keeper">Null exactly when <paramref name="device"/> is.</param>
/// <param name="charge">Null exactly when <paramref name="device"/> is.</param>
public sealed class RequestHandler(Board machine, IDevice? device, ModeKeeper? keeper, ChargeKeeper? charge)
{
    public HelperReply Handle(HelperRequest request)
    {
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
                    return HelperReply.Success(device.Snapshot()) with { Device = profile };

                case GetReadings:
                    return HelperReply.Success(device.Readings());

                case SetPerformance(var mode, var manual, var custom) chosen:
                    if (!profile.Offers(mode))
                        return HelperReply.Failure($"This device has no {mode} mode.");

                    // A preset's own limits, if sent, are clamped where they are used: DeviceProfile.LimitsFor.
                    device.SetPerformance(mode, profile.ManualRange?.Clamp(manual) ?? manual, custom);
                    keeper?.Remember(chosen);
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

            return HelperReply.Success(device.Snapshot());
        }
        catch (Exception error)
        {
            Log.Write($"{request.GetType().Name} failed: {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }
}
