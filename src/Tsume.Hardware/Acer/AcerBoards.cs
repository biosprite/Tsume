using System.Management;
using Tsume.Core.Cooling;
using Tsume.Core.Devices;
using Tsume.Core.Performance;

namespace Tsume.Hardware.Acer;

/// <summary>
/// The Acer handheld boards Tsume knows. Predator Atlas 8 (<c>PA08-I51</c>, board <c>F900_PTG</c>, BIOS <c>F7V</c>)
/// uses the same Intel Panther Lake Arc G3 / Arc G3 Extreme silicon as the MSI Claw 8 EX AI+ (<c>MS-1T91</c>) and
/// adopts its tuned TDP preset and manual limits (including the PL2 &gt;= PL1 + 2 W rule), enforced through EC HID
/// OC Profile 3 + Usage Mode 3.
/// </summary>
public static class AcerBoards
{
    /// <summary>
    /// Predator Atlas 8 (<c>PA08-I51</c>, Arc G3 Extreme). Presets and manual range mirror the MSI Claw 8 EX AI+
    /// (<c>12/15</c>, <c>25/28</c>, <c>32/36 W</c>; Manual <c>8–35 W</c> PL1 / <c>8–45 W</c> PL2 with 2 W headroom).
    /// All modes are applied via EC HID OC Profile 3 + Usage Mode 3 because Atlas 8's native modes 0–2 hard-code
    /// PECI limits (40/65, 35/65, 30/45 W) and ignore OC Profiles 0–2.
    /// </summary>
    public static readonly AcerBoard Atlas8 = new()
    {
        Model = "F900_PTG",
        SystemModelPrefix = "Predator PA08",
        CheckedOnDevice = false,
        EcHidVendorId = 0x1025,
        EcHidProductId = 0x174B,
        EcHidUsagePage = 0xFF05,
        FanIds = [2, 3],

        Profile = new()
        {
            Name = "Acer Predator Atlas 8 (PA08-I51)",
            Modes =
            [
                new(PerformanceMode.Endurance,   new PowerLimits(Sustained: 12, Boost: 15)),
                new(PerformanceMode.Balanced,    new PowerLimits(Sustained: 25, Boost: 28)),
                new(PerformanceMode.Performance, new PowerLimits(Sustained: 32, Boost: 36)),
                new(PerformanceMode.Manual),
            ],
            ManualRange = new PowerRange(Min: 8, MaxSustained: 35, MaxBoost: 45, MinBoostHeadroom: 2),
            ManualDefault = new PowerLimits(Sustained: 25, Boost: 28),
            FanModes = [FanMode.Auto, FanMode.Max],
            ChargeLimits = [80, 100],
            TempWarnC = 85,
            TempHotC = 95,
            Controls = new()
            {
                HasGameBarButton = true,
                GameBarButtonLabel = "Quick Menu button",
                GameBarNativeWinG = false,
                HasOemButton = true,
                OemButtonLabel = "PredatorSense button",
                OemSoftware = OemSoftwareKind.AcerPredatorSense,
                HasAcerHidButtons = true,
                LightingProtocol = ControlsProfile.AcerHidLighting,
                BackButtons = new()
                {
                    Protocol = BackButtonsProfile.MsiHidV1,
                    VendorId = 0x0502,
                    VendorIds = [0x0502, 0x0489],
                    ProductIds = [0xF900, 0xF901, 0xF909, 0xF90A, 0xF310, 0xF311, 0xF319, 0xF31A],
                    ProfileLength = 1478,
                    BlockLength = 8,
                    MaxSimultaneousKeys = 3,
                    MinFirmwareVersion = null,
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
    /// Predator Atlas 8 with the plain Arc G3 (10 Xe cores, 60 Wh SKU) instead of the Arc G3 Extreme.
    /// Mirrors <c>MsiBoards.Claw8ArcG3</c>: Performance <c>30/37 W</c> and Manual sustained ceiling <c>30 W</c>
    /// (PL2 &gt;= PL1 + 2 W).
    /// </summary>
    public static readonly AcerBoard Atlas8ArcG3 = Atlas8 with
    {
        Profile = Atlas8.Profile with
        {
            Name = "Acer Predator Atlas 8 (PA08-I51, Arc G3)",
            Modes =
            [
                new(PerformanceMode.Endurance,   new PowerLimits(Sustained: 12, Boost: 15)),
                new(PerformanceMode.Balanced,    new PowerLimits(Sustained: 25, Boost: 28)),
                new(PerformanceMode.Performance, new PowerLimits(Sustained: 30, Boost: 37)),
                new(PerformanceMode.Manual),
            ],
            ManualRange = new PowerRange(Min: 8, MaxSustained: 30, MaxBoost: 45, MinBoostHeadroom: 2),
        },
    };

    private static readonly AcerBoard[] Known = [Atlas8];

    /// <summary>The map for this machine's board and processor, or null if Tsume has none.</summary>
    public static AcerBoard? Find(Board board)
    {
        var processor = Processor.Name();
        var direct = Find(board, processor, systemMaker: null, systemModel: null);
        if (direct is not null) return direct;

        var (sysMaker, sysModel) = ReadComputerSystemIdentity();
        return Find(board, processor, sysMaker, sysModel);
    }

    /// <summary>
    /// Resolves an Acer board by baseboard product (ignoring baseboard maker, which is <c>"PTL"</c> on retail
    /// Atlas 8 units) or by <c>Win32_ComputerSystem</c> manufacturer + model prefix fallback.
    /// </summary>
    public static AcerBoard? Find(Board board, string processor, string? systemMaker = null, string? systemModel = null)
    {
        var known = Known.FirstOrDefault(k => k.Model.Equals(board.Model, StringComparison.OrdinalIgnoreCase));

        if (known is null
            && !string.IsNullOrWhiteSpace(systemMaker)
            && systemMaker.Contains("Acer", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(systemModel))
        {
            known = Known.FirstOrDefault(k =>
                !string.IsNullOrEmpty(k.SystemModelPrefix)
                && systemModel.StartsWith(k.SystemModelPrefix, StringComparison.OrdinalIgnoreCase));
        }

        return ReferenceEquals(known, Atlas8) && IsPlainArcG3(processor) ? Atlas8ArcG3 : known;
    }

    private static bool IsPlainArcG3(string processor) =>
        processor.Contains("G3", StringComparison.OrdinalIgnoreCase)
        && !processor.Contains("Extreme", StringComparison.OrdinalIgnoreCase);

    private static (string? Maker, string? Model) ReadComputerSystemIdentity()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var results = search.Get();
            foreach (var row in results)
            {
                using (row)
                    return ($"{row["Manufacturer"]}".Trim(), $"{row["Model"]}".Trim());
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return (null, null);
    }
}
