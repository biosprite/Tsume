using System.Diagnostics;
using System.Text.Json;
using Tsume.Core;

namespace Tsume.Helper.Oem;

/// <summary>
/// Pauses and resumes Acer PredatorSense for HandHeld without uninstalling anything.
/// Stops <c>AASHHSvc</c> first (which cleanly terminates <c>AcerAgentService</c> and <c>AcerHardwareService</c>
/// instead of respawning them), pauses lighting services (<c>AcerLightingServiceHH</c>, <c>ALSSvc</c>), sets their
/// start modes to demand (remembering originals), disables the logon task <c>PredatorHandheldLauncher</c>, and kills
/// leftover UI processes. Leaves <c>AcerHandheld.Service</c>, <c>AcerServiceSvc</c>, and <c>AcerCCAgentSvis</c> alone.
/// </summary>
internal static class AcerSenseControl
{
    private const string LauncherTask = "PredatorHandheldLauncher";

    private static readonly string[] ManagedServices =
    [
        "AASHHSvc",
        "AcerLightingServiceHH",
        "ALSSvc",
    ];

    private static readonly string[] LeftoverProcesses =
    [
        "AcerAgentService",
        "AcerHardwareService",
        "PredatorHandheldLauncher",
        "Handheld.Desktop",
        "Handheld.Widget",
        "Handheld.OSD",
        "SenseOverlay",
        "OpenRGB",
    ];

    private static readonly string SavedStartTypesPath = Path.Combine(DataFolder.Path, "acer-services.json");

    public static void Pause()
    {
        RunTool("schtasks.exe", $"/Change /TN \"{LauncherTask}\" /DISABLE");

        var saved = LoadStartTypes();
        foreach (var service in ManagedServices)
        {
            if (!saved.ContainsKey(service) && QueryStartType(service) is { } original)
                saved[service] = original;

            RunTool("sc.exe", $"stop \"{service}\"");
            RunTool("sc.exe", $"config \"{service}\" start= demand");
        }
        SaveStartTypes(saved);

        Stop(name => LeftoverProcesses.Contains(name, StringComparer.OrdinalIgnoreCase));

        Log.Write("PredatorSense paused.");
    }

    /// <remarks>
    /// The uninstaller runs this as SYSTEM, which cannot see the user's saved start types. Without one, a service
    /// that is not installed is skipped and one set to disabled is left alone (pausing never disables anything, so
    /// that was the user's choice); the rest go back to auto, the start type PredatorSense's INFs install.
    /// </remarks>
    public static void Resume()
    {
        var saved = LoadStartTypes();
        var anyInstalled = false;
        foreach (var service in ManagedServices)
        {
            var current = QueryStartType(service);
            if (current is null) continue;   // not installed
            anyInstalled = true;

            string startType;
            if (saved.TryGetValue(service, out var original)) startType = original;
            else if (current == "disabled") continue;
            else startType = "auto";

            RunTool("sc.exe", $"config \"{service}\" start= {startType}");
            if (startType != "disabled") RunTool("sc.exe", $"start \"{service}\"");
        }

        if (!anyInstalled)
        {
            Log.Write("PredatorSense is not installed; nothing to resume.");
            return;
        }

        RunTool("schtasks.exe", $"/Change /TN \"{LauncherTask}\" /ENABLE");
        RunTool("schtasks.exe", $"/Run /TN \"{LauncherTask}\"");

        try { File.Delete(SavedStartTypesPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        Log.Write("PredatorSense resumed.");
    }

    private static string? QueryStartType(string service)
    {
        var output = RunToolCapture("sc.exe", $"qc \"{service}\"");
        if (string.IsNullOrEmpty(output)) return null;

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("DELAYED", StringComparison.OrdinalIgnoreCase)) return "delayed-auto";
            if (line.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase)) return "auto";
            if (line.Contains("DEMAND_START", StringComparison.OrdinalIgnoreCase)) return "demand";
            if (line.Contains("DISABLED", StringComparison.OrdinalIgnoreCase)) return "disabled";
        }

        return null;
    }

    private static Dictionary<string, string> LoadStartTypes()
    {
        try
        {
            if (File.Exists(SavedStartTypesPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SavedStartTypesPath))
                    ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveStartTypes(Dictionary<string, string> types)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavedStartTypesPath)!);
            var temp = SavedStartTypesPath + ".tsume-tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(types));
            File.Move(temp, SavedStartTypesPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not save Acer service start types: {error.Message}");
        }
    }

    private static void Stop(Func<string, bool> matches)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!matches(process.ProcessName)) continue;

                try
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Log.Write($"Could not stop {process.ProcessName}: {error.Message}");
                }
            }
        }
    }

    private static void RunTool(string fileName, string arguments)
    {
        var exe = Path.Combine(Environment.SystemDirectory, fileName);
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            Log.Write($"Could not start {fileName} {arguments}.");
            return;
        }

        if (!process.WaitForExit(10_000))
        {
            Log.Write($"{fileName} {arguments} timed out.");
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return;
        }

        if (process.ExitCode != 0) Log.Write($"{fileName} {arguments} exited with {process.ExitCode}.");
    }

    private static string? RunToolCapture(string fileName, string arguments)
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, fileName);
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}
