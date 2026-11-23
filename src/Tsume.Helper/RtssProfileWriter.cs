using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Tsume.Core.Ipc;

namespace Tsume.Helper;

/// <summary>
/// Writes validated, clamped settings into RTSS's <c>Profiles\Global</c> file, manages Tsume-owned
/// per-game anti-cheat exclusion profiles (<c>EnableHooking=0</c>), and stops RTSS on behalf of
/// the unelevated panel. Never loads any third-party DLL into the service and never accepts a path
/// from the caller.
/// </summary>
internal static class RtssProfileWriter
{
    private const int MinPosition = -5000;
    private const int MaxPosition = 5000;
    private const int MinZoom = 1;
    private const int MaxZoom = 8;
    private const int MaxFramerateLimit = 240;
    private const int MaxExclusions = 512;

    private const string TsumeMarker = "; Created by Tsume";
    private const string RtssTaskName = @"\RTSS";
    private const string RtssRunValueName = "RTSS";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyWow64Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupApproved32Path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const int SchtasksTimeoutMs = 2500;

    private static readonly string[] RtssProcessNames =
    [
        "RTSS",
        "RTSSHooksLoader",
        "RTSSHooksLoader64",
        "EncoderServer",
        "EncoderServer64",
        "DesktopOverlayHost",
        "DesktopOverlayHost64",
    ];

    internal static readonly Lock FileGate = new();

    public static HelperReply Apply(SetRtssProfile request)
    {
        var installDir = FindRtssInstallDir();
        if (installDir is null)
            return HelperReply.Failure("RTSS is not installed.");

        var profilesDir = Path.Combine(installDir, "Profiles");
        var globalFile = Path.Combine(profilesDir, "Global");
        var templateFile = Path.Combine(installDir, "ProfileTemplates", "Global");

        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(profilesDir);

                var lines = File.Exists(globalFile)
                    ? File.ReadAllLines(globalFile, Encoding.UTF8).ToList()
                    : File.Exists(templateFile)
                        ? File.ReadAllLines(templateFile, Encoding.UTF8).ToList()
                        : [];

                if (request.FramerateLimit is { } limit)
                    SetKey(lines, "Framerate", "Limit", Math.Clamp(limit, 0, MaxFramerateLimit).ToString(CultureInfo.InvariantCulture));

                if (request.PositionX is { } x)
                    SetKey(lines, "OSD", "PositionX", Math.Clamp(x, MinPosition, MaxPosition).ToString(CultureInfo.InvariantCulture));

                if (request.PositionY is { } y)
                    SetKey(lines, "OSD", "PositionY", Math.Clamp(y, MinPosition, MaxPosition).ToString(CultureInfo.InvariantCulture));

                if (request.ZoomRatio is { } zoom)
                    SetKey(lines, "OSD", "ZoomRatio", Math.Clamp(zoom, MinZoom, MaxZoom).ToString(CultureInfo.InvariantCulture));

                if (request.EnableFill is { } enableFill)
                    SetKey(lines, "OSD", "EnableFill", (enableFill != 0 ? 1 : 0).ToString(CultureInfo.InvariantCulture));

                if (request.FillColor is { } fillColor)
                    SetKey(lines, "OSD", "FillColor", fillColor.ToString("X8", CultureInfo.InvariantCulture));

                var tempFile = globalFile + ".tsume-tmp";
                File.WriteAllLines(tempFile, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempFile, globalFile, overwrite: true);
            }

            return new HelperReply { Ok = true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not update RTSS Global profile: {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }

    public static HelperReply SetExclusions(SetRtssExclusions request)
    {
        var installDir = FindRtssInstallDir();
        if (installDir is null)
            return HelperReply.Failure("RTSS is not installed.");

        var profilesDir = Path.Combine(installDir, "Profiles");
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in request.Executables.Take(MaxExclusions))
        {
            if (SanitizeExeName(raw) is { } clean) wanted.Add(clean);
        }

        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(profilesDir);

                // Remove stale Tsume-owned exclusions no longer requested; never touch user-created profiles.
                foreach (var file in Directory.EnumerateFiles(profilesDir, "*.exe.cfg"))
                {
                    if (!IsTsumeOwned(file)) continue;

                    var exeName = Path.GetFileName(file)[..^4]; // strip ".cfg"
                    if (!wanted.Contains(exeName))
                    {
                        try { File.Delete(file); }
                        catch (IOException) { }
                    }
                }

                // Write exclusion profiles for every requested anti-cheat executable.
                foreach (var exeName in wanted)
                {
                    var cfgPath = Path.Combine(profilesDir, exeName + ".cfg");
                    if (File.Exists(cfgPath) && !IsTsumeOwned(cfgPath)) continue;

                    var content = $"{TsumeMarker}{Environment.NewLine}[Hooking]{Environment.NewLine}EnableHooking=0{Environment.NewLine}";
                    File.WriteAllText(cfgPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }

            return new HelperReply { Ok = true };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not update RTSS anti-cheat exclusions: {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }

    public static HelperReply Stop()
    {
        var installDir = FindRtssInstallDir();
        var fence = installDir is null
            ? null
            : Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        try
        {
            foreach (var name in RtssProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            // Only stop processes verified to live inside RTSS's install directory.
                            if (fence is not null)
                            {
                                var exe = TryReadProcessPath(process);
                                if (exe is null || !Path.GetFullPath(exe).StartsWith(fence, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            process.Kill(entireProcessTree: false);
                            process.WaitForExit(3000);
                        }
                        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                    }
                }
            }

            return new HelperReply { Ok = true };
        }
        catch (Exception error)
        {
            Log.Write($"Could not stop RTSS: {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }

    /// <summary>Starts RTSS minimized to the tray in the caller's session without a UAC prompt.</summary>
    public static HelperReply Start(int session)
    {
        var installDir = FindRtssInstallDir();
        if (installDir is null)
            return HelperReply.Failure("RTSS is not installed.");

        if (Process.GetProcessesByName("RTSS").Length > 0)
            return new HelperReply { Ok = true };

        // Do not pass /s here: RTSS treats /s as a Windows-startup launch and exits immediately when StartWithWindows=0.
        return PanelStarter.StartElevated(session, Path.Combine(installDir, "RTSS.exe"), "")
            ? new HelperReply { Ok = true }
            : HelperReply.Failure("RTSS could not be started.");
    }

    public static HelperReply GetStartup(string? userSid)
    {
        try
        {
            var enabled = IsScheduledTaskEnabled() || HasEnabledRunEntry(userSid);
            return new HelperReply { Ok = true, RtssStartsWithWindows = enabled };
        }
        catch (Exception error)
        {
            Log.Write($"Could not check RTSS startup state: {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }

    public static HelperReply SetStartup(bool enabled, string? userSid)
    {
        var installDir = FindRtssInstallDir();
        if (installDir is null)
            return HelperReply.Failure("RTSS is not installed.");

        var rtssExe = Path.Combine(installDir, "RTSS.exe");

        try
        {
            if (enabled)
            {
                if (!CreateScheduledTask(rtssExe, installDir, userSid, out var error))
                {
                    Log.Write($"Could not create RTSS startup task: {error}");
                    return HelperReply.Failure(error ?? "Could not create RTSS startup task.");
                }

                // Clear any legacy Run-key entry so Windows does not double-launch RTSS (or show a UAC prompt).
                RemoveRunEntries(userSid);
            }
            else
            {
                var (exitCode, _, stderr) = RunSchtasks("/Delete", "/TN", RtssTaskName, "/F");
                if (exitCode != 0 && IsScheduledTaskEnabled())
                {
                    var detail = string.IsNullOrWhiteSpace(stderr) ? $"schtasks exited with {exitCode}" : stderr.Trim();
                    Log.Write($"Could not delete RTSS startup task: {detail}");
                    return HelperReply.Failure(detail);
                }

                RemoveRunEntries(userSid);
            }

            UpdateConfigStartWithWindows(installDir, enabled);
            return new HelperReply { Ok = true, RtssStartsWithWindows = enabled };
        }
        catch (Exception error)
        {
            Log.Write($"Could not update RTSS startup ({enabled}): {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }

    private static bool CreateScheduledTask(string rtssExe, string installDir, string? userSid, out string? error)
    {
        var tempXml = Path.Combine(Path.GetTempPath(), $"tsume-rtss-task-{Guid.NewGuid():N}.xml");
        try
        {
            var doc = BuildTaskDocument(rtssExe, installDir, userSid);
            var settings = new XmlWriterSettings
            {
                Encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
                Indent = true,
            };
            using (var writer = XmlWriter.Create(tempXml, settings))
                doc.Save(writer);

            var (exitCode, stdout, stderr) = RunSchtasks("/Create", "/TN", RtssTaskName, "/XML", tempXml, "/F");
            if (exitCode == 0)
            {
                error = null;
                return true;
            }

            error = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                : $"schtasks exited with {exitCode}";
            return false;
        }
        finally
        {
            try { File.Delete(tempXml); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static XDocument BuildTaskDocument(string rtssExe, string installDir, string? userSid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var trigger = new XElement(ns + "LogonTrigger",
            new XElement(ns + "Enabled", "true"));
        if (!string.IsNullOrWhiteSpace(userSid))
            trigger.Add(new XElement(ns + "UserId", userSid));

        var principal = new XElement(ns + "Principal", new XAttribute("id", "Author"));
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            principal.Add(new XElement(ns + "UserId", userSid));
            principal.Add(new XElement(ns + "LogonType", "InteractiveToken"));
        }
        else
        {
            principal.Add(new XElement(ns + "GroupId", AdministratorsSid));
        }
        principal.Add(new XElement(ns + "RunLevel", "HighestAvailable"));

        return new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "RivaTuner Statistics Server")),
                new XElement(ns + "Triggers", trigger),
                new XElement(ns + "Principals", principal),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", rtssExe),
                        new XElement(ns + "Arguments", "/s"),
                        new XElement(ns + "WorkingDirectory", installDir)))));
    }

    private static bool IsScheduledTaskEnabled()
    {
        var (exitCode, stdout, _) = RunSchtasks("/Query", "/TN", RtssTaskName, "/XML");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return false;

        try
        {
            var doc = XDocument.Parse(stdout);
            var root = doc.Root;
            if (root is null) return false;

            var ns = root.GetDefaultNamespace();
            var settingsEnabled = root.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value;
            if (string.Equals(settingsEnabled, "false", StringComparison.OrdinalIgnoreCase))
                return false;

            var triggers = root.Element(ns + "Triggers")?.Elements().ToList();
            if (triggers is { Count: > 0 }
                && triggers.All(t => string.Equals(t.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
        }
        catch (XmlException)
        {
            return true;
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunSchtasks(params string[] args)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null) return (-1, "", "Could not start schtasks.exe");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(SchtasksTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            return (-1, "", "schtasks.exe timed out");
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static bool HasEnabledRunEntry(string? userSid)
    {
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            try
            {
                using var hive = Registry.Users.OpenSubKey(userSid);
                if (hive is not null && HasApprovedRunValue(hive, RunKeyPath, StartupApprovedPath))
                    return true;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        try
        {
            if (HasApprovedRunValue(Registry.LocalMachine, RunKeyPath, StartupApprovedPath)) return true;
            if (HasApprovedRunValue(Registry.LocalMachine, RunKeyWow64Path, StartupApproved32Path)) return true;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }

        return false;
    }

    private static bool HasApprovedRunValue(RegistryKey root, string runSubKey, string approvedSubKey)
    {
        using var run = root.OpenSubKey(runSubKey);
        if (run?.GetValue(RtssRunValueName) is not string { Length: > 0 }) return false;

        // Task Manager → Startup apps keeps its switch here: an odd first byte means disabled.
        using var approved = root.OpenSubKey(approvedSubKey);
        return approved?.GetValue(RtssRunValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
    }

    private static void RemoveRunEntries(string? userSid)
    {
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            try
            {
                using var run = Registry.Users.OpenSubKey($@"{userSid}\{RunKeyPath}", writable: true);
                run?.DeleteValue(RtssRunValueName, throwOnMissingValue: false);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        foreach (var path in new[] { RunKeyPath, RunKeyWow64Path })
        {
            try
            {
                using var run = Registry.LocalMachine.OpenSubKey(path, writable: true);
                run?.DeleteValue(RtssRunValueName, throwOnMissingValue: false);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
    }

    private static void UpdateConfigStartWithWindows(string installDir, bool enabled)
    {
        var profilesDir = Path.Combine(installDir, "Profiles");
        var configFile = Path.Combine(profilesDir, "Config");
        var templateFile = Path.Combine(installDir, "ProfileTemplates", "Config");

        try
        {
            lock (FileGate)
            {
                Directory.CreateDirectory(profilesDir);

                var lines = File.Exists(configFile)
                    ? File.ReadAllLines(configFile, Encoding.UTF8).ToList()
                    : File.Exists(templateFile)
                        ? File.ReadAllLines(templateFile, Encoding.UTF8).ToList()
                        : [];

                SetKey(lines, "Settings", "StartWithWindows", enabled ? "1" : "0");

                var tempFile = configFile + ".tsume-tmp";
                File.WriteAllLines(tempFile, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tempFile, configFile, overwrite: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not update RTSS Config StartWithWindows: {error.Message}");
        }
    }

    private static string? TryReadProcessPath(Process process)
    {
        try
        {
            if (process.MainModule?.FileName is { Length: > 0 } path) return path;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }

        // Fallback for 32-bit WOW64 or restricted processes: QueryFullProcessImageNameW only needs PROCESS_QUERY_LIMITED_INFORMATION.
        const uint ProcessQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, unchecked((uint)process.Id));
        if (handle == 0) return null;

        try
        {
            var sb = new StringBuilder(1024);
            var len = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(handle, 0, sb, ref len) && len > 0 ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    private static string? SanitizeExeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var name = raw.Trim();
        if (name.Length > 180 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Contains('\\') || name.Contains('/') || name.Contains(':') || name.Contains("..")) return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name;
    }

    private static bool IsTsumeOwned(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            var firstLine = reader.ReadLine();
            return string.Equals(firstLine?.Trim(), TsumeMarker, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void SetKey(List<string> lines, string section, string key, string value)
    {
        var sectionHeader = $"[{section}]";
        var sectionStart = -1;
        var sectionEnd = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) continue;

            if (sectionStart < 0)
            {
                if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                    sectionStart = i;
            }
            else
            {
                sectionEnd = i;
                break;
            }
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
            return;
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var eq = lines[i].IndexOf('=');
            if (eq <= 0) continue;

            var existingKey = lines[i][..eq].Trim();
            if (!existingKey.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

            lines[i] = $"{key}={value}";
            return;
        }

        // Insert right after the last non-blank line in the section.
        var insertAt = sectionEnd;
        while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
            insertAt--;

        lines.Insert(insertAt, $"{key}={value}");
    }

    internal static string? FindRtssInstallDir()
    {
        string[] registryKeys =
        [
            @"SOFTWARE\WOW6432Node\Unwinder\RTSS",
            @"SOFTWARE\Unwinder\RTSS",
        ];

        foreach (var subKey in registryKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key is null) continue;

                foreach (var valueName in new[] { "InstallPath", "InstallDir" })
                {
                    if (NormalizeFolder(key.GetValue(valueName) as string) is { } folder
                        && File.Exists(Path.Combine(folder, "RTSS.exe")))
                    {
                        return folder;
                    }
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        string[] defaults =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server"),
        ];

        foreach (var folder in defaults)
        {
            if (!string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "RTSS.exe")))
                return folder;
        }

        return null;
    }

    private static string? NormalizeFolder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Trim().Trim('"');
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            clean = Path.GetDirectoryName(clean);
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
