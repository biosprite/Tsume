using Tsume.Core.Diagnostics;

namespace Tsume.Hardware.Diagnostics;

/// <summary>
/// Reads the active Windows power plan, effective power-mode overlay, and Processor Performance
/// Boost Mode (AC &amp; DC) via <c>powrprof.dll</c>. Strictly read-only, no subprocesses.
/// </summary>
public static class PowerPlanInfo
{
    private static readonly Guid OverlayBetterBattery = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid OverlayBalanced = new("00000000-0000-0000-0000-000000000000");
    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static PowerPlanSnapshot Read()
    {
        Guid? active = PowerSchemes.GetActiveScheme();
        Guid? overlay = PowerSchemes.GetEffectiveOverlay();

        uint? boostAc = null;
        uint? boostDc = null;
        string? schemeName = null;

        if (active is { } scheme)
        {
            schemeName = PowerSchemes.ReadFriendlyName(scheme);
            boostAc = PowerSchemes.ReadSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, ac: true);
            boostDc = PowerSchemes.ReadSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, ac: false);
        }

        return new PowerPlanSnapshot
        {
            ActiveSchemeGuid = active?.ToString("D"),
            ActiveSchemeName = schemeName,
            EffectiveOverlayGuid = overlay?.ToString("D"),
            EffectiveOverlayLabel = LabelOverlay(overlay),
            CpuBoostModeAc = boostAc,
            CpuBoostModeAcLabel = LabelBoostMode(boostAc),
            CpuBoostModeDc = boostDc,
            CpuBoostModeDcLabel = LabelBoostMode(boostDc),
        };
    }

    private static string? LabelOverlay(Guid? overlay)
    {
        if (overlay is not { } g) return null;
        if (g == OverlayBetterBattery) return "Best power efficiency";
        if (g == OverlayBalanced) return "Balanced";
        if (g == OverlayBestPerformance) return "Best performance";
        return g.ToString("D");
    }

    private static string? LabelBoostMode(uint? mode) => mode switch
    {
        null => null,
        0 => "Disabled",
        1 => "Enabled",
        2 => "Aggressive",
        3 => "Efficient Enabled",
        4 => "Efficient Aggressive",
        5 => "Aggressive At Guaranteed",
        6 => "Efficient Aggressive At Guaranteed",
        _ => $"Unknown ({mode})",
    };
}
