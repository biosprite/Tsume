using System.Diagnostics;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Platform;

/// <summary>The four things the power sheet can do to the machine.</summary>
public static class PowerActions
{
    public static void Hibernate() => PowrProf.SetSuspendState(hibernate: true, force: false, disableWakeEvents: false);

    public static void Lock() => User32.LockWorkStation();

    public static void Restart() => Shutdown("/r /t 0");

    public static void ShutDown() => Shutdown("/s /t 0");

    private static void Shutdown(string arguments) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false });
}
