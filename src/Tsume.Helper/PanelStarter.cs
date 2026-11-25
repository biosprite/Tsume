using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
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
    private static readonly TimeSpan WakePause = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CrashRestartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);
    private const int MaxCrashRestartsInWindow = 3;

    /// <summary>Serialises boot/logon/unlock/request/recovery start attempts so overlapping events never double-launch.</summary>
    private static readonly Lock LaunchGate = new();
    private static readonly Lock CrashGate = new();
    private static readonly Dictionary<int, Queue<DateTime>> RecentCrashes = [];
    private static CancellationToken _serviceStop = CancellationToken.None;

    /// <summary>Registers the service's stop token so crash recovery and startup waits abort cleanly on stop/shutdown.</summary>
    public static void SetServiceStop(CancellationToken stop) => _serviceStop = stop;

    /// <summary>The session shown on the device's own screen; -1 if there is none.</summary>
    public static int ScreenSession => unchecked((int)WTSGetActiveConsoleSessionId());

    /// <summary>Returns the Windows user SID signed into <paramref name="session"/>, or null if unavailable.</summary>
    internal static string? UserSidForSession(int session)
    {
        if (session < 0 || !WTSQueryUserToken((uint)session, out var person)) return null;
        try
        {
            using var identity = new WindowsIdentity(person);
            return identity.User?.Value;
        }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        finally
        {
            CloseHandle(person);
        }
    }

    /// <summary>Waits briefly after wake from sleep/hibernate and starts the panel if it is not running.</summary>
    public static void EnsureRunningAfterWake(int session, CancellationToken stop = default)
    {
        if (session < 0 || stop.WaitHandle.WaitOne(WakePause)) return;
        StartFor(session, "wake", stop);
    }

    /// <param name="occasion">Why now — "sign-in", "start-up", "unlock", "wake" — for the log.</param>
    public static void StartFor(int session, string occasion, CancellationToken stop = default)
    {
        if (session < 0 || stop.IsCancellationRequested || !WTSQueryUserToken((uint)session, out var person)) return;   // nobody signed in there

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, Panel);

            // Compared against the person's start-up entry, which has no --relaunched; Start adds that itself.
            var command = $"\"{path}\" {Quietly}";

            if (!File.Exists(path) || !Wants(person, command, stop)) return;

            lock (LaunchGate)
            {
                if (stop.IsCancellationRequested || IsPanelRunningIn(session)) return;

                WaitForDesktop(session, stop);
                if (stop.IsCancellationRequested || IsPanelRunningIn(session)) return;

                for (var attempt = 1; attempt <= LaunchAttempts && !stop.IsCancellationRequested; attempt++)
                {
                    var granted = Start(person, path, command, VerifyAliveMs, out var exitedCode, out var processHandle);
                    if (exitedCode is null)
                    {
                        Log.Write($"Started the panel{(granted ? " with UIAccess" : "")} for session {session} on {occasion}.");
                        WatchForCrash(session, processHandle);
                        return;
                    }

                    // Another copy (e.g. Explorer's Run entry) may have won the single-instance lock while we started.
                    if (exitedCode == 0 && IsRunningIn(session)) return;

                    Log.Write($"Panel exited early (code 0x{exitedCode.Value:X8}) on {occasion}, attempt {attempt}/{LaunchAttempts}.");
                    if (attempt < LaunchAttempts && stop.WaitHandle.WaitOne(RetryPause)) return;
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
    /// <returns>Whether a copy was started (or a UIAccess copy was already running for a background request).</returns>
    public static bool StartForRequest(int session, StartPanel request)
    {
        if (session < 0 || !WTSQueryUserToken((uint)session, out var person)) return false;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, Panel);
            if (!File.Exists(path)) return false;

            lock (LaunchGate)
            {
                // Avoid spawning a redundant copy when Explorer's Run entry arrives after the helper already started Tsume.
                if (request.Background && IsPanelRunningIn(session)) return true;

                var granted = Start(person, path, BuildCommand(path, request), verifyAliveMs: 0, out _, out var processHandle);
                Log.Write($"Started the panel{(granted ? " with UIAccess" : "")} for session {session} on request.");
                WatchForCrash(session, processHandle);
                return true;
            }
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
    private static bool Wants(nint person, string command, CancellationToken stop)
    {
        string? account;
        using (var identity = new WindowsIdentity(person)) account = identity.User?.Value;
        if (account is null) return false;

        for (var attempt = 0; attempt < SettingsAttempts && !stop.IsCancellationRequested; attempt++)
        {
            using var settings = Registry.Users.OpenSubKey(account);
            if (settings is null)
            {
                if (stop.WaitHandle.WaitOne(SettingsPause)) return false;
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
    /// Whether the panel is already running in <paramref name="session"/> with UIAccess (or any copy when
    /// UIAccess cannot be granted). Ignores a non-UIAccess Explorer Run trampoline waiting on the pipe.
    /// </summary>
    private static bool IsPanelRunningIn(int session) =>
        TcbEnabled.Value ? IsUiAccessRunningIn(session) : IsRunningIn(session);

    private static bool IsUiAccessRunningIn(int session)
    {
        var found = false;
        var processes = Process.GetProcessesByName("Tsume");
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    if (HasUiAccess(process.Id)) found = true;
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }

        return found;
    }

    private static bool HasUiAccess(int processId)
    {
        var proc = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, unchecked((uint)processId));
        if (proc == 0) return false;

        try
        {
            if (!OpenProcessToken(proc, TokenQuery, out var token)) return false;
            try
            {
                return GetTokenInformation(token, TokenUIAccess, out var uiAccess, sizeof(int), out _) && uiAccess != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(proc);
        }
    }

    /// <summary>
    /// Waits briefly for the interactive session's shell host to appear before launching WPF.
    /// On a cold boot (desktop or Xbox FSE), SessionLogon can fire while only pre-logon dwm.exe is running;
    /// checking sihost/explorer/XboxPcApp ensures the user desktop exists without adding delay once up.
    /// </summary>
    private static void WaitForDesktop(int session, CancellationToken stop)
    {
        for (var attempt = 0; attempt < DesktopAttempts && !stop.IsCancellationRequested; attempt++)
        {
            if (AnyProcessIn(session, "sihost")
                || AnyProcessIn(session, "explorer")
                || AnyProcessIn(session, "XboxPcApp")
                || AnyProcessIn(session, "XboxPcAppCE"))
            {
                return;
            }

            if (stop.WaitHandle.WaitOne(DesktopPause)) return;
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

    /// <summary>
    /// Watches a helper-started panel process with zero dedicated threads. If it exits abnormally (non-zero
    /// crash code, not a normal Quit or session logoff), restarts it quietly with UIAccess (rate-limited).
    /// Takes ownership of <paramref name="processHandle"/>.
    /// </summary>
    private static void WatchForCrash(int session, nint processHandle)
    {
        if (processHandle == 0) return;

        var waitEvent = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: true),
        };

        var waitGate = new Lock();
        RegisteredWaitHandle? registration = null;
        var completed = false;

        var reg = ThreadPool.RegisterWaitForSingleObject(
            waitEvent,
            (_, _) =>
            {
                uint exitCode = 0;
                try
                {
                    GetExitCodeProcess(waitEvent.SafeWaitHandle.DangerousGetHandle(), out exitCode);
                }
                catch (ObjectDisposedException) { }
                finally
                {
                    lock (waitGate)
                    {
                        completed = true;
                        registration?.Unregister(null);
                    }
                    waitEvent.Dispose();
                }

                // Exit code 0 is a clean Quit or single-instance handoff; 0x40010004 is Windows session logoff/shutdown.
                if (exitCode is 0 or DbgTerminateProcess || _serviceStop.IsCancellationRequested)
                    return;

                if (!RecordCrashWithinBudget(session))
                {
                    Log.Write($"Panel crashed (exit code 0x{exitCode:X8}) too often in session {session}; not restarting again.");
                    return;
                }

                Log.Write($"Panel crashed (exit code 0x{exitCode:X8}) in session {session}; restarting.");
                _ = Task.Run(() => RestartAfterCrash(session, _serviceStop));
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);

        lock (waitGate)
        {
            registration = reg;
            if (completed) reg.Unregister(null);
        }
    }

    private static bool RecordCrashWithinBudget(int session)
    {
        var now = DateTime.UtcNow;
        lock (CrashGate)
        {
            if (!RecentCrashes.TryGetValue(session, out var times))
                RecentCrashes[session] = times = new Queue<DateTime>();

            while (times.Count > 0 && now - times.Peek() > CrashWindow)
                times.Dequeue();

            if (times.Count >= MaxCrashRestartsInWindow) return false;
            times.Enqueue(now);
            return true;
        }
    }

    private static void RestartAfterCrash(int session, CancellationToken stop)
    {
        // Give WER / Windows a moment to finish tearing down the crashed process and release its mutex.
        if (stop.WaitHandle.WaitOne(CrashRestartDelay)) return;
        if (session < 0 || !WTSQueryUserToken((uint)session, out var person)) return;

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, Panel);
            if (!File.Exists(path)) return;

            var command = $"\"{path}\" {Quietly}";

            lock (LaunchGate)
            {
                if (stop.IsCancellationRequested || IsPanelRunningIn(session)) return;

                var granted = Start(person, path, command, VerifyAliveMs, out var exitedCode, out var processHandle);
                if (exitedCode is null)
                {
                    Log.Write($"Restarted the panel{(granted ? " with UIAccess" : "")} for session {session} after crash.");
                    WatchForCrash(session, processHandle);
                }
                else
                {
                    Log.Write($"Panel exited early (code 0x{exitedCode.Value:X8}) after crash restart.");
                }
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write($"Could not restart the panel after crash: {error.Message}");
        }
        finally
        {
            CloseHandle(person);
        }
    }

    // Every copy the helper starts carries --relaunched, whether or not UIAccess took, so a grant that quietly
    // failed can never send the copy back to ask again and set off a loop of launches.
    private static bool Start(nint person, string path, string command, uint verifyAliveMs, out int? exitedCode, out nint processHandle)
    {
        exitedCode = null;
        processHandle = 0;
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

                CloseHandle(started.Thread);

                if (verifyAliveMs > 0 && WaitForSingleObject(started.Process, verifyAliveMs) == WaitObject0)
                {
                    exitedCode = GetExitCodeProcess(started.Process, out var code) ? unchecked((int)code) : -1;
                    CloseHandle(started.Process);
                }
                else
                {
                    processHandle = started.Process;
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

    /// <summary>
    /// Starts <paramref name="path"/> in <paramref name="session"/> as the person signed in there, with the full-rights
    /// half of their administrator token, so a program whose manifest demands elevation starts without a UAC prompt.
    /// Only for a program path the helper resolved itself (RTSS from its own install record), never a caller's path.
    /// A standard user has no full-rights token, so nothing starts for them.
    /// </summary>
    internal static bool StartElevated(int session, string path, string arguments)
    {
        if (session < 0 || !WTSQueryUserToken((uint)session, out var person)) return false;

        try
        {
            var token = FullRightsToken(person);
            if (token == 0)
            {
                Log.Write($"No administrator token in session {session}; not starting {Path.GetFileName(path)}");
                return false;
            }

            try
            {
                if (!CreateEnvironmentBlock(out var environment, token, inherit: false)) throw new Win32Exception();

                try
                {
                    var start = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfo>(),
                        Desktop = @"winsta0\default",
                        Flags = StartfUseShowWindow,
                        ShowWindow = SwShowMinNoActive,
                    };
                    var command = string.IsNullOrWhiteSpace(arguments)
                        ? new StringBuilder($"\"{path}\"")
                        : new StringBuilder($"\"{path}\" {arguments}");

                    if (!CreateProcessAsUser(token, path, command, 0, 0, false, UnicodeEnvironment,
                            environment, Path.GetDirectoryName(path)!, ref start, out var started))
                    {
                        throw new Win32Exception();
                    }

                    CloseHandle(started.Thread);
                    CloseHandle(started.Process);
                    return true;
                }
                finally
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch (Win32Exception error)
        {
            Log.Write($"Could not start {Path.GetFileName(path)} elevated: {error.Message}");
            return false;
        }
        finally
        {
            CloseHandle(person);
        }
    }

    /// <summary>
    /// The person's elevated token: the linked full token when UAC split it (needs SeTcbPrivilege for a primary
    /// token), or a copy of the token itself when it is already elevated. Zero for a standard user.
    /// </summary>
    private static nint FullRightsToken(nint person)
    {
        if (!GetTokenInformation(person, TokenElevationType, out var type, sizeof(int), out _)) return 0;

        if (type == ElevationTypeLimited)
        {
            if (!TcbEnabled.Value) return 0;
            return GetTokenHandle(person, TokenLinkedToken, out var linked, IntPtr.Size, out _) ? linked : 0;
        }

        if (!GetTokenInformation(person, TokenElevation, out var elevated, sizeof(int), out _) || elevated == 0) return 0;
        return DuplicateTokenEx(person, TokenForLaunch, 0, SecurityImpersonation, TokenPrimary, out var copy) ? copy : 0;
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
    private const uint DbgTerminateProcess = 0x4001_0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int StartfUseShowWindow = 0x0001;   // STARTF_USESHOWWINDOW
    private const short SwShowMinNoActive = 7;        // SW_SHOWMINNOACTIVE

    private const int TokenUIAccess = 26;             // TOKEN_INFORMATION_CLASS.TokenUIAccess
    private const int TokenElevationType = 18;        // TOKEN_INFORMATION_CLASS.TokenElevationType
    private const int TokenLinkedToken = 19;          // TOKEN_INFORMATION_CLASS.TokenLinkedToken
    private const int TokenElevation = 20;            // TOKEN_INFORMATION_CLASS.TokenElevation
    private const int ElevationTypeLimited = 3;       // TOKEN_ELEVATION_TYPE.TokenElevationTypeLimited
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
    private static extern bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetTokenHandle(nint token, int infoClass, out nint info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges newState,
        int bufferLength, nint previous, nint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
