using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tsume.Upscaling;

/// <summary>
/// Identifies the active high-performance GPU on this machine via DXGI, ignoring removed/ghost
/// registry entries, software adapters, and secondary integrated GPUs on hybrid systems.
/// </summary>
public static partial class GpuVendor
{
    private const uint VendorIntel = 0x8086;
    private const uint VendorAmd = 0x1002;
    private const uint VendorMicrosoft = 0x1414;
    private const uint DxgiAdapterFlagSoftware = 2;
    private const uint DxgiGpuPreferenceHighPerformance = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    internal readonly record struct GpuCapabilities(
        bool Intel,
        bool IntelArc,
        bool IntelXmx,
        bool Amd,
        bool AmdRdna3Plus);

    private static readonly Lazy<GpuCapabilities> Primary = new(Detect);

    /// <summary>True if the active high-performance GPU is an Intel GPU.</summary>
    public static bool HasIntelGpu() => Primary.Value.Intel;

    /// <summary>True if the active high-performance GPU is an Intel Arc GPU (Xe-LPG, Xe2, Xe3, or discrete Arc).</summary>
    public static bool HasIntelArcGpu() => Primary.Value.IntelArc;

    /// <summary>
    /// True if the active high-performance GPU is an Intel GPU with hardware XMX matrix engines
    /// (Lunar Lake, Arrow Lake-H, Panther Lake / Core G3, or discrete Arc — excludes Meteor Lake Xe-LPG and Iris Xe).
    /// </summary>
    public static bool HasIntelXmxGpu() => Primary.Value.IntelXmx;

    /// <summary>True if the active high-performance GPU is an AMD Radeon GPU.</summary>
    public static bool HasAmdGpu() => Primary.Value.Amd;

    /// <summary>
    /// True if the active high-performance GPU is an AMD RDNA 3 / RDNA 3.5 or newer GPU (e.g. Z1/Z1E, Z2E, HX 370),
    /// excluding RDNA 2 and older APUs (e.g. Z2 Go / 680M, Z2 A, Vega).
    /// </summary>
    public static bool HasAmdRdna3PlusGpu() => Primary.Value.AmdRdna3Plus;

    /// <summary>Classifies a GPU adapter from its PCI vendor ID, PCI device ID, and DXGI adapter description.</summary>
    internal static GpuCapabilities Classify(uint vendorId, uint deviceId, string? description)
    {
        if (vendorId == VendorIntel)
        {
            var (arc, xmx) = ClassifyIntel(deviceId, description);
            return new GpuCapabilities(Intel: true, IntelArc: arc, IntelXmx: xmx, Amd: false, AmdRdna3Plus: false);
        }

        if (vendorId == VendorAmd)
        {
            var rdna3Plus = ClassifyAmdRdna3Plus(deviceId, description);
            return new GpuCapabilities(Intel: false, IntelArc: false, IntelXmx: false, Amd: true, AmdRdna3Plus: rdna3Plus);
        }

        return default;
    }

    internal static GpuCapabilities Classify(uint vendorId, string? description) =>
        Classify(vendorId, 0, description);

    /// <summary>
    /// Intel handheld GPUs ordered oldest → newest: returns <c>(IntelArc, IntelXmx)</c>.
    /// </summary>
    private static (bool Arc, bool Xmx) ClassifyIntel(uint deviceId, string? description)
    {
        switch (deviceId)
        {
            // --- Pre-Arc: Tiger Lake & Alder Lake (Iris Xe — DP4a only, no Arc DX11 XeSS, no XeFG 3×/4×, no XMX) ---
            case 0x9A49: // Intel Iris Xe Graphics G7 (Core i7-1165G7 / 1195G7 — OneXPlayer 1S, GPD Win 3)
            case 0x46A6: // Intel Iris Xe Graphics (Core i7-1260P / 1280P — OneXPlayer Mini, AYANEO Next)
                return (Arc: false, Xmx: false);

            // --- Meteor Lake & Arrow Lake-U/S (Xe-LPG — Arc DX11 XeSS & XeFG 3×/4× where Arc-branded, NO XMX matrix engines) ---
            case 0x7D40: // Intel Graphics (Core Ultra 100U Meteor Lake-U — no XMX)
            case 0x7D45: // Intel Arc Graphics (Core Ultra 100H Meteor Lake-H — no XMX)
            case 0x7D55: // Intel Arc Graphics (Core Ultra 5 135H / Ultra 7 155H — MSI Claw A1M, OneXPlayer X1 Mini — no XMX)
            case 0x7D60: // Intel Graphics (Core Ultra 100 Meteor Lake — no XMX)
            case 0x7DD5: // Intel Arc Graphics (Core Ultra 100H Meteor Lake-H refresh — no XMX)
            case 0x7D41: // Intel Graphics (Core Ultra 200U Arrow Lake-U — no XMX)
            case 0x7D67: // Intel Graphics (Core Ultra 200 Arrow Lake 4-core Xe-LPG — no XMX)
                return (Arc: HasArcBrand(description, defaultWhenUnbranded: deviceId is 0x7D45 or 0x7D55 or 0x7DD5), Xmx: false);

            // --- Lunar Lake (Xe2-LPG — Arc + hardware XMX) ---
            case 0x6420: // Intel Arc 130V GPU (Core Ultra 5 226V / 228V — MSI Claw 7 AI+ / Lunar Lake handhelds)
            case 0x64A0: // Intel Arc 140V GPU (Core Ultra 7 256V / 258V — MSI Claw 8 AI+, MSI Claw 7 AI+)
            case 0x64B0: // Intel Arc 140V / Xe2 variant (Core Ultra 200V Lunar Lake)
                return (Arc: true, Xmx: true);

            // --- Arrow Lake-H (Xe-LPG+ — Arc + hardware XMX) ---
            case 0x7D51: // Intel Arc 140T / 130T GPU (Core Ultra 200H — OneXPlayer X1 Pro 255H / 285H)
            case 0x7DD1: // Intel Arc 140T / 130T GPU (Core Ultra 200H Arrow Lake-H)
                return (Arc: true, Xmx: true);

            // --- Panther Lake / Arc G3 (Xe3 — Arc + hardware XMX) ---
            case 0xB081 or 0xB083 or 0xB085 or 0xB087: // Intel Arc B370 GPU, 10 Xe3 cores (Arc G3 non-Extreme & Core Ultra 300)
            case 0xB080 or 0xB082 or 0xB084 or 0xB086: // Intel Arc B390 GPU, 12 Xe3 cores (Arc G3 Extreme & Core Ultra X7/X9 — MSI Claw 8 EX AI+)
            case >= 0xB088 and <= 0xB0FF:              // Intel Graphics / Arc Xe3 (remaining Panther Lake steppings)
                return (Arc: true, Xmx: true);
        }

        // Unlisted / future Intel SKUs: fall back to adapter name ("Arc 140V", "Arc B390", etc. vs bare "Arc Graphics").
        var intelArc = !string.IsNullOrEmpty(description) && ArcWord().IsMatch(description);
        var intelXmx = intelArc && !string.IsNullOrEmpty(description) && XmxArcModel().IsMatch(description);
        return (intelArc, intelXmx);

        static bool HasArcBrand(string? desc, bool defaultWhenUnbranded) =>
            string.IsNullOrEmpty(desc) ? defaultWhenUnbranded : ArcWord().IsMatch(desc);
    }

    /// <summary>
    /// AMD Windows handheld GPUs ordered oldest → newest: true for RDNA 3 / 3.5+ (defaults to FSR 4),
    /// false for Vega and RDNA 2 APUs (defaults to FSR 3).
    /// </summary>
    private static bool ClassifyAmdRdna3Plus(uint deviceId, string? description)
    {
        switch (deviceId)
        {
            // --- Vega & RDNA 2 APUs (Default: FSR 3) ---
            case 0x15D8: // AMD Radeon Vega Mobile (Ryzen 3000U Picasso — GPD Win Max 2020, AYANEO Founders)
            case 0x1636: // AMD Radeon Graphics Vega (Ryzen 4000U Renoir — AYANEO 2021, OneXPlayer AMD)
            case 0x164C: // AMD Radeon Graphics Vega (Ryzen 5000U Lucienne — AYANEO Next Lite)
            case 0x1638: // AMD Radeon Graphics Vega (Ryzen 5000U Cezanne / Barcelo — AYANEO Next, OneXPlayer Mini)
            case 0x1681: // AMD Radeon 680M / 660M, RDNA 2 (Ryzen Z2 Go & Ryzen 6800U — Lenovo Legion Go S, GPD Win 4, AYANEO 2)
            case 0x1506: // AMD Radeon 610M, RDNA 2 (Ryzen 7020 Mendocino — AYANEO Air Plus)
            case 0x163F: // AMD Radeon Graphics, RDNA 2 (Ryzen Z2 A — ASUS ROG Xbox Ally)
            case 0x1435: // AMD Radeon Graphics, RDNA 2 (Ryzen Z2 A variant — ASUS ROG Xbox Ally)
                return false;

            // --- RDNA 3 APUs (Default: FSR 4 INT8) ---
            case 0x15BF: // AMD Radeon 780M / 760M, RDNA 3 (Ryzen Z1 Extreme & 7840U — ASUS ROG Ally, ROG Ally X, Lenovo Legion Go)
            case 0x15C8: // AMD Radeon 740M, RDNA 3 (Ryzen Z1 & 7540U — ASUS ROG Ally Z1)
            case 0x1900: // AMD Radeon 780M / 760M, RDNA 3 (Ryzen 8840U / 8640U Hawk Point — GPD Win Mini 2024, AYANEO Flip, OneXFly)
                return true;

            // --- RDNA 3.5+ APUs (Default: FSR 4 INT8) ---
            case 0x150E: // AMD Radeon 890M / 880M, RDNA 3.5 (Ryzen Z2 Extreme & AI 9 HX 370 — ROG Xbox Ally X, MSI Claw A8, Legion Go 2, OneXFly F1 Pro)
            case 0x1114: // AMD Radeon 860M / 840M, RDNA 3.5 (Ryzen Z2 / Ryzen AI 300 Krackan Point handhelds)
            case 0x1586: // AMD Radeon 8060S / 8050S, RDNA 3.5 (Ryzen AI Max+ 395 / 385 Strix Halo — GPD Win 5, AYANEO Next II)
                return true;
        }

        // Unlisted / future AMD SKUs (e.g. Z3 / RDNA 4+): default to RDNA 3+ unless the name marks an older 600M / Vega iGPU.
        return string.IsNullOrEmpty(description) || !AmdPreRdna3Name().IsMatch(description);
    }

    [GeneratedRegex(@"\bArc\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArcWord();

    [GeneratedRegex(@"\bArc(?:\(TM\)|™|\(R\))?\s+(?:Pro\b|[A-Z]\d{2,3}\b|\d{3}[A-Z]\b|G\d\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmxArcModel();

    [GeneratedRegex(@"\b(?:Radeon(?:\(TM\)|™)?\s+6\d0M|Vega|RX\s+[56]\d{3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AmdPreRdna3Name();

    private static GpuCapabilities Detect()
    {
        try
        {
            if (TryDetectHighPerformance(out var desc))
                return Classify(desc.VendorId, desc.DeviceId, desc.Description);

            if (TryDetectFallback(out desc))
                return Classify(desc.VendorId, desc.DeviceId, desc.Description);
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Fall through to unknown GPU (Auto defaults, conservative feature gates).
        }

        return default;
    }

    /// <summary>
    /// Queries <c>IDXGIFactory6::EnumAdapterByGpuPreference(HIGH_PERFORMANCE)</c> and returns the first
    /// non-software hardware adapter.
    /// </summary>
    private static bool TryDetectHighPerformance(out DxgiAdapterDesc1 found)
    {
        found = default;
        var factoryIid = typeof(IDXGIFactory6).GUID;
        if (CreateDXGIFactory1(in factoryIid, out var factoryObj) < 0 || factoryObj is not IDXGIFactory6 factory)
            return false;

        try
        {
            var adapterIid = typeof(IDXGIAdapter1).GUID;
            for (uint i = 0; ; i++)
            {
                var hr = factory.EnumAdapterByGpuPreference(i, DxgiGpuPreferenceHighPerformance, in adapterIid, out var adapterObj);
                if (hr == DxgiErrorNotFound || hr < 0 || adapterObj is null) return false;

                try
                {
                    if (adapterObj is IDXGIAdapter1 adapter && adapter.GetDesc1(out var desc) >= 0 && IsHardware(desc))
                    {
                        found = desc;
                        return true;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapterObj);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factoryObj);
        }
    }

    /// <summary>
    /// Fallback for systems without <c>IDXGIFactory6</c>: enumerates hardware adapters via
    /// <c>IDXGIFactory1::EnumAdapters1</c> and picks the one with the largest dedicated video memory.
    /// </summary>
    private static bool TryDetectFallback(out DxgiAdapterDesc1 best)
    {
        best = default;
        var factoryIid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(in factoryIid, out var factoryObj) < 0 || factoryObj is not IDXGIFactory1 factory)
            return false;

        var hasBest = false;

        try
        {
            for (uint i = 0; ; i++)
            {
                var hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == DxgiErrorNotFound || hr < 0 || adapter is null) break;

                try
                {
                    if (adapter.GetDesc1(out var desc) >= 0 && IsHardware(desc))
                    {
                        if (!hasBest || desc.DedicatedVideoMemory > best.DedicatedVideoMemory)
                        {
                            best = desc;
                            hasBest = true;
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factoryObj);
        }

        return hasBest;
    }

    private static bool IsHardware(in DxgiAdapterDesc1 desc) =>
        (desc.Flags & DxgiAdapterFlagSoftware) == 0
        && desc.VendorId != 0
        && desc.VendorId != VendorMicrosoft;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppFactory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void _SetPrivateData();
        void _SetPrivateDataInterface();
        void _GetPrivateData();
        void _GetParent();
        void _EnumOutputs();
        void _GetDesc();
        void _CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 pDesc);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void _SetPrivateData();
        void _SetPrivateDataInterface();
        void _GetPrivateData();
        void _GetParent();
        void _EnumAdapters();
        void _MakeWindowAssociation();
        void _GetWindowAssociation();
        void _CreateSwapChain();
        void _CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? ppAdapter);
        void _IsCurrent();
    }

    [ComImport, Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory6
    {
        // IDXGIObject (4)
        void _SetPrivateData();
        void _SetPrivateDataInterface();
        void _GetPrivateData();
        void _GetParent();
        // IDXGIFactory (5)
        void _EnumAdapters();
        void _MakeWindowAssociation();
        void _GetWindowAssociation();
        void _CreateSwapChain();
        void _CreateSoftwareAdapter();
        // IDXGIFactory1 (2)
        void _EnumAdapters1();
        void _IsCurrent();
        // IDXGIFactory2 (11)
        void _IsWindowedStereoEnabled();
        void _CreateSwapChainForHwnd();
        void _CreateSwapChainForCoreWindow();
        void _GetSharedResourceAdapterLuid();
        void _RegisterStereoStatusWindow();
        void _RegisterStereoStatusEvent();
        void _UnregisterStereoStatus();
        void _RegisterOcclusionStatusWindow();
        void _RegisterOcclusionStatusEvent();
        void _UnregisterOcclusionStatus();
        void _CreateSwapChainForComposition();
        // IDXGIFactory3 (1)
        void _GetCreationFlags();
        // IDXGIFactory4 (2)
        void _EnumAdapterByLuid();
        void _EnumWarpAdapter();
        // IDXGIFactory5 (1)
        void _CheckFeatureSupport();
        // IDXGIFactory6 (1)
        [PreserveSig] int EnumAdapterByGpuPreference(
            uint adapter,
            uint gpuPreference,
            in Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppvAdapter);
    }
}
