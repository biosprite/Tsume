using System.Security;
using Microsoft.Win32;

namespace Tsume.Hardware;

/// <summary>
/// Which chip the machine has, by the name it reports. A board can be sold with more than one chip, and
/// <see cref="Board"/> does not say which.
/// </summary>
public static class Processor
{
    private const string Key = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string Value = "ProcessorNameString";

    /// <summary>E.g. "Intel(R) Arc(TM) G3 Extreme"; empty when Windows would not say.</summary>
    // Read from the registry, not WMI: it is there from the start of a boot, when WMI may not answer yet.
    public static string Name()
    {
        try
        {
            using var chip = Registry.LocalMachine.OpenSubKey(Key);
            return $"{chip?.GetValue(Value)}".Trim();
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            return "";
        }
    }
}
