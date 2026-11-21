using Microsoft.Win32;

namespace Tsume.App.Platform;

/// <summary>
/// Whether Tsume starts at sign-in. A per-user Run-key entry, so it needs no elevation.
/// Also opts into "Start at log in" under Windows 11's Full Screen Experience startup key so
/// Explorer does not defer Tsume until the user exits Xbox FSE to the desktop.
/// </summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string GamingStartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration\Startup\Run";
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

            if (value)
            {
                key.SetValue(Name, Command);
                SetGamingStartup(allowAtLogon: true);
            }
            else
            {
                key.DeleteValue(Name, throwOnMissingValue: false);
                SetGamingStartup(allowAtLogon: false);
            }
        }
    }

    /// <summary>On uninstall: drops the entry if it starts this copy. One for a copy elsewhere stays.</summary>
    public static void RemoveIfMine()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

        if (key?.GetValue(Name) as string == Command)
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
            SetGamingStartup(allowAtLogon: false);
        }
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

        using (var gaming = Registry.CurrentUser.OpenSubKey(GamingStartupRunKey, writable: true))
            gaming?.DeleteValue(OldName, throwOnMissingValue: false);

        if (!IsEnabled) return;

        if (key.GetValue(Name) as string != Command) key.SetValue(Name, Command);

        // Existing installs created before FSE support have no GamingConfiguration entry yet: opt in once,
        // without overriding an explicit "Start when exiting to desktop" (0) chosen in Windows Settings.
        using var fse = Registry.CurrentUser.CreateSubKey(GamingStartupRunKey);
        if (fse.GetValue(Name) is null) fse.SetValue(Name, 1, RegistryValueKind.DWord);
    }

    private static void SetGamingStartup(bool allowAtLogon)
    {
        if (allowAtLogon)
        {
            using var key = Registry.CurrentUser.CreateSubKey(GamingStartupRunKey);
            key.SetValue(Name, 1, RegistryValueKind.DWord);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(GamingStartupRunKey, writable: true);
            key?.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
