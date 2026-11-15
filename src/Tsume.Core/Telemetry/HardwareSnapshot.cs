using Tsume.Core.Cooling;
using Tsume.Core.Performance;

namespace Tsume.Core.Telemetry;

/// <summary>The machine's state at one instant, as the helper reads it.</summary>
public sealed record HardwareSnapshot
{
    public required PerformanceMode Mode { get; init; }
    public required PowerLimits Limits { get; init; }
    public required FanMode Fan { get; init; }

    /// <summary>
    /// The percent the battery stops charging at; 100 charges to full.
    /// Null when the machine has no such limit or the firmware did not answer.
    /// </summary>
    public int? ChargeLimit { get; init; }

    /// <summary>Processor temperature in °C from the embedded controller, or null when the firmware did not answer.</summary>
    public int? CpuTemperature { get; init; }

    /// <summary>Fan speed in RPM, or null when the firmware did not answer.</summary>
    public int? FanRpm { get; init; }

    /// <summary>Embedded-controller firmware string, e.g. "1T91EMS1.103".</summary>
    public string? FirmwareVersion { get; init; }
}
