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
    /// <paramref name="onBattery"/> lets a caller pass a power-source reading it already took so comparison and
    /// write observe the same state; null queries the current power source when the board has a battery cap.
    /// </summary>
    void SetPerformance(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null, bool? onBattery = null);

    /// <summary>
    /// The limits <see cref="SetPerformance"/> would write right now for these arguments — the one answer every
    /// caller that compares the firmware with a choice must use, or it would see a difference that is not there.
    /// Null when the machine does not offer the mode. Defaults to <see cref="DeviceProfile.LimitsFor"/>; a device
    /// may lower it (e.g. to <see cref="DeviceProfile.BatteryCap"/> while on battery).
    /// </summary>
    PowerLimits? LimitsToApply(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null, bool? onBattery = null) =>
        Profile.LimitsFor(mode, manual, custom);

    /// <summary>The mode and limits in force right now. Cheap: asked on every wake.</summary>
    (PerformanceMode Mode, PowerLimits Limits) Performance();

    /// <summary>
    /// The mode the firmware register itself holds when <paramref name="mode"/> is applied with
    /// <paramref name="limits"/> (the limits <see cref="LimitsToApply"/> resolves for it).
    /// Defaults to <paramref name="mode"/>; boards whose EC ignores power-limit registers in
    /// built-in shift modes return <see cref="PerformanceMode.Manual"/> instead, and some boards
    /// pick a shift mode from the limits themselves.
    /// </summary>
    PerformanceMode HardwareModeFor(PerformanceMode mode, PowerLimits limits) => mode;

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
