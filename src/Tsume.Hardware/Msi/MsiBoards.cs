using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Performance;

namespace Tsume.Hardware.Msi;

/// <summary>
/// The MSI boards Tsume knows (docs/hardware/firmware.md); Tsume writes to no others. Only MS-1T91 has been
/// probed, write-checked and mode-checked on a real unit (<see cref="MsiBoard.CheckedOnDevice"/>). The other
/// maps have not been tested on real hardware, and writes to them are guarded by the WMI interface check in
/// <see cref="MsiAcpiBridge"/>.
/// </summary>
public static class MsiBoards
{
    /// <summary>
    /// Claw 8 EX AI+ (CG3EM). Intel: the limits are PL1 and PL2. Presets are tuned for the Core G3 Extreme
    /// (MSI Center M's own are 15/20, 25/37 and 30/37); MSI's names for the four shift codes (0x02, 0x01,
    /// 0x04, 0x06) are ECO, Green, Sport and Manual.
    /// </summary>
    public static readonly MsiBoard Claw8 = new()
    {
        Model = "MS-1T91",
        CheckedOnDevice = true,

        Registers = new()
        {
            PowerLimitSustained = 0x50,
            PowerLimitBoost = 0x51,
            CpuTemperature = 0x68,
            CoolerBoost = 0x98,
            ShiftMode = 0xD2,
            FanControl = 0xD4,
            ChargeLimit = 0xD7,
        },

        Shift = new()
        {
            Codes = new Dictionary<PerformanceMode, byte>
            {
                [PerformanceMode.Endurance] = 0x02,
                [PerformanceMode.Balanced] = 0x01,
                [PerformanceMode.Performance] = 0x04,
                [PerformanceMode.Manual] = 0x06,
            },
            Otherwise = PerformanceMode.Balanced,   // including MSI's "Comfort", 0x00
        },

        PowerFloor = 8,

        Profile = new()
        {
            Name = "MSI Claw 8 EX AI+ (MS-1T91)",
            Modes =
            [
                new(PerformanceMode.Endurance,   new PowerLimits(Sustained: 12, Boost: 15)),
                new(PerformanceMode.Balanced,    new PowerLimits(Sustained: 25, Boost: 28)),
                new(PerformanceMode.Performance, new PowerLimits(Sustained: 32, Boost: 36)),
                new(PerformanceMode.Manual),
            ],
            ManualRange = new PowerRange(Min: 8, MaxSustained: 35, MaxBoost: 45),
            ManualDefault = new PowerLimits(Sustained: 25, Boost: 28),
            FanModes = [FanMode.Auto, FanMode.Max],
            ChargeLimits = [80, 90, 100],   // the owner's choice; MSI Center M's own are 60, 80 and 100
            ChargeResumeGap = 10,
            TempWarnC = 85,
            TempHotC = 95,
            Controls = new()
            {
                HasGameBarButton = true,
                GameBarNativeWinG = true,
                GameBarTriggers = [new KeyTrigger(VirtualKey: 0x47, Win: true)],   // Win+G; older Claw BIOSes may also need F15/F16
                HasOemButton = true,
                HasMsiCenterConflict = true,
                OemButtonWmiCode = 0x29,
                GameBarWmiCode = 0x58,
                BackButtons = new()
                {
                    Protocol = BackButtonsProfile.MsiHidV1,
                    VendorId = 0x0DB0,
                    ProductIds = [0x1901, 0x1902],
                    ProfileLength = 1478,
                    BlockLength = 8,
                    MaxSimultaneousKeys = 3,
                    Buttons =
                    [
                        new("M1", "M1", "Left back button", Offset: 186),
                        new("M2", "M2", "Right back button", Offset: 355),
                    ],
                },
            },
        },
    };

    /// <summary>
    /// Claw 8 AI+ (A2VM, Intel Core Ultra 7 258V / Lunar Lake). Uses the same MSI EC register map as the EX,
    /// except Manual shift mode uses 0x04 (0xC4, shared with Sport/Performance) instead of EX's 0x06 (0xC6).
    /// Power ceilings are 30 W PL1 / 37 W PL2 with at least 1 W boost headroom (PL2 &gt;= PL1 + 1) to avoid
    /// Lunar Lake's PL1 == PL2 throttling quirk.
    /// </summary>
    public static readonly MsiBoard Claw8AiPlus = new()
    {
        Model = "MS-1T52",

        // MSI Center M never runs PL1 = 8 W under 0xC4: in Manual with PL1 == 8 it switches to its Super
        // Battery scenario, whose shift code is 0x02 / 0xC2.
        SustainedShift = new(SustainedWatts: 8, PerformanceMode.Endurance),

        Registers = new()
        {
            PowerLimitSustained = 0x50,
            PowerLimitBoost = 0x51,
            CpuTemperature = 0x68,
            CoolerBoost = 0x98,
            ShiftMode = 0xD2,
            FanControl = 0xD4,
            ChargeLimit = 0xD7,
        },

        Shift = new()
        {
            Codes = new Dictionary<PerformanceMode, byte>
            {
                [PerformanceMode.Endurance] = 0x02,
                [PerformanceMode.Balanced] = 0x01,
                [PerformanceMode.Performance] = 0x04,
                [PerformanceMode.Manual] = 0x04,
            },
            Otherwise = PerformanceMode.Balanced,   // including MSI's "Comfort", 0x00
        },

        PowerFloor = 8,

        Profile = new()
        {
            Name = "MSI Claw 8 AI+ (MS-1T52)",
            Modes =
            [
                new(PerformanceMode.Endurance,   new PowerLimits(Sustained: 8,  Boost: 12)),
                new(PerformanceMode.Balanced,    new PowerLimits(Sustained: 17, Boost: 20)),
                new(PerformanceMode.Performance, new PowerLimits(Sustained: 30, Boost: 37)),
                new(PerformanceMode.Manual),
            ],
            ManualRange = new PowerRange(Min: 8, MaxSustained: 30, MaxBoost: 37, MinBoostHeadroom: 1),
            ManualDefault = new PowerLimits(Sustained: 17, Boost: 20),
            FanModes = [FanMode.Auto, FanMode.Max],
            ChargeLimits = [80, 90, 100],
            ChargeResumeGap = 10,
            TempWarnC = 85,
            TempHotC = 95,
            Controls = new()
            {
                HasGameBarButton = true,
                GameBarNativeWinG = true,
                GameBarTriggers =
                [
                    new KeyTrigger(VirtualKey: 0x47, Win: true),   // Win+G (BIOS 10F+)
                    new KeyTrigger(VirtualKey: 0x7F),              // F16 (older Claw 8 AI+ BIOSes)
                ],
                HasOemButton = true,
                HasMsiCenterConflict = true,
                OemButtonWmiCode = 0x29,
                GameBarWmiCode = 0x58,
                BackButtons = new()
                {
                    Protocol = BackButtonsProfile.MsiHidV1,
                    VendorId = 0x0DB0,
                    ProductIds = [0x1901, 0x1902],
                    ProfileLength = 1478,
                    BlockLength = 8,
                    MaxSimultaneousKeys = 3,
                    // The legacy layout is not in MSI Center M (it has one layout); it exists per the Linux
                    // kernel hid-msi driver. This cutoff is unconfirmed.
                    MinFirmwareVersion = 0x0217,
                    Buttons =
                    [
                        new("M1", "M1", "Left back button", Offset: 186),
                        new("M2", "M2", "Right back button", Offset: 355),
                    ],
                },
            },
        },
    };

    /// <summary>
    /// Claw 7 AI+ / A7 (A2VM, Intel Core Ultra 7 258V / Lunar Lake, 7" 1080p). Identical to the Claw 8 AI+
    /// (MS-1T52) in MSI Center M's power, shift (Manual = 0x04 / 0xC4), fan and battery handling, and Tsume
    /// uses the same PL1/PL2 limits (8–30 W / 8–37 W, PL2 &gt;= PL1 + 1) and 2.xx controller firmware.
    /// That its WMI interface and EC registers match MS-1T52 is assumed, not tested.
    /// </summary>
    public static readonly MsiBoard Claw7AiPlus = new()
    {
        Model = "MS-1T42",

        // As on MS-1T52: MSI Center M uses its Super Battery shift code 0x02 / 0xC2 whenever PL1 == 8.
        SustainedShift = new(SustainedWatts: 8, PerformanceMode.Endurance),

        Registers = new()
        {
            PowerLimitSustained = 0x50,
            PowerLimitBoost = 0x51,
            CpuTemperature = 0x68,
            CoolerBoost = 0x98,
            ShiftMode = 0xD2,
            FanControl = 0xD4,
            ChargeLimit = 0xD7,
        },

        Shift = new()
        {
            Codes = new Dictionary<PerformanceMode, byte>
            {
                [PerformanceMode.Endurance] = 0x02,
                [PerformanceMode.Balanced] = 0x01,
                [PerformanceMode.Performance] = 0x04,
                [PerformanceMode.Manual] = 0x04,
            },
            Otherwise = PerformanceMode.Balanced,   // including MSI's "Comfort", 0x00
        },

        PowerFloor = 8,

        Profile = new()
        {
            Name = "MSI Claw 7 AI+ (MS-1T42)",
            Modes =
            [
                new(PerformanceMode.Endurance,   new PowerLimits(Sustained: 8,  Boost: 12)),
                new(PerformanceMode.Balanced,    new PowerLimits(Sustained: 17, Boost: 20)),
                new(PerformanceMode.Performance, new PowerLimits(Sustained: 30, Boost: 37)),
                new(PerformanceMode.Manual),
            ],
            ManualRange = new PowerRange(Min: 8, MaxSustained: 30, MaxBoost: 37, MinBoostHeadroom: 1),
            ManualDefault = new PowerLimits(Sustained: 17, Boost: 20),
            FanModes = [FanMode.Auto, FanMode.Max],
            ChargeLimits = [80, 90, 100],
            ChargeResumeGap = 10,
            TempWarnC = 85,
            TempHotC = 95,
            Controls = new()
            {
                HasGameBarButton = true,
                GameBarNativeWinG = true,
                GameBarTriggers =
                [
                    new KeyTrigger(VirtualKey: 0x47, Win: true),   // Win+G (BIOS 10F+)
                    new KeyTrigger(VirtualKey: 0x7F),              // F16 (older A2VM BIOSes)
                ],
                HasOemButton = true,
                HasMsiCenterConflict = true,
                OemButtonWmiCode = 0x29,
                GameBarWmiCode = 0x58,
                BackButtons = new()
                {
                    Protocol = BackButtonsProfile.MsiHidV1,
                    VendorId = 0x0DB0,
                    ProductIds = [0x1901, 0x1902],
                    ProfileLength = 1478,
                    BlockLength = 8,
                    MaxSimultaneousKeys = 3,
                    // 2.xx branch. The legacy layout is not in MSI Center M (it has one layout); it exists per
                    // the Linux kernel hid-msi driver. This cutoff is unconfirmed.
                    MinFirmwareVersion = 0x0217,
                    Buttons =
                    [
                        new("M1", "M1", "Left back button", Offset: 186),
                        new("M2", "M2", "Right back button", Offset: 355),
                    ],
                },
            },
        },
    };

    /// <summary>
    /// Claw A1M (Intel Core Ultra 5 135H / Ultra 7 155H Meteor Lake, 7" 1080p). Manual shift mode 0x04 (0xC4),
    /// PowerFloor 15 W, and 1.xx controller firmware. That it has the same MSI_ACPI WMI bridge and EC register
    /// map is assumed, not tested. MSI's own Manual sliders are 20–40 W for both PL1 and
    /// PL2 on AC and 20–35 W on battery, and its presets differ on AC and battery
    /// (Performance 43/45 W on AC, 35/35 W on battery), re-applied on every AC/battery change.
    /// Rule: on AC, Manual goes as far as the highest MSI itself applies on each tier: its Performance preset,
    /// 43/45 W (its Manual sliders stop at 40 W). Performance is that preset. On battery everything is held to
    /// MSI's battery limit, the battery cap of 35 W on both tiers: its battery sliders' end and its Performance
    /// preset on battery.
    /// </summary>
    public static readonly MsiBoard ClawA1M = new()
    {
        Model = "MS-1T41",

        Registers = new()
        {
            PowerLimitSustained = 0x50,
            PowerLimitBoost = 0x51,
            CpuTemperature = 0x68,
            CoolerBoost = 0x98,
            ShiftMode = 0xD2,
            FanControl = 0xD4,
            ChargeLimit = 0xD7,
        },

        Shift = new()
        {
            Codes = new Dictionary<PerformanceMode, byte>
            {
                [PerformanceMode.Endurance] = 0x02,
                [PerformanceMode.Balanced] = 0x01,
                [PerformanceMode.Performance] = 0x04,
                [PerformanceMode.Manual] = 0x04,
            },
            Otherwise = PerformanceMode.Balanced,   // including MSI's "Comfort", 0x00
        },

        PowerFloor = 15,

        Profile = new()
        {
            Name = "MSI Claw A1M (MS-1T41)",
            Modes =
            [
                new(PerformanceMode.Endurance,   new PowerLimits(Sustained: 20, Boost: 20)),
                new(PerformanceMode.Balanced,    new PowerLimits(Sustained: 30, Boost: 35)),
                new(PerformanceMode.Performance, new PowerLimits(Sustained: 43, Boost: 45)),
                new(PerformanceMode.Manual),
            ],
            ManualRange = new PowerRange(Min: 20, MaxSustained: 43, MaxBoost: 45),
            ManualDefault = new PowerLimits(Sustained: 30, Boost: 35),
            BatteryCap = new PowerLimits(Sustained: 35, Boost: 35),
            FanModes = [FanMode.Auto, FanMode.Max],
            ChargeLimits = [80, 90, 100],
            ChargeResumeGap = 10,
            TempWarnC = 85,
            TempHotC = 95,
            Controls = new()
            {
                HasGameBarButton = true,
                GameBarNativeWinG = true,
                GameBarTriggers =
                [
                    new KeyTrigger(VirtualKey: 0x47, Win: true),   // Win+G (newer A1M BIOSes)
                    new KeyTrigger(VirtualKey: 0x7E),              // F15 (older A1M BIOSes)
                    new KeyTrigger(VirtualKey: 0x7F),              // F16 (older A1M BIOSes)
                ],
                HasOemButton = true,
                HasMsiCenterConflict = true,
                OemButtonWmiCode = 0x29,
                GameBarWmiCode = 0x58,
                BackButtons = new()
                {
                    Protocol = BackButtonsProfile.MsiHidV1,
                    VendorId = 0x0DB0,
                    ProductIds = [0x1901, 0x1902],
                    ProfileLength = 1478,
                    BlockLength = 8,
                    MaxSimultaneousKeys = 3,
                    // 1.xx branch. The legacy layout is not in MSI Center M (it has one layout); it exists per
                    // the Linux kernel hid-msi driver. This cutoff is unconfirmed.
                    MinFirmwareVersion = 0x0164,
                    Buttons =
                    [
                        new("M1", "M1", "Left back button", Offset: 186),
                        new("M2", "M2", "Right back button", Offset: 355),
                    ],
                },
            },
        },
    };

    /// <summary>
    /// Claw A8 (BZ2EM, AMD Ryzen Z2 Extreme). Uses the same MSI_ACPI WMI bridge and writes 3-tier AMD power
    /// limits directly to EC 0x50 (SPL, 7–35 W), 0x51 (SPPT, 9–45 W), and 0x52 (FPPT, 11–55 W) in MSI's safe order:
    /// 0x50=7 -&gt; 0x51=9 -&gt; 0x52=FPPT -&gt; 0x51=SPPT -&gt; 0x50=SPL. Manual shift mode is 0x04 (0xC4).
    /// Rule: Manual goes as far as the highest MSI itself applies on each tier without STAMP boost: SPL 35 W (its
    /// Manual slider), SPPT 45 / FPPT 55 W (its Performance preset, 28/45/55 W; its
    /// Manual sliders stop at 40 / 48 W). So Manual can reach 35/45/55 W, a mix MSI's own UI does not offer but
    /// inside its own upper limits of 35 / 50 / 60 W, which its AI mode reaches with STAMP on.
    /// MSI applies the same watts on AC and battery on this board, so there is no battery cap.
    /// </summary>
    public static readonly MsiBoard ClawA8 = new()
    {
        Model = "MS-1T8K",

        Registers = new()
        {
            PowerLimitSustained = 0x50,
            PowerLimitBoost = 0x51,
            PowerLimitFastBoost = 0x52,
            CpuTemperature = 0x68,
            CoolerBoost = 0x98,
            AmdStamp = 0xC5,
            ShiftMode = 0xD2,
            FanControl = 0xD4,
            ChargeLimit = 0xD7,
        },

        Shift = new()
        {
            Codes = new Dictionary<PerformanceMode, byte>
            {
                [PerformanceMode.Endurance] = 0x02,
                [PerformanceMode.Balanced] = 0x01,
                [PerformanceMode.Performance] = 0x04,
                [PerformanceMode.Manual] = 0x04,
            },
            Otherwise = PerformanceMode.Balanced,   // including MSI's "Comfort", 0x00
        },

        PowerFloor = 7,
        BoostFloor = 9,

        Profile = new()
        {
            Name = "MSI Claw A8 (MS-1T8K)",
            Modes =
            [
                new(PerformanceMode.Endurance,   new PowerLimits(Sustained: 15, Boost: 28, FastBoost: 33)),
                new(PerformanceMode.Balanced,    new PowerLimits(Sustained: 20, Boost: 33, FastBoost: 43)),
                new(PerformanceMode.Performance, new PowerLimits(Sustained: 28, Boost: 45, FastBoost: 55)),
                new(PerformanceMode.Manual),
            ],
            ManualRange = new PowerRange(
                Min: 7, MaxSustained: 35, MaxBoost: 45,
                MinBoostHeadroom: 0, MinBoostFloor: 9,
                MaxFastBoost: 55, MinFastBoostHeadroom: 0, MinFastBoostFloor: 11),
            ManualDefault = new PowerLimits(Sustained: 20, Boost: 33, FastBoost: 43),
            FanModes = [FanMode.Auto, FanMode.Max],
            ChargeLimits = [80, 90, 100],
            ChargeResumeGap = 10,
            TempWarnC = 75,
            TempHotC = 85,
            Controls = new()
            {
                HasGameBarButton = true,
                GameBarNativeWinG = true,
                GameBarTriggers = [new KeyTrigger(VirtualKey: 0x47, Win: true)],   // Win+G
                HasOemButton = true,
                HasMsiCenterConflict = true,
                OemButtonWmiCode = 0x29,
                GameBarWmiCode = 0x58,
                BackButtons = new()
                {
                    Protocol = BackButtonsProfile.MsiHidV1,
                    VendorId = 0x0DB0,
                    ProductIds = [0x1901, 0x1902],
                    ProfileLength = 1478,
                    BlockLength = 8,
                    MaxSimultaneousKeys = 3,
                    Buttons =
                    [
                        new("M1", "M1", "Left back button", Offset: 186),
                        new("M2", "M2", "Right back button", Offset: 355),
                    ],
                },
            },
        },
    };

    // MS-1T7K is deliberately absent: it is recognized by MSI's software but not supported by Tsume yet. Its
    // power path, EC and marketing name are unconfirmed, so it needs a probe from a real unit before it gets a
    // map. Until then it is an unknown board (read-only discovery).
    private static readonly MsiBoard[] Known = [Claw8, Claw8AiPlus, Claw7AiPlus, ClawA1M, ClawA8];

    /// <summary>The map for a board, or null if Tsume has none.</summary>
    public static MsiBoard? Find(Board board) =>
        Known.FirstOrDefault(known => known.Model.Equals(board.Model, StringComparison.OrdinalIgnoreCase));
}
