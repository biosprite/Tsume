namespace Tsume.Core.Telemetry;

/// <summary>
/// Temperature and fan speed, polled while the panel is open. Costs two firmware calls;
/// a full <see cref="HardwareSnapshot"/> costs eight.
/// </summary>
/// <param name="CpuTemperature">Processor temperature in °C from the embedded controller, or null when the firmware did not answer.</param>
/// <param name="FanRpm">Fan speed in RPM, or null when the firmware did not answer.</param>
public sealed record LiveReadings(int? CpuTemperature, int? FanRpm);
