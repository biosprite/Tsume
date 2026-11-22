using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tsume.Core.Diagnostics;

/// <summary>
/// Versioned schemas, diff engine, and Markdown report builder for Tsume's read-only Hardware Discovery
/// tool. Shared by Tsume.Core, Tsume.Hardware, Tsume.Helper, and Tsume.App.
/// </summary>
public static class DiscoveryJson
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record ElevatedInventory
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset CollectedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Elevated { get; init; }
    public string? Note { get; init; }

    public SystemIdentityInfo Identity { get; init; } = new();
    public PowerPlanSnapshot PowerPlan { get; init; } = new();
    public IReadOnlyList<AcpiDeviceInfo> AcpiPowerDevices { get; init; } = [];
    public IReadOnlyList<WmiClassInfo> WmiClasses { get; init; } = [];
    public FirmwareReadSnapshot? Firmware { get; init; }
    public TimedDeltaSnapshot? TimedDelta { get; init; }
    public OemFootprintSnapshot OemFootprint { get; init; } = new();
}

public sealed record SystemIdentityInfo
{
    public string BaseBoardManufacturer { get; init; } = "";
    public string BaseBoardProduct { get; init; } = "";
    public string BaseBoardVersion { get; init; } = "";
    public string SystemManufacturer { get; init; } = "";
    public string SystemModel { get; init; } = "";
    public string SystemSku { get; init; } = "";
    public string SystemFamily { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string ProductVersion { get; init; } = "";
    public string BiosManufacturer { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public string BiosReleaseDate { get; init; } = "";
    public string? EcMajorMinor { get; init; }
    public string CpuName { get; init; } = "";
    public string CpuManufacturer { get; init; } = "";
    public int CpuCores { get; init; }
    public int CpuLogicalProcessors { get; init; }
    public uint CpuMaxClockMhz { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public BatteryStaticInfo? Battery { get; init; }
    public string WindowsVersion { get; init; } = "";
    public string OsArchitecture { get; init; } = "";
}

public sealed record GpuInfo(string Name, string Vendor, string DriverVersion, string ScrubbedPnpId);

public sealed record BatteryStaticInfo(
    string Name,
    uint? DesignedCapacityMwh,
    uint? FullChargedCapacityMwh,
    bool? AcOnline,
    int? ChargePercent,
    int? RateMw);

public sealed record PowerPlanSnapshot
{
    public string? ActiveSchemeGuid { get; init; }
    public string? ActiveSchemeName { get; init; }
    public string? EffectiveOverlayGuid { get; init; }
    public string? EffectiveOverlayLabel { get; init; }
    public uint? CpuBoostModeAc { get; init; }
    public string? CpuBoostModeAcLabel { get; init; }
    public uint? CpuBoostModeDc { get; init; }
    public string? CpuBoostModeDcLabel { get; init; }
}

public sealed record AcpiDeviceInfo(string Name, string ScrubbedDeviceId, string Manufacturer, string Service, string Status);

public sealed record WmiClassInfo
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public bool IsEvent { get; init; }
    public IReadOnlyList<string> Instances { get; init; } = [];
    public IReadOnlyList<WmiMethodInfo> Methods { get; init; } = [];
    public IReadOnlyList<string> Properties { get; init; } = [];
    public string? MofText { get; init; }
}

public sealed record WmiMethodInfo(
    string Name,
    IReadOnlyList<string> InParameters,
    IReadOnlyList<string> OutParameters);

public sealed record FirmwareReadSnapshot
{
    public required string VendorFamily { get; init; }
    public string? BridgeInstance { get; init; }
    public string? FirmwareVersionText { get; init; }
    public Dictionary<string, string> Values { get; init; } = [];
    public Dictionary<string, string> Tables { get; init; } = [];
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string? EcMemoryHex { get; init; }
}

public sealed record TimedDeltaSnapshot
{
    public DateTimeOffset Idle1Utc { get; init; }
    public DateTimeOffset Idle2Utc { get; init; }
    public DateTimeOffset UnderLoadUtc { get; init; }
    public int LoadDurationMs { get; init; }
    public IReadOnlyList<VolatileEntry> VolatileValues { get; init; } = [];
    public IReadOnlyList<VolatileEcByte> VolatileEcBytes { get; init; } = [];
    public IReadOnlyList<EnergyMeterSample> EnergyMeters { get; init; } = [];
    public Dictionary<string, double> ThermalZonesIdleC { get; init; } = [];
    public Dictionary<string, double> ThermalZonesLoadC { get; init; } = [];
    public int? BatteryRateIdleMw { get; init; }
    public int? BatteryRateLoadMw { get; init; }
    public bool? AcOnline { get; init; }
}

public sealed record VolatileEntry(string Key, string Idle1, string Idle2, string UnderLoad);

public sealed record VolatileEcByte(
    int Offset,
    string OffsetHex,
    byte Idle1,
    byte Idle2,
    byte UnderLoad);

public sealed record EnergyMeterSample(
    string InstanceName,
    double? IdleWatts,
    double? LoadWatts);

public sealed record OemFootprintSnapshot
{
    public IReadOnlyList<string> VendorKeywords { get; init; } = [];
    public IReadOnlyList<OemProcessInfo> Processes { get; init; } = [];
    public IReadOnlyList<OemServiceInfo> Services { get; init; } = [];
    public IReadOnlyList<OemScheduledTaskInfo> ScheduledTasks { get; init; } = [];
    public Dictionary<string, string> RegistryValues { get; init; } = [];
}

public sealed record OemProcessInfo(string Name, string ScrubbedPath, string? FileVersion, string? ProductName);
public sealed record OemServiceInfo(string Name, string DisplayName, string StartMode, string State, string ScrubbedPath);
public sealed record OemScheduledTaskInfo(string TaskPath, string TaskName, string State, string Actions);

/// <summary>Fast read-only snapshot used before/after guided steps and during live Watch Mode.</summary>
public sealed record LiveHardwareState
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string VendorFamily { get; init; } = "generic";
    public Dictionary<string, string> FirmwareValues { get; init; } = [];
    public string? EcMemoryHex { get; init; }
    public string? PowerSchemeGuid { get; init; }
    public string? PowerOverlayGuid { get; init; }
    public Dictionary<string, double> ThermalZonesC { get; init; } = [];
    public Dictionary<string, string> OemRegistry { get; init; } = [];
}

public sealed record HidTopologySnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<HidCollectionInfo> Collections { get; init; } = [];
    public IReadOnlyList<int> XInputActiveSlots { get; init; } = [];
    public IReadOnlyList<string> LampArrayDevices { get; init; } = [];
    public IReadOnlyList<VendorControllerSnapshot> VendorControllers { get; init; } = [];
}

public sealed record HidCollectionInfo
{
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort VersionBcd { get; init; }
    public string VersionText { get; init; } = "";
    public string? InterfaceNumber { get; init; }
    public string? CollectionNumber { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Product { get; init; } = "";
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public ushort InputReportLength { get; init; }
    public ushort OutputReportLength { get; init; }
    public ushort FeatureReportLength { get; init; }
    public IReadOnlyList<byte> InputReportIds { get; init; } = [];
    public IReadOnlyList<byte> OutputReportIds { get; init; } = [];
    public IReadOnlyList<byte> FeatureReportIds { get; init; } = [];
    public bool CanOpenReadWrite { get; init; }
    public string ScrubbedPath { get; init; } = "";
}

public sealed record VendorControllerSnapshot
{
    public required string Protocol { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string FirmwareVersion { get; init; } = "";
    public byte? ModeByte { get; init; }
    public byte? MKeysByte { get; init; }
    public int ProfileLength { get; init; }
    public string? ProfileSha256 { get; init; }
    public string? ProfileHex { get; init; }
    public string? Summary { get; init; }
}

public sealed record DisplayInventorySnapshot
{
    public int ActiveWidth { get; init; }
    public int ActiveHeight { get; init; }
    public int BitsPerPel { get; init; }
    public int CurrentRefreshHz { get; init; }
    public IReadOnlyList<int> SignalRefreshRates { get; init; } = [];
    public IReadOnlyList<int> DesktopRefreshRates { get; init; } = [];
    public IReadOnlyList<DisplayPanelInfo> Panels { get; init; } = [];
}

public sealed record DisplayPanelInfo(
    string FriendlyName,
    string EdidManufacturerId,
    string EdidProductCode,
    string OutputTechnology);

public sealed record CapturedButtonEvent
{
    public required string PromptId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public long ElapsedMs { get; init; }
    public required string Source { get; init; }   // "wmi" | "keyboard_ll" | "raw_hid" | "xinput"
    public required string Summary { get; init; }
    public Dictionary<string, string> Fields { get; init; } = [];
}

public sealed record CombinedStateSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public LiveHardwareState? Elevated { get; init; }
    public HidTopologySnapshot? Hid { get; init; }
    public int CurrentRefreshHz { get; init; }
}

public sealed record ValueChange(string Key, string? Before, string? After, bool IsVolatile);

public sealed record EcByteDiff(
    int Offset,
    string OffsetHex,
    byte Before,
    byte After,
    bool IsVolatile);

public sealed record ProfileByteDiff(
    string ControllerKey,
    int Offset,
    string OffsetHex,
    byte Before,
    byte After);

public sealed record CaptureDiffEntry
{
    public required string StepId { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }   // "baseline" | "guided" | "watch" | "skipped"
    public string? UserNote { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ValueChange> FirmwareChanges { get; init; } = [];
    public IReadOnlyList<EcByteDiff> EcByteChanges { get; init; } = [];
    public IReadOnlyList<ValueChange> PowerChanges { get; init; } = [];
    public IReadOnlyList<ValueChange> RegistryChanges { get; init; } = [];
    public IReadOnlyList<string> HidAdded { get; init; } = [];
    public IReadOnlyList<string> HidRemoved { get; init; } = [];
    public IReadOnlyList<ValueChange> ControllerMetaChanges { get; init; } = [];
    public IReadOnlyList<ProfileByteDiff> ControllerProfileChanges { get; init; } = [];
    public string? XInputBefore { get; init; }
    public string? XInputAfter { get; init; }

    [JsonIgnore]
    public int NonVolatileChangeCount =>
        FirmwareChanges.Count(c => !c.IsVolatile)
        + EcByteChanges.Count(c => !c.IsVolatile)
        + PowerChanges.Count
        + RegistryChanges.Count
        + HidAdded.Count
        + HidRemoved.Count
        + ControllerMetaChanges.Count
        + ControllerProfileChanges.Count
        + (XInputBefore != XInputAfter ? 1 : 0);
}

public sealed record EnvironmentCheckFinding(string Level, string Area, string Message);

public sealed record HardwareEnvironmentCheck
{
    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool HelperOnline { get; init; }
    public bool DeviceSupported { get; init; }
    public string DeviceName { get; init; } = "unknown";
    public string? FirmwareVersion { get; init; }
    public IReadOnlyList<EnvironmentCheckFinding> Findings { get; init; } = [];
}

public sealed record DeviceSupportBundle
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string TsumeVersion { get; init; } = "";
    public ElevatedInventory? Inventory { get; init; }
    public HidTopologySnapshot? Hid { get; init; }
    public DisplayInventorySnapshot? Display { get; init; }
    public HardwareEnvironmentCheck? EnvironmentCheck { get; init; }
    public IReadOnlyList<CaptureDiffEntry> Diffs { get; init; } = [];
    public IReadOnlyList<CapturedButtonEvent> ButtonEvents { get; init; } = [];
}

/// <summary>Computes exact byte-level and field-level diffs between two read-only snapshots.</summary>
public static class DiscoveryDiffEngine
{
    public static CaptureDiffEntry Compute(
        string stepId,
        string title,
        string kind,
        CombinedStateSnapshot baseline,
        CombinedStateSnapshot current,
        ISet<string>? volatileFirmwareKeys = null,
        ISet<int>? volatileEcOffsets = null,
        string? userNote = null)
    {
        volatileFirmwareKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatileEcOffsets ??= new HashSet<int>();

        var fwChanges = DiffDictionaries(
            baseline.Elevated?.FirmwareValues,
            current.Elevated?.FirmwareValues,
            volatileFirmwareKeys);

        var ecChanges = DiffHexBytes(
            baseline.Elevated?.EcMemoryHex,
            current.Elevated?.EcMemoryHex,
            volatileEcOffsets);

        var powerChanges = new List<ValueChange>();
        if (baseline.Elevated?.PowerSchemeGuid != current.Elevated?.PowerSchemeGuid)
            powerChanges.Add(new("PowerSchemeGuid", baseline.Elevated?.PowerSchemeGuid, current.Elevated?.PowerSchemeGuid, false));
        if (baseline.Elevated?.PowerOverlayGuid != current.Elevated?.PowerOverlayGuid)
            powerChanges.Add(new("PowerOverlayGuid", baseline.Elevated?.PowerOverlayGuid, current.Elevated?.PowerOverlayGuid, false));

        var regChanges = DiffDictionaries(
            baseline.Elevated?.OemRegistry,
            current.Elevated?.OemRegistry,
            new HashSet<string>());

        var hidBefore = HidKeySet(baseline.Hid);
        var hidAfter = HidKeySet(current.Hid);
        var hidAdded = hidAfter.Except(hidBefore, StringComparer.OrdinalIgnoreCase).Order().ToList();
        var hidRemoved = hidBefore.Except(hidAfter, StringComparer.OrdinalIgnoreCase).Order().ToList();

        var xBefore = baseline.Hid is null ? null : string.Join(",", baseline.Hid.XInputActiveSlots);
        var xAfter = current.Hid is null ? null : string.Join(",", current.Hid.XInputActiveSlots);

        var ctrlMeta = new List<ValueChange>();
        var ctrlProfile = new List<ProfileByteDiff>();
        DiffControllers(baseline.Hid, current.Hid, ctrlMeta, ctrlProfile);

        return new CaptureDiffEntry
        {
            StepId = stepId,
            Title = title,
            Kind = kind,
            UserNote = userNote,
            CapturedAtUtc = current.CapturedAtUtc,
            FirmwareChanges = fwChanges,
            EcByteChanges = ecChanges,
            PowerChanges = powerChanges,
            RegistryChanges = regChanges,
            HidAdded = hidAdded,
            HidRemoved = hidRemoved,
            ControllerMetaChanges = ctrlMeta,
            ControllerProfileChanges = ctrlProfile,
            XInputBefore = xBefore,
            XInputAfter = xAfter,
        };
    }

    private static List<ValueChange> DiffDictionaries(
        Dictionary<string, string>? before,
        Dictionary<string, string>? after,
        ISet<string> volatileKeys)
    {
        var list = new List<ValueChange>();
        if (before is null && after is null) return list;

        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (before is not null) foreach (var k in before.Keys) keys.Add(k);
        if (after is not null) foreach (var k in after.Keys) keys.Add(k);

        foreach (var key in keys)
        {
            string? b = null;
            string? a = null;
            before?.TryGetValue(key, out b);
            after?.TryGetValue(key, out a);
            if (!string.Equals(b, a, StringComparison.Ordinal))
                list.Add(new ValueChange(key, b, a, volatileKeys.Contains(key)));
        }

        return list;
    }

    private static List<EcByteDiff> DiffHexBytes(string? beforeHex, string? afterHex, ISet<int> volatileOffsets)
    {
        var list = new List<EcByteDiff>();
        if (string.IsNullOrEmpty(beforeHex) || string.IsNullOrEmpty(afterHex)) return list;

        var before = ParseHex(beforeHex);
        var after = ParseHex(afterHex);
        var len = Math.Min(before.Length, after.Length);

        for (var i = 0; i < len; i++)
        {
            if (before[i] != after[i])
                list.Add(new EcByteDiff(i, $"0x{i:X2}", before[i], after[i], volatileOffsets.Contains(i)));
        }

        return list;
    }

    private static HashSet<string> HidKeySet(HidTopologySnapshot? hid)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hid is null) return set;

        foreach (var c in hid.Collections)
            set.Add($"VID_{c.VendorId:X4}&PID_{c.ProductId:X4} MI={c.InterfaceNumber ?? "-"} Col={c.CollectionNumber ?? "-"} UP=0x{c.UsagePage:X4} U=0x{c.Usage:X4} In={c.InputReportLength} Out={c.OutputReportLength}");

        return set;
    }

    private static void DiffControllers(
        HidTopologySnapshot? before,
        HidTopologySnapshot? after,
        List<ValueChange> metaChanges,
        List<ProfileByteDiff> profileChanges)
    {
        var mapBefore = IndexControllers(before?.VendorControllers);
        var mapAfter = IndexControllers(after?.VendorControllers);

        foreach (var key in mapBefore.Keys.Union(mapAfter.Keys, StringComparer.OrdinalIgnoreCase))
        {
            mapBefore.TryGetValue(key, out var b);
            mapAfter.TryGetValue(key, out var a);

            if (b is null || a is null)
            {
                metaChanges.Add(new ValueChange($"{key}.present", b is not null ? "yes" : "no", a is not null ? "yes" : "no", false));
                continue;
            }

            if (b.ModeByte != a.ModeByte)
                metaChanges.Add(new ValueChange($"{key}.mode", FormatByte(b.ModeByte), FormatByte(a.ModeByte), false));

            if (b.MKeysByte != a.MKeysByte)
                metaChanges.Add(new ValueChange($"{key}.mkeys", FormatByte(b.MKeysByte), FormatByte(a.MKeysByte), false));

            if (!string.IsNullOrEmpty(b.ProfileHex) && !string.IsNullOrEmpty(a.ProfileHex)
                && !string.Equals(b.ProfileHex, a.ProfileHex, StringComparison.OrdinalIgnoreCase))
            {
                var bytesB = ParseHex(b.ProfileHex);
                var bytesA = ParseHex(a.ProfileHex);
                var len = Math.Min(bytesB.Length, bytesA.Length);
                for (var i = 0; i < len; i++)
                {
                    if (bytesB[i] != bytesA[i])
                        profileChanges.Add(new ProfileByteDiff(key, i, $"0x{i:X4}", bytesB[i], bytesA[i]));
                }
            }
        }
    }

    private static Dictionary<string, VendorControllerSnapshot> IndexControllers(IReadOnlyList<VendorControllerSnapshot>? list)
    {
        var map = new Dictionary<string, VendorControllerSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (list is null) return map;

        foreach (var c in list)
        {
            var baseKey = $"{c.Protocol}:{c.VendorId:X4}:{c.ProductId:X4}";
            var key = baseKey;
            for (var n = 2; map.ContainsKey(key); n++)
                key = $"{baseKey}#{n}";
            map[key] = c;
        }

        return map;
    }

    private static string? FormatByte(byte? b) => b is { } v ? $"0x{v:X2} ({v})" : null;

    private static byte[] ParseHex(string hex)
    {
        var clean = hex.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (clean.Length % 2 != 0) return [];
        try { return Convert.FromHexString(clean); }
        catch (FormatException) { return []; }
    }
}

/// <summary>Renders a human-readable Markdown audit report from a <see cref="DeviceSupportBundle"/>.</summary>
public static class DiscoveryReportBuilder
{
    public static string BuildMarkdown(DeviceSupportBundle bundle)
    {
        var sb = new StringBuilder();
        var inv = bundle.Inventory;
        var id = inv?.Identity;

        sb.AppendLine("# Tsume Device Support Report");
        sb.AppendLine();
        sb.AppendLine($"- **Tsume:** {bundle.TsumeVersion}");
        sb.AppendLine($"- **Exported (UTC):** {bundle.ExportedAtUtc:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"- **Elevated firmware scan:** {(inv?.Elevated == true ? "Yes" : "No (unelevated fallback)")}");
        if (!string.IsNullOrWhiteSpace(inv?.Note))
            sb.AppendLine($"- **Note:** {inv.Note}");
        sb.AppendLine();

        if (id is not null)
        {
            sb.AppendLine("## 1. System Identity");
            sb.AppendLine();
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Baseboard | `{id.BaseBoardManufacturer}` / `{id.BaseBoardProduct}` (rev `{id.BaseBoardVersion}`) |");
            sb.AppendLine($"| System | `{id.SystemManufacturer}` / `{id.SystemModel}` |");
            sb.AppendLine($"| SKU / Family | `{id.SystemSku}` / `{id.SystemFamily}` |");
            sb.AppendLine($"| BIOS | `{id.BiosManufacturer}` `{id.BiosVersion}` ({id.BiosReleaseDate}) |");
            if (!string.IsNullOrWhiteSpace(id.EcMajorMinor))
                sb.AppendLine($"| SMBIOS EC | `{id.EcMajorMinor}` |");
            sb.AppendLine($"| CPU | {id.CpuName} ({id.CpuManufacturer}, {id.CpuCores}C/{id.CpuLogicalProcessors}T, {id.CpuMaxClockMhz} MHz) |");
            foreach (var gpu in id.Gpus)
                sb.AppendLine($"| GPU | {gpu.Name} (`{gpu.DriverVersion}`, `{gpu.ScrubbedPnpId}`) |");
            if (id.Battery is { } bat)
                sb.AppendLine($"| Battery | {bat.Name} (Design: {bat.DesignedCapacityMwh?.ToString(CultureInfo.InvariantCulture) ?? "?"} mWh, Full: {bat.FullChargedCapacityMwh?.ToString(CultureInfo.InvariantCulture) ?? "?"} mWh, AC: {bat.AcOnline}) |");
            sb.AppendLine($"| OS | {id.WindowsVersion} ({id.OsArchitecture}) |");
            sb.AppendLine();
        }

        if (inv?.PowerPlan is { } pp)
        {
            sb.AppendLine("## 2. Windows Power & Display");
            sb.AppendLine();
            sb.AppendLine($"- **Power scheme:** {pp.ActiveSchemeName ?? "unknown"} (`{pp.ActiveSchemeGuid ?? "?"}`)");
            sb.AppendLine($"- **Power overlay:** {pp.EffectiveOverlayLabel ?? "unknown"} (`{pp.EffectiveOverlayGuid ?? "?"}`)");
            sb.AppendLine($"- **CPU Boost Mode (AC / DC):** {pp.CpuBoostModeAcLabel ?? "?"} ({pp.CpuBoostModeAc}) / {pp.CpuBoostModeDcLabel ?? "?"} ({pp.CpuBoostModeDc})");
            if (pp.CpuBoostModeAc == 0 || pp.CpuBoostModeDc == 0)
                sb.AppendLine("  - **WARNING:** Processor performance boost mode is Disabled. Power limits above base-clock wattage will not engage under load.");
        }

        if (bundle.Display is { } disp)
        {
            sb.AppendLine($"- **Display:** {disp.ActiveWidth}×{disp.ActiveHeight} @ {disp.CurrentRefreshHz} Hz (signal rates: [{string.Join(", ", disp.SignalRefreshRates)}] Hz; desktop rates: [{string.Join(", ", disp.DesktopRefreshRates)}] Hz)");
            foreach (var panel in disp.Panels)
                sb.AppendLine($"  - Panel: `{panel.FriendlyName}` (EDID `{panel.EdidManufacturerId}:{panel.EdidProductCode}`, {panel.OutputTechnology})");
        }
        sb.AppendLine();

        if (inv is not null)
        {
            sb.AppendLine("## 3. Firmware & WMI Surface");
            sb.AppendLine();
            sb.AppendLine($"- **Detected OEM WMI classes:** {inv.WmiClasses.Count} (full MOF definitions in `wmi-mof/`)");
            foreach (var cls in inv.WmiClasses)
            {
                var tag = cls.IsEvent ? "event" : $"{cls.Methods.Count} methods";
                sb.AppendLine($"  - `{cls.Namespace}:{cls.Name}` ({tag}, {cls.Instances.Count} instance(s))");
            }
            sb.AppendLine();

            if (inv.Firmware is { } fw)
            {
                sb.AppendLine($"### Read-Only Firmware Snapshot ({fw.VendorFamily})");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(fw.BridgeInstance))
                    sb.AppendLine($"- **Instance:** `{fw.BridgeInstance}`");
                if (!string.IsNullOrWhiteSpace(fw.FirmwareVersionText))
                    sb.AppendLine($"- **Firmware version:** `{fw.FirmwareVersionText}`");
                if (fw.Values.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| Key | Value |");
                    sb.AppendLine("|---|---|");
                    foreach (var kv in fw.Values.OrderBy(k => k.Key, StringComparer.Ordinal))
                        sb.AppendLine($"| `{kv.Key}` | `{kv.Value}` |");
                }
                if (fw.Tables.Count > 0)
                {
                    var acceptedTables = fw.Tables
                        .Where(kv => !string.Equals(kv.Value, "refused", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .ToList();
                    if (acceptedTables.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("| Table | Data |");
                        sb.AppendLine("|---|---|");
                        foreach (var kv in acceptedTables)
                            sb.AppendLine($"| `{kv.Key}` | `{kv.Value}` |");
                    }
                }
                if (fw.Capabilities.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Capabilities:**");
                    foreach (var cap in fw.Capabilities.Take(64))
                        sb.AppendLine($"- `{cap}`");
                }
                sb.AppendLine();
            }

            if (inv.TimedDelta is { } td)
            {
                sb.AppendLine("### Live Telemetry Delta (Idle → +2s → 3s CPU Load)");
                sb.AppendLine();
                if (td.EnergyMeters.Count > 0)
                {
                    foreach (var em in td.EnergyMeters)
                        sb.AppendLine($"- Energy meter `{em.InstanceName}`: idle {em.IdleWatts?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} W → load {em.LoadWatts?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a"} W");
                }
                else
                {
                    sb.AppendLine("- Energy meter: none exposed by Windows");
                }
                if (td.BatteryRateIdleMw is not null || td.BatteryRateLoadMw is not null)
                    sb.AppendLine($"- Battery rate: idle {td.BatteryRateIdleMw} mW → load {td.BatteryRateLoadMw} mW (AC: {td.AcOnline})");
                if (td.VolatileEcBytes.Count > 0)
                {
                    sb.AppendLine($"- **Volatile EC bytes ({td.VolatileEcBytes.Count}):** " +
                        string.Join(", ", td.VolatileEcBytes.Select(b => $"`{b.OffsetHex}` ({b.Idle1:X2}→{b.Idle2:X2}→{b.UnderLoad:X2})")));
                }
                if (td.VolatileValues.Count > 0)
                {
                    sb.AppendLine($"- **Volatile firmware values ({td.VolatileValues.Count}):** " +
                        string.Join(", ", td.VolatileValues.Select(v => $"`{v.Key}` ({v.Idle1}→{v.UnderLoad})")));
                }
                sb.AppendLine();
            }

            if (inv.AcpiPowerDevices.Count > 0)
            {
                sb.AppendLine("### ACPI / PCI Power Devices");
                sb.AppendLine();
                foreach (var dev in inv.AcpiPowerDevices)
                    sb.AppendLine($"- `{dev.ScrubbedDeviceId}`: {dev.Name} (svc: `{dev.Service}`, {dev.Status})");
                sb.AppendLine();
            }
        }

        if (bundle.Hid is { } hid)
        {
            sb.AppendLine("## 4. Controllers, HID & Lighting");
            sb.AppendLine();
            sb.AppendLine($"- **HID collections:** {hid.Collections.Count}");
            sb.AppendLine($"- **Active XInput slots:** {(hid.XInputActiveSlots.Count == 0 ? "none" : string.Join(", ", hid.XInputActiveSlots))}");
            sb.AppendLine($"- **Windows LampArray devices:** {hid.LampArrayDevices.Count}");
            foreach (var vc in hid.VendorControllers)
                sb.AppendLine($"- **Vendor controller ({vc.Protocol}):** `VID_{vc.VendorId:X4}&PID_{vc.ProductId:X4}` fw `{vc.FirmwareVersion}` mode={vc.ModeByte} mkeys={vc.MKeysByte} profile={vc.ProfileLength}B sha256=`{vc.ProfileSha256?[..Math.Min(12, vc.ProfileSha256.Length)]}`");
            sb.AppendLine();
            sb.AppendLine("| VID:PID | Ver | MI / Col | UsagePage:Usage | In / Out / Feat | Report IDs (In / Out / Feat) | RW | Product |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var c in hid.Collections)
            {
                var inIds = string.Join(",", c.InputReportIds.Select(id => $"0x{id:X2}"));
                var outIds = string.Join(",", c.OutputReportIds.Select(id => $"0x{id:X2}"));
                var featIds = string.Join(",", c.FeatureReportIds.Select(id => $"0x{id:X2}"));
                sb.AppendLine($"| `{c.VendorId:X4}:{c.ProductId:X4}` | {c.VersionText} | {c.InterfaceNumber ?? "-"} / {c.CollectionNumber ?? "-"} | `0x{c.UsagePage:X4}:0x{c.Usage:X4}` | {c.InputReportLength}/{c.OutputReportLength}/{c.FeatureReportLength} | {inIds} / {outIds} / {featIds} | {(c.CanOpenReadWrite ? "yes" : "no")} | {c.Product} |");
            }
            sb.AppendLine();
        }

        if (bundle.Diffs.Count > 0)
        {
            sb.AppendLine("## 5. State Diffs (Guided Capture & Watch Mode)");
            sb.AppendLine();
            foreach (var diff in bundle.Diffs)
            {
                sb.AppendLine($"### {diff.Title} (`{diff.StepId}`, {diff.Kind})");
                if (!string.IsNullOrWhiteSpace(diff.UserNote))
                    sb.AppendLine($"- Note: {diff.UserNote}");
                if (diff.Kind == "skipped")
                {
                    sb.AppendLine("- *Skipped by user.*");
                    sb.AppendLine();
                    continue;
                }

                var nonVolEc = diff.EcByteChanges.Where(e => !e.IsVolatile).ToList();
                var nonVolFw = diff.FirmwareChanges.Where(f => !f.IsVolatile).ToList();

                if (nonVolEc.Count > 0)
                    sb.AppendLine("- **EC bytes changed (non-volatile):** " + string.Join(", ", nonVolEc.Select(e => $"`{e.OffsetHex}`: `0x{e.Before:X2}` ({e.Before}) → `0x{e.After:X2}` ({e.After})")));
                if (nonVolFw.Count > 0)
                    sb.AppendLine("- **Firmware values changed:** " + string.Join(", ", nonVolFw.Select(f => $"`{f.Key}`: `{f.Before}` → `{f.After}`")));
                if (diff.PowerChanges.Count > 0)
                    sb.AppendLine("- **Windows power changed:** " + string.Join(", ", diff.PowerChanges.Select(p => $"`{p.Key}`: `{p.Before}` → `{p.After}`")));
                if (diff.RegistryChanges.Count > 0)
                    sb.AppendLine("- **OEM registry changed:** " + string.Join(", ", diff.RegistryChanges.Select(r => $"`{r.Key}`: `{r.Before}` → `{r.After}`")));
                if (diff.HidAdded.Count > 0 || diff.HidRemoved.Count > 0)
                    sb.AppendLine($"- **HID bus changed:** +{diff.HidAdded.Count} added / -{diff.HidRemoved.Count} removed");
                if (diff.XInputBefore != diff.XInputAfter)
                    sb.AppendLine($"- **XInput slots:** `{diff.XInputBefore}` → `{diff.XInputAfter}`");
                if (diff.ControllerMetaChanges.Count > 0)
                    sb.AppendLine("- **Controller meta:** " + string.Join(", ", diff.ControllerMetaChanges.Select(c => $"`{c.Key}`: `{c.Before}` → `{c.After}`")));
                if (diff.ControllerProfileChanges.Count > 0)
                {
                    var preview = diff.ControllerProfileChanges.Take(40).Select(p => $"`{p.Offset}`(`{p.OffsetHex}`): `{p.Before:X2}`→`{p.After:X2}`");
                    var suffix = diff.ControllerProfileChanges.Count > 40 ? $" … (+{diff.ControllerProfileChanges.Count - 40} more in diffs/{diff.StepId}.json)" : "";
                    sb.AppendLine($"- **Controller profile bytes ({diff.ControllerProfileChanges.Count}):** {string.Join(", ", preview)}{suffix}");
                }
                if (diff.NonVolatileChangeCount == 0 && diff.Kind != "baseline")
                    sb.AppendLine("- *No non-volatile differences detected.*");
                sb.AppendLine();
            }
        }

        if (bundle.ButtonEvents.Count > 0)
        {
            sb.AppendLine("## 6. Dedicated Hardware Button Timeline");
            sb.AppendLine();
            sb.AppendLine("| Prompt | +ms | Source | Summary |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (var ev in bundle.ButtonEvents)
                sb.AppendLine($"| `{ev.PromptId}` | {ev.ElapsedMs} | `{ev.Source}` | {ev.Summary} |");
            sb.AppendLine();
        }

        if (inv?.OemFootprint is { } oem)
        {
            sb.AppendLine("## 7. OEM Software Footprint");
            sb.AppendLine();
            sb.AppendLine($"- **Vendor keywords:** {string.Join(", ", oem.VendorKeywords)}");
            sb.AppendLine($"- **Matching processes ({oem.Processes.Count}):** {string.Join(", ", oem.Processes.Select(p => $"`{p.Name}`"))}");
            sb.AppendLine($"- **Matching services ({oem.Services.Count}):** {string.Join(", ", oem.Services.Select(s => $"`{s.Name}` ({s.State}/{s.StartMode})"))}");
            sb.AppendLine($"- **Matching scheduled tasks ({oem.ScheduledTasks.Count}):** {string.Join(", ", oem.ScheduledTasks.Select(t => $"`{t.TaskName}` ({t.State})"))}");
            sb.AppendLine($"- **OEM config registry values captured:** {oem.RegistryValues.Count}");
            sb.AppendLine();
        }

        if (bundle.EnvironmentCheck is { } env)
        {
            sb.AppendLine("## 8. Environment & Read-Back Check");
            sb.AppendLine();
            foreach (var f in env.Findings)
                sb.AppendLine($"- **[{f.Level.ToUpperInvariant()}] {f.Area}:** {f.Message}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
