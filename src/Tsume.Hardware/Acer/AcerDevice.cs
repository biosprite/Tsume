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
public sealed class AcerDevice(IAcerEcHid ecHid, IAcerWmi wmi, AcerBoard board) : IDevice
{
    /// <summary>PredatorSense processes that fight Tsume for modes and settings while active.</summary>
    private static readonly string[] PredatorSenseProcesses = ["Handheld.Desktop", "AcerAgentService"];

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

    private readonly Lock _oneIntentionAtATime = new();
    private string? _biosVersion;
    private bool _biosVersionRead;

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
                    if (!wmi.SetGamingFanBehavior(FanBehaviorAuto))
                        throw new FirmwareException("The BIOS refused to switch fans to Auto.");
                    break;

                case FanMode.Max:
                    if (!wmi.SetGamingFanBehavior(FanBehaviorMax))
                        throw new FirmwareException("The BIOS refused to switch fans to Max.");
                    break;

                case FanMode.Custom:
                    throw new NotSupportedException("Acer Predator Atlas 8 does not expose a writable firmware fan curve.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }
    }

    public FanMode Fan()
    {
        lock (_oneIntentionAtATime)
        {
            return ReadFanMode();
        }
    }

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
            return new()
            {
                Mode = mode,
                Limits = limits,
                Fan = ReadFanMode(),
                ChargeLimit = Profile.ChargeLimits.Count > 0 ? ReadChargeLimit() : null,
                CpuTemperature = ReadCpuTemperature(),
                FanRpm = ReadFanRpm(),
                FirmwareVersion = ReadBiosVersionCached(),
            };
        }
    }

    public LiveReadings Readings()
    {
        lock (_oneIntentionAtATime)
        {
            return new(ReadCpuTemperature(), ReadFanRpm());
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

    private FanMode ReadFanMode()
    {
        if (wmi.GetGamingFanBehavior(FanMaskBoth) is not { } raw)
            return FanMode.Auto;

        // Fan bit n occupies bits (8 + 2n)..(9 + 2n): System Fan 1 = bits 10..11, System Fan 2 = bits 12..13.
        var fan1 = (int)((raw >> 10) & 0x3);
        var fan2 = (int)((raw >> 12) & 0x3);

        if (fan1 == 2 || fan2 == 2) return FanMode.Max;
        if (fan1 == 3 || fan2 == 3) return FanMode.Custom;
        return FanMode.Auto;
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
        ecHid.Dispose();
        wmi.Dispose();
    }
}
