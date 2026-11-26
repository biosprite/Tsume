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
    private const uint ModeDisabled = 0;
    private const uint DefaultEnabledMode = 2;    // Aggressive — Windows default on Intel & AMD Balanced plans
    private const uint DefaultBoostPolicy = 100;  // 100% — used only if a plan like Power Saver holds 0%

    private static readonly string MemoryFile = Path.Combine(Core.DataFolder.Path, "cpu-boost.json");
    private static readonly Lock Gate = new();

    private sealed record SchemeMemory(
        uint? AcMode = null,
        uint? DcMode = null,
        bool BumpedPolicyAc = false,
        bool BumpedPolicyDc = false);

    private readonly record struct PlanState(uint? BoostAc, uint? BoostDc, uint? PolicyAc, uint? PolicyDc)
    {
        public bool IsReadable => BoostAc is not null || BoostDc is not null;

        public bool? EffectiveEnabled()
        {
            if (!IsReadable) return null;

            var onBattery = PowerSource.IsOnBattery();
            var mode = onBattery ? (BoostDc ?? BoostAc) : (BoostAc ?? BoostDc);
            var policy = onBattery ? (PolicyDc ?? PolicyAc) : (PolicyAc ?? PolicyDc);

            return mode is > ModeDisabled && policy is not 0;
        }
    }

    public sealed record Result(bool Ok, bool? Enabled, string? Error = null);

    /// <summary>
    /// Reads whether CPU boost is currently enabled for the active Windows power plan and current power source
    /// (AC when plugged in, DC when on battery). Returns null if the power setting cannot be read.
    /// </summary>
    public static bool? Read()
    {
        lock (Gate)
        {
            if (PowerSchemes.GetActiveScheme() is not { } scheme) return null;
            return ReadPlanState(scheme).EffectiveEnabled();
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
            if (PowerSchemes.GetActiveScheme() is not { } scheme)
                return new(false, null, "Could not read the active Windows power plan.");

            var initial = ReadPlanState(scheme);
            if (!initial.IsReadable)
                return new(false, null, "Processor performance boost mode is not available on this system.");

            var memory = LoadMemory();
            var key = scheme.ToString("D");
            memory.TryGetValue(key, out var saved);
            saved ??= new SchemeMemory();

            var acMode = saved.AcMode;
            var dcMode = saved.DcMode;
            var bumpedAc = saved.BumpedPolicyAc;
            var bumpedDc = saved.BumpedPolicyDc;

            uint targetAc;
            uint targetDc;

            if (!enabled)
            {
                if (IsEnabledMode(initial.BoostAc)) acMode = initial.BoostAc;
                if (IsEnabledMode(initial.BoostDc)) dcMode = initial.BoostDc;
                targetAc = ModeDisabled;
                targetDc = ModeDisabled;
            }
            else
            {
                targetAc = ResolveEnabledMode(scheme, initial.BoostAc, saved.AcMode, ac: true);
                targetDc = ResolveEnabledMode(scheme, initial.BoostDc, saved.DcMode, ac: false);
            }

            var (wrote, modeError) = WriteBoostModes(scheme, initial, targetAc, targetDc, enabling: enabled);
            if (modeError is not null)
                return new(false, initial.EffectiveEnabled(), modeError);

            if (!enabled)
            {
                // Restore boost policy to 0% only when Tsume previously raised it from 0% (e.g. on Power Saver).
                TryRestorePolicy(scheme, ac: true, initial.PolicyAc, ref bumpedAc, ref wrote);
                TryRestorePolicy(scheme, ac: false, initial.PolicyDc, ref bumpedDc, ref wrote);
            }
            else
            {
                // On plans like Power Saver, PERFBOOSTPOL defaults to 0% which suppresses boost even when
                // PERFBOOSTMODE is non-zero. Only raise it when it is explicitly 0% so Balanced/overlay tuning stays intact.
                TryBumpPolicy(scheme, ac: true, initial.PolicyAc, ref bumpedAc, ref wrote);
                TryBumpPolicy(scheme, ac: false, initial.PolicyDc, ref bumpedDc, ref wrote);
            }

            if (wrote)
            {
                var applyErr = PowerSchemes.ApplyActiveScheme(scheme);
                if (applyErr != 0)
                {
                    RestorePlanState(scheme, initial);
                    return new(false, initial.EffectiveEnabled(), $"Could not apply the active power plan (error {applyErr}).");
                }
            }

            var updated = new SchemeMemory(acMode, dcMode, bumpedAc, bumpedDc);
            if (updated != saved)
            {
                memory[key] = updated;
                SaveMemory(memory);
            }

            var actual = wrote ? ReadPlanState(scheme).EffectiveEnabled() : initial.EffectiveEnabled();
            if (actual != enabled)
                return new(false, actual, "Windows did not keep the CPU boost change.");

            return new(true, actual);
        }
    }

    private static PlanState ReadPlanState(Guid scheme) => new(
        BoostAc: PowerSchemes.ReadSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, ac: true),
        BoostDc: PowerSchemes.ReadSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, ac: false),
        PolicyAc: PowerSchemes.ReadSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostPolicy, ac: true),
        PolicyDc: PowerSchemes.ReadSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostPolicy, ac: false));

    private static (bool Wrote, string? Error) WriteBoostModes(
        Guid scheme, PlanState initial, uint targetAc, uint targetDc, bool enabling)
    {
        var wroteAc = false;
        var wroteDc = false;
        var verb = enabling ? "enable" : "disable";

        if (initial.BoostAc is { } currentAc && currentAc != targetAc)
        {
            var err = PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, targetAc, ac: true);
            if (err != 0)
                return (false, $"Could not {verb} AC CPU boost (error {err}).");
            wroteAc = true;
        }

        if (initial.BoostDc is { } currentDc && currentDc != targetDc)
        {
            var err = PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, targetDc, ac: false);
            if (err != 0)
            {
                if (wroteAc && initial.BoostAc is { } priorAc)
                    PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, priorAc, ac: true);
                return (false, $"Could not {verb} DC CPU boost (error {err}).");
            }
            wroteDc = true;
        }

        return (wroteAc || wroteDc, null);
    }

    private static void TryRestorePolicy(Guid scheme, bool ac, uint? currentPolicy, ref bool bumped, ref bool wrote)
    {
        if (!bumped) return;
        if (currentPolicy == 0)
        {
            bumped = false;
            return;
        }

        if (PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostPolicy, 0, ac) == 0)
        {
            wrote = true;
            bumped = false;
        }
    }

    private static void TryBumpPolicy(Guid scheme, bool ac, uint? currentPolicy, ref bool bumped, ref bool wrote)
    {
        if (currentPolicy != 0) return;

        var target = ResolveEnabledPolicy(scheme, ac);
        if (PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostPolicy, target, ac) == 0)
        {
            wrote = true;
            bumped = true;
        }
    }

    private static void RestorePlanState(Guid scheme, PlanState initial)
    {
        if (initial.BoostAc is { } boostAc)
            PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, boostAc, ac: true);
        if (initial.BoostDc is { } boostDc)
            PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, boostDc, ac: false);
        if (initial.PolicyAc is { } policyAc)
            PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostPolicy, policyAc, ac: true);
        if (initial.PolicyDc is { } policyDc)
            PowerSchemes.WriteSettingIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostPolicy, policyDc, ac: false);
    }

    private static uint ResolveEnabledMode(Guid scheme, uint? current, uint? remembered, bool ac)
    {
        if (IsEnabledMode(current)) return current!.Value;
        if (IsEnabledMode(remembered)) return remembered!.Value;

        if (PowerSchemes.IsBuiltInPersonality(scheme))
        {
            var schemeDefault = PowerSchemes.ReadDefaultIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, ac);
            if (IsEnabledMode(schemeDefault)) return schemeDefault!.Value;
        }

        var balancedDefault = PowerSchemes.ReadDefaultIndex(
            PowerSchemes.PersonalityBalanced, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostMode, ac);
        if (IsEnabledMode(balancedDefault)) return balancedDefault!.Value;

        return DefaultEnabledMode;
    }

    private static uint ResolveEnabledPolicy(Guid scheme, bool ac)
    {
        if (PowerSchemes.IsBuiltInPersonality(scheme))
        {
            var schemeDefault = PowerSchemes.ReadDefaultIndex(scheme, PowerSchemes.SubProcessor, PowerSchemes.PerfBoostPolicy, ac);
            if (schemeDefault is > 0 and <= 100) return schemeDefault.Value;
        }

        return DefaultBoostPolicy;
    }

    private static bool IsEnabledMode(uint? mode) => mode is > ModeDisabled;

    private static Dictionary<string, SchemeMemory> LoadMemory()
    {
        var result = new Dictionary<string, SchemeMemory>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(MemoryFile)) return result;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, SchemeMemory?>>(File.ReadAllText(MemoryFile));
            if (parsed is not null)
            {
                foreach (var (key, value) in parsed)
                {
                    if (value is not null) result[key] = value;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            // Corrupt or unreadable restore memory is non-fatal.
        }
        return result;
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
}
