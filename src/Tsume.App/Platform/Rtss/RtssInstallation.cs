using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Tsume.App.Platform.Rtss;

/// <summary>An RTSS installation found on this machine.</summary>
/// <param name="Folder">The RTSS install folder.</param>
/// <param name="Executable">Full path to <c>RTSS.exe</c>.</param>
/// <param name="HooksDll">Full path to <c>RTSSHooks64.dll</c>, or null if absent.</param>
/// <param name="Version">Version string from <c>RTSS.exe</c> (e.g. "7.3.7"), or empty if unknown.</param>
public sealed record RtssInstallInfo(string Folder, string Executable, string? HooksDll, string Version);

/// <summary>
/// Locates RivaTuner Statistics Server on this PC and links to Guru3D's official download page.
/// Tsume never bundles or copies RTSS binaries, keeping Tsume's storage footprint tiny.
/// </summary>
public static class RtssInstallation
{
    public const string OfficialDownloadUrl = "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/";

    private const string ExeName = "RTSS.exe";
    private const string Hooks64Name = "RTSSHooks64.dll";

    /// <summary>Finds an installed copy of RTSS, verifying that <c>RTSS.exe</c> actually exists.</summary>
    public static RtssInstallInfo? Find()
    {
        foreach (var folder in CandidateFolders())
        {
            if (InspectFolder(folder) is { } found) return found;
        }

        return null;
    }

    /// <summary>True when <c>RTSS.exe</c> is currently running.</summary>
    public static bool IsRunning()
    {
        // Check the RTSS process directly rather than RTSSSharedMemoryV2: hooked games or other tools
        // can keep the shared-memory section alive briefly after RTSS.exe has already exited.
        var processes = Process.GetProcessesByName("RTSS");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>Opens the official Guru3D RTSS 7.3.7 download page in the user's browser.</summary>
    public static bool OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(OfficialDownloadUrl) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Starts RTSS minimized to the tray (or asks Windows to elevate if RTSS requires it).</summary>
    public static bool Launch(RtssInstallInfo install, bool minimized = true)
    {
        if (!File.Exists(install.Executable)) return false;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(install.Executable)
            {
                WorkingDirectory = install.Folder,
                UseShellExecute = true,
                WindowStyle = minimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
            });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Unhides and brings RTSS's configuration window to the foreground when RTSS is already running
    /// minimized to the system tray. Falls back to launching <c>RTSS.exe</c> if no window is found.
    /// </summary>
    public static bool BringWindowToFront(RtssInstallInfo? install = null)
    {
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("RTSS"))
        {
            using (process)
            {
                try { pids.Add(unchecked((uint)process.Id)); }
                catch (InvalidOperationException) { }
            }
        }

        if (pids.Count == 0)
            return install is not null && Launch(install, minimized: false);

        nint best = 0;
        var bestScore = -1;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!pids.Contains(pid)) return true;
            if (GetWindow(hwnd, GwOwner) != 0) return true;

            var cls = WindowClass(hwnd);
            if (cls.Contains("IME", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase)
                || cls.Equals("Tooltips_class32", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var title = WindowTitle(hwnd);
            var score = 0;
            if (title.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase)) score += 100;
            else if (title.Contains("RTSS", StringComparison.OrdinalIgnoreCase)) score += 60;
            if (cls.Equals("#32770", StringComparison.Ordinal)) score += 40; // RTSS's main MFC dialog
            if (title.Length > 0) score += 10;

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
            return true;
        }, 0);

        if (best == 0) return false;

        ShowWindow(best, SwShow);
        ShowWindow(best, SwRestore);
        SetForegroundWindow(best);
        return true;
    }

    private static string WindowTitle(nint hwnd)
    {
        var sb = new StringBuilder(256);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string WindowClass(nint hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private const uint GwOwner = 4;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>
    /// Looks on the Desktop and in Downloads for an RTSS setup package the user has already fetched
    /// from Guru3D (<c>RTSSSetup*.exe</c> or <c>RTSSSetup*.zip</c>), newest first.
    /// </summary>
    public static string? FindDownloadedInstaller()
    {
        string?[] places =
        [
            DownloadsFolder(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ];

        try
        {
            return places
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists)
                .SelectMany(SafeFindInstallers)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a downloaded RTSS installer (<c>.exe</c> directly, or unpacks a Guru3D <c>.zip</c> with
    /// Windows' own <c>tar.exe</c> into a temporary folder and runs the <c>RTSSSetup*.exe</c> inside).
    /// </summary>
    public static async Task<bool> RunDownloadedInstallerAsync(string packagePath)
    {
        if (!File.Exists(packagePath)) return false;

        string setupExe;
        if (packagePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            setupExe = packagePath;
        }
        else if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = await UnpackZipAsync(packagePath);
            if (extracted is null) return false;
            setupExe = extracted;
        }
        else
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(setupExe) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async Task<string?> UnpackZipAsync(string archivePath)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar)) return null;

        var tempDir = Path.Combine(Path.GetTempPath(), "Tsume-RTSS-Setup");
        try
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            Directory.CreateDirectory(tempDir);

            using var process = Process.Start(new ProcessStartInfo(tar)
            {
                ArgumentList = { "-xf", archivePath, "-C", tempDir },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) return null;

            return Directory.EnumerateFiles(tempDir, "RTSSSetup*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeFindInstallers(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*RTSS*")
                .Where(file =>
                {
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    return ext is ".exe" or ".zip"
                        && (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Guru3D", StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> CandidateFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static IEnumerable<string> FromRunningProcess()
        {
            var results = new List<string>();
            var processes = Process.GetProcessesByName("RTSS");
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { Length: > 0 } exe
                            && Path.GetDirectoryName(exe) is { Length: > 0 } dir)
                        {
                            results.Add(dir);
                        }
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                }
            }
            return results;
        }

        foreach (var dir in FromRunningProcess())
            if (seen.Add(dir)) yield return dir;

        foreach (var dir in FromRegistry())
            if (seen.Add(dir)) yield return dir;

        string[] defaults =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server"),
        ];

        foreach (var dir in defaults)
            if (!string.IsNullOrWhiteSpace(dir) && seen.Add(dir)) yield return dir;
    }

    private static IEnumerable<string> FromRegistry()
    {
        var results = new List<string>();

        string[] directKeys =
        [
            @"SOFTWARE\WOW6432Node\Unwinder\RTSS",
            @"SOFTWARE\Unwinder\RTSS",
        ];

        foreach (var subKey in directKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key is null) continue;

                foreach (var valueName in new[] { "InstallPath", "InstallDir" })
                {
                    if (NormalizeFolder(key.GetValue(valueName) as string) is { } folder)
                        results.Add(folder);
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        string[] uninstallRoots =
        [
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var root in uninstallRoots)
        {
            try
            {
                using var parent = Registry.LocalMachine.OpenSubKey(root);
                if (parent is null) continue;

                foreach (var name in parent.GetSubKeyNames())
                {
                    using var entry = parent.OpenSubKey(name);
                    var display = entry?.GetValue("DisplayName") as string;
                    if (display is null || !display.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase)) continue;

                    if (NormalizeFolder(entry?.GetValue("InstallLocation") as string) is { } loc)
                        results.Add(loc);
                    if (NormalizeFolder(entry?.GetValue("UninstallString") as string) is { } uninst)
                        results.Add(uninst);
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        }

        return results;
    }

    private static string? NormalizeFolder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Trim().Trim('"');
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            clean = Path.GetDirectoryName(clean);
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static RtssInstallInfo? InspectFolder(string folder)
    {
        try
        {
            var exe = Path.Combine(folder, ExeName);
            if (!File.Exists(exe)) return null;

            var hooks = Path.Combine(folder, Hooks64Name);
            var version = ReadVersion(exe);

            return new RtssInstallInfo(folder, exe, File.Exists(hooks) ? hooks : null, version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (info.ProductMajorPart > 0)
                return $"{info.ProductMajorPart}.{info.ProductMinorPart}.{info.ProductBuildPart}";

            return (info.ProductVersion ?? info.FileVersion ?? "").Replace(',', '.').Replace(" ", "").Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string? DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        return SHGetKnownFolderPath(in downloads, 0, 0, out var path) == 0 ? path : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token,
        [MarshalAs(UnmanagedType.LPWStr)] out string? path);
}
