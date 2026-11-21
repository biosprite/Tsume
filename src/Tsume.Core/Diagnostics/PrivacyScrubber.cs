using System.Text.RegularExpressions;

namespace Tsume.Core.Diagnostics;

/// <summary>
/// Redacts personally identifying details (user profile paths, usernames, hostnames, hardware serials,
/// and sensitive registry values) before hardware discovery bundles are written to disk.
/// </summary>
public static partial class PrivacyScrubber
{
    private static readonly string? UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } p ? p : null;
    private static readonly string? UserName = Environment.UserName is { Length: > 1 } u ? u : null;
    private static readonly string? MachineName = Environment.MachineName is { Length: > 1 } m ? m : null;

    private static readonly string[] SensitiveKeyFragments =
    [
        "token", "secret", "password", "passwd", "auth", "cookie", "credential",
        "email", "mail", "account", "userid", "username", "serial", "uuid",
        "guid", "license", "licence", "apikey", "clientid", "session", "sid",
    ];

    private static readonly string[] AllowedRegistryFragments =
    [
        "mode", "fan", "battery", "charge", "manual", "pl1", "pl2", "tdp",
        "ppt", "stapm", "thermal", "power", "boost", "light", "rgb", "led",
        "bright", "autoswitch", "controller", "gamepad", "deadzone", "gyro",
        "macro", "remap", "fps", "refresh", "version", "shift", "cooler",
        "scenario", "preset", "profile", "curve", "temp", "rpm",
    ];

    /// <summary>Replaces user profile paths, usernames and hostnames inside freeform text or paths.</summary>
    public static string ScrubText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var result = value;

        if (UserProfile is not null)
            result = result.Replace(UserProfile, @"C:\Users\<USER>", StringComparison.OrdinalIgnoreCase);

        // Scrub any C:\Users\<name> pattern even if run as LocalSystem (where UserProfile is systemprofile).
        result = UsersPathRegex().Replace(result, @"$1<USER>");

        if (UserName is not null && !UserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            result = Regex.Replace(result, $@"\b{Regex.Escape(UserName)}\b", "<USER>", RegexOptions.IgnoreCase);

        if (MachineName is not null)
            result = Regex.Replace(result, $@"\b{Regex.Escape(MachineName)}\b", "<HOST>", RegexOptions.IgnoreCase);

        return result;
    }

    /// <summary>
    /// Strips the unique instance/serial tail from a Windows PNP or HID device path while keeping
    /// the bus, VID, PID, MI, Col, and subsystem IDs needed to identify the hardware.
    /// </summary>
    public static string ScrubDevicePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var cleaned = path.Trim();
        // HID interface path: \\?\hid#vid_0db0&pid_1901&mi_03&col02#8&1a2b3c4d&0&0001#{4d1e55b2-...}
        var parts = cleaned.Split('#');
        if (parts.Length >= 4 && parts[0].StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            parts[2] = "<INSTANCE>";
            return string.Join('#', parts);
        }

        // PNP ID: PCI\VEN_8086&DEV_64A0&SUBSYS_1T911462&REV_04\3&11583659&0&10
        var slashParts = cleaned.Split('\\');
        if (slashParts.Length >= 3)
        {
            slashParts[2] = "<INSTANCE>";
            return string.Join('\\', slashParts);
        }

        return ScrubText(cleaned);
    }

    /// <summary>Returns true if an OEM registry value name is safe to include in a discovery report.</summary>
    public static bool IsSafeRegistryValueName(string valueName)
    {
        if (string.IsNullOrWhiteSpace(valueName)) return false;

        foreach (var fragment in SensitiveKeyFragments)
        {
            if (valueName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>Returns true if a registry value name looks relevant to handheld hardware control.</summary>
    public static bool IsHardwareRelevantRegistryName(string valueName)
    {
        if (!IsSafeRegistryValueName(valueName)) return false;

        foreach (var fragment in AllowedRegistryFragments)
        {
            if (valueName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)([^\\/\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UsersPathRegex();
}
