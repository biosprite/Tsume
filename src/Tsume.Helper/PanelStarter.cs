using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Tsume.Helper;

/// <summary>
/// Starts the panel for whoever signs in, as that person and without elevation. Windows holds its own start-up
/// apps back for some twenty seconds after the desktop appears; the service is running well before that.
/// </summary>
// The person's "Start with Windows" entry is only a yes or no. What is started is always the Tsume.exe beside
// this helper, never a path read from the registry. When Windows runs the entry later, that copy finds this one
// and exits.
internal static class PanelStarter
{
    private const string Panel = "Tsume.exe";
    private const string Quietly = "--background";   // App.BackgroundSwitch
    private const string EntryName = "Tsume";        // StartWithWindows.Name

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The person's settings are loaded a moment after sign-in is announced.</summary>
    private const int SettingsAttempts = 40;
    private static readonly TimeSpan SettingsPause = TimeSpan.FromMilliseconds(250);

    /// <summary>The session shown on the device's own screen; -1 if there is none.</summary>
    public static int ScreenSession => unchecked((int)WTSGetActiveConsoleSessionId());

    /// <param name="occasion">Why now — "sign-in", "start-up" — for the log.</param>
    public static void StartFor(int session, string occasion)
    {
        if (session < 0 || !WTSQueryUserToken((uint)session, out var person)) return;   // nobody signed in there

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, Panel);
            var command = $"\"{path}\" {Quietly}";

            if (!File.Exists(path) || !Wants(person, command)) return;

            Start(person, path, command);
            Log.Write($"Started the panel for session {session} on {occasion}.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not start the panel on {occasion}: {error.Message}");
        }
        finally
        {
            CloseHandle(person);
        }
    }

    /// <summary>True if the person's start-up entry asks for exactly this copy and Task Manager has not switched it off.</summary>
    private static bool Wants(nint person, string command)
    {
        string? account;
        using (var identity = new WindowsIdentity(person)) account = identity.User?.Value;
        if (account is null) return false;

        for (var attempt = 0; attempt < SettingsAttempts; attempt++)
        {
            using var settings = Registry.Users.OpenSubKey(account);
            if (settings is null)
            {
                Thread.Sleep(SettingsPause);
                continue;
            }

            using var run = settings.OpenSubKey(RunKey);
            if (!string.Equals(run?.GetValue(EntryName) as string, command, StringComparison.OrdinalIgnoreCase)) return false;

            // Task Manager → Startup apps keeps its switch here: an odd first byte means off.
            using var approved = settings.OpenSubKey(ApprovedKey);
            return approved?.GetValue(EntryName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }

        return false;
    }

    private static void Start(nint person, string path, string command)
    {
        if (!CreateEnvironmentBlock(out var environment, person, inherit: false)) throw new Win32Exception();

        try
        {
            var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };

            if (!CreateProcessAsUser(person, path, new StringBuilder(command), 0, 0, false, UnicodeEnvironment,
                    environment, AppContext.BaseDirectory, ref start, out var started))
            {
                throw new Win32Exception();
            }

            CloseHandle(started.Thread);
            CloseHandle(started.Process);
        }
        finally
        {
            DestroyEnvironmentBlock(environment);
        }
    }

    private const uint UnicodeEnvironment = 0x0400;   // CREATE_UNICODE_ENVIRONMENT

    // STARTUPINFOW
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, Input, Output, Error;
    }

    // PROCESS_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct Started
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint session, out nint token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(nint token, string application, StringBuilder command,
        nint processSecurity, nint threadSecurity, bool inheritHandles, uint flags, nint environment,
        string folder, ref StartupInfo start, out Started started);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
