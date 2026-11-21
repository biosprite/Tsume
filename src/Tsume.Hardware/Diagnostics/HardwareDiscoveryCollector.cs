using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Tsume.Core.Diagnostics;

namespace Tsume.Hardware.Diagnostics;

/// <summary>
/// Strictly read-only elevated hardware discovery collector:
/// SMBIOS/CPU/GPU/Battery identity, ACPI/PCI power devices, vendor WMI class &amp; MOF dump,
/// OEM-safe read-only firmware snapshot, 3-point timed live-telemetry delta (idle → +2s → 3s CPU load),
/// and OEM software footprint. Never writes to firmware, registers, or the registry.
/// </summary>
public static class HardwareDiscoveryCollector
{
    private const double JoulesPerPicowattHour = 3600e-12;

    private static readonly string[] OemWmiNamespaces =
    [
        @"root\WMI",
        @"root\ASUS",
        @"root\Lenovo",
        @"root\MSI",
        @"root\HP",
    ];

    private static readonly string[] StockWmiPrefixes =
    [
        "__", "MSNdis_", "WmiMonitor", "MSStorageDriver_", "MSKeyboard_", "MSMouse_",
        "MSSerial_", "MSIde_", "MSPower_", "MSRedbook_", "MSMCA", "EventTrace",
        "MSChanger", "MSTape", "MSDisk", "MSParallel", "MS_SmBios", "WmiEvent",
        "Win32_", "CIM_", "MSFT_", "StdRegProv", "SystemSecurity",
    ];

    private static readonly string[] KeepExplicitWmiClasses =
    [
        "MSAcpi_ThermalZoneTemperature",
        "BatteryStatus",
        "BatteryStaticData",
        "BatteryFullChargedCapacity",
    ];

    public static ElevatedInventory CollectInventory(bool includeTimedLoad = true)
    {
        var identity = CollectIdentity();
        var powerPlan = PowerPlanInfo.Read();
        var acpiDevices = CollectAcpiPowerDevices();
        var wmiClasses = CollectVendorWmiClasses();

        FirmwareReadSnapshot? firmware = null;
        TimedDeltaSnapshot? timedDelta = null;

        using (var prober = FirmwareReadProbers.TryOpen())
        {
            if (prober is not null)
                firmware = prober.ReadFull();

            if (includeTimedLoad)
                timedDelta = CollectTimedDelta(prober);
        }

        var oem = OemFootprintCollector.Collect(identity.BaseBoardManufacturer, identity.SystemManufacturer);

        return new ElevatedInventory
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            Elevated = IsRunningElevated(),
            Identity = identity,
            PowerPlan = powerPlan,
            AcpiPowerDevices = acpiDevices,
            WmiClasses = wmiClasses,
            Firmware = firmware,
            TimedDelta = timedDelta,
            OemFootprint = oem,
        };
    }

    public static LiveHardwareState CollectFastState()
    {
        var power = PowerPlanInfo.Read();
        var family = "generic";
        Dictionary<string, string> fwValues = [];
        string? ecHex = null;

        using (var prober = FirmwareReadProbers.TryOpen())
        {
            if (prober is not null)
            {
                family = prober.VendorFamily;
                (fwValues, ecHex) = prober.ReadFastState();
            }
        }

        return new LiveHardwareState
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            VendorFamily = family,
            FirmwareValues = fwValues,
            EcMemoryHex = ecHex,
            PowerSchemeGuid = power.ActiveSchemeGuid,
            PowerOverlayGuid = power.EffectiveOverlayGuid,
            ThermalZonesC = ReadThermalZones(),
            OemRegistry = OemFootprintCollector.CollectOemRegistry(),
        };
    }

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SystemIdentityInfo CollectIdentity()
    {
        string bbMaker = "", bbProduct = "", bbVer = "";
        QueryFirst("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", row =>
        {
            bbMaker = Str(row, "Manufacturer");
            bbProduct = Str(row, "Product");
            bbVer = Str(row, "Version");
        });

        string sysMaker = "", sysModel = "", sysSku = "", sysFamily = "";
        QueryFirst("SELECT Manufacturer, Model, SystemSKUNumber, SystemFamily FROM Win32_ComputerSystem", row =>
        {
            sysMaker = Str(row, "Manufacturer");
            sysModel = Str(row, "Model");
            sysSku = Str(row, "SystemSKUNumber");
            sysFamily = Str(row, "SystemFamily");
        });

        string prodName = "", prodVer = "";
        QueryFirst("SELECT Name, Version FROM Win32_ComputerSystemProduct", row =>
        {
            prodName = Str(row, "Name");
            prodVer = Str(row, "Version");
        });

        string biosMaker = "", biosVer = "", biosDate = "";
        string? ecVer = null;
        QueryFirst("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, EmbeddedControllerMajorVersion, EmbeddedControllerMinorVersion FROM Win32_BIOS", row =>
        {
            biosMaker = Str(row, "Manufacturer");
            biosVer = Str(row, "SMBIOSBIOSVersion");
            biosDate = Str(row, "ReleaseDate");
            if (row["EmbeddedControllerMajorVersion"] is byte maj && row["EmbeddedControllerMinorVersion"] is byte min && maj != 255)
                ecVer = $"{maj}.{min}";
        });

        string cpuName = "", cpuMaker = "";
        int cores = 0, logical = 0;
        uint maxClk = 0;
        QueryFirst("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", row =>
        {
            cpuName = Str(row, "Name");
            cpuMaker = Str(row, "Manufacturer");
            cores = Convert.ToInt32(row["NumberOfCores"] ?? 0);
            logical = Convert.ToInt32(row["NumberOfLogicalProcessors"] ?? 0);
            maxClk = Convert.ToUInt32(row["MaxClockSpeed"] ?? 0u);
        });

        var gpus = new List<GpuInfo>();
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, DriverVersion, PNPDeviceID FROM Win32_VideoController");
            using var rows = search.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    gpus.Add(new GpuInfo(
                        Str(row, "Name"),
                        Str(row, "AdapterCompatibility"),
                        Str(row, "DriverVersion"),
                        PrivacyScrubber.ScrubDevicePath(Str(row, "PNPDeviceID"))));
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return new SystemIdentityInfo
        {
            BaseBoardManufacturer = bbMaker,
            BaseBoardProduct = bbProduct,
            BaseBoardVersion = bbVer,
            SystemManufacturer = sysMaker,
            SystemModel = sysModel,
            SystemSku = sysSku,
            SystemFamily = sysFamily,
            ProductName = prodName,
            ProductVersion = prodVer,
            BiosManufacturer = biosMaker,
            BiosVersion = biosVer,
            BiosReleaseDate = biosDate,
            EcMajorMinor = ecVer,
            CpuName = cpuName,
            CpuManufacturer = cpuMaker,
            CpuCores = cores,
            CpuLogicalProcessors = logical,
            CpuMaxClockMhz = maxClk,
            Gpus = gpus,
            Battery = ReadBatteryStatic(),
            WindowsVersion = WindowsDescription(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        };
    }

    private static BatteryStaticInfo? ReadBatteryStatic()
    {
        string name = "";
        uint? designed = null;
        uint? full = null;
        bool? acOnline = null;
        int? pct = null;
        int? rate = null;

        QueryFirst("SELECT Name, DesignCapacity, EstimatedChargeRemaining FROM Win32_Battery", row =>
        {
            name = Str(row, "Name");
            if (row["DesignCapacity"] is uint d) designed = d;
            if (row["EstimatedChargeRemaining"] is ushort p) pct = p;
        });

        QueryFirstIn(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData", row =>
        {
            if (row["DesignedCapacity"] is uint d) designed = d;
        });

        QueryFirstIn(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", row =>
        {
            if (row["FullChargedCapacity"] is uint f) full = f;
        });

        var live = ReadBatteryLiveRate();
        acOnline = live.AcOnline;
        rate = live.RateMw;

        if (string.IsNullOrEmpty(name) && designed is null && full is null && acOnline is null) return null;
        return new BatteryStaticInfo(string.IsNullOrEmpty(name) ? "Battery" : name, designed, full, acOnline, pct, rate);
    }

    private static (bool? AcOnline, int? RateMw) ReadBatteryLiveRate()
    {
        bool? ac = null;
        int? rate = null;
        QueryFirstIn(@"root\WMI", "SELECT PowerOnline, Charging, Discharging, ChargeRate, DischargeRate FROM BatteryStatus", row =>
        {
            if (row["PowerOnline"] is bool p) ac = p;
            var charging = row["Charging"] is true;
            var discharging = row["Discharging"] is true;
            if (charging && row["ChargeRate"] is uint cr && cr != 0xFFFF_FFFF) rate = (int)cr;
            else if (discharging && row["DischargeRate"] is uint dr && dr != 0xFFFF_FFFF) rate = -(int)dr;
            else rate = 0;
        });
        return (ac, rate);
    }

    private static List<AcpiDeviceInfo> CollectAcpiPowerDevices()
    {
        var results = new List<AcpiDeviceInfo>();
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, Manufacturer, Service, Status FROM Win32_PnPEntity " +
                "WHERE PNPDeviceID LIKE 'ACPI\\\\PNP0C14%' OR PNPDeviceID LIKE 'ACPI\\\\INTC%' OR PNPDeviceID LIKE 'ACPI\\\\INT34%' " +
                "OR PNPDeviceID LIKE 'ACPI\\\\AMDI%' OR PNPDeviceID LIKE 'ACPI\\\\ASUS%' OR PNPDeviceID LIKE 'ACPI\\\\MSI%' " +
                "OR PNPDeviceID LIKE 'ACPI\\\\LEN%' OR PNPDeviceID LIKE 'PCI\\\\VEN_1022&DEV_14%' OR PNPDeviceID LIKE 'PCI\\\\VEN_1022&DEV_15%' " +
                "OR PNPDeviceID LIKE 'PCI\\\\VEN_8086&DEV_64%' OR PNPDeviceID LIKE 'PCI\\\\VEN_8086&DEV_7D%'");
            using var rows = search.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    results.Add(new AcpiDeviceInfo(
                        Str(row, "Name"),
                        PrivacyScrubber.ScrubDevicePath(Str(row, "PNPDeviceID")),
                        Str(row, "Manufacturer"),
                        Str(row, "Service"),
                        Str(row, "Status")));
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return [.. results.OrderBy(d => d.ScrubbedDeviceId, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<WmiClassInfo> CollectVendorWmiClasses()
    {
        var results = new List<WmiClassInfo>();

        foreach (var ns in OemWmiNamespaces)
        {
            try
            {
                var scope = new ManagementScope(ns);
                scope.Connect();

                using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM meta_class"));
                using var classes = search.Get();

                foreach (ManagementClass cls in classes)
                {
                    using (cls)
                    {
                        var name = cls.ClassPath?.ClassName;
                        if (string.IsNullOrEmpty(name)) continue;

                        if (!IsInterestingWmiClass(ns, name, cls)) continue;

                        var isEvent = IsEventClass(cls);
                        var methods = new List<WmiMethodInfo>();
                        foreach (MethodData m in cls.Methods)
                        {
                            var inParams = m.InParameters?.Properties.Cast<PropertyData>().Select(p => $"{p.Type} {p.Name}").ToList() ?? [];
                            var outParams = m.OutParameters?.Properties.Cast<PropertyData>().Select(p => $"{p.Type} {p.Name}").ToList() ?? [];
                            methods.Add(new WmiMethodInfo(m.Name, inParams, outParams));
                        }

                        var props = cls.Properties.Cast<PropertyData>()
                            .Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal))
                            .Select(p => $"{p.Type} {p.Name}")
                            .ToList();

                        var instances = new List<string>();
                        if (!isEvent)
                        {
                            try
                            {
                                using var instCol = cls.GetInstances();
                                foreach (ManagementObject inst in instCol)
                                {
                                    using (inst)
                                    {
                                        var instName = inst["InstanceName"]?.ToString() ?? inst.Path?.RelativePath ?? "default";
                                        instances.Add(PrivacyScrubber.ScrubDevicePath(instName));
                                        if (instances.Count >= 12) break;
                                    }
                                }
                            }
                            catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
                            {
                            }
                        }

                        string? mof = null;
                        try
                        {
                            mof = cls.GetText(TextFormat.Mof);
                            if (mof.Length > 32_768) mof = mof[..32_768] + "\n// [truncated]";
                        }
                        catch (Exception error) when (error is ManagementException or COMException)
                        {
                        }

                        results.Add(new WmiClassInfo
                        {
                            Namespace = ns,
                            Name = name,
                            IsEvent = isEvent,
                            Instances = instances,
                            Methods = methods,
                            Properties = props,
                            MofText = mof,
                        });

                        if (results.Count >= 120) break;
                    }
                }
            }
            catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
            {
                // Namespace does not exist on this machine — normal for other OEMs.
            }
        }

        return [.. results.OrderBy(c => c.Namespace, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsInterestingWmiClass(string ns, string name, ManagementClass cls)
    {
        if (name.StartsWith("__", StringComparison.Ordinal)) return false;
        if (KeepExplicitWmiClasses.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

        // In OEM-specific namespaces (root\ASUS, root\Lenovo, etc.), keep non-system classes.
        if (!ns.Equals(@"root\WMI", StringComparison.OrdinalIgnoreCase))
            return !StockWmiPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (StockWmiPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Keep classes with vendor names or ACPI/WMI methods or extrinsic events.
        if (name.Contains("MSI", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ASUS", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ATK", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LENOVO", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ACER", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HPQ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GPD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AYANEO", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ONEX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ZOTAC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ACPI", StringComparison.OrdinalIgnoreCase)
            || name.Contains("WMI_Event", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GameZone", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Fan", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Thermal", StringComparison.OrdinalIgnoreCase))
            return true;

        // Also keep any non-stock class in root\WMI that exposes WMI methods (very likely an ACPI PNP0C14 method block).
        return cls.Methods.Count > 0;
    }

    private static bool IsEventClass(ManagementClass cls)
    {
        try
        {
            if (cls["__DERIVATION"] is string[] chain)
                return chain.Any(c => c.Equals("__ExtrinsicEvent", StringComparison.OrdinalIgnoreCase)
                                   || c.Equals("WMIEvent", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
        }

        return false;
    }

    private static TimedDeltaSnapshot CollectTimedDelta(IFirmwareReadProber? prober)
    {
        var idle1Utc = DateTimeOffset.UtcNow;
        var s1 = prober?.ReadFastState();
        var em1 = ReadAllEnergyMeters();
        var tzIdle = ReadThermalZones();
        var batIdle = ReadBatteryLiveRate();

        Thread.Sleep(TimeSpan.FromSeconds(2));

        var idle2Utc = DateTimeOffset.UtcNow;
        var s2 = prober?.ReadFastState();
        var em2 = ReadAllEnergyMeters();

        // Brief 3-second CPU load to make temperature, fan tach/RPM, and package power move visibly.
        const int loadMs = 3000;
        var emLoadStart = ReadAllEnergyMeters();
        SpinCpu(loadMs);
        var emLoadEnd = ReadAllEnergyMeters();

        var loadUtc = DateTimeOffset.UtcNow;
        var s3 = prober?.ReadFastState();
        var tzLoad = ReadThermalZones();
        var batLoad = ReadBatteryLiveRate();

        var volatileValues = new List<VolatileEntry>();
        if (s1 is { } a && s2 is { } b && s3 is { } c)
        {
            foreach (var key in a.Values.Keys.Union(b.Values.Keys).Union(c.Values.Keys))
            {
                a.Values.TryGetValue(key, out var v1);
                b.Values.TryGetValue(key, out var v2);
                c.Values.TryGetValue(key, out var v3);
                if (v1 != v2 || v2 != v3 || v1 != v3)
                    volatileValues.Add(new VolatileEntry(key, v1 ?? "", v2 ?? "", v3 ?? ""));
            }
        }

        var volatileEc = new List<VolatileEcByte>();
        if (s1?.EcMemoryHex is { } h1 && s2?.EcMemoryHex is { } h2 && s3?.EcMemoryHex is { } h3)
        {
            var b1 = Convert.FromHexString(h1);
            var b2 = Convert.FromHexString(h2);
            var b3 = Convert.FromHexString(h3);
            var len = Math.Min(b1.Length, Math.Min(b2.Length, b3.Length));
            for (var i = 0; i < len; i++)
            {
                if (b1[i] != b2[i] || b2[i] != b3[i] || b1[i] != b3[i])
                    volatileEc.Add(new VolatileEcByte(i, $"0x{i:X2}", b1[i], b2[i], b3[i]));
            }
        }

        var energySamples = new List<EnergyMeterSample>();
        foreach (var name in em1.Keys.Union(emLoadEnd.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            em1.TryGetValue(name, out var r1);
            em2.TryGetValue(name, out var r2);
            emLoadStart.TryGetValue(name, out var r3);
            emLoadEnd.TryGetValue(name, out var r4);
            energySamples.Add(new EnergyMeterSample(name, ComputeWatts(r1, r2), ComputeWatts(r3, r4)));
        }

        return new TimedDeltaSnapshot
        {
            Idle1Utc = idle1Utc,
            Idle2Utc = idle2Utc,
            UnderLoadUtc = loadUtc,
            LoadDurationMs = loadMs,
            VolatileValues = volatileValues,
            VolatileEcBytes = volatileEc,
            EnergyMeters = energySamples,
            ThermalZonesIdleC = tzIdle,
            ThermalZonesLoadC = tzLoad,
            BatteryRateIdleMw = batIdle.RateMw,
            BatteryRateLoadMw = batLoad.RateMw,
            AcOnline = batLoad.AcOnline ?? batIdle.AcOnline,
        };
    }

    private static void SpinCpu(int durationMs)
    {
        using var stop = new CancellationTokenSource(durationMs);
        var workers = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount))
            .Select(_ => new Thread(() =>
            {
                var x = 1.0001;
                while (!stop.IsCancellationRequested)
                    for (var i = 0; i < 50_000; i++)
                        x = Math.Sqrt(x * x + i);
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            })
            .ToList();

        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
    }

    private static Dictionary<string, (ulong Energy, ulong Time)> ReadAllEnergyMeters()
    {
        var map = new Dictionary<string, (ulong, ulong)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var search = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, Energy, Time FROM Win32_PerfRawData_PowerMeterCounter_EnergyMeter");
            using var rows = search.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    var name = Str(row, "Name");
                    if (string.IsNullOrEmpty(name)) continue;
                    map[name] = (Convert.ToUInt64(row["Energy"] ?? 0UL), Convert.ToUInt64(row["Time"] ?? 0UL));
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return map;
    }

    private static double? ComputeWatts((ulong Energy, ulong Time) a, (ulong Energy, ulong Time) b)
    {
        if (b.Time <= a.Time || b.Energy < a.Energy) return null;
        var joules = (b.Energy - a.Energy) * JoulesPerPicowattHour;
        var seconds = (b.Time - a.Time) / 1000.0;
        return seconds > 0 ? joules / seconds : null;
    }

    private static Dictionary<string, double> ReadThermalZones()
    {
        var zones = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var search = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var rows = search.Get();
            foreach (var row in rows)
            {
                using (row)
                {
                    var name = PrivacyScrubber.ScrubDevicePath(Str(row, "InstanceName"));
                    if (row["CurrentTemperature"] is uint tenthsKelvin && tenthsKelvin > 2000)
                        zones[name] = Math.Round((tenthsKelvin / 10.0) - 273.15, 1);
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return zones;
    }

    private static void QueryFirst(string query, Action<ManagementBaseObject> onRow) =>
        QueryFirstIn(@"root\CIMV2", query, onRow);

    private static void QueryFirstIn(string scope, string query, Action<ManagementBaseObject> onRow)
    {
        try
        {
            using var search = new ManagementObjectSearcher(scope, query);
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

    private static string Str(ManagementBaseObject row, string prop) => $"{row[prop]}".Trim();

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
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return RuntimeInformation.OSDescription;
    }
}
