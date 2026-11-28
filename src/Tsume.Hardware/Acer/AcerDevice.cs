using System.Buffers.Binary;
using System.Diagnostics;
using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Performance;
using Tsume.Core.Telemetry;

namespace Tsume.Hardware.Acer;

/// <summary>
/// An Acer handheld (Predator Atlas 8): <see cref="IDevice"/> backed by EC HID (<c>1025:174B</c>, usage page
/// <c>0xFF05</c>, report <c>0xA0</c>) for performance modes and PL1/PL2 limits, and BIOS WMI (<c>AcerGamingFunction</c>
/// + <c>BatteryControl</c>) for fans, telemetry, and battery health charging.
/// </summary>
/// <remarks>
/// Custom fan curves use an EC firmware behavior verified in the F7V ITE EC image (<c>FUN_ram_0000f560</c> /
/// <c>FUN_ram_0000fa62</c>): while the fans remain in WMI Auto mode (<c>0x564</c> bits 4 &amp; 5 clear), WMI
/// <c>SetGamingFanSpeed</c> (<c>0x7D</c>/<c>0xCD</c>) writes an unclamped <c>0..255</c> duty straight into the ITE
/// PWM registers (<c>0xF01808</c>/<c>0xF01807</c>, period <c>0xFF</c>), allowing speeds both above and below the EC's
/// built-in auto tables. Because the EC's Auto PID loop drifts PWM by only <c>±1/255</c> every 300 ms (~1.3 %/s),
/// refreshing every 1.5 s holds the curve closely while remaining fail-safe if the helper stops.
/// </remarks>
public sealed class AcerDevice(
    IAcerEcHid ecHid,
    IAcerWmi wmi,
    AcerBoard board,
    bool enableFanLoop = true,
    TimeSpan? fanLoopInterval = null,
    Func<bool>? isMakerToolRunning = null) : IDevice
{
    /// <summary>PredatorSense processes that fight Tsume for modes and settings while active.</summary>
    private static readonly string[] PredatorSenseProcesses = ["Handheld.Desktop", "AcerAgentService"];

    /// <summary>
    /// Refresh cadence for the Auto-mode direct-PWM custom fan loop. At 1.5 s, the EC's 300 ms Auto PID loop drifts
    /// PWM by at most 5/255 (~2 %) between refreshes.
    /// </summary>
    public static readonly TimeSpan DefaultFanLoopInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>Cooling hysteresis (°C) before stepping down the target fan speed as CPU temperature falls.</summary>
    public const int CoolingHysteresisC = 2;

    // EC usage modes (TUGM / applied mode 0x554).
    public const byte ModeTurbo = 0;
    public const byte ModeBalanced = 1;
    public const byte ModeQuiet = 2;
    public const byte ModeManual = 3;

    // OC Profile index for Manual mode (bytes f1=PL1 at 0..1, f3=PL2 at 4..5).
    public const byte ManualOcProfile = 3;
    private const int Pl1Offset = 0;
    private const int Pl2Offset = 4;

    // WMI AcerGamingFunction constants for System Fan 1 (bit 1, ID 2) & System Fan 2 (bit 2, ID 3).
    public const uint FanMaskBoth = 0x0000_0006;
    public const ulong FanBehaviorAuto = 0x0014_0006;   // (1 << 18) | (1 << 20) | 0x06
    public const ulong FanBehaviorMax = 0x0028_0006;    // (2 << 18) | (2 << 20) | 0x06

    // GetGamingSysInfo inputs: (sensorIndex << 8) | 0x01.
    public const uint SensorCpuTemp = 0x0101;
    public const uint SensorFan1Rpm = 0x0401;
    public const uint SensorFan2Rpm = 0x0801;

    private readonly TimeSpan _fanLoopInterval = fanLoopInterval ?? DefaultFanLoopInterval;
    private readonly Lock _oneIntentionAtATime = new();
    private string? _biosVersion;
    private bool _biosVersionRead;
    private bool _disposed;

    // Software-driven custom fan curve state (guarded by _oneIntentionAtATime).
    private int[]? _activeCurve;
    private int[]? _chosenCurve;
    private int? _effectiveCelsius;
    private int? _lastAppliedPercent;
    private bool _standbyPaused;
    private Timer? _fanLoopTimer;

    /// <summary>
    /// Opens an Acer handheld when the board/system matches <see cref="AcerBoards"/> AND both the required
    /// EC HID power interface (<c>1025:174B</c>) and <c>AcerGamingFunction</c> WMI interface are present.
    /// </summary>
    public static IDevice? TryOpen(Board machine)
    {
        var known = AcerBoards.Find(machine);
        if (known is null) return null;

        var ecHid = AcerEcHid.TryOpen(known.EcHidVendorId, known.EcHidProductId, known.EcHidUsagePage);
        if (ecHid is null) return null;

        var wmi = AcerWmiBridge.TryOpen();
        if (wmi is null)
        {
            ecHid.Dispose();
            return null;
        }

        return new AcerDevice(ecHid, wmi, known);
    }

    public DeviceProfile Profile => board.Profile;

    public bool MakerToolIsRunning
    {
        get
        {
            if (isMakerToolRunning is not null) return isMakerToolRunning();

            foreach (var name in PredatorSenseProcesses)
            {
                var found = Process.GetProcessesByName(name);
                var any = found.Length > 0;
                foreach (var process in found) process.Dispose();
                if (any) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// All modes apply through EC Usage Mode 3 (Manual) + OC Profile 3 so Tsume's MSI Claw EX watt presets
    /// (<c>12/15</c>, <c>25/28</c>, <c>32/36 W</c>) and custom limits (with PL2 &gt;= PL1 + 2 W) are enforced
    /// directly via EC PECI rather than Acer's hard-coded native ceilings (<c>30/45</c>, <c>35/65</c>, <c>40/65 W</c>).
    /// </summary>
    public PerformanceMode HardwareModeFor(PerformanceMode mode, PowerLimits limits) => PerformanceMode.Manual;

    /// <summary>
    /// The EC preserves its mode byte across Modern Standby (<c>0xE4</c>/<c>0xE5</c>) without re-triggering PECI,
    /// and cold boot leaves <c>0x554 = 1</c> without raising <c>_Q30</c>; always re-apply on wake and start-up.
    /// </summary>
    public bool ReapplyOnResume => true;

    public void SetPerformance(PerformanceMode mode, PowerLimits manual, PowerLimits? custom = null, bool? onBattery = null,
        PowerLimits? customOnBattery = null)
    {
        var limits = Profile.LimitsFor(mode, manual, custom)
            ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "This device does not offer that mode.");

        lock (_oneIntentionAtATime)
        {
            var beforeMode = ecHid.GetSystemUsageMode();
            var beforeProfile = ecHid.GetOcProfile(ManualOcProfile);
            if (beforeProfile is not { Length: AcerEcHid.OcProfileFieldLength })
                throw new FirmwareException("The EC did not answer GetOCProfile(3).");

            // After an EC power loss, OC Profile 3 reads back all 0xFF; start from zeros rather than preserving 0xFFFF.
            var nextProfile = IsAllFf(beforeProfile)
                ? new byte[AcerEcHid.OcProfileFieldLength]
                : beforeProfile.ToArray();

            BinaryPrimitives.WriteUInt16LittleEndian(nextProfile.AsSpan(Pl1Offset, 2), (ushort)limits.Sustained);
            BinaryPrimitives.WriteUInt16LittleEndian(nextProfile.AsSpan(Pl2Offset, 2), (ushort)limits.Boost);

            // Verified Acer write order: SetOCProfile(3) first, then SetSystemUsageMode(3).
            if (!ecHid.SetOcProfile(ManualOcProfile, nextProfile))
                throw new FirmwareException($"The EC refused to write OC Profile 3 ({limits.Sustained}/{limits.Boost} W).");

            if (!ecHid.SetSystemUsageMode(ModeManual))
            {
                // Best-effort rollback of profile and mode before surfacing the failure.
                if (!IsAllFf(beforeProfile))
                    ecHid.SetOcProfile(ManualOcProfile, beforeProfile);
                if (beforeMode is <= AcerEcHid.MaxUsageMode)
                    ecHid.SetSystemUsageMode(beforeMode.Value);

                throw new FirmwareException("The EC refused to switch to Manual operating mode (mode 3).");
            }

            // EC command 0x83 (from _Q30) reloads the auto fan table and may pulse PWM; immediately re-apply our custom duty.
            if (_activeCurve is { } active && !_standbyPaused && !MakerToolIsRunning)
                ApplyFanCurveStepLocked(active, forceWrite: true, resetHysteresis: true);
        }
    }

    public (PerformanceMode Mode, PowerLimits Limits) Performance()
    {
        lock (_oneIntentionAtATime)
        {
            return ReadPerformance();
        }
    }

    public void SetFan(FanMode mode)
    {
        lock (_oneIntentionAtATime)
        {
            switch (mode)
            {
                case FanMode.Auto:
                    ClearActiveCurveLocked();
                    if (!wmi.SetGamingFanBehavior(FanBehaviorAuto))
                        throw new FirmwareException("The BIOS refused to switch fans to Auto.");
                    break;

                case FanMode.Max:
                    ClearActiveCurveLocked();
                    if (!wmi.SetGamingFanBehavior(FanBehaviorMax))
                        throw new FirmwareException("The BIOS refused to switch fans to Max.");
                    break;

                case FanMode.Custom:
                    throw new NotSupportedException("Custom fans need a curve: set one with SetFanCurve.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }
    }

    public FanMode Fan()
    {
        lock (_oneIntentionAtATime)
        {
            return ReadFanModeLocked();
        }
    }

    public FanCurveState? FanCurve()
    {
        if (Profile.FanCurve is not { CustomTemperatures: { Count: > 0 } temps } limits) return null;

        lock (_oneIntentionAtATime)
        {
            var stock = StockCurveFor(limits, temps);
            IReadOnlyList<int> speeds = _activeCurve is { Length: var n } && n == temps.Count
                ? [.. _activeCurve]
                : _chosenCurve is { Length: var m } && m == temps.Count && ReadFanModeLocked() == FanMode.Max
                    ? [.. _chosenCurve]
                    : stock;

            return new()
            {
                Temperatures = temps,
                Speeds = speeds,
                Stock = stock,
                StockKnown = true,
            };
        }
    }

    public IReadOnlyList<int> SetFanCurve(IReadOnlyList<int> speeds)
    {
        if (Profile.FanCurve is not { CustomTemperatures: { Count: > 0 } temps } limits)
            throw new NotSupportedException("This device takes no custom fan curve.");

        var stock = StockCurveFor(limits, temps);
        var clamped = limits.Clamp(temps, speeds, stock);

        lock (_oneIntentionAtATime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var previousCurve = _activeCurve;
            var previousEffectiveTemp = _effectiveCelsius;
            var previousApplied = _lastAppliedPercent;

            // Keep the EC in WMI Auto mode (0x564 bits 4 & 5 == 0) so SetGamingFanSpeed (0x7D/0xCD) writes
            // directly to the hardware PWM registers (0xF01808/0xF01807) instead of acting as an additive boost.
            if (!wmi.SetGamingFanBehavior(FanBehaviorAuto))
                throw new FirmwareException("The BIOS refused to prepare fans for custom curve control.");

            if (!ApplyFanCurveStepLocked(clamped, forceWrite: true, resetHysteresis: true))
            {
                _activeCurve = previousCurve;
                _effectiveCelsius = previousEffectiveTemp;
                _lastAppliedPercent = previousApplied;
                if (_activeCurve is not null) ArmTimerLocked();
                throw new FirmwareException("The BIOS refused to write the custom fan speed.");
            }

            _activeCurve = [.. clamped];
            ArmTimerLocked();
            return clamped;
        }
    }

    public bool HoldsFanCurve(IReadOnlyList<int> speeds)
    {
        if (Profile.FanCurve is not { CustomTemperatures: { Count: > 0 } temps } || speeds.Count != temps.Count)
            return false;

        lock (_oneIntentionAtATime)
        {
            if (_activeCurve is null || !_activeCurve.SequenceEqual(speeds))
                return false;

            if (ReadHardwareFanBehavior() is { } hw && (hw.Fan1 != 1 || hw.Fan2 != 1))
            {
                ClearActiveCurveLocked();
                return false;
            }

            return true;
        }
    }

    public void NoteChosenFanCurve(IReadOnlyList<int>? speeds)
    {
        lock (_oneIntentionAtATime)
        {
            _chosenCurve = speeds?.ToArray();
        }
    }

    public void OnSystemPowerState(bool suspended)
    {
        lock (_oneIntentionAtATime)
        {
            if (_disposed || _standbyPaused == suspended) return;

            _standbyPaused = suspended;
            if (suspended)
            {
                StopTimerLocked();
                _effectiveCelsius = null;
                return;
            }

            if (_activeCurve is { } active)
            {
                if (!MakerToolIsRunning)
                    ApplyFanCurveStepLocked(active, forceWrite: true, resetHysteresis: true);
                ArmTimerLocked();
            }
        }
    }

    /// <summary>
    /// Executes one custom-fan-curve evaluation and PWM refresh step synchronously. Used by unit tests to verify
    /// interpolation, hysteresis, idle skipping, and standby behavior without relying on wall-clock timers.
    /// </summary>
    internal bool TickFanCurve()
    {
        lock (_oneIntentionAtATime)
        {
            if (_disposed || _standbyPaused || _activeCurve is not { } curve)
                return false;

            if (MakerToolIsRunning)
                return false;

            return ApplyFanCurveStepLocked(curve, forceWrite: false, resetHysteresis: false);
        }
    }

    /// <summary>Converts a 0–100 % curve speed to the 0–255 ITE hardware PWM duty byte (CTR = 0xFF).</summary>
    public static byte PercentToPwmDuty(int percent) =>
        (byte)Math.Clamp((int)Math.Round(percent * 255.0 / 100.0, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>Builds the <c>SetGamingFanSpeed</c> <c>gmInput</c> payload: <c>(duty &lt;&lt; 8) | fanId</c>.</summary>
    public static ulong FanSpeedInput(byte fanId, byte duty) => ((ulong)duty << 8) | fanId;

    public void SetChargeLimit(int percent)
    {
        if (!Profile.ChargeLimits.Contains(percent))
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "This device does not offer that charge limit.");

        lock (_oneIntentionAtATime)
        {
            // Clear any active EC HID charge boundary first (as Acer Care Center does) so the two mechanisms never conflict.
            ecHid.SetBatteryBoundary(enabled: false, upperPercent: 80, lowerPercent: 50);

            var enableHealthLimit = percent < 100;
            if (!wmi.SetBatteryHealthControl(enableHealthLimit))
                throw new FirmwareException($"The BIOS refused to set the battery charge limit to {percent} %.");
        }
    }

    public int? ChargeLimit()
    {
        lock (_oneIntentionAtATime)
        {
            return ReadChargeLimit();
        }
    }

    public HardwareSnapshot Snapshot()
    {
        lock (_oneIntentionAtATime)
        {
            var (mode, limits) = ReadPerformance();
            var fanMode = ReadFanModeLocked();
            return new()
            {
                Mode = mode,
                Limits = limits,
                Fan = fanMode,
                ChargeLimit = Profile.ChargeLimits.Count > 0 ? ReadChargeLimit() : null,
                CpuTemperature = ReadCpuTemperature(),
                FanRpm = ReadFanRpm(),
                FanSpeed = ReportedFanSpeedLocked(fanMode),
                FirmwareVersion = ReadBiosVersionCached(),
            };
        }
    }

    public LiveReadings Readings()
    {
        lock (_oneIntentionAtATime)
        {
            int? fanSpeed = _activeCurve is not null ? _lastAppliedPercent : null;
            return new(ReadCpuTemperature(), ReadFanRpm(), fanSpeed);
        }
    }

    private (PerformanceMode Mode, PowerLimits Limits) ReadPerformance()
    {
        var rawMode = ecHid.GetSystemUsageMode()
            ?? throw new FirmwareException("The EC did not answer GetSystemUsageMode.");

        if (rawMode == ModeManual)
        {
            var profile = ecHid.GetOcProfile(ManualOcProfile);
            if (profile is { Length: AcerEcHid.OcProfileFieldLength } && !IsAllFf(profile))
            {
                var pl1 = BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(Pl1Offset, 2));
                var pl2 = BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(Pl2Offset, 2));
                if (pl1 < 0xFFFF && pl2 < 0xFFFF)
                    return (PerformanceMode.Manual, new PowerLimits(pl1, pl2));
            }

            return (PerformanceMode.Manual, default);
        }

        // Native EC modes: Quiet (2) = 25/45 W, Balanced (1) = 30/65 W, Turbo (0) = 35/65 W.
        return rawMode switch
        {
            ModeQuiet    => (PerformanceMode.Endurance,   new PowerLimits(Sustained: 25, Boost: 45)),
            ModeBalanced => (PerformanceMode.Balanced,    new PowerLimits(Sustained: 30, Boost: 65)),
            ModeTurbo    => (PerformanceMode.Performance, new PowerLimits(Sustained: 35, Boost: 65)),
            _            => (PerformanceMode.Balanced,    default),
        };
    }

    private (int Fan1, int Fan2)? ReadHardwareFanBehavior()
    {
        if (wmi.GetGamingFanBehavior(FanMaskBoth) is not { } raw)
            return null;

        // Fan bit n occupies bits (8 + 2n)..(9 + 2n): System Fan 1 = bits 10..11, System Fan 2 = bits 12..13.
        return ((int)((raw >> 10) & 0x3), (int)((raw >> 12) & 0x3));
    }

    private FanMode ReadFanModeLocked()
    {
        if (ReadHardwareFanBehavior() is { } hw)
        {
            if (hw.Fan1 == 2 || hw.Fan2 == 2)
            {
                ClearActiveCurveLocked();
                return FanMode.Max;
            }

            if (hw.Fan1 == 3 || hw.Fan2 == 3)
            {
                ClearActiveCurveLocked();
                return FanMode.Custom;
            }
        }

        return _activeCurve is not null ? FanMode.Custom : FanMode.Auto;
    }

    private int? ReportedFanSpeedLocked(FanMode fanMode) => fanMode switch
    {
        FanMode.Custom when _activeCurve is not null => _lastAppliedPercent,
        FanMode.Max => Profile.FanCurve?.MaxSpeed,
        _ => null,
    };

    private bool ApplyFanCurveStepLocked(IReadOnlyList<int> curve, bool forceWrite, bool resetHysteresis)
    {
        if (Profile.FanCurve is not { CustomTemperatures: { Count: > 0 } temps } || temps.Count != curve.Count)
            return false;

        var celsius = ReadCpuTemperature();
        if (celsius is not { } temp)
        {
            // During Modern Standby (EC 0xE4), CTMP (0x558) is zeroed and fans are shut off by the EC.
            // Skip writing so Modern Standby emits zero SMIs and transient sensor errors leave EC Auto safely active.
            _effectiveCelsius = null;
            return true;
        }

        if (resetHysteresis || _effectiveCelsius is not { } prev || temp >= prev || temp <= prev - CoolingHysteresisC)
            _effectiveCelsius = temp;

        var sampled = FanCurveLimits.Sample(temps, curve, [_effectiveCelsius.Value])[0];
        var targetPercent = Math.Clamp(sampled, 0, Profile.FanCurve.MaxSpeed);

        // When cool and already stopped at 0 RPM, the EC's auto level is 0 (0 RPM target) with zero drift to fight.
        // Skip periodic SMIs until either the target rises above 0 or the EC begins spinning a fan.
        if (!forceWrite && targetPercent == 0 && _lastAppliedPercent == 0 && ReadFanRpm() is 0)
            return true;

        var duty = PercentToPwmDuty(targetPercent);
        foreach (var fanId in board.FanIds)
        {
            if (!wmi.SetGamingFanSpeed(FanSpeedInput(fanId, duty)))
                return false;
        }

        _lastAppliedPercent = targetPercent;
        return true;
    }

    private void OnFanLoopTick(object? state)
    {
        lock (_oneIntentionAtATime)
        {
            if (_disposed || _standbyPaused || _activeCurve is not { } curve)
                return;

            if (!MakerToolIsRunning)
                ApplyFanCurveStepLocked(curve, forceWrite: false, resetHysteresis: false);

            ArmTimerLocked();
        }
    }

    private void ArmTimerLocked()
    {
        if (!enableFanLoop || _disposed || _standbyPaused || _activeCurve is null)
            return;

        _fanLoopTimer ??= new Timer(OnFanLoopTick, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _fanLoopTimer.Change(_fanLoopInterval, Timeout.InfiniteTimeSpan);
    }

    private void StopTimerLocked() =>
        _fanLoopTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void ClearActiveCurveLocked()
    {
        _activeCurve = null;
        _effectiveCelsius = null;
        _lastAppliedPercent = null;
        StopTimerLocked();
    }

    private int[] StockCurveFor(FanCurveLimits limits, IReadOnlyList<int> temps)
    {
        if (board.StockFanSpeeds is { Count: var count } stock && count == temps.Count)
            return [.. limits.Clamp(temps, stock)];

        return [.. limits.Clamp(temps, new int[temps.Count])];
    }

    private int? ReadChargeLimit() => wmi.GetBatteryHealthControl() switch
    {
        true  => 80,
        false => 100,
        null  => null,
    };

    private int? ReadCpuTemperature()
    {
        if (wmi.GetGamingSysInfo(SensorCpuTemp) is not { } raw) return null;
        var celsius = (int)((raw >> 8) & 0xFFFF);
        return celsius is > 0 and < 120 ? celsius : null;
    }

    private int? ReadFanRpm()
    {
        int? fan1 = wmi.GetGamingSysInfo(SensorFan1Rpm) is { } r1 ? (int)((r1 >> 8) & 0xFFFF) : null;
        int? fan2 = wmi.GetGamingSysInfo(SensorFan2Rpm) is { } r2 ? (int)((r2 >> 8) & 0xFFFF) : null;

        if (fan1 is { } a && fan2 is { } b) return Math.Max(a, b);
        return fan1 ?? fan2;
    }

    private string? ReadBiosVersionCached()
    {
        if (_biosVersionRead) return _biosVersion;
        _biosVersionRead = true;
        return _biosVersion = wmi.ReadBiosVersion();
    }

    private static bool IsAllFf(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            if (b != 0xFF) return false;
        return bytes.Length > 0;
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_oneIntentionAtATime)
        {
            if (_disposed) return;
            _disposed = true;
            _activeCurve = null;
            timer = _fanLoopTimer;
            _fanLoopTimer = null;
        }

        timer?.Dispose();
        ecHid.Dispose();
        wmi.Dispose();
    }
}
