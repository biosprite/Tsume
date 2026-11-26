using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tsume.Hardware;

/// <summary>
/// Reads and toggles Windows Processor Performance Boost Mode (<c>PERFBOOSTMODE</c>) via <c>powrprof.dll</c>.
/// Works uniformly across every CPU Tsume supports — Intel HWP hybrid chips (Meteor Lake 135H/155H, Lunar Lake
/// 258V, Panther Lake Arc G3 / Arc G3 Extreme across all efficiency classes) and AMD CPPC2 chips (Ryzen Z2
/// Extreme / Strix Point) — without kernel drivers or MSR writes.
/// </summary>
public static class CpuBoostControl
{
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid PerfBoostPolicy = new("45bcc044-d885-43e2-8605-ee0ec6e96b59");
    private static readonly Guid SchemeBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    private const uint ModeDisabled = 0;
    private const uint MinEnabledMode = 1;
    private const uint MaxEnabledMode = 6;
    private const uint DefaultEnabledMode = 2;    // Aggressive — Windows default on Intel & AMD Balanced plans
    private const uint DefaultBoostPolicy = 100;  // 100% — used only if a plan like Power Saver holds 0%

    private static readonly string MemoryFile = Path.Combine(Core.DataFolder.Path, "cpu-boost.json");
    private static readonly Lock Gate = new();

    private sealed record SchemeMemory(
        uint? AcMode = null,
        uint? DcMode = null,
        bool BumpedPolicyAc = false,
        bool BumpedPolicyDc = false);

    public sealed record Result(bool Ok, bool? Enabled, string? Error = null);

    /// <summary>
    /// Reads whether CPU boost is currently enabled for the active Windows power plan and current power source
    /// (AC when plugged in, DC when on battery). Returns null if the power setting cannot be read.
    /// </summary>
    public static bool? Read()
    {
        lock (Gate)
        {
            if (GetActiveScheme() is not { } scheme) return null;
            return ReadEffectiveLocked(scheme);
        }
    }

    /// <summary>
    /// Enables or disables CPU boost on both AC and DC for the active Windows power plan and commits immediately
    /// via <c>PowerSetActiveScheme</c>. Preserves each scheme's prior non-zero boost mode (e.g. Aggressive on
    /// Intel or Efficient Aggressive on AMD) so toggling off and back on restores the original mode.
    /// </summary>
    public static Result Set(bool enabled)
    {
        lock (Gate)
        {
            if (GetActiveScheme() is not { } scheme)
                return new(false, null, "Could not read the active Windows power plan.");

            var boostAc = ReadSettingIndex(scheme, SubProcessor, PerfBoostMode, ac: true);
            var boostDc = ReadSettingIndex(scheme, SubProcessor, PerfBoostMode, ac: false);
            if (boostAc is null && boostDc is null)
                return new(false, null, "Processor performance boost mode is not available on this system.");

            var polAc = ReadSettingIndex(scheme, SubProcessor, PerfBoostPolicy, ac: true);
            var polDc = ReadSettingIndex(scheme, SubProcessor, PerfBoostPolicy, ac: false);

            var memory = LoadMemory();
            var key = scheme.ToString("D");
            memory.TryGetValue(key, out var saved);
            saved ??= new SchemeMemory();
            var updated = saved;
            var wrote = false;

            if (!enabled)
            {
                if (IsEnabledMode(boostAc)) updated = updated with { AcMode = boostAc };
                if (IsEnabledMode(boostDc)) updated = updated with { DcMode = boostDc };
                if (updated != saved)
                {
                    memory[key] = updated;
                    SaveMemory(memory);
                    saved = updated;
                }

                if (boostAc is { } currentAc && currentAc != ModeDisabled)
                {
                    var err = WriteSettingIndex(scheme, SubProcessor, PerfBoostMode, ModeDisabled, ac: true);
                    if (err != 0) return new(false, ReadEffectiveLocked(scheme), $"Could not disable AC CPU boost (error {err}).");
                    wrote = true;
                }

                if (boostDc is { } currentDc && currentDc != ModeDisabled)
                {
                    var err = WriteSettingIndex(scheme, SubProcessor, PerfBoostMode, ModeDisabled, ac: false);
                    if (err != 0) return new(false, ReadEffectiveLocked(scheme), $"Could not disable DC CPU boost (error {err}).");
                    wrote = true;
                }

                // Restore boost policy to 0% only when Tsume previously raised it from 0% (e.g. on Power Saver).
                if (saved.BumpedPolicyAc)
                {
                    if (WriteSettingIndex(scheme, SubProcessor, PerfBoostPolicy, 0, ac: true) == 0) wrote = true;
                    updated = updated with { BumpedPolicyAc = false };
                }

                if (saved.BumpedPolicyDc)
                {
                    if (WriteSettingIndex(scheme, SubProcessor, PerfBoostPolicy, 0, ac: false) == 0) wrote = true;
                    updated = updated with { BumpedPolicyDc = false };
                }

                if (updated != saved)
                {
                    memory[key] = updated;
                    SaveMemory(memory);
                }
            }
            else
            {
                var targetAc = ResolveEnabledMode(scheme, boostAc, saved.AcMode, ac: true);
                var targetDc = ResolveEnabledMode(scheme, boostDc, saved.DcMode, ac: false);

                if (boostAc is { } currentAc && currentAc != targetAc)
                {
                    var err = WriteSettingIndex(scheme, SubProcessor, PerfBoostMode, targetAc, ac: true);
                    if (err != 0) return new(false, ReadEffectiveLocked(scheme), $"Could not enable AC CPU boost (error {err}).");
                    wrote = true;
                }

                if (boostDc is { } currentDc && currentDc != targetDc)
                {
                    var err = WriteSettingIndex(scheme, SubProcessor, PerfBoostMode, targetDc, ac: false);
                    if (err != 0) return new(false, ReadEffectiveLocked(scheme), $"Could not enable DC CPU boost (error {err}).");
                    wrote = true;
                }

                // On plans like Power Saver, PERFBOOSTPOL defaults to 0% which suppresses boost even when
                // PERFBOOSTMODE is non-zero. Only raise it when it is explicitly 0% so Balanced/overlay tuning stays intact.
                if (polAc == 0)
                {
                    var policyTarget = ResolveEnabledPolicy(scheme, ac: true);
                    if (WriteSettingIndex(scheme, SubProcessor, PerfBoostPolicy, policyTarget, ac: true) == 0)
                    {
                        updated = updated with { BumpedPolicyAc = true };
                        wrote = true;
                    }
                }

                if (polDc == 0)
                {
                    var policyTarget = ResolveEnabledPolicy(scheme, ac: false);
                    if (WriteSettingIndex(scheme, SubProcessor, PerfBoostPolicy, policyTarget, ac: false) == 0)
                    {
                        updated = updated with { BumpedPolicyDc = true };
                        wrote = true;
                    }
                }

                if (updated != saved)
                {
                    memory[key] = updated;
                    SaveMemory(memory);
                }
            }

            if (wrote)
            {
                var applyErr = ApplyActiveScheme(scheme);
                if (applyErr != 0)
                    return new(false, ReadEffectiveLocked(scheme), $"Could not apply the active power plan (error {applyErr}).");
            }

            var actual = ReadEffectiveLocked(scheme);
            if (actual != enabled)
                return new(false, actual, "Windows did not keep the CPU boost change.");

            return new(true, actual);
        }
    }

    private static bool? ReadEffectiveLocked(Guid scheme)
    {
        var boostAc = ReadSettingIndex(scheme, SubProcessor, PerfBoostMode, ac: true);
        var boostDc = ReadSettingIndex(scheme, SubProcessor, PerfBoostMode, ac: false);
        if (boostAc is null && boostDc is null) return null;

        var polAc = ReadSettingIndex(scheme, SubProcessor, PerfBoostPolicy, ac: true);
        var polDc = ReadSettingIndex(scheme, SubProcessor, PerfBoostPolicy, ac: false);

        var onBattery = PowerSource.IsOnBattery();
        var mode = onBattery ? (boostDc ?? boostAc) : (boostAc ?? boostDc);
        var policy = onBattery ? polDc : polAc;

        return mode is > ModeDisabled && policy is not 0;
    }

    private static uint ResolveEnabledMode(Guid scheme, uint? current, uint? remembered, bool ac)
    {
        if (IsEnabledMode(current)) return current!.Value;
        if (IsEnabledMode(remembered)) return remembered!.Value;

        var schemeDefault = ReadDefaultIndex(scheme, SubProcessor, PerfBoostMode, ac);
        if (IsEnabledMode(schemeDefault)) return schemeDefault!.Value;

        var balancedDefault = ReadDefaultIndex(SchemeBalanced, SubProcessor, PerfBoostMode, ac);
        if (IsEnabledMode(balancedDefault)) return balancedDefault!.Value;

        return DefaultEnabledMode;
    }

    private static uint ResolveEnabledPolicy(Guid scheme, bool ac)
    {
        var schemeDefault = ReadDefaultIndex(scheme, SubProcessor, PerfBoostPolicy, ac);
        if (schemeDefault is > 0 and <= 100) return schemeDefault.Value;
        return DefaultBoostPolicy;
    }

    private static bool IsEnabledMode(uint? mode) => mode is >= MinEnabledMode and <= MaxEnabledMode;

    private static Dictionary<string, SchemeMemory> LoadMemory()
    {
        try
        {
            if (!File.Exists(MemoryFile)) return new(StringComparer.OrdinalIgnoreCase);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, SchemeMemory>>(File.ReadAllText(MemoryFile));
            return parsed is not null
                ? new Dictionary<string, SchemeMemory>(parsed, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveMemory(Dictionary<string, SchemeMemory> memory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MemoryFile)!);
            var temp = MemoryFile + ".tsume-tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(memory));
            File.Move(temp, MemoryFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing restore memory is non-fatal: turning boost on falls back to the scheme default.
        }
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

    private static uint? ReadDefaultIndex(Guid schemePersonality, Guid subGroup, Guid setting, bool ac)
    {
        try
        {
            var status = ac
                ? PowerReadACDefaultIndex(0, ref schemePersonality, ref subGroup, ref setting, out var value)
                : PowerReadDCDefaultIndex(0, ref schemePersonality, ref subGroup, ref setting, out value);
            return status == 0 ? value : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static uint WriteSettingIndex(Guid scheme, Guid subGroup, Guid setting, uint value, bool ac)
    {
        try
        {
            return ac
                ? PowerWriteACValueIndex(0, ref scheme, ref subGroup, ref setting, value)
                : PowerWriteDCValueIndex(0, ref scheme, ref subGroup, ref setting, value);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1;
        }
    }

    private static uint ApplyActiveScheme(Guid scheme)
    {
        try
        {
            return PowerSetActiveScheme(0, ref scheme);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACDefaultIndex(
        nint rootPowerKey, ref Guid schemePersonalityGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acDefaultIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCDefaultIndex(
        nint rootPowerKey, ref Guid schemePersonalityGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcDefaultIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(
        nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint dcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint hMem);
}
