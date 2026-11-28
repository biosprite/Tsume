using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
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

    private static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromSeconds(5);

    private static readonly string SavedStartTypesPath = Path.Combine(DataFolder.Path, "acer-services.json");

    public static void Pause()
    {
        RunTool("schtasks.exe", $"/Change /TN \"{LauncherTask}\" /DISABLE");

        var saved = LoadStartTypes();
        foreach (var service in ManagedServices)
        {
            var original = QueryStartType(service);
            if (original is null) continue;   // service not installed

            if (!saved.ContainsKey(service))
                saved[service] = original;

            // Switch to demand start first so SCM cannot auto-restart while stopping, then stop and wait.
            RunTool("sc.exe", $"config \"{service}\" start= demand");
            StopServiceAndWait(service);
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

    private static void StopServiceAndWait(string service)
    {
        try
        {
            using var controller = new ServiceController(service);
            if (controller.Status == ServiceControllerStatus.Stopped)
                return;

            if (controller.Status != ServiceControllerStatus.StopPending)
                controller.Stop();

            controller.WaitForStatus(ServiceControllerStatus.Stopped, ServiceStopTimeout);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            Log.Write($"Timed out waiting for service {service} to stop.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Write($"Could not stop service {service}: {error.Message}");
        }
    }

    /// <summary>Reads the service's SCM start mode from the registry (locale-independent, no child process).</summary>
    private static string? QueryStartType(string service)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
            if (key?.GetValue("Start") is not int start) return null;

            return start switch
            {
                2 => key.GetValue("DelayedAutostart") is int delayed && delayed != 0 ? "delayed-auto" : "auto",
                3 => "demand",
                4 => "disabled",
                _ => null,
            };
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
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
}
