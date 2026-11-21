using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    private static readonly Lock FileGate = new();

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
                                var exe = process.MainModule?.FileName;
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

    private static void SetKey(List<string> lines, string section, string key, string value)
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

    private static string? FindRtssInstallDir()
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
