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

        // The list can grow between asking its size and reading it; try again if so.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var length, hidGuid, 0, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
                return [];

            var buffer = new char[length];
            uint result;
            fixed (char* chars = buffer)
                result = CM_Get_Device_Interface_List(hidGuid, 0, chars, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

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

    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const int HidP_Output = 1;

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
    private struct HiddAttributes
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
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(nint preparsed);

    /// <summary>Throws away input reports that arrived before anyone asked for them.</summary>
    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HidD_FlushQueue(SafeFileHandle device);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint preparsed, out HidpCaps caps);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetButtonCaps(int reportType, byte* caps, ref ushort length, nint preparsed);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetValueCaps(int reportType, byte* caps, ref ushort length, nint preparsed);

    /// <summary>Firmware version as the device reports it: 0x0414 for "4.14".</summary>
    public static ushort? Version(SafeFileHandle device)
    {
        var attributes = new HiddAttributes { Size = (uint)sizeof(HiddAttributes) };
        return HidD_GetAttributes(device, ref attributes) ? attributes.VersionNumber : null;
    }

    /// <summary>The collection's summary, and the IDs of every output report it declares.</summary>
    public static (HidpCaps Caps, IReadOnlySet<byte> OutputReports)? Describe(SafeFileHandle device)
    {
        if (!HidD_GetPreparsedData(device, out var preparsed)) return null;

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS) return null;

            var reports = new HashSet<byte>();
            Collect(caps.NumberOutputButtonCaps, (buffer, ref length) => HidP_GetButtonCaps(HidP_Output, buffer, ref length, preparsed));
            Collect(caps.NumberOutputValueCaps, (buffer, ref length) => HidP_GetValueCaps(HidP_Output, buffer, ref length, preparsed));

            return (caps, reports);

            void Collect(ushort count, CapsReader read)
            {
                if (count == 0) return;

                var buffer = new byte[count * CapsSize];
                var length = count;
                fixed (byte* bytes = buffer)
                    if (read(bytes, ref length) != HIDP_STATUS_SUCCESS) return;

                for (var i = 0; i < length; i++) reports.Add(buffer[i * CapsSize + CapsReportIdOffset]);
            }
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private delegate int CapsReader(byte* buffer, ref ushort length);
}
