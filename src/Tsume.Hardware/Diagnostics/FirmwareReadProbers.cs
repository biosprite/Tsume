using System.Buffers.Binary;
using System.Globalization;
using System.Management;
using System.Text;
using Tsume.Core.Diagnostics;
using Tsume.Hardware.Acer;
using Tsume.Hardware.Msi;
using Tsume.Hardware.Msi.Diagnostics;

namespace Tsume.Hardware.Diagnostics;

/// <summary>Safe property lookup for <see cref="ManagementBaseObject"/> (its indexer throws <see cref="ManagementException"/> when a property is absent).</summary>
internal static class WmiProps
{
    public static bool TryGet(ManagementBaseObject? obj, string name, out object? value)
    {
        value = null;
        if (obj is null) return false;

        try
        {
            foreach (PropertyData prop in obj.Properties)
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException)
        {
        }

        return false;
    }

    public static string? TryGetString(ManagementBaseObject? obj, string name) =>
        TryGet(obj, name, out var val) ? val?.ToString() : null;
}

/// <summary>
/// Strictly read-only OEM firmware inspector. Implementations must NEVER invoke write/setter WMI methods
/// (such as <c>Set_Data</c>, <c>DEVS</c>, or <c>SetFeatureValue</c>).
/// </summary>
public interface IFirmwareReadProber : IDisposable
{
    string VendorFamily { get; }
    FirmwareReadSnapshot ReadFull();
    (Dictionary<string, string> Values, string? EcMemoryHex) ReadFastState();
}

/// <summary>Discovers the first matching read-only OEM firmware interface present on this machine.</summary>
public static class FirmwareReadProbers
{
    public static IFirmwareReadProber? TryOpen() =>
        MsiReadProber.TryOpen()
        ?? AcerReadProber.TryOpen()
        ?? AsusReadProber.TryOpen()
        ?? LenovoReadProber.TryOpen();
}

/// <summary>
/// Generalized MSI ACPI-WMI read-only prober. Auto-discovers any <c>MSI_ACPI</c> instance instead of
/// hardcoding <c>ACPI\PNP0C14\0_0</c>, reads all standard tables and the full 256-byte EC map.
/// </summary>
internal sealed class MsiReadProber : IFirmwareReadProber
{
    private const string Scope = @"root\WMI";

    private static readonly (string Method, byte[] Selectors)[] FastStateTables =
    [
        ("Get_Fan",         [0]),
        ("Get_Temperature", [0]),
        ("Get_Thermal",     [0, 1, 2, 3]),
        ("Get_AP",          [0, 1, 2, 3]),
        ("Get_Power",       [0, 1, 2, 3]),
    ];

    private readonly ManagementObject _acpi;
    private readonly string _instanceName;

    private MsiReadProber(ManagementObject acpi, string instanceName)
    {
        _acpi = acpi;
        _instanceName = instanceName;
    }

    public string VendorFamily => "msi-acpi";

    public static IFirmwareReadProber? TryOpen()
    {
        try
        {
            using var search = new ManagementObjectSearcher(Scope, "SELECT * FROM MSI_ACPI");
            using var results = search.Get();
            foreach (ManagementObject obj in results)
            {
                var name = WmiProps.TryGetString(obj, "InstanceName") ?? obj.Path?.RelativePath ?? "MSI_ACPI";
                return new MsiReadProber(obj, name);
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public FirmwareReadSnapshot ReadFull()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        string? fwText = null;

        var wmi = Call("Get_WMI", selector: null);
        if (wmi.Ok) values["Get_WMI"] = Hex(wmi.Data, 16);

        var ec = Call("Get_EC", selector: null);
        if (ec.Ok)
        {
            if (ec.Data.Length > 1)
            {
                fwText = Encoding.ASCII.GetString(ec.Data, 1, Math.Min(28, ec.Data.Length - 1)).TrimEnd('\0', ' ');
                values["Get_EC"] = fwText;
            }
            tables["Get_EC_raw"] = Hex(ec.Data, 31);
        }

        foreach (var (method, selectors) in FirmwareProbe.Tables)
        {
            foreach (var selector in selectors)
            {
                var reply = Call(method, selector);
                tables[$"{method}({selector})"] = reply.Ok ? Hex(reply.Data, 16) : "refused";
            }
        }

        var ecBytes = DumpEcBytes();

        // Annotate live fan tachometer periods if Get_Fan(0) succeeded.
        var fan0 = Call("Get_Fan", 0);
        if (fan0.Ok && fan0.Data.Length >= 4)
        {
            var p1 = (fan0.Data[0] << 8) | fan0.Data[1];
            var p2 = (fan0.Data[2] << 8) | fan0.Data[3];
            values["Get_Fan(0).period1"] = p1.ToString(CultureInfo.InvariantCulture);
            values["Get_Fan(0).period2"] = p2.ToString(CultureInfo.InvariantCulture);
        }

        var temp0 = Call("Get_Temperature", 0);
        if (temp0.Ok && temp0.Data.Length >= 1)
            values["Get_Temperature(0)[0]"] = $"{temp0.Data[0]} C";

        return new FirmwareReadSnapshot
        {
            VendorFamily = VendorFamily,
            BridgeInstance = _instanceName,
            FirmwareVersionText = fwText,
            Values = values,
            Tables = tables,
            EcMemoryHex = ecBytes is null ? null : Convert.ToHexString(ecBytes),
        };
    }

    public (Dictionary<string, string> Values, string? EcMemoryHex) ReadFastState()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ecBytes = DumpEcBytes();

        foreach (var (method, selectors) in FastStateTables)
        {
            foreach (var selector in selectors)
            {
                var reply = Call(method, selector);
                if (reply.Ok && reply.Data.Length > 0)
                    values[$"{method}({selector})"] = Hex(reply.Data, 16);
            }
        }

        return (values, ecBytes is null ? null : Convert.ToHexString(ecBytes));
    }

    private byte[]? DumpEcBytes()
    {
        var dump = new byte[256];
        var anyAccepted = false;

        for (var addr = 0; addr < 256; addr++)
        {
            var reply = Call("Get_Data", (byte)addr);
            if (reply.Ok && reply.Data.Length > 0)
            {
                dump[addr] = reply.Data[0];
                anyAccepted = true;
            }
        }

        return anyAccepted ? dump : null;
    }

    private (bool Ok, byte[] Data) Call(string method, byte? selector)
    {
        try
        {
            lock (MsiAcpiBridge.AcpiCallGate)
            {
                using var output = selector is null
                    ? _acpi.InvokeMethod(method, null, null)
                    : InvokeWithSelector(method, selector.Value);

                if (WmiProps.TryGet(output, "Data", out var dataObj))
                {
                    using var data = dataObj as ManagementBaseObject;
                    if (data is not null
                        && WmiProps.TryGet(data, "Bytes", out var bytesObj)
                        && bytesObj is byte[] { Length: > 1 } bytes)
                        return (bytes[0] != 0, bytes[1..]);
                }
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return (false, []);
    }

    private ManagementBaseObject InvokeWithSelector(string method, byte selector)
    {
        var package = new byte[MsiAcpiBridge.PackageSize];
        package[0] = selector;
        return MsiAcpiBridge.InvokeWithPackage(_acpi, method, package);
    }

    private static string Hex(byte[] data, int maxLen)
    {
        var take = Math.Min(maxLen, data.Length);
        return string.Join(' ', Convert.ToHexString(data, 0, take).Chunk(2).Select(pair => new string(pair)));
    }

    public void Dispose() => _acpi.Dispose();
}

/// <summary>
/// Strictly read-only ASUS ATKACPI-WMI (<c>AsusAtkWmi_WMNB</c>) prober.
/// Invokes ONLY <c>DSTS</c> (status read) on a curated whitelist of safe handheld device IDs; never calls <c>DEVS</c>.
/// </summary>
internal sealed class AsusReadProber : IFirmwareReadProber
{
    private const string Scope = @"root\WMI";

    private static readonly (string Name, uint DeviceId)[] ReadOnlyDstsIds =
    [
        ("ThermalPolicy_Throttle", 0x00120075),
        ("PPT_SPL_Sustained",      0x001200A0),
        ("PPT_SPPT_Slow",          0x001200C1),
        ("PPT_FPPT_Fast",          0x001200C2),
        ("PPT_APU_SPPT",           0x001200B0),
        ("PPT_Platform_SPPT",      0x001200B1),
        ("Fan1_CPU_RPM",           0x00110013),
        ("Fan2_GPU_RPM",           0x00110014),
        ("Fan_Mode",               0x00110019),
        ("ChargeLimit_RSOC",       0x00120057),
        ("Ally_MCU_PowerSave",     0x000600C7),
        ("Panel_Overdrive",        0x00050019),
        ("GPU_Mux",                0x00090016),
        ("EGPU_Connected",         0x00090018),
        ("BootSound",              0x00130022),
    ];

    private readonly ManagementObject _acpi;
    private readonly string _instanceName;

    private AsusReadProber(ManagementObject acpi, string instanceName)
    {
        _acpi = acpi;
        _instanceName = instanceName;
    }

    public string VendorFamily => "asus-atk-wmi";

    public static IFirmwareReadProber? TryOpen()
    {
        foreach (var className in new[] { "AsusAtkWmi_WMNB", "AsusAtkWmi_WMBD" })
        {
            try
            {
                using var search = new ManagementObjectSearcher(Scope, $"SELECT * FROM {className}");
                using var results = search.Get();
                foreach (ManagementObject obj in results)
                {
                    var name = WmiProps.TryGetString(obj, "InstanceName") ?? obj.Path?.RelativePath ?? className;
                    return new AsusReadProber(obj, $"{className}:{name}");
                }
            }
            catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public FirmwareReadSnapshot ReadFull()
    {
        var values = QueryAll();
        return new FirmwareReadSnapshot
        {
            VendorFamily = VendorFamily,
            BridgeInstance = _instanceName,
            Values = values,
        };
    }

    public (Dictionary<string, string> Values, string? EcMemoryHex) ReadFastState() => (QueryAll(), null);

    private Dictionary<string, string> QueryAll()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, id) in ReadOnlyDstsIds)
        {
            if (QueryDsts(id) is { } raw)
                values[$"{name} (0x{id:X8})"] = $"0x{raw:X8} ({raw & 0xFFFF})";
        }

        return values;
    }

    private uint? QueryDsts(uint deviceId)
    {
        try
        {
            using var input = _acpi.GetMethodParameters("DSTS");
            input["Device_ID"] = deviceId;
            using var output = _acpi.InvokeMethod("DSTS", input, null);
            if (WmiProps.TryGet(output, "device_status", out var devStatus))
            {
                if (devStatus is uint status) return status;
                if (devStatus is int signed) return unchecked((uint)signed);
            }
            if (WmiProps.TryGet(output, "Return", out var retVal))
            {
                if (retVal is uint ret) return ret;
                if (retVal is int retSigned) return unchecked((uint)retSigned);
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public void Dispose() => _acpi.Dispose();
}

/// <summary>
/// Strictly read-only Lenovo Legion WMI (<c>LENOVO_GAMEZONE_DATA</c>, <c>LENOVO_CAPABILITY_DATA_*</c>,
/// <c>LENOVO_OTHER_METHOD</c>) prober. Calls only <c>IsSupport*</c> / <c>Get*</c> and dumps capability tables.
/// </summary>
internal sealed class LenovoReadProber : IFirmwareReadProber
{
    private const string Scope = @"root\WMI";

    private static readonly string[] GameZoneReadMethods =
    [
        "IsSupportSmartFan",
        "GetSmartFanMode",
        "GetThermalMode",
        "IsSupportFanCooling",
        "GetFanCoolingStatus",
        "GetPowerChargeMode",
        "IsSupportLightingFeature",
        "GetVersion",
        "GetBIOSVersion",
        "GetProductName",
    ];

    private readonly ManagementObject? _gameZone;
    private readonly ManagementObject? _otherMethod;
    private readonly string _instanceName;

    private LenovoReadProber(ManagementObject? gameZone, ManagementObject? otherMethod, string instanceName)
    {
        _gameZone = gameZone;
        _otherMethod = otherMethod;
        _instanceName = instanceName;
    }

    public string VendorFamily => "lenovo-wmi";

    public static IFirmwareReadProber? TryOpen()
    {
        var gz = FirstInstance("LENOVO_GAMEZONE_DATA");
        var other = FirstInstance("LENOVO_OTHER_METHOD");
        if (gz is null && other is null) return null;

        var name = WmiProps.TryGetString(gz, "InstanceName")
            ?? WmiProps.TryGetString(other, "InstanceName")
            ?? gz?.Path?.RelativePath
            ?? other?.Path?.RelativePath
            ?? "LENOVO_WMI";
        return new LenovoReadProber(gz, other, name);
    }

    public FirmwareReadSnapshot ReadFull()
    {
        var values = QueryValues();
        var capabilities = ReadCapabilityTables();

        // Also query GetFeatureValue for every feature ID discovered in capability tables (strictly read-only).
        if (_otherMethod is not null)
        {
            foreach (var featureId in ExtractCapabilityIds())
            {
                if (QueryOtherFeature(featureId) is { } val)
                    values[$"Feature(0x{featureId:X8})"] = $"{val} (0x{val:X8})";
            }
        }

        return new FirmwareReadSnapshot
        {
            VendorFamily = VendorFamily,
            BridgeInstance = _instanceName,
            Values = values,
            Capabilities = capabilities,
        };
    }

    public (Dictionary<string, string> Values, string? EcMemoryHex) ReadFastState()
    {
        var values = QueryValues();
        if (_otherMethod is not null)
        {
            foreach (var featureId in ExtractCapabilityIds().Take(64))
            {
                if (QueryOtherFeature(featureId) is { } val)
                    values[$"Feature(0x{featureId:X8})"] = $"{val} (0x{val:X8})";
            }
        }

        return (values, null);
    }

    private Dictionary<string, string> QueryValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_gameZone is null) return values;

        foreach (var method in GameZoneReadMethods)
        {
            try
            {
                using var output = _gameZone.InvokeMethod(method, null, null);
                if (output is null) continue;
                object? val = null;
                if (!WmiProps.TryGet(output, "Data", out val) || val is null)
                    WmiProps.TryGet(output, "ReturnValue", out val);
                if (val is null)
                {
                    foreach (PropertyData prop in output.Properties)
                    {
                        if (!prop.Name.StartsWith("__", StringComparison.Ordinal) && prop.Value is not null)
                        {
                            val = prop.Value;
                            break;
                        }
                    }
                }
                if (val is not null)
                {
                    using (val as IDisposable)
                        values[$"GameZone.{method}"] = $"{val}";
                }
            }
            catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
            }
        }

        return values;
    }

    private static List<string> ReadCapabilityTables()
    {
        var list = new List<string>();
        foreach (var cls in new[] { "LENOVO_CAPABILITY_DATA_00", "LENOVO_CAPABILITY_DATA_01" })
        {
            try
            {
                using var search = new ManagementObjectSearcher(Scope, $"SELECT * FROM {cls}");
                using var results = search.Get();
                foreach (ManagementObject row in results)
                {
                    using (row)
                    {
                        var fields = row.Properties.Cast<PropertyData>()
                            .Where(p => !p.Name.Equals("InstanceName", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("Active", StringComparison.OrdinalIgnoreCase))
                            .Select(p => $"{p.Name}={FormatProp(p.Value)}");
                        list.Add($"{cls}: {string.Join(", ", fields)}");
                    }
                }
            }
            catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    private static IReadOnlyList<uint> ExtractCapabilityIds()
    {
        var ids = new SortedSet<uint>();
        foreach (var cls in new[] { "LENOVO_CAPABILITY_DATA_00", "LENOVO_CAPABILITY_DATA_01" })
        {
            try
            {
                using var search = new ManagementObjectSearcher(Scope, $"SELECT IDs FROM {cls}");
                using var results = search.Get();
                foreach (ManagementObject row in results)
                {
                    using (row)
                    {
                        if (row["IDs"] is uint id) ids.Add(id);
                        else if (row["IDs"] is uint[] arr) foreach (var x in arr) ids.Add(x);
                    }
                }
            }
            catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
            }
        }

        return [.. ids];
    }

    private uint? QueryOtherFeature(uint id)
    {
        if (_otherMethod is null) return null;
        try
        {
            using var input = _otherMethod.GetMethodParameters("GetFeatureValue");
            input["IDs"] = id;
            using var output = _otherMethod.InvokeMethod("GetFeatureValue", input, null);
            if (WmiProps.TryGet(output, "Value", out var valObj))
            {
                if (valObj is uint value) return value;
                if (valObj is int signed) return unchecked((uint)signed);
            }
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static ManagementObject? FirstInstance(string className)
    {
        try
        {
            using var search = new ManagementObjectSearcher(Scope, $"SELECT * FROM {className}");
            using var results = search.Get();
            foreach (ManagementObject obj in results) return obj;
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string FormatProp(object? value) => value switch
    {
        null => "null",
        uint u => $"{u} (0x{u:X})",
        uint[] arr => "[" + string.Join(", ", arr.Take(16).Select(x => $"0x{x:X}")) + "]",
        _ => $"{value}",
    };

    public void Dispose()
    {
        _gameZone?.Dispose();
        _otherMethod?.Dispose();
    }
}

/// <summary>
/// Strictly read-only Acer handheld prober (<c>AcerGamingFunction</c>, <c>BatteryControl</c>, and EC HID
/// <c>1025:174B</c>). Invokes only getters; never writes operating modes, profiles, fan settings, or charge limits.
/// </summary>
internal sealed class AcerReadProber : IFirmwareReadProber
{
    private readonly AcerWmiBridge? _wmi;
    private readonly AcerEcHid? _ecHid;
    private readonly string _instanceName;

    private AcerReadProber(AcerWmiBridge? wmi, AcerEcHid? ecHid, string instanceName)
    {
        _wmi = wmi;
        _ecHid = ecHid;
        _instanceName = instanceName;
    }

    public string VendorFamily => "acer-wmi-echid";

    public static IFirmwareReadProber? TryOpen()
    {
        var wmi = AcerWmiBridge.TryOpen();
        var ecHid = AcerEcHid.TryOpen();
        if (wmi is null && ecHid is null) return null;

        var label = (wmi is not null, ecHid is not null) switch
        {
            (true, true)  => "AcerGamingFunction + EC-HID 1025:174B",
            (true, false) => "AcerGamingFunction",
            _             => "EC-HID 1025:174B",
        };

        return new AcerReadProber(wmi, ecHid, label);
    }

    public FirmwareReadSnapshot ReadFull()
    {
        var values = QueryAll(includeStaticLimits: true);
        return new FirmwareReadSnapshot
        {
            VendorFamily = VendorFamily,
            BridgeInstance = _instanceName,
            FirmwareVersionText = _wmi?.ReadBiosVersion(),
            Values = values,
        };
    }

    public (Dictionary<string, string> Values, string? EcMemoryHex) ReadFastState() =>
        (QueryAll(includeStaticLimits: false), null);

    private Dictionary<string, string> QueryAll(bool includeStaticLimits)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_wmi is not null)
        {
            if (_wmi.GetGamingSysInfo(0x0000) is { } cap)
                values["WMI.SensorBitmap"] = $"0x{(cap >> 24) & 0xFFFF:X4}";
            if (_wmi.GetGamingSysInfo(AcerDevice.SensorCpuTemp) is { } cpu)
                values["WMI.CpuTempC"] = $"{(cpu >> 8) & 0xFFFF} C";
            if (_wmi.GetGamingSysInfo(0x0301) is { } sys)
                values["WMI.SysTempC"] = $"{(sys >> 8) & 0xFFFF} C";
            if (_wmi.GetGamingSysInfo(AcerDevice.SensorFan1Rpm) is { } f1)
                values["WMI.Fan1Rpm"] = $"{(f1 >> 8) & 0xFFFF} rpm";
            if (_wmi.GetGamingSysInfo(AcerDevice.SensorFan2Rpm) is { } f2)
                values["WMI.Fan2Rpm"] = $"{(f2 >> 8) & 0xFFFF} rpm";
            if (_wmi.GetGamingFanBehavior(AcerDevice.FanMaskBoth) is { } fb)
                values["WMI.FanBehavior"] = $"0x{fb:X8} (fan1={(fb >> 10) & 3}, fan2={(fb >> 12) & 3})";
            if (_wmi.GetGamingFanSpeed(2) is { } fs1)
                values["WMI.Fan1StoredDuty"] = $"{(fs1 >> 8) & 0xFF}%";
            if (_wmi.GetGamingFanSpeed(3) is { } fs2)
                values["WMI.Fan2StoredDuty"] = $"{(fs2 >> 8) & 0xFF}%";
            if (_wmi.GetBatteryHealthControl() is { } bh)
                values["WMI.BatteryHealthLimit80"] = bh ? "on" : "off";
        }

        if (_ecHid is not null)
        {
            if (_ecHid.GetSystemUsageMode() is { } mode)
                values["EcHid.SystemUsageMode"] = mode.ToString(CultureInfo.InvariantCulture);

            if (_ecHid.GetOcProfile(AcerDevice.ManualOcProfile) is { Length: >= 6 } prof)
            {
                var pl1 = BinaryPrimitives.ReadUInt16LittleEndian(prof.AsSpan(0, 2));
                var pl2 = BinaryPrimitives.ReadUInt16LittleEndian(prof.AsSpan(4, 2));
                values["EcHid.OcProfile3.PL1_PL2"] = $"{pl1} W / {pl2} W";
                values["EcHid.OcProfile3.Raw"] = Convert.ToHexString(prof);
            }

            if (includeStaticLimits)
            {
                if (_ecHid.GetOcProfileLimit(AcerDevice.ManualOcProfile, 1) is { } pl1Min)
                    values["EcHid.Limit.PL1_Min"] = $"{pl1Min} W";
                if (_ecHid.GetOcProfileLimit(AcerDevice.ManualOcProfile, 2) is { } pl1Max)
                    values["EcHid.Limit.PL1_Max"] = $"{pl1Max} W";
                if (_ecHid.GetOcProfileLimit(AcerDevice.ManualOcProfile, 4) is { } pl2Max)
                    values["EcHid.Limit.PL2_Max"] = $"{pl2Max} W";
            }

            if (_ecHid.GetBatteryBoundary() is { } bound)
                values["EcHid.BatteryBoundary"] = bound.Enabled ? $"on ({bound.Lower}%..{bound.Upper}%)" : "off";
        }

        return values;
    }

    public void Dispose()
    {
        _wmi?.Dispose();
        _ecHid?.Dispose();
    }
}
