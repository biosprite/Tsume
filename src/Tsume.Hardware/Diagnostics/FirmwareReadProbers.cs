using System.Globalization;
using System.Management;
using System.Text;
using Tsume.Core.Diagnostics;

namespace Tsume.Hardware.Diagnostics;

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
    private const string PackageClass = "Package_32";
    private const int PackageSize = 32;

    private static readonly (string Method, byte[] Selectors)[] Tables =
    [
        ("Get_Fan",         [0, 1, 2, 3]),
        ("Get_Temperature", [0, 1, 2]),
        ("Get_Thermal",     [0, 1, 2, 3]),
        ("Get_AP",          [0, 1, 2, 3]),
        ("Get_Device",      [0, 1]),
        ("Get_Power",       [0]),
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
                var name = obj["InstanceName"]?.ToString() ?? obj.Path?.RelativePath ?? "MSI_ACPI";
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
            fwText = Encoding.ASCII.GetString(ec.Data, 1, Math.Min(28, ec.Data.Length - 1)).TrimEnd('\0', ' ');
            values["Get_EC"] = fwText;
            tables["Get_EC_raw"] = Hex(ec.Data, 31);
        }

        foreach (var (method, selectors) in Tables)
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

        var fan0 = Call("Get_Fan", 0);
        if (fan0.Ok && fan0.Data.Length >= 4)
            values["Get_Fan(0)"] = Hex(fan0.Data, 8);

        var temp0 = Call("Get_Temperature", 0);
        if (temp0.Ok && temp0.Data.Length >= 4)
            values["Get_Temperature(0)"] = Hex(temp0.Data, 8);

        var ap0 = Call("Get_AP", 0);
        if (ap0.Ok && ap0.Data.Length >= 4)
            values["Get_AP(0)"] = Hex(ap0.Data, 8);

        var ap1 = Call("Get_AP", 1);
        if (ap1.Ok && ap1.Data.Length >= 4)
            values["Get_AP(1)"] = Hex(ap1.Data, 8);

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
            using var output = selector is null
                ? _acpi.InvokeMethod(method, null, null)
                : InvokeWithPackage(method, selector.Value);

            if (output?["Data"] is ManagementBaseObject { } data && data["Bytes"] is byte[] { Length: > 1 } bytes)
                return (bytes[0] != 0, bytes[1..]);
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
        }

        return (false, []);
    }

    private ManagementBaseObject InvokeWithPackage(string method, byte selector)
    {
        using var input = _acpi.GetMethodParameters(method);
        using var packageClass = new ManagementClass(Scope, PackageClass, null);
        using var instance = packageClass.CreateInstance();

        var package = new byte[PackageSize];
        package[0] = selector;
        instance["Bytes"] = package;
        input["Data"] = instance;

        return _acpi.InvokeMethod(method, input, null);
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
                    var name = obj["InstanceName"]?.ToString() ?? className;
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
            if (output?["device_status"] is uint status) return status;
            if (output?["device_status"] is int signed) return unchecked((uint)signed);
            if (output?["Return"] is uint ret) return ret;
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

        var name = gz?["InstanceName"]?.ToString() ?? other?["InstanceName"]?.ToString() ?? "LENOVO_WMI";
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
            foreach (var featureId in ExtractCapabilityIds().Take(32))
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
                var val = output["Data"] ?? output["ReturnValue"];
                if (val is not null) values[$"GameZone.{method}"] = $"{val}";
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
            if (output?["Value"] is uint value) return value;
            if (output?["Value"] is int signed) return unchecked((uint)signed);
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
