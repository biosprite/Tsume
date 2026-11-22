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

    private static readonly Lazy<(bool Intel, bool IntelArc, bool Amd)> Primary = new(Detect);

    /// <summary>True if the active high-performance GPU is an Intel GPU.</summary>
    public static bool HasIntelGpu() => Primary.Value.Intel;

    /// <summary>True if the active high-performance GPU is an Intel Arc GPU.</summary>
    public static bool HasIntelArcGpu() => Primary.Value.IntelArc;

    /// <summary>True if the active high-performance GPU is an AMD Radeon GPU.</summary>
    public static bool HasAmdGpu() => Primary.Value.Amd;

    /// <summary>Classifies a GPU adapter from its PCI vendor ID and DXGI adapter description.</summary>
    internal static (bool Intel, bool IntelArc, bool Amd) Classify(uint vendorId, string? description)
    {
        var intel = vendorId == VendorIntel;
        var intelArc = intel && !string.IsNullOrEmpty(description) && ArcWord().IsMatch(description);
        var amd = vendorId == VendorAmd;
        return (intel, intelArc, amd);
    }

    [GeneratedRegex(@"\bArc\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArcWord();

    private static (bool Intel, bool IntelArc, bool Amd) Detect()
    {
        try
        {
            if (TryDetectHighPerformance(out var desc))
                return Classify(desc.VendorId, desc.Description);

            if (TryDetectFallback(out desc))
                return Classify(desc.VendorId, desc.Description);
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // Fall through to unknown GPU (Auto defaults, conservative feature gates).
        }

        return (false, false, false);
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
