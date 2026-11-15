using Microsoft.Win32;

namespace Tsume.App.Platform;

/// <summary>
/// Whether Tsume starts at sign-in. A per-user Run-key entry, so it needs no elevation.
/// </summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Tsume";
    private const string OldName = "Talon";

    /// <summary>At sign-in Tsume starts quietly, with the panel closed.</summary>
    private static string Command => $"\"{Environment.ProcessPath}\" {App.BackgroundSwitch}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);

            if (value) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    /// <summary>On uninstall: drops the entry if it starts this copy. One for a copy elsewhere stays.</summary>
    public static void RemoveIfMine()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

        if (key?.GetValue(Name) as string == Command) key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>If Tsume has moved since the entry was written, point the entry at the new place.</summary>
    public static void RepairIfMoved()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);

        // An entry from when Tsume was called Talon carries over under the new name.
        if (key.GetValue(OldName) is string)
        {
            key.DeleteValue(OldName);
            key.SetValue(Name, Command);
        }

        if (!IsEnabled) return;

        if (key.GetValue(Name) as string != Command) key.SetValue(Name, Command);
    }
}
