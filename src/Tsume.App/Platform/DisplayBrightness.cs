using System.Management;

namespace Tsume.App.Platform;

/// <summary>Backlight of the built-in screen, 0–100. Uses the standard Windows WMI monitor classes.</summary>
public static class DisplayBrightness
{
    private const string Scope = @"root\WMI";

    /// <summary>Current brightness, or null when the display does not report one.</summary>
    public static int? Read()
    {
        try
        {
            using var search = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness");

            foreach (var monitor in search.Get())
                using (monitor)
                    return (byte)monitor["CurrentBrightness"];
        }
        catch (ManagementException) { }

        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var methods = new ManagementClass(Scope, "WmiMonitorBrightnessMethods", null);

            foreach (ManagementObject monitor in methods.GetInstances())
                using (monitor)
                    monitor.InvokeMethod("WmiSetBrightness", [/* timeout, seconds */ 1u, (byte)Math.Clamp(percent, 0, 100)]);
        }
        catch (ManagementException) { }
    }
}
