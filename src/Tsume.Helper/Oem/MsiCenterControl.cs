using System.Diagnostics;

namespace Tsume.Helper.Oem;

/// <summary>
/// Pauses and resumes MSI Center M without uninstalling anything. A scheduled task starts a supervisor that
/// restarts every other piece within a second, so: disable the tasks, stop the supervisor first, then the rest.
/// </summary>
// The "MSI Foundation Service" is left alone: it only acts when asked to.
internal static class MsiCenterControl
{
    private const string SupervisorTask = "MSI_Center_M_Server";
    private const string UpdaterTask = "MSI_Center_M_Updater";

    private const string Supervisor = "MSI_Center_M_Server";

    /// <summary>What the supervisor leaves behind when it is stopped abruptly.</summary>
    private static readonly string[] Leftovers =
    [
        "MSI_Center_M_Launcher",   // listens for the hardware buttons
        "Command Center",          // backend of the Game Bar Quick Settings widget
        "MCMOSDInfo",              // the on-screen performance monitor
        "MSI Center M",            // the main app window
    ];

    public static void Pause()
    {
        Schtasks($"/Change /TN \"{SupervisorTask}\" /DISABLE");
        Schtasks($"/Change /TN \"{UpdaterTask}\" /DISABLE");
        Schtasks($"/End /TN \"{SupervisorTask}\"");

        Stop(name => name == Supervisor);
        Stop(name => name.StartsWith(Supervisor + "_", StringComparison.Ordinal) || Leftovers.Contains(name));

        Log.Write("MSI Center M paused.");
    }

    public static void Resume()
    {
        Schtasks($"/Change /TN \"{SupervisorTask}\" /ENABLE");
        Schtasks($"/Change /TN \"{UpdaterTask}\" /ENABLE");
        Schtasks($"/Run /TN \"{SupervisorTask}\"");

        Log.Write("MSI Center M resumed.");
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

    private static void Schtasks(string arguments)
    {
        using var schtasks = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

        schtasks.WaitForExit(10_000);
        if (schtasks.ExitCode != 0) Log.Write($"schtasks {arguments} exited with {schtasks.ExitCode}.");
    }
}
