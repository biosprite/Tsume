using System.Security;
using Microsoft.Win32;

namespace Tsume.Hardware.Msi;

/// <summary>
/// Copies Tsume's charge limit into MSI Center M's own registry record, because its battery host writes that
/// record over the firmware each time it starts. See docs/hardware/msi-center-m.md.
/// </summary>
internal static class MsiCenterBattery
{
    private const string Key = @"SOFTWARE\MSI\MSI Center M\Battery";   // in the 32-bit half of the registry
    private const string Value = "BatteryLevel";

    public static void Mirror(int percent)
    {
        // MSI Center M knows only these three levels. For any other (90 %) its record is left alone,
        // so it would put its older level back if it ran again.
        var level = percent switch { 100 => "0", 80 => "1", 60 => "2", _ => null };
        if (level is null) return;

        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var battery = machine.OpenSubKey(Key, writable: true);

            // Only change a value MSI Center M has already written; no record means it is not installed.
            if (battery?.GetValue(Value) is null) return;

            battery.SetValue(Value, level, RegistryValueKind.String);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Ignored: the firmware already has the limit; the mirror is best-effort.
        }
    }
}
