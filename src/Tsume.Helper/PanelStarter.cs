using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Tsume.Core.Ipc;

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
    private const string EntryName = "Tsume";        // StartWithWindows.Name

    // The panel's own start-up switches (App.xaml.cs). The helper builds a command from whitelisted flags, never
    // from a string a caller sends, so an unelevated program cannot smuggle arguments into a UIAccess launch.
    private const string Quietly = "--background";   // App.BackgroundSwitch
    private const string Relaunched = "--relaunched"; // App.RelaunchedSwitch; marks a copy the helper started, so it never asks again
    private const string Settings = "--settings";    // App.OpenSettingsSwitch
    private const string Lighting = "--lighting";    // App.OpenLightingSwitch
    private const string OptiScaler = "--optiscaler"; // App.OpenOptiScalerSwitch
    private const string Rtss = "--rtss";            // App.OpenRtssSwitch
    private const string Stay = "--stay";            // App.StayOpenSwitch

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The person's settings are loaded a moment after sign-in is announced.</summary>
    private const int SettingsAttempts = 40;
    private static readonly TimeSpan SettingsPause = TimeSpan.FromMilliseconds(250);

    /// <summary>Wait up to 15 s for the session compositor/shell on cold boot (especially straight into Xbox FSE).</summary>
    private const int DesktopAttempts = 60;
    private static readonly TimeSpan DesktopPause = TimeSpan.FromMilliseconds(250);

    private const int LaunchAttempts = 3;
    private const uint VerifyAliveMs = 3000;
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(3);

    /// <summary>Serialises boot/logon/unlock start attempts so overlapping session events never double-launch.</summary>
    private static readonly Lock LaunchGate = new();

    /// <summary>The session shown on the device's own screen; -1 if there is none.</summary>
    public static int ScreenSession => unchecked((int)WTSGetActiveConsoleSessionId());

    /// <param name="occasion">Why now — "sign-in", "start-up", "unlock" — for the log.</param>
    public static void StartFor(int session, string occasion)
    {
        if (session < 0 || !WTSQueryUserToken((uint)session, out var person)) return;   // nobody signed in there

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, Panel);

            // Compared against the person's start-up entry, which has no --relaunched; Start adds that itself.
            var command = $"\"{path}\" {Quietly}";

            if (!File.Exists(path) || !Wants(person, command)) return;

            lock (LaunchGate)
            {
                if (IsRunningIn(session)) return;

                WaitForDesktop(session);
                if (IsRunningIn(session)) return;

                for (var attempt = 1; attempt <= LaunchAttempts; attempt++)
                {
                    var granted = Start(person, path, command, VerifyAliveMs, out var exitedCode);
                    if (exitedCode is null)
                    {
                        Log.Write($"Started the panel{(granted ? " with UIAccess" : "")} for session {session} on {occasion}.");
                        return;
                    }

                    // Another copy (e.g. Explorer's Run entry) may have won the single-instance lock while we started.
                    if (IsRunningIn(session)) return;

                    Log.Write($"Panel exited early (code {exitedCode}) on {occasion}, attempt {attempt}/{LaunchAttempts}.");
                    if (attempt < LaunchAttempts) Thread.Sleep(RetryPause);
                }
            }
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

    /// <summary>
    /// Starts the panel for the person at the given session, in answer to a copy that found itself without UIAccess.
    /// The caller's session is settled from the pipe, not sent in the request (see <see cref="PipeServer"/>), and the
    /// program started is always the Tsume.exe beside this helper.
    /// </summary>
    /// <returns>Whether a copy was started.</returns>
    public static bool StartForRequest(int session, StartPanel request)
    {
        if (session < 0 || !WTSQueryUserToken((uint)session, out var person)) return false;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, Panel);
            if (!File.Exists(path)) return false;

            var granted = Start(person, path, BuildCommand(path, request), verifyAliveMs: 0, out _);
            Log.Write($"Started the panel{(granted ? " with UIAccess" : "")} for session {session} on request.");
            return true;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not start the panel on request: {error.Message}");
            return false;
        }
        finally
        {
            CloseHandle(person);
        }
    }

    /// <summary>Builds the command line from whitelisted flags only. Start appends <see cref="Relaunched"/>.</summary>
    private static string BuildCommand(string path, StartPanel request)
    {
        var command = new StringBuilder($"\"{path}\"");

        if (request.Background) command.Append(' ').Append(Quietly);
        if (request.Stay) command.Append(' ').Append(Stay);
        if (request.Settings) command.Append(' ').Append(Settings);
        if (request.Lighting) command.Append(' ').Append(Lighting);
        if (request.OptiScaler) command.Append(' ').Append(OptiScaler);
        if (request.Rtss) command.Append(' ').Append(Rtss);

        return command.ToString();
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

    /// <summary>Whether Tsume is already running in <paramref name="session"/>.</summary>
    private static bool IsRunningIn(int session) => AnyProcessIn(session, "Tsume");

    /// <summary>
    /// Waits briefly for the interactive session's compositor or shell host to appear before launching WPF.
    /// On a cold boot straight into Xbox FSE, SessionLogon can fire before DWM/shell have finished setting up
    /// the desktop; returning immediately when the session is already up adds zero delay.
    /// </summary>
    private static void WaitForDesktop(int session)
    {
        for (var attempt = 0; attempt < DesktopAttempts; attempt++)
        {
            if (AnyProcessIn(session, "sihost") || AnyProcessIn(session, "dwm")) return;
            Thread.Sleep(DesktopPause);
        }
    }

    private static bool AnyProcessIn(int session, string name)
    {
        var found = false;
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == session) found = true;
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }

        return found;
    }

    // Every copy the helper starts carries --relaunched, whether or not UIAccess took, so a grant that quietly
    // failed can never send the copy back to ask again and set off a loop of launches.
    private static bool Start(nint person, string path, string command, uint verifyAliveMs, out int? exitedCode)
    {
        exitedCode = null;
        command = $"{command} {Relaunched}";

        var uiAccess = GrantUiAccess(person);
        var token = uiAccess != 0 ? uiAccess : person;

        try
        {
            if (!CreateEnvironmentBlock(out var environment, token, inherit: false)) throw new Win32Exception();

            try
            {
                var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };

                if (!CreateProcessAsUser(token, path, new StringBuilder(command), 0, 0, false, UnicodeEnvironment,
                        environment, AppContext.BaseDirectory, ref start, out var started))
                {
                    throw new Win32Exception();
                }

                try
                {
                    if (verifyAliveMs > 0 && WaitForSingleObject(started.Process, verifyAliveMs) == WaitObject0)
                        exitedCode = GetExitCodeProcess(started.Process, out var code) ? unchecked((int)code) : -1;
                }
                finally
                {
                    CloseHandle(started.Thread);
                    CloseHandle(started.Process);
                }

                return uiAccess != 0;
            }
            finally
            {
                DestroyEnvironmentBlock(environment);
            }
        }
        finally
        {
            if (uiAccess != 0) CloseHandle(uiAccess);
        }
    }

    /// <summary>
    /// Copies the person's token and turns on UIAccess, the flag that lets the panel's window sit in the band above
    /// the taskbar, Start, Search, and elevated windows. The copy keeps the person's medium integrity and
    /// limited rights, so the panel still runs as them, unelevated. Setting the flag needs SeTcbPrivilege, which the
    /// system account holds. Returns zero if any step fails, and the caller falls back to the plain token (today's
    /// behaviour, without UIAccess) rather than not starting the panel at all.
    /// </summary>
    private static nint GrantUiAccess(nint person)
    {
        if (!TcbEnabled.Value) return 0;

        if (!DuplicateTokenEx(person, TokenForLaunch, 0, SecurityImpersonation, TokenPrimary, out var copy))
            return 0;

        var on = 1;
        if (!SetTokenInformation(copy, TokenUIAccess, ref on, sizeof(int)))
        {
            Log.Write($"Could not turn on UIAccess: {new Win32Exception().Message}");
            CloseHandle(copy);
            return 0;
        }

        return copy;
    }

    private static readonly Lazy<bool> TcbEnabled = new(EnableTcbPrivilege);

    /// <summary>Enables SeTcbPrivilege on this service once, so <see cref="SetTokenInformation"/> may set UIAccess.</summary>
    private static bool EnableTcbPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var me)) return false;

        try
        {
            if (!LookupPrivilegeValue(null, "SeTcbPrivilege", out var luid)) return false;

            var privileges = new TokenPrivileges
            {
                Count = 1,
                LuidLow = unchecked((int)luid),
                LuidHigh = (int)(luid >> 32),
                Attributes = SePrivilegeEnabled,
            };

            // AdjustTokenPrivileges reports success even when it changed nothing; the last error tells the two apart.
            return AdjustTokenPrivileges(me, false, ref privileges, 0, 0, 0)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(me);
        }
    }

    private const uint UnicodeEnvironment = 0x0400;   // CREATE_UNICODE_ENVIRONMENT
    private const uint WaitObject0 = 0x0000_0000;     // WAIT_OBJECT_0

    private const int TokenUIAccess = 26;             // TOKEN_INFORMATION_CLASS.TokenUIAccess
    private const int TokenPrimary = 1;               // TOKEN_TYPE.TokenPrimary
    private const int SecurityImpersonation = 2;      // SECURITY_IMPERSONATION_LEVEL
    private const int SePrivilegeEnabled = 0x2;       // SE_PRIVILEGE_ENABLED
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;

    // Enough to copy the token, set UIAccess on the copy, and launch with it:
    // TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT.
    private const uint TokenForLaunch = 0x0001 | 0x0002 | 0x0008 | 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int Count;
        public int LuidLow, LuidHigh;   // one LUID_AND_ATTRIBUTES, kept as ints so the layout needs no padding
        public int Attributes;
    }

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint existing, uint desiredAccess, nint attributes,
        int impersonationLevel, int tokenType, out nint copy);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(nint token, int infoClass, ref int info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges newState,
        int bufferLength, nint previous, nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
