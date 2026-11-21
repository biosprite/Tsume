using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Performance;

namespace Tsume.Hardware.Msi;

/// <summary>
/// The MSI boards whose maps are verified against the real machine (docs/hardware/firmware.md); Tsume writes
/// to no others. Add a board only after the probe, write check and mode check have been run and recorded on it.
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

    private static readonly MsiBoard[] Verified = [Claw8];

    /// <summary>The verified map for a board, or null if it has none.</summary>
    public static MsiBoard? Find(Board board) =>
        Verified.FirstOrDefault(known => known.Model.Equals(board.Model, StringComparison.OrdinalIgnoreCase));
}
