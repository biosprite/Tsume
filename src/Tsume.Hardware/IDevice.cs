using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Performance;
using Tsume.Core.Telemetry;

namespace Tsume.Hardware;

/// <summary>
/// One supported machine as the helper sees it: what it can do, and the operations to do it. How they reach
/// the firmware is each implementation's concern. To add a machine: another MSI board is one entry in
/// Msi/MsiBoards.cs; another maker implements this and adds a line to DeviceCatalog.
/// </summary>
public interface IDevice : IDisposable
{
    /// <summary>What this machine offers. The helper refuses any request outside it before calling the device.</summary>
    DeviceProfile Profile { get; }

    /// <summary>
    /// True while the maker's own control program is running. It keeps its own mode, so the helper does not
    /// restore a remembered one meanwhile.
    /// </summary>
    bool MakerToolIsRunning { get; }

    /// <summary>
    /// Applies a mode. <paramref name="manual"/> matters only to a mode set by hand;
    /// <paramref name="custom"/>, when given, replaces a preset mode's own limits —
    /// see <see cref="DeviceProfile.LimitsFor"/>.
    /// </summary>
    void SetPerformance(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null);

    /// <summary>The mode and limits in force right now. Cheap: asked on every wake.</summary>
    (PerformanceMode Mode, PowerLimits Limits) Performance();

    void SetFan(FanMode mode);

    /// <summary>Stops charging at this level, which must be one of the profile's <see cref="DeviceProfile.ChargeLimits"/>.</summary>
    void SetChargeLimit(int percent);

    /// <summary>The level charging stops at right now, or null if the firmware holds none. Cheap: asked on every wake.</summary>
    int? ChargeLimit();

    /// <summary>Every setting and reading at once.</summary>
    HardwareSnapshot Snapshot();

    /// <summary>Temperature and fan speed only. Asked once a second while the panel is open.</summary>
    LiveReadings Readings();
}

/// <summary>The firmware refused a call or did not answer.</summary>
public sealed class FirmwareException(string message) : Exception(message);
