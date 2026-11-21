using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using Tsume.Core.Diagnostics;

namespace Tsume.Hardware.Diagnostics;

/// <summary>
/// Collects a privacy-filtered footprint of the handheld manufacturer's OEM software:
/// matching processes, services, scheduled tasks, and hardware-relevant config registry values.
/// Never enumerates unrelated user applications or credentials.
/// </summary>
public static class OemFootprintCollector
{
    private static readonly string[] BaseOemKeywords =
    [
        "msi", "micro-star", "claw", "mystic",
        "asus", "asustek", "armoury", "rog", "acpower",
        "lenovo", "legion", "vantage",
        "gpd", "ayaneo", "ayaspace", "onex", "aokzoe", "zotac", "acer", "nitro", "predator",
        "handheldcompanion", "hhd", "ryzenadj",
    ];

    private static readonly (RegistryHive Hive, string SubKey)[] KnownOemRegistryRoots =
    [
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\MSI\MSI Center M"),
        (RegistryHive.LocalMachine, @"SOFTWARE\MSI\MSI Center M"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\ASUS\ARMOURY CRATE Service"),
        (RegistryHive.LocalMachine, @"SOFTWARE\ASUS\ARMOURY CRATE Service"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Lenovo\LegionSpace"),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Lenovo\LegionSpace"),
    ];

    public static OemFootprintSnapshot Collect(string? boardManufacturer, string? systemManufacturer)
    {
        var keywords = BuildKeywords(boardManufacturer, systemManufacturer);

        return new OemFootprintSnapshot
        {
            VendorKeywords = keywords,
            Processes = CollectProcesses(keywords),
            Services = CollectServices(keywords),
            ScheduledTasks = CollectScheduledTasks(keywords),
            RegistryValues = CollectOemRegistry(),
        };
    }

    public static Dictionary<string, string> CollectOemRegistry()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, subKey) in KnownOemRegistryRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var root = baseKey.OpenSubKey(subKey);
                if (root is null) continue;
                WalkRegistry(root, $"{hive}\\{subKey}", values, maxDepth: 3);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return values;
    }

    private static void WalkRegistry(RegistryKey key, string path, Dictionary<string, string> output, int maxDepth)
    {
        if (output.Count >= 300) return;

        foreach (var name in key.GetValueNames())
        {
            if (output.Count >= 300) break;
            if (!PrivacyScrubber.IsHardwareRelevantRegistryName(name)) continue;

            var raw = key.GetValue(name);
            var formatted = FormatRegistryValue(raw);
            if (formatted is not null)
                output[$"{path}\\{name}"] = PrivacyScrubber.ScrubText(formatted);
        }

        if (maxDepth <= 0) return;

        foreach (var childName in key.GetSubKeyNames())
        {
            if (output.Count >= 300) break;
            try
            {
                using var child = key.OpenSubKey(childName);
                if (child is not null) WalkRegistry(child, $"{path}\\{childName}", output, maxDepth - 1);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    private static string? FormatRegistryValue(object? raw) => raw switch
    {
        null => null,
        string s when s.Length <= 200 => s,
        int i => i.ToString(),
        long l => l.ToString(),
        byte[] bytes when bytes.Length <= 64 => Convert.ToHexString(bytes),
        string[] arr when arr.Length <= 16 => string.Join("; ", arr),
        _ => null,
    };

    private static List<string> BuildKeywords(string? boardMaker, string? sysMaker)
    {
        var set = new HashSet<string>(BaseOemKeywords, StringComparer.OrdinalIgnoreCase);

        foreach (var raw in new[] { boardMaker, sysMaker })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // Keep hyphenated vendor names intact (e.g. "micro-star") so "micro" doesn't match Microsoft.
            foreach (var token in raw.Split([' ', '_', '.', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length >= 3 && !IsGenericCompanyWord(token))
                    set.Add(token.ToLowerInvariant());
            }
        }

        return [.. set.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsGenericCompanyWord(string word) =>
        word.Equals("inc", StringComparison.OrdinalIgnoreCase)
        || word.Equals("corp", StringComparison.OrdinalIgnoreCase)
        || word.Equals("corporation", StringComparison.OrdinalIgnoreCase)
        || word.Equals("ltd", StringComparison.OrdinalIgnoreCase)
        || word.Equals("limited", StringComparison.OrdinalIgnoreCase)
        || word.Equals("llc", StringComparison.OrdinalIgnoreCase)
        || word.Equals("co", StringComparison.OrdinalIgnoreCase)
        || word.Equals("computer", StringComparison.OrdinalIgnoreCase)
        || word.Equals("computers", StringComparison.OrdinalIgnoreCase)
        || word.Equals("technology", StringComparison.OrdinalIgnoreCase)
        || word.Equals("technologies", StringComparison.OrdinalIgnoreCase)
        || word.Equals("electronics", StringComparison.OrdinalIgnoreCase)
        || word.Equals("international", StringComparison.OrdinalIgnoreCase)
        || word.Equals("systems", StringComparison.OrdinalIgnoreCase)
        || word.Equals("micro", StringComparison.OrdinalIgnoreCase)
        || word.Equals("star", StringComparison.OrdinalIgnoreCase)
        || word.Equals("microsoft", StringComparison.OrdinalIgnoreCase)
        || word.Equals("windows", StringComparison.OrdinalIgnoreCase)
        || word.Equals("default", StringComparison.OrdinalIgnoreCase)
        || word.Equals("string", StringComparison.OrdinalIgnoreCase)
        || word.Equals("filled", StringComparison.OrdinalIgnoreCase)
        || word.Equals("oem", StringComparison.OrdinalIgnoreCase)
        || word.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static List<OemProcessInfo> CollectProcesses(IReadOnlyList<string> keywords)
    {
        var results = new List<OemProcessInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path = null;
                string? product = null;
                string? version = null;
                try
                {
                    path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        var info = FileVersionInfo.GetVersionInfo(path);
                        product = info.ProductName;
                        version = info.FileVersion;
                    }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                }

                if (!Matches(keywords, process.ProcessName, path, product)) continue;

                var dedupKey = $"{process.ProcessName}|{path}";
                if (!seen.Add(dedupKey)) continue;

                results.Add(new OemProcessInfo(
                    process.ProcessName,
                    PrivacyScrubber.ScrubText(path),
                    version,
                    product));
            }
        }

        return [.. results.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<OemServiceInfo> CollectServices(IReadOnlyList<string> keywords)
    {
        var results = new List<OemServiceInfo>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name, DisplayName, StartMode, State, PathName FROM Win32_Service");
            using var rows = search.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    var name = $"{row["Name"]}";
                    var display = $"{row["DisplayName"]}";
                    var path = $"{row["PathName"]}";
                    if (!Matches(keywords, name, display, path)) continue;

                    results.Add(new OemServiceInfo(
                        name,
                        display,
                        $"{row["StartMode"]}",
                        $"{row["State"]}",
                        PrivacyScrubber.ScrubText(path)));
                }
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return [.. results.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<OemScheduledTaskInfo> CollectScheduledTasks(IReadOnlyList<string> keywords)
    {
        var results = new List<OemScheduledTaskInfo>();
        try
        {
            using var search = new ManagementObjectSearcher(@"root\Microsoft\Windows\TaskScheduler", "SELECT TaskName, TaskPath, State, Actions FROM MSFT_ScheduledTask");
            using var rows = search.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    var name = $"{row["TaskName"]}";
                    var taskPath = $"{row["TaskPath"]}";
                    var actions = ExtractTaskActions(row["Actions"]);
                    if (!Matches(keywords, name, taskPath, actions)) continue;

                    var state = row["State"] switch
                    {
                        ushort s => s switch { 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => s.ToString() },
                        _ => $"{row["State"]}",
                    };

                    results.Add(new OemScheduledTaskInfo(
                        taskPath,
                        name,
                        state,
                        PrivacyScrubber.ScrubText(actions)));
                }
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return [.. results.OrderBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase)];
    }

    private static string ExtractTaskActions(object? rawActions)
    {
        if (rawActions is not ManagementBaseObject[] actions) return "";
        var parts = new List<string>();
        foreach (var act in actions)
        {
            var exe = act["Execute"]?.ToString();
            if (!string.IsNullOrWhiteSpace(exe)) parts.Add(exe);
        }

        return string.Join("; ", parts);
    }

    private static bool Matches(IReadOnlyList<string> keywords, params string?[] fields)
    {
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field)) continue;

            // Tokenize on path/word separators so short OEM tags ("rog", "msi", "gpd", "hhd")
            // never false-match inside unrelated words like "Program Files" or "amsi".
            var tokens = field.Split(['\\', '/', ' ', '.', '_', '-', '(', ')', ':'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var kw in keywords)
            {
                if (kw.Length <= 4)
                {
                    foreach (var token in tokens)
                    {
                        if (token.Equals(kw, StringComparison.OrdinalIgnoreCase)
                            || token.StartsWith(kw, StringComparison.OrdinalIgnoreCase) && token.Length <= kw.Length + 6 && !IsFalsePositiveToken(token))
                            return true;
                    }
                }
                else if (field.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsFalsePositiveToken(string token) =>
        token.Equals("msiserver", StringComparison.OrdinalIgnoreCase)
        || token.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
}
