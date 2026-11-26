using System.Runtime.InteropServices;
using System.Text;

namespace Tsume.Hardware;

/// <summary>
/// Shared <c>powrprof.dll</c> helpers for reading and updating Windows power scheme settings.
/// </summary>
internal static class PowerSchemes
{
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    public static readonly Guid PerfBoostPolicy = new("45bcc044-d885-43e2-8605-ee0ec6e96b59");

    public static readonly Guid PersonalityBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PersonalityHighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PersonalityPowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    public static bool IsBuiltInPersonality(Guid scheme) =>
        scheme == PersonalityBalanced
        || scheme == PersonalityHighPerformance
        || scheme == PersonalityPowerSaver;

    public static Guid? GetActiveScheme() => Invoke(() =>
    {
        if (PowerGetActiveScheme(0, out var ptr) != 0 || ptr == 0) return (Guid?)null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }, fallback: null);

    public static Guid? GetEffectiveOverlay() => Invoke(() =>
        PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? (Guid?)overlay : null, fallback: null);

    public static string? ReadFriendlyName(Guid scheme) => Invoke(() =>
    {
        uint size = 0;
        var res = PowerReadFriendlyName(0, ref scheme, 0, 0, null, ref size);
        if ((res != 0 && res != 234) || size == 0 || size > 4096) return null;

        var buffer = new byte[size];
        if (PowerReadFriendlyName(0, ref scheme, 0, 0, buffer, ref size) != 0) return null;
        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }, fallback: null);

    public static uint? ReadSettingIndex(Guid scheme, Guid subGroup, Guid setting, bool ac) => Invoke(() =>
    {
        var status = ac
            ? PowerReadACValueIndex(0, ref scheme, ref subGroup, ref setting, out uint value)
            : PowerReadDCValueIndex(0, ref scheme, ref subGroup, ref setting, out value);
        return status == 0 ? (uint?)value : null;
    }, fallback: null);

    public static uint? ReadDefaultIndex(Guid schemePersonality, Guid subGroup, Guid setting, bool ac) => Invoke(() =>
    {
        var status = ac
            ? PowerReadACDefaultIndex(0, ref schemePersonality, ref subGroup, ref setting, out uint value)
            : PowerReadDCDefaultIndex(0, ref schemePersonality, ref subGroup, ref setting, out value);
        return status == 0 ? (uint?)value : null;
    }, fallback: null);

    public static uint WriteSettingIndex(Guid scheme, Guid subGroup, Guid setting, uint value, bool ac) => Invoke(() =>
        ac
            ? PowerWriteACValueIndex(0, ref scheme, ref subGroup, ref setting, value)
            : PowerWriteDCValueIndex(0, ref scheme, ref subGroup, ref setting, value),
        fallback: 1u);

    public static uint ApplyActiveScheme(Guid scheme) => Invoke(() =>
        PowerSetActiveScheme(0, ref scheme), fallback: 1u);

    private static T Invoke<T>(Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return fallback;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

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
