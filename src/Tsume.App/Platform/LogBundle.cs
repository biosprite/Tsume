using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Tsume.App.Panel;
using Tsume.Core;
using Tsume.Core.Ipc;

namespace Tsume.App.Platform;

/// <summary>
/// Packs Tsume's logs, the helper service's own log and a short system summary into a single zip
/// on the Desktop so someone asking for help has one file to attach.
/// </summary>
public static class LogBundle
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Collects all logs and writes <c>tsume-logs-&lt;timestamp&gt;.zip</c> to the Desktop
    /// (falling back to <see cref="DataFolder.Path"/> if the Desktop is unavailable).
    /// Returns the path of the zip created.
    /// </summary>
    public static async Task<string> ExportAsync(HelperClient helper)
    {
        // One pipe call at a time: the helper serves requests sequentially.
        var snapshot = await helper.SendAsync(new GetSnapshot());
        var helperLog = await helper.SendAsync(new GetLog());

        return await Task.Run(() => CreateArchive(snapshot, helperLog));
    }

    /// <summary>Copies <paramref name="filePath"/> onto the clipboard as a file drop (must run on the UI thread).</summary>
    public static void CopyFileToClipboard(string filePath)
    {
        var files = new StringCollection { filePath };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetFileDropList(files);
                return;
            }
            catch (ExternalException) when (attempt == 0)
            {
                // Another program momentarily holds the clipboard; try once more.
                Thread.Sleep(50);
            }
            catch (ExternalException)
            {
                return;
            }
        }
    }

    /// <summary>Opens File Explorer with <paramref name="filePath"/> selected.</summary>
    public static void RevealInExplorer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = false,
            });
        }
        catch (Win32Exception) { }
    }

    /// <summary>Creates <paramref name="folderPath"/> if needed and opens it in File Explorer.</summary>
    public static void OpenFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException) { }
    }

    private static string CreateArchive(HelperReply snapshot, HelperReply helperLog)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            try
            {
                return CreateArchiveIn(desktop, snapshot, helperLog);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Desktop is unavailable or protected (e.g. Controlled Folder Access): fall back to Tsume's own folder.
            }
        }

        return CreateArchiveIn(DataFolder.Path, snapshot, helperLog);
    }

    private static string CreateArchiveIn(string destination, HelperReply snapshot, HelperReply helperLog)
    {
        Directory.CreateDirectory(destination);

        var zipPath = UniqueZipPath(destination);
        var included = new List<string>();
        string? localHelperLog = null;
        string? serviceLogEntry = null;

        try
        {
            using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            if (Directory.Exists(DataFolder.Path))
            {
                foreach (var file in Directory.EnumerateFiles(DataFolder.Path, "*.log").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(file);
                    if (ReadSharedBytes(file) is not { } bytes) continue;

                    AddBytes(archive, name, bytes);
                    included.Add(name);

                    if (name.Equals("helper.log", StringComparison.OrdinalIgnoreCase))
                        localHelperLog = Utf8.GetString(bytes);
                }

                var statePath = Path.Combine(DataFolder.Path, "state.json");
                if (File.Exists(statePath) && ReadSharedBytes(statePath) is { } stateBytes)
                {
                    AddBytes(archive, "state.json", stateBytes);
                    included.Add("state.json");
                }
            }

            if (helperLog.Ok && !string.IsNullOrEmpty(helperLog.Log))
            {
                if (localHelperLog is null)
                {
                    serviceLogEntry = "helper.log";
                    AddText(archive, serviceLogEntry, helperLog.Log);
                    included.Add(serviceLogEntry);
                }
                else if (!string.Equals(localHelperLog, helperLog.Log, StringComparison.Ordinal))
                {
                    serviceLogEntry = "helper-service.log";
                    AddText(archive, serviceLogEntry, helperLog.Log);
                    included.Add(serviceLogEntry);
                }
                else
                {
                    serviceLogEntry = "helper.log";
                }
            }

            included.Insert(0, "summary.txt");
            AddText(archive, "summary.txt", BuildSummary(snapshot, helperLog, serviceLogEntry, included));

            return zipPath;
        }
        catch
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch (IOException) { }
            throw;
        }
    }

    private static string UniqueZipPath(string folder)
    {
        var stem = $"tsume-logs-{DateTime.Now:yyyyMMdd-HHmmss}";
        var candidate = Path.Combine(folder, $"{stem}.zip");

        for (var n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem}-{n}.zip");

        return candidate;
    }

    private static byte[]? ReadSharedBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void AddBytes(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(bytes, 0, bytes.Length);
    }

    private static void AddText(ZipArchive archive, string name, string text) =>
        AddBytes(archive, name, Utf8.GetBytes(text));

    private static string BuildSummary(
        HelperReply snapshot, HelperReply helperLog, string? serviceLogEntry, IReadOnlyList<string> included)
    {
        var fullVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? HeaderView.Version;

        var summary = new StringBuilder();
        summary.AppendLine($"Tsume: {HeaderView.Version} ({fullVersion})");
        summary.AppendLine($"Executable: {Environment.ProcessPath ?? "unknown"}");
        summary.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ})");
        summary.AppendLine($"Windows: {WindowsDescription()} ({RuntimeInformation.OSArchitecture})");
        summary.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        summary.AppendLine();

        summary.AppendLine("Panel");
        summary.AppendLine($"  UIAccess: {(UiAccess.IsGranted ? "yes" : "no")}");
        summary.AppendLine($"  Xbox Full Screen Experience: {(FullScreenExperience.IsActive ? "active" : "inactive")}");
        summary.AppendLine($"  Start with Windows: {(StartWithWindows.IsEnabled ? "enabled" : "disabled")}");
        summary.AppendLine($"  Start menu: {(StartMenuShortcut.IsProvidedByInstaller ? "installer" : StartMenuShortcut.Exists ? "shortcut" : "none")}");
        summary.AppendLine($"  MSI Center M: {(MsiCenter.IsRunning ? "running" : "paused")} (can control: {(MsiCenter.CanControl ? "yes" : "no")})");

        var refresh = DisplayMode.CurrentRefreshRate();
        if (refresh > 0) summary.AppendLine($"  Refresh rate: {refresh} Hz");
        summary.AppendLine();

        summary.AppendLine("Helper");
        summary.AppendLine($"  Service installed: {(HelperServiceInstalled() ? "yes" : "no")}");

        if (snapshot.Ok)
        {
            summary.AppendLine("  Status: online");
            if (snapshot.Device is { } device)
                summary.AppendLine($"  Device: {device.Name}");

            if (snapshot.Snapshot is { } state)
            {
                if (!string.IsNullOrWhiteSpace(state.FirmwareVersion))
                    summary.AppendLine($"  Firmware: {state.FirmwareVersion}");
                summary.AppendLine($"  Performance: {state.Mode} ({state.Limits})");
                summary.AppendLine($"  Fan: {state.Fan}");
                if (state.ChargeLimit is { } limit)
                    summary.AppendLine($"  Charge limit: {limit} %");
                if (state.CpuTemperature is { } temp)
                    summary.AppendLine($"  CPU temperature: {temp} °C");
                if (state.FanRpm is { } rpm)
                    summary.AppendLine($"  Fan speed: {rpm} rpm");
            }
        }
        else if (snapshot.Unsupported)
        {
            summary.AppendLine("  Status: online (unsupported device)");
            if (!string.IsNullOrWhiteSpace(snapshot.Error))
                summary.AppendLine($"  Detail: {snapshot.Error}");
        }
        else
        {
            summary.AppendLine($"  Status: offline ({snapshot.Error ?? "no answer"})");
        }

        summary.AppendLine(helperLog.Ok
            ? $"  Helper log: {(serviceLogEntry is not null ? $"included ({serviceLogEntry})" : "empty")}"
            : $"  Helper log: unavailable ({helperLog.Error ?? "no answer"})");
        summary.AppendLine();

        summary.AppendLine($"Data folder: {DataFolder.Path}");
        summary.AppendLine($"Files: {string.Join(", ", included)}");

        return summary.ToString();
    }

    private static string WindowsDescription()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var display = key?.GetValue("DisplayVersion") as string;
            var build = key?.GetValue("CurrentBuild") as string;
            var ubr = key?.GetValue("UBR");

            if (!string.IsNullOrWhiteSpace(build))
            {
                var buildText = ubr is int revision ? $"{build}.{revision}" : build;
                var version = string.IsNullOrWhiteSpace(display) ? "" : $" {display}";
                return $"{RuntimeInformation.OSDescription}{version} (build {buildText})";
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }

        return RuntimeInformation.OSDescription;
    }

    private static bool HelperServiceInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\TsumeHelper");
            return key is not null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
