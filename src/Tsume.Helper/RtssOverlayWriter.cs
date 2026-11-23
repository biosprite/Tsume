using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Tsume.Core.Ipc;

namespace Tsume.Helper;

/// <summary>
/// Switches RTSS's own OverlayEditor plugin to one of Tsume's bundled Essential layouts (realHfde, MIT) and turns the
/// plugin on in RTSS's <c>Profiles\Config</c>. Before the first change it records the plugin state and layout the user
/// had, beside OverlayEditor's own config, so a later restore puts exactly that back.
/// Layout files are resolved from Tsume's install folder by name; no path ever comes from the caller.
/// </summary>
internal static class RtssOverlayWriter
{
    private const string PluginDll = "OverlayEditor.dll";
    private const string BackupSection = "TsumeBackup";

    private static readonly Dictionary<string, string> Layouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tsume"] = "essential_tsume",
    };

    private static readonly Dictionary<string, string> Themes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dark"] = "Dark",
        ["smoke"] = "Smoke",
        ["white"] = "White",
        ["float"] = "Float",
    };

    public static HelperReply Apply(SetRtssOverlay request)
    {
        var installDir = RtssProfileWriter.FindRtssInstallDir();
        if (installDir is null)
            return HelperReply.Failure("RTSS is not installed.");

        var pluginFolder = Path.Combine(installDir, "Plugins", "Client");
        if (!File.Exists(Path.Combine(pluginFolder, PluginDll)))
            return HelperReply.Failure("This RTSS has no OverlayEditor plugin.");

        var editorConfig = Path.Combine(pluginFolder, "OverlayEditor.cfg");
        var backupFile = editorConfig + ".tsume-backup";
        var rtssConfig = Path.Combine(installDir, "Profiles", "Config");

        string? layoutPath = null;
        if (request.Layout is not null)
        {
            layoutPath = ResolveLayout(request.Layout, request.Theme);
            if (layoutPath is null)
                return HelperReply.Failure("Unknown overlay layout or theme.");
        }

        try
        {
            lock (RtssProfileWriter.FileGate)
            {
                var editorLines = ReadLines(editorConfig);
                var configLines = ReadLines(rtssConfig);
                var changed = false;

                if (layoutPath is not null)
                {
                    if (!File.Exists(backupFile))
                    {
                        var backup = new List<string>();
                        RtssProfileWriter.SetKey(backup, BackupSection, "PluginEnabled", GetKey(configLines, "Plugins", PluginDll) ?? "0");
                        RtssProfileWriter.SetKey(backup, BackupSection, "Layout", GetKey(editorLines, "Settings", "Layout") ?? "");
                        File.WriteAllLines(backupFile, backup, Encoding.Latin1);
                    }

                    changed |= Set(editorLines, "Settings", "Layout", layoutPath);
                    changed |= Set(configLines, "Plugins", PluginDll, "1");
                }
                else
                {
                    if (!File.Exists(backupFile))
                        return new HelperReply { Ok = true, RtssOverlayChanged = false };

                    var backup = ReadLines(backupFile);
                    var enabled = GetKey(backup, BackupSection, "PluginEnabled") == "1" ? "1" : "0";
                    var layout = GetKey(backup, BackupSection, "Layout") ?? "";

                    changed |= Set(configLines, "Plugins", PluginDll, enabled);
                    if (layout.Length > 0) changed |= Set(editorLines, "Settings", "Layout", layout);
                    else changed |= RemoveKey(editorLines, "Settings", "Layout");
                }

                if (changed)
                {
                    WriteLines(editorConfig, editorLines);
                    WriteLines(rtssConfig, configLines);
                }

                if (layoutPath is null) File.Delete(backupFile);

                var needsRestart = changed || (layoutPath is not null && IsRunningRtssOlderThan(layoutPath));

                Log.Write(layoutPath is null
                    ? $"restored the user's OverlayEditor setup (changed: {changed})"
                    : $"OverlayEditor layout set to {Path.GetFileName(layoutPath)} (changed: {changed}, restart: {needsRestart})");

                return new HelperReply { Ok = true, RtssOverlayChanged = needsRestart };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not update the OverlayEditor setup: {error.Message}");
            return HelperReply.Failure(error.Message);
        }
    }

    /// <summary>
    /// OverlayEditor only reads its <c>.ovl</c> layout at RTSS startup, whereas a running game can reload the
    /// companion atlas PNG on disk. After a Tsume upgrade, restart RTSS once if it was started before the
    /// bundled layout file was written so it never mixes an old layout with a new atlas.
    /// </summary>
    private static bool IsRunningRtssOlderThan(string layoutPath)
    {
        try
        {
            var layoutWriteUtc = File.GetLastWriteTimeUtc(layoutPath);
            if (layoutWriteUtc > DateTime.UtcNow.AddMinutes(5)) return false;

            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                using (process)
                {
                    try
                    {
                        if (process.StartTime.ToUniversalTime() < layoutWriteUtc) return true;
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        return false;
    }

    /// <summary>The bundled layout file for a known layout and theme name, or null.</summary>
    private static string? ResolveLayout(string layout, string? theme)
    {
        if (!Layouts.TryGetValue(layout, out var stem)) return null;
        if (!Themes.TryGetValue(theme ?? "dark", out var folder)) return null;

        var suffix = folder == "Dark" ? "" : "_" + folder.ToLowerInvariant();
        var path = Path.Combine(AppContext.BaseDirectory, "Overlays", "Essential", folder, stem + suffix + ".ovl");
        return File.Exists(path) ? path : null;
    }

    private static List<string> ReadLines(string path) =>
        File.Exists(path) ? File.ReadAllLines(path, Encoding.Latin1).ToList() : [];

    private static void WriteLines(string path, List<string> lines)
    {
        var temp = path + ".tsume-tmp";
        File.WriteAllLines(temp, lines, Encoding.Latin1);
        File.Move(temp, path, overwrite: true);
    }

    private static bool Set(List<string> lines, string section, string key, string value)
    {
        if (string.Equals(GetKey(lines, section, key), value, StringComparison.Ordinal)) return false;
        RtssProfileWriter.SetKey(lines, section, key, value);
        return true;
    }

    private static bool RemoveKey(List<string> lines, string section, string key)
    {
        var index = FindKey(lines, section, key);
        if (index < 0) return false;
        lines.RemoveAt(index);
        return true;
    }

    private static string? GetKey(List<string> lines, string section, string key)
    {
        var index = FindKey(lines, section, key);
        return index < 0 ? null : lines[index][(lines[index].IndexOf('=') + 1)..].Trim();
    }

    private static int FindKey(List<string> lines, string section, string key)
    {
        var inSection = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = trimmed[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            var eq = trimmed.IndexOf('=');
            if (eq > 0 && trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }
}
