using System.Runtime.InteropServices;
using System.Text;
using Tsume.Core.Diagnostics;

namespace Tsume.Hardware.Diagnostics;

/// <summary>
/// Reads the active Windows power plan, effective power-mode overlay, and Processor Performance
/// Boost Mode (AC &amp; DC) via <c>powrprof.dll</c>. Strictly read-only, no subprocesses.
/// </summary>
public static class PowerPlanInfo
{
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    private static readonly Guid OverlayBetterBattery = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid OverlayBalanced = new("00000000-0000-0000-0000-000000000000");
    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static PowerPlanSnapshot Read()
    {
        Guid? active = GetActiveScheme();
        Guid? overlay = GetEffectiveOverlay();

        uint? boostAc = null;
        uint? boostDc = null;
        string? schemeName = null;

        if (active is { } scheme)
        {
            schemeName = ReadFriendlyName(scheme);
            boostAc = ReadSettingIndex(scheme, SubProcessor, PerfBoostMode, ac: true);
            boostDc = ReadSettingIndex(scheme, SubProcessor, PerfBoostMode, ac: false);
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

    private static Guid? GetActiveScheme()
    {
        try
        {
            if (PowerGetActiveScheme(0, out var ptr) != 0 || ptr == 0) return null;
            try { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static Guid? GetEffectiveOverlay()
    {
        try
        {
            return PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? overlay : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        try
        {
            uint size = 0;
            var res = PowerReadFriendlyName(0, ref scheme, 0, 0, null, ref size);
            if ((res != 0 && res != 234) || size == 0 || size > 4096) return null;

            var buffer = new byte[size];
            if (PowerReadFriendlyName(0, ref scheme, 0, 0, buffer, ref size) != 0) return null;
            return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static uint? ReadSettingIndex(Guid scheme, Guid subGroup, Guid setting, bool ac)
    {
        try
        {
            var status = ac
                ? PowerReadACValueIndex(0, ref scheme, ref subGroup, ref setting, out var value)
                : PowerReadDCValueIndex(0, ref scheme, ref subGroup, ref setting, out value);
            return status == 0 ? value : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
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

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayPolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        nint rootPowerKey, ref Guid schemeGuid, nint subGroupOfPowerSettingsGuid, nint powerSettingGuid,
        [Out] byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint hMem);
}
