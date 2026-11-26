namespace Tsume.Core.Telemetry;

/// <summary>
/// Temperature and fan readings, polled while the panel is open.
/// </summary>
/// <param name="CpuTemperature">Processor temperature in °C from the embedded controller, or null when the firmware did not answer.</param>
/// <param name="FanRpm">Fan speed in RPM from the tachometer, or null when the firmware did not answer.</param>
/// <param name="FanSpeed">Current fan duty in the firmware's curve units, or null when unavailable.</param>
public sealed record LiveReadings(int? CpuTemperature, int? FanRpm, int? FanSpeed = null);
