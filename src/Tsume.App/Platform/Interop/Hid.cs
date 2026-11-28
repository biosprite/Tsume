using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tsume.App.Platform.Interop;

/// <summary>
/// Finding and opening HID devices: the device-interface list, CreateFile, and
/// the parsed report descriptor. Names mirror the Win32 headers.
/// </summary>
internal static unsafe partial class Hid
{
    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    private const uint CR_SUCCESS = 0;
    private const uint CR_BUFFER_SMALL = 0x1A;

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static partial uint CM_Get_Device_Interface_List(in Guid interfaceClass, nint deviceId, char* buffer, uint length, uint flags);

    /// <summary>The path of every HID collection present right now.</summary>
    public static IReadOnlyList<string> DevicePaths()
    {
        HidD_GetHidGuid(out var hidGuid);
        return InterfacePaths(hidGuid);
    }

    /// <summary>GUID_DEVINTERFACE_LAMP_ARRAY — Windows Dynamic Lighting devices.</summary>
    private static readonly Guid LampArrayGuid = new("F0679F4B-B3B7-4382-A5B4-8B4707C6DC23");

    /// <summary>The path of every Windows LampArray (Dynamic Lighting) device present right now.</summary>
    public static IReadOnlyList<string> LampArrayDevicePaths() => InterfacePaths(LampArrayGuid);

    private static string[] InterfacePaths(Guid interfaceGuid)
    {
        // The list can grow between asking its size and reading it; try again if so.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var length, interfaceGuid, 0, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || length <= 1)
                return [];

            var buffer = new char[length];
            uint result;
            fixed (char* chars = buffer)
                result = CM_Get_Device_Interface_List(interfaceGuid, 0, chars, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

            if (result == CR_BUFFER_SMALL) continue;
            if (result != CR_SUCCESS) return [];

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        return [];
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    /// <summary>Opens a collection for overlapped reading and writing, shared with everyone else using it.</summary>
    public static SafeFileHandle? OpenReadWrite(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);

        if (!handle.IsInvalid) return handle;

        handle.Dispose();
        return null;
    }

    /// <summary>
    /// Opens a collection synchronously for <c>HidD_GetFeature</c>/<c>HidD_SetFeature</c> calls.
    /// Falls back to zero data-access rights when a collection rejects read/write access.
    /// </summary>
    public static SafeFileHandle? OpenForFeature(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            0, OPEN_EXISTING, 0, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();

        return OpenMetadataOnly(path);
    }

    /// <summary>
    /// Opens a collection with zero data-access rights so Windows allows querying attributes, strings, and
    /// preparsed capabilities even on exclusive system keyboards, mice, and touchpads.
    /// </summary>
    public static SafeFileHandle? OpenMetadataOnly(string path)
    {
        var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            0, OPEN_EXISTING, 0, 0);

        if (!handle.IsInvalid) return handle;

        handle.Dispose();
        return null;
    }

    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HidP_Input = 0;
    private const int HidP_Output = 1;
    private const int HidP_Feature = 2;

    /// <summary>HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS are both 72 bytes, with the report ID at byte 2.</summary>
    private const int CapsSize = 72;
    private const int CapsReportIdOffset = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        private fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HiddAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetManufacturerString(SafeFileHandle device, char* buffer, uint bufferLengthBytes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetProductString(SafeFileHandle device, char* buffer, uint bufferLengthBytes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(nint preparsed);

    /// <summary>Throws away input reports that arrived before anyone asked for them.</summary>
    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FlushQueue(SafeFileHandle device);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_SetFeature(SafeFileHandle device, byte[] reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_GetFeature(SafeFileHandle device, [Out] byte[] reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint preparsed, out HidpCaps caps);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetButtonCaps(int reportType, byte* caps, ref ushort length, nint preparsed);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetValueCaps(int reportType, byte* caps, ref ushort length, nint preparsed);

    /// <summary>
    /// Reads each feature value cap's byte size keyed by <c>(ReportId, Usage)</c>. Matches how Acer's
    /// <c>LightingHID</c> determines field widths (e.g. 1-byte vs 2-byte LED count in report <c>0xA3</c>).
    /// </summary>
    public static Dictionary<(byte ReportId, ushort Usage), int> FeatureValueByteSizes(SafeFileHandle device)
    {
        var map = new Dictionary<(byte, ushort), int>();
        if (!HidD_GetPreparsedData(device, out var preparsed)) return map;

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS || caps.NumberFeatureValueCaps == 0)
                return map;

            var count = caps.NumberFeatureValueCaps;
            var buffer = new byte[count * CapsSize];
            fixed (byte* bytes = buffer)
                if (HidP_GetValueCaps(HidP_Feature, bytes, ref count, preparsed) != HIDP_STATUS_SUCCESS)
                    return map;

            for (var i = 0; i < count; i++)
            {
                var off = i * CapsSize;
                var reportId = buffer[off + CapsReportIdOffset];
                var isRange = buffer[off + 12] != 0;
                var bitSize = (ushort)(buffer[off + 18] | (buffer[off + 19] << 8));
                var byteSize = Math.Max(1, bitSize / 8);
                var usageMin = (ushort)(buffer[off + 56] | (buffer[off + 57] << 8));
                var usageMax = isRange ? (ushort)(buffer[off + 58] | (buffer[off + 59] << 8)) : usageMin;

                for (var usage = usageMin; usage <= usageMax; usage++)
                {
                    map[(reportId, (ushort)usage)] = byteSize;
                    if (usage == ushort.MaxValue) break;
                }
            }
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }

        return map;
    }

    /// <summary>Firmware version as the device reports it: 0x0414 for "4.14".</summary>
    public static ushort? Version(SafeFileHandle device) => Attributes(device)?.VersionNumber;

    public static HiddAttributes? Attributes(SafeFileHandle device)
    {
        var attributes = new HiddAttributes { Size = (uint)sizeof(HiddAttributes) };
        return HidD_GetAttributes(device, ref attributes) ? attributes : null;
    }

    public static string ManufacturerString(SafeFileHandle device)
    {
        var buffer = new char[128];
        fixed (char* ptr = buffer)
            return HidD_GetManufacturerString(device, ptr, (uint)(buffer.Length * 2))
                ? new string(ptr).Trim()
                : "";
    }

    public static string ProductString(SafeFileHandle device)
    {
        var buffer = new char[128];
        fixed (char* ptr = buffer)
            return HidD_GetProductString(device, ptr, (uint)(buffer.Length * 2))
                ? new string(ptr).Trim()
                : "";
    }

    /// <summary>The collection's summary, and the IDs of every output report it declares.</summary>
    public static (HidpCaps Caps, IReadOnlySet<byte> OutputReports)? Describe(SafeFileHandle device)
    {
        if (DescribeAll(device) is not { } full) return null;
        return (full.Caps, full.OutputReports);
    }

    /// <summary>Full collection summary plus every Input, Output, and Feature report ID it declares.</summary>
    public static (HidpCaps Caps, IReadOnlySet<byte> InputReports, IReadOnlySet<byte> OutputReports, IReadOnlySet<byte> FeatureReports)? DescribeAll(SafeFileHandle device)
    {
        if (!HidD_GetPreparsedData(device, out var preparsed)) return null;

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS) return null;

            var inputReports = new HashSet<byte>();
            var outputReports = new HashSet<byte>();
            var featureReports = new HashSet<byte>();

            Collect(inputReports, caps.NumberInputButtonCaps, (buffer, ref length) => HidP_GetButtonCaps(HidP_Input, buffer, ref length, preparsed));
            Collect(inputReports, caps.NumberInputValueCaps, (buffer, ref length) => HidP_GetValueCaps(HidP_Input, buffer, ref length, preparsed));

            Collect(outputReports, caps.NumberOutputButtonCaps, (buffer, ref length) => HidP_GetButtonCaps(HidP_Output, buffer, ref length, preparsed));
            Collect(outputReports, caps.NumberOutputValueCaps, (buffer, ref length) => HidP_GetValueCaps(HidP_Output, buffer, ref length, preparsed));

            Collect(featureReports, caps.NumberFeatureButtonCaps, (buffer, ref length) => HidP_GetButtonCaps(HidP_Feature, buffer, ref length, preparsed));
            Collect(featureReports, caps.NumberFeatureValueCaps, (buffer, ref length) => HidP_GetValueCaps(HidP_Feature, buffer, ref length, preparsed));

            return (caps, inputReports, outputReports, featureReports);

            static void Collect(HashSet<byte> target, ushort count, CapsReader read)
            {
                if (count == 0) return;

                var buffer = new byte[count * CapsSize];
                var length = count;
                fixed (byte* bytes = buffer)
                    if (read(bytes, ref length) != HIDP_STATUS_SUCCESS) return;

                for (var i = 0; i < length; i++) target.Add(buffer[i * CapsSize + CapsReportIdOffset]);
            }
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private delegate int CapsReader(byte* buffer, ref ushort length);
}
