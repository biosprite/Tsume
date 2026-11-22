using Microsoft.Win32;

namespace Tsume.Upscaling;

/// <summary>Checks which GPU vendors are present on this machine, read once from Windows' display adapter registry keys.</summary>
internal static class GpuVendor
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly Lazy<bool> IntelPresent = new(() => HasVendor("VEN_8086"));

    /// <summary>True if an Intel GPU (Arc / integrated graphics) is installed.</summary>
    public static bool HasIntelGpu() => IntelPresent.Value;

    private static bool HasVendor(string vendorToken)
    {
        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (displayClass is null) return false;

            foreach (var name in displayClass.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;

                try
                {
                    using var adapter = displayClass.OpenSubKey(name);
                    var matchingId = adapter?.GetValue("MatchingDeviceId") as string;
                    if (matchingId?.Contains(vendorToken, StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }
                catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }

        return false;
    }
}
