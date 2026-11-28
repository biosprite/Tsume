using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Tsume.App.Platform;

/// <summary>
/// Whether Acer PredatorSense for HandHeld (or its lighting stack) is running, and pausing or resuming it
/// through <c>Tsume.Helper.exe --acer-sense pause|resume</c> (elevated). Nothing is uninstalled.
/// </summary>
public static class AcerSense
{
    private static readonly string[] ControlProcesses = ["Handheld.Desktop", "AcerAgentService"];
    private static readonly string[] LightingProcesses = ["AcerLightingService", "AcerLightingServiceHH", "ALSSvc", "OpenRGB"];

    public static bool IsRunning => AnyRunning(ControlProcesses);

    /// <summary>True when Acer's lighting service or OpenRGB server is running and may overwrite RGB changes.</summary>
    public static bool IsLightingRunning => AnyRunning(LightingProcesses);

    /// <summary>True when the helper executable can be found.</summary>
    public static bool CanControl => HelperPath() is not null;

    public static Task<bool> PauseAsync() => RunHelperAsync("pause");

    public static Task<bool> ResumeAsync() => RunHelperAsync("resume");

    private static bool AnyRunning(string[] names)
    {
        foreach (var name in names)
        {
            var found = Process.GetProcessesByName(name);
            var any = found.Length > 0;
            foreach (var process in found) process.Dispose();
            if (any) return true;
        }

        return false;
    }

    private static async Task<bool> RunHelperAsync(string verb)
    {
        if (HelperPath() is not { } helper) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(helper, $"--acer-sense {verb}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;   // the UAC prompt was declined
        }
    }

    private static string? HelperPath()
    {
        const string name = "Tsume.Helper.exe";
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        string[] candidates =
        [
            Path.Combine(here, name),
            Path.Combine(here, "..", "..", "Tsume.Helper", Path.GetFileName(here), name),
        ];

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}
