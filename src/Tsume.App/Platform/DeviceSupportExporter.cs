using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tsume.App.Panel;
using Tsume.Core;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;

namespace Tsume.App.Platform;

/// <summary>
/// Orchestrates strictly read-only hardware discovery (elevated via Tsume.Helper IPC, with graceful
/// fallback to unelevated identity/HID/display capture), environment read-back checks, and exporting
/// privacy-scrubbed <c>tsume-device-support-*.zip</c> bundles to the Desktop.
/// </summary>
public static class DeviceSupportExporter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<ElevatedInventory> CollectInventoryAsync(
        HelperClient helper,
        bool includeTimedLoad = true,
        bool allowUacFallback = false,
        CancellationToken cancel = default)
    {
        var reply = await SendDiscoveryJobAsync(
            helper,
            start: new GetHardwareInventory(includeTimedLoad, PollOnly: false),
            poll: new GetHardwareInventory(includeTimedLoad, PollOnly: true),
            maxWait: TimeSpan.FromSeconds(45),
            cancel);

        if (reply.Ok && !string.IsNullOrWhiteSpace(reply.PayloadJson))
        {
            if (JsonSerializer.Deserialize<ElevatedInventory>(reply.PayloadJson, DiscoveryJson.Compact) is { } inv)
                return inv;
        }

        if (allowUacFallback && TryRunHelperCli(includeTimedLoad ? "--hardware-inventory" : "--hardware-inventory-fast") is { } cliJson)
        {
            if (JsonSerializer.Deserialize<ElevatedInventory>(cliJson, DiscoveryJson.Compact) is { } cliInv)
                return cliInv;
        }

        return await Task.Run(() => CollectUnelevatedFallback(reply.Error ?? "Helper service offline"), cancel);
    }

    public static async Task<LiveHardwareState?> CollectFastStateAsync(HelperClient helper, CancellationToken cancel = default)
    {
        var reply = await SendDiscoveryJobAsync(
            helper,
            start: new GetHardwareState(PollOnly: false),
            poll: new GetHardwareState(PollOnly: true),
            maxWait: TimeSpan.FromSeconds(15),
            cancel);

        if (reply.Ok && !string.IsNullOrWhiteSpace(reply.PayloadJson))
            return JsonSerializer.Deserialize<LiveHardwareState>(reply.PayloadJson, DiscoveryJson.Compact);

        return null;
    }

    private static async Task<HelperReply> SendDiscoveryJobAsync(
        HelperClient helper,
        HelperRequest start,
        HelperRequest poll,
        TimeSpan maxWait,
        CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + maxWait;
        var reply = await helper.SendAsync(start, cancel);
        var transientErrors = 0;

        while (reply.Ok && reply.Pending)
        {
            if (DateTime.UtcNow >= deadline)
                return HelperReply.Failure("Hardware scan timed out waiting for the helper.");

            await Task.Delay(250, cancel);
            reply = await helper.SendAsync(poll, cancel);

            // The helper serves one pipe connection at a time; under 100% CPU load or panel polling
            // a single poll connect can transiently time out even while the scan is still running.
            if (!reply.Ok && !reply.Unsupported && transientErrors < 3 && DateTime.UtcNow < deadline && !cancel.IsCancellationRequested)
            {
                transientErrors++;
                reply = new HelperReply { Ok = true, Pending = true };
                continue;
            }

            transientErrors = 0;
        }

        return reply;
    }

    public static async Task<CombinedStateSnapshot> CaptureCombinedStateAsync(
        HelperClient helper,
        bool readVendorProfiles = true,
        CancellationToken cancel = default)
    {
        var elevatedTask = CollectFastStateAsync(helper, cancel);
        var hidTask = Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles), cancel);
        var refresh = DisplayMode.CurrentRefreshRate();

        await Task.WhenAll(elevatedTask, hidTask);

        return new CombinedStateSnapshot
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Elevated = await elevatedTask,
            Hid = await hidTask,
            CurrentRefreshHz = refresh,
        };
    }

    /// <summary>Runs a non-destructive read-only pre-flight and read-back check.</summary>
    public static async Task<HardwareEnvironmentCheck> RunEnvironmentCheckAsync(
        HelperClient helper,
        ElevatedInventory? existingInventory = null,
        HidTopologySnapshot? existingHid = null,
        CancellationToken cancel = default)
    {
        var findings = new List<EnvironmentCheckFinding>();
        var snapshot = await helper.SendAsync(new GetSnapshot(), cancel: cancel);
        var inv = existingInventory ?? await CollectInventoryAsync(helper, includeTimedLoad: false, allowUacFallback: false, cancel: cancel);

        var helperOnline = snapshot.Ok || snapshot.Unsupported;
        var deviceSupported = snapshot.Ok && snapshot.Device is not null;
        var deviceName = snapshot.Device?.Name
            ?? $"{inv.Identity.BaseBoardManufacturer} {inv.Identity.BaseBoardProduct}".Trim();

        if (snapshot.Ok)
            findings.Add(new("ok", "Helper", $"Online and driving {deviceName}."));
        else if (snapshot.Unsupported)
            findings.Add(new("warn", "Helper", $"Online, but this board ({deviceName}) is not yet supported. Read-only discovery works normally."));
        else
            findings.Add(new("warn", "Helper", $"Offline ({snapshot.Error ?? "no answer"}). Install/start TsumeHelper for elevated firmware reads."));

        if (snapshot.Ok && snapshot.Snapshot is { } s)
        {
            var temp = s.CpuTemperature is { } t ? $"{t} °C" : "n/a";
            var rpm = s.FanRpm is { } r ? $"{r} rpm" : "n/a";
            var cap = s.ChargeLimit is { } c ? $"{c}%" : "n/a";
            findings.Add(new("ok", "Live Read-Back", $"Mode={s.Mode} ({s.Limits}), Fan={s.Fan} ({rpm}), CPU={temp}, ChargeLimit={cap}, FW={s.FirmwareVersion ?? "n/a"}."));
        }

        if (inv.PowerPlan.CpuBoostModeAc == 0 || inv.PowerPlan.CpuBoostModeDc == 0)
        {
            findings.Add(new("warn", "Windows CPU Boost",
                $"Processor performance boost mode is Disabled (AC={inv.PowerPlan.CpuBoostModeAcLabel}, DC={inv.PowerPlan.CpuBoostModeDcLabel}). " +
                "Power limits above base-clock wattage will not engage under load until boost is enabled."));
        }
        else if (inv.PowerPlan.CpuBoostModeAcLabel is not null)
        {
            findings.Add(new("ok", "Windows CPU Boost",
                $"AC={inv.PowerPlan.CpuBoostModeAcLabel}, DC={inv.PowerPlan.CpuBoostModeDcLabel} ({inv.PowerPlan.ActiveSchemeName ?? "active scheme"})."));
        }

        if (inv.OemFootprint.Processes.Count > 0)
        {
            findings.Add(new("info", "OEM Software",
                $"Detected {inv.OemFootprint.Processes.Count} vendor process(es): {string.Join(", ", inv.OemFootprint.Processes.Take(6).Select(p => p.Name))}. " +
                "Keep OEM software running during Guided Capture/Watch Mode, and pause it when testing Tsume writes."));
        }
        else
        {
            findings.Add(new("ok", "OEM Software", "No conflicting OEM supervisor processes detected running."));
        }

        if (inv.Firmware is { } fw)
            findings.Add(new("ok", "Firmware Surface", $"Read-only {fw.VendorFamily} responder active ({fw.Values.Count} values, EC dump={(fw.EcMemoryHex is not null ? "256B" : "n/a")})."));
        else
            findings.Add(new("info", "Firmware Surface", $"No built-in OEM prober matched yet; captured {inv.WmiClasses.Count} vendor WMI class definition(s) for analysis."));

        var hid = existingHid ?? await Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles: false), cancel);
        findings.Add(new("ok", "Controllers & HID",
            $"{hid.Collections.Count} HID collection(s), XInput slots=[{(hid.XInputActiveSlots.Count == 0 ? "none" : string.Join(",", hid.XInputActiveSlots))}], LampArray={hid.LampArrayDevices.Count}."));

        return new HardwareEnvironmentCheck
        {
            CheckedAtUtc = DateTimeOffset.UtcNow,
            HelperOnline = helperOnline,
            DeviceSupported = deviceSupported,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "unknown" : deviceName,
            FirmwareVersion = snapshot.Snapshot?.FirmwareVersion ?? inv.Firmware?.FirmwareVersionText,
            Findings = findings,
        };
    }

    /// <summary>1-click hands-free scan and zip export straight to the Desktop.</summary>
    public static async Task<string> ExportQuickSnapshotAsync(HelperClient helper, CancellationToken cancel = default)
    {
        var invTask = CollectInventoryAsync(helper, includeTimedLoad: true, allowUacFallback: false, cancel: cancel);
        var hidTask = Task.Run(() => HidInventory.CaptureAsync(readVendorProfiles: true), cancel);
        var display = DisplayInventory.Capture();

        await Task.WhenAll(invTask, hidTask);
        var inv = await invTask;
        var hid = await hidTask;
        var env = await RunEnvironmentCheckAsync(helper, inv, hid, cancel);

        var bundle = new DeviceSupportBundle
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            TsumeVersion = TsumeVersionLabel(),
            Inventory = inv,
            Hid = hid,
            Display = display,
            EnvironmentCheck = env,
        };

        return await ExportBundleAsync(helper, bundle, cancel);
    }

    public static async Task<string> ExportBundleAsync(
        HelperClient helper,
        DeviceSupportBundle bundle,
        CancellationToken cancel = default)
    {
        var helperLog = await helper.SendAsync(new GetLog(), cancel: cancel);
        return await Task.Run(() => WriteZip(bundle, helperLog), cancel);
    }

    public static string TsumeVersionLabel()
    {
        var full = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? HeaderView.Version;
        return $"{HeaderView.Version} ({full})";
    }

    private static string WriteZip(DeviceSupportBundle bundle, HelperReply helperLog)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            try { return WriteZipIn(desktop, bundle, helperLog); }
            catch (Exception error) when (error is UnauthorizedAccessException or DirectoryNotFoundException) { }
        }

        return WriteZipIn(DataFolder.Path, bundle, helperLog);
    }

    private static string WriteZipIn(string folder, DeviceSupportBundle bundle, HelperReply helperLog)
    {
        Directory.CreateDirectory(folder);

        var boardTag = SanitizeFileToken(bundle.Inventory?.Identity.BaseBoardProduct ?? bundle.Inventory?.Identity.SystemModel ?? "device");
        var zipPath = UniqueZipPath(folder, $"tsume-device-support-{boardTag}-{DateTime.Now:yyyyMMdd-HHmmss}");

        try
        {
            using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            // 1. Human-readable report
            AddText(archive, "report.md", DiscoveryReportBuilder.BuildMarkdown(bundle));

            // 2. Full structured JSON (without duplicating huge MOF texts inline if written separately? Keep complete so single-file parsing always works!)
            AddText(archive, "discovery.json", JsonSerializer.Serialize(bundle, DiscoveryJson.Indented));

            // 3. Separate MOF definitions per vendor WMI class
            if (bundle.Inventory?.WmiClasses is { Count: > 0 } classes)
            {
                var usedMofEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var cls in classes)
                {
                    if (string.IsNullOrWhiteSpace(cls.MofText)) continue;
                    var safeNs = SanitizeFileToken(cls.Namespace.Replace('\\', '_'));
                    var safeCls = SanitizeFileToken(cls.Name);
                    var basePath = $"wmi-mof/{safeNs}__{safeCls}";
                    var entryName = $"{basePath}.mof";
                    for (var n = 2; !usedMofEntries.Add(entryName); n++)
                        entryName = $"{basePath}-{n}.mof";
                    AddText(archive, entryName, cls.MofText);
                }
            }

            // 4. Separate per-step diff files
            for (var i = 0; i < bundle.Diffs.Count; i++)
            {
                var diff = bundle.Diffs[i];
                var safeStep = SanitizeFileToken(diff.StepId);
                AddText(archive, $"diffs/{i + 1:D2}-{safeStep}.json", JsonSerializer.Serialize(diff, DiscoveryJson.Indented));
            }

            // 5. Unified button timeline (JSONL)
            if (bundle.ButtonEvents.Count > 0)
            {
                var lines = string.Join(Environment.NewLine,
                    bundle.ButtonEvents.Select(ev => JsonSerializer.Serialize(ev, DiscoveryJson.Compact)));
                AddText(archive, "buttons.jsonl", lines + Environment.NewLine);
            }

            // 6. Existing Tsume logs (scrubbed)
            if (Directory.Exists(DataFolder.Path))
            {
                foreach (var file in Directory.EnumerateFiles(DataFolder.Path, "*.log").Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (ReadSharedText(file) is { } text)
                        AddText(archive, $"logs/{Path.GetFileName(file)}", PrivacyScrubber.ScrubText(text));
                }
            }

            if (helperLog.Ok && !string.IsNullOrEmpty(helperLog.Log))
                AddText(archive, "logs/helper-service.log", PrivacyScrubber.ScrubText(helperLog.Log));

            return zipPath;
        }
        catch
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); }
            catch (IOException) { }
            throw;
        }
    }

    private static ElevatedInventory CollectUnelevatedFallback(string reason)
    {
        string bbMaker = "", bbProduct = "", bbVer = "";
        QueryFirst("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", r =>
        {
            bbMaker = $"{r["Manufacturer"]}".Trim();
            bbProduct = $"{r["Product"]}".Trim();
            bbVer = $"{r["Version"]}".Trim();
        });

        string sysMaker = "", sysModel = "", sysSku = "", sysFamily = "";
        QueryFirst("SELECT Manufacturer, Model, SystemSKUNumber, SystemFamily FROM Win32_ComputerSystem", r =>
        {
            sysMaker = $"{r["Manufacturer"]}".Trim();
            sysModel = $"{r["Model"]}".Trim();
            sysSku = $"{r["SystemSKUNumber"]}".Trim();
            sysFamily = $"{r["SystemFamily"]}".Trim();
        });

        string biosMaker = "", biosVer = "", biosDate = "";
        QueryFirst("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", r =>
        {
            biosMaker = $"{r["Manufacturer"]}".Trim();
            biosVer = $"{r["SMBIOSBIOSVersion"]}".Trim();
            biosDate = $"{r["ReleaseDate"]}".Trim();
        });

        string cpuName = "", cpuMaker = "";
        int cores = 0, logical = 0;
        uint maxClk = 0;
        QueryFirst("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", r =>
        {
            cpuName = $"{r["Name"]}".Trim();
            cpuMaker = $"{r["Manufacturer"]}".Trim();
            cores = Convert.ToInt32(r["NumberOfCores"] ?? 0);
            logical = Convert.ToInt32(r["NumberOfLogicalProcessors"] ?? 0);
            maxClk = Convert.ToUInt32(r["MaxClockSpeed"] ?? 0u);
        });

        return new ElevatedInventory
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            Elevated = false,
            Note = $"Unelevated fallback ({reason}). Start the TsumeHelper service for full EC/ACPI WMI reads.",
            Identity = new SystemIdentityInfo
            {
                BaseBoardManufacturer = bbMaker,
                BaseBoardProduct = bbProduct,
                BaseBoardVersion = bbVer,
                SystemManufacturer = sysMaker,
                SystemModel = sysModel,
                SystemSku = sysSku,
                SystemFamily = sysFamily,
                BiosManufacturer = biosMaker,
                BiosVersion = biosVer,
                BiosReleaseDate = biosDate,
                CpuName = cpuName,
                CpuManufacturer = cpuMaker,
                CpuCores = cores,
                CpuLogicalProcessors = logical,
                CpuMaxClockMhz = maxClk,
                WindowsVersion = RuntimeInformation.OSDescription,
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            },
        };
    }

    private static string? TryRunHelperCli(string flag)
    {
        var helperPath = FindHelperExe();
        if (helperPath is null) return null;

        var tempFile = Path.Combine(Path.GetTempPath(), $"tsume-discovery-{Guid.NewGuid():N}.json");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(helperPath, $"{flag} \"{tempFile}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (proc is null || !proc.WaitForExit(35_000)) return null;
            return proc.ExitCode == 0 && File.Exists(tempFile) ? File.ReadAllText(tempFile) : null;
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (IOException) { }
        }
    }

    private static string? FindHelperExe()
    {
        const string name = "Tsume.Helper.exe";
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string[] candidates =
        [
            Path.Combine(here, name),
            Path.Combine(here, "..", "..", "Tsume.Helper", Path.GetFileName(here), name),
        ];

        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static void QueryFirst(string query, Action<ManagementBaseObject> onRow)
    {
        try
        {
            using var search = new ManagementObjectSearcher(@"root\CIMV2", query);
            using var rows = search.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    onRow(row);
                    return;
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }
    }

    private static string UniqueZipPath(string folder, string stem)
    {
        var candidate = Path.Combine(folder, $"{stem}.zip");
        for (var n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem}-{n}.zip");
        return candidate;
    }

    private static string SanitizeFileToken(string raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var cleaned = sb.ToString().Trim('-', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "device" : cleaned[..Math.Min(48, cleaned.Length)];
    }

    private static void AddText(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var output = entry.Open();
        var bytes = Utf8.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    private static string? ReadSharedText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
