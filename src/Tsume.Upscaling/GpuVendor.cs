using Microsoft.Win32;

namespace Tsume.Upscaling;

/// <summary>Checks which GPU vendors are present on this machine, read once from Windows' display adapter registry keys.</summary>
internal static class GpuVendor
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Lazy<(bool Intel, bool Amd)> Present = new(Detect);

    /// <summary>True if an Intel GPU (Arc / integrated graphics) is installed.</summary>
    public static bool HasIntelGpu() => Present.Value.Intel;

    /// <summary>True if an AMD GPU (Radeon) is installed.</summary>
    public static bool HasAmdGpu() => Present.Value.Amd;

    private static (bool Intel, bool Amd) Detect()
    {
        var intel = false;
        var amd = false;

        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (displayClass is null) return (false, false);

            foreach (var name in displayClass.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;

                try
                {
                    using var adapter = displayClass.OpenSubKey(name);
                    var matchingId = adapter?.GetValue("MatchingDeviceId") as string;
                    if (matchingId is null) continue;

                    if (matchingId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) intel = true;
                    if (matchingId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)) amd = true;
                }
                catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }

        return (intel, amd);
    }
}
