using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Tsume.App.Platform;

/// <summary>
/// Whether MSI Center M is running, and pausing or resuming it. Pausing changes system scheduled
/// tasks, so <c>Tsume.Helper</c> does it elevated (a UAC prompt each time); nothing is uninstalled.
/// </summary>
public static class MsiCenter
{
    private const string Supervisor = "MSI_Center_M_Server";

    public static bool IsRunning
    {
        get
        {
            var found = Process.GetProcessesByName(Supervisor);
            foreach (var process in found) process.Dispose();
            return found.Length > 0;
        }
    }

    /// <summary>True when the helper executable can be found.</summary>
    public static bool CanControl => HelperPath() is not null;

    public static Task<bool> PauseAsync() => RunHelperAsync("pause");

    public static Task<bool> ResumeAsync() => RunHelperAsync("resume");

    /// <returns>False if the helper is missing, failed, or the UAC prompt was declined.</returns>
    private static async Task<bool> RunHelperAsync(string verb)
    {
        if (HelperPath() is not { } helper) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(helper, $"--msi-center {verb}")
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

    /// <summary>Beside Tsume.exe once installed; in the neighbouring build folder during development.</summary>
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
