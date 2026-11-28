using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tsume.Hardware.Acer;

/// <summary>
/// Talks directly to the Acer EC HID-over-I2C collection (<c>VID_1025&amp;PID_174B</c>, usage page <c>0xFF05</c>,
/// feature report <c>0xA0</c>). Every set/get pair acquires the named mutex
/// <c>Global\ECHIDFeatureVID_1025&amp;PID_174B</c> so Acer services never interleave requests against the EC's
/// single latched reply buffer.
/// </summary>
public sealed unsafe partial class AcerEcHid : IAcerEcHid
{
    public const byte ReportId = 0xA0;
    public const int DefaultReportLength = 65;   // 1-byte report ID + 64-byte payload
    public const int OcProfileFieldLength = 19;  // f1..f5 (u16x5) + f6..f8 (u8x3) + f9..f11 (u16x3)

    public const ushort CmdSystemUsageMode = 0x0001;
    public const ushort CmdOcProfile = 0x0002;
    public const ushort CmdBatteryFunction = 0x0B03;

    public const byte OpSet = 0x01;
    public const byte OpGet = 0x02;
    public const byte OpGetLimit = 0x03;

    public const byte BatteryFnBoundary = 0x03;
    public const byte MaxUsageMode = 3;

    private const int ReplyDelayMs = 20;
    private const int MutexTimeoutMs = 2000;

    private readonly Lock _handleGate = new();
    private readonly ushort _vendorId;
    private readonly ushort _productId;
    private readonly ushort _usagePage;
    private readonly string _mutexName;
    private SafeFileHandle? _handle;
    private int _reportLength;
    private bool _disposed;

    private AcerEcHid(SafeFileHandle handle, int reportLength, ushort vendorId, ushort productId, ushort usagePage)
    {
        _handle = handle;
        _reportLength = Math.Max(reportLength, DefaultReportLength);
        _vendorId = vendorId;
        _productId = productId;
        _usagePage = usagePage;
        _mutexName = $"Global\\ECHIDFeatureVID_{vendorId:X4}&PID_{productId:X4}";
    }

    /// <summary>Finds and opens the Acer EC HID collection matching <paramref name="vendorId"/>, <paramref name="productId"/>, and <paramref name="usagePage"/>.</summary>
    public static AcerEcHid? TryOpen(ushort vendorId = 0x1025, ushort productId = 0x174B, ushort usagePage = 0xFF05) =>
        TryOpenHandle(vendorId, productId, usagePage, out var featureLength) is { } handle
            ? new AcerEcHid(handle, featureLength, vendorId, productId, usagePage)
            : null;

    public byte? GetSystemUsageMode()
    {
        var reply = Exchange(CmdSystemUsageMode, OpGet, []);
        return reply is not null && reply.Length > 5 ? reply[5] : null;
    }

    public bool SetSystemUsageMode(byte mode)
    {
        // EC accepts any byte, while _Q30 only defines cases 0..3. Never send an out-of-range mode.
        if (mode > MaxUsageMode)
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Acer EC usage mode must be 0..3.");

        return Exchange(CmdSystemUsageMode, OpSet, [mode]) is not null;
    }

    public byte[]? GetOcProfile(byte profile)
    {
        var reply = Exchange(CmdOcProfile, OpGet, [profile]);
        return reply is null ? null : ParseOcProfileReply(reply, profile);
    }

    public bool SetOcProfile(byte profile, ReadOnlySpan<byte> fields)
    {
        if (fields.Length != OcProfileFieldLength)
            throw new ArgumentOutOfRangeException(nameof(fields), $"OC profile payload must be {OcProfileFieldLength} bytes.");

        var payload = BuildOcProfilePayload(profile, fields);
        return Exchange(CmdOcProfile, OpSet, payload) is not null;
    }

    public ushort? GetOcProfileLimit(byte profile, byte limitType)
    {
        Span<byte> payload = stackalloc byte[21];
        payload[0] = profile;
        payload[20] = limitType;   // byte 26 on the wire (6 + 20)

        var reply = Exchange(CmdOcProfile, OpGetLimit, payload);
        return reply is null ? null : ParseOcProfileLimitReply(reply, limitType);
    }

    public bool SetBatteryBoundary(bool enabled, byte upperPercent, byte lowerPercent)
    {
        ReadOnlySpan<byte> payload = [BatteryFnBoundary, enabled ? (byte)1 : (byte)0, upperPercent, lowerPercent];
        var reply = Exchange(CmdBatteryFunction, OpSet, payload);
        return reply is not null && reply.Length > 6 && reply[6] == BatteryFnBoundary;
    }

    public (bool Enabled, byte Upper, byte Lower)? GetBatteryBoundary()
    {
        ReadOnlySpan<byte> payload = [BatteryFnBoundary, 0x00];
        var reply = Exchange(CmdBatteryFunction, OpGet, payload);
        return reply is null ? null : ParseBatteryBoundaryReply(reply);
    }

    /// <summary>Builds a zero-padded feature request: <c>[0xA0, 0x00, 0xA0, cmdLo, cmdHi, op, payload...]</c>.</summary>
    public static byte[] BuildRequest(ushort command, byte operation, ReadOnlySpan<byte> payload, int reportLength = DefaultReportLength)
    {
        var buffer = new byte[Math.Max(reportLength, DefaultReportLength)];
        buffer[0] = ReportId;
        buffer[1] = 0x00;
        buffer[2] = 0xA0;   // 0xA000 LE request tag required by EC firmware (FUN_ram_0001845a)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(3, 2), command);
        buffer[5] = operation;

        var copyLen = Math.Min(payload.Length, buffer.Length - 6);
        payload[..copyLen].CopyTo(buffer.AsSpan(6));
        return buffer;
    }

    /// <summary>Validates that a feature reply carries <c>0xA0</c>, status <c>0xE000</c> (<c>00 E0</c>), and echoes <paramref name="expectedCommand"/>.</summary>
    public static bool IsValidReply(ReadOnlySpan<byte> reply, ushort expectedCommand) =>
        reply.Length >= 6
        && reply[0] == ReportId
        && reply[1] == 0x00
        && reply[2] == 0xE0
        && BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(3, 2)) == expectedCommand;

    public static byte[] BuildOcProfilePayload(byte profile, ReadOnlySpan<byte> fields)
    {
        var payload = new byte[1 + OcProfileFieldLength];
        payload[0] = profile;
        fields.CopyTo(payload.AsSpan(1));
        return payload;
    }

    public static byte[]? ParseOcProfileReply(ReadOnlySpan<byte> reply, byte expectedProfile)
    {
        if (reply.Length < 7 + OcProfileFieldLength || reply[6] != expectedProfile)
            return null;

        return reply.Slice(7, OcProfileFieldLength).ToArray();
    }

    public static ushort? ParseOcProfileLimitReply(ReadOnlySpan<byte> reply, byte expectedType)
    {
        if (reply.Length < 29 || reply[26] != expectedType)
            return null;

        return BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(27, 2));
    }

    public static (bool Enabled, byte Upper, byte Lower)? ParseBatteryBoundaryReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 10 || reply[6] != BatteryFnBoundary)
            return null;

        var enabled = reply[7] == 1;
        return (enabled, reply[8], reply[9]);
    }

    private byte[]? Exchange(ushort command, byte operation, ReadOnlySpan<byte> payload)
    {
        lock (_handleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(MutexTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired) return null;

                // Retry once with a freshly re-opened HID handle if the cached handle went stale (e.g. after sleep or I2C-HID reset),
                // matching AcerHardwareService's close-and-reopen error recovery.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (!EnsureOpenLocked()) return null;

                    var request = BuildRequest(command, operation, payload, _reportLength);
                    var reply = new byte[_reportLength];
                    reply[0] = ReportId;

                    if (HidD_SetFeature(_handle!, request, (uint)request.Length))
                    {
                        Thread.Sleep(ReplyDelayMs);

                        if (HidD_GetFeature(_handle!, reply, (uint)reply.Length))
                            return IsValidReply(reply, command) ? reply : null;
                    }

                    ResetHandleLocked();
                }

                return null;
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }
            }
        }
    }

    private bool EnsureOpenLocked()
    {
        if (_handle is { IsInvalid: false, IsClosed: false }) return true;

        ResetHandleLocked();
        var reopened = TryOpenHandle(_vendorId, _productId, _usagePage, out var featureLength);
        if (reopened is null) return false;

        _handle = reopened;
        _reportLength = Math.Max(featureLength, DefaultReportLength);
        return true;
    }

    private void ResetHandleLocked()
    {
        _handle?.Dispose();
        _handle = null;
    }

    public void Dispose()
    {
        lock (_handleGate)
        {
            if (_disposed) return;
            _disposed = true;
            ResetHandleLocked();
        }
    }

    // --- Minimal Win32 HID P/Invoke (self-contained inside Tsume.Hardware) ---

    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    private const uint CR_SUCCESS = 0;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
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

    [LibraryImport("hid.dll")]
    private static partial void HidD_GetHidGuid(out Guid hidGuid);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static partial uint CM_Get_Device_Interface_List(in Guid interfaceClass, nint deviceId, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsed);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_FreePreparsedData(nint preparsed);

    [LibraryImport("hid.dll")]
    private static partial int HidP_GetCaps(nint preparsed, out HidpCaps caps);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_SetFeature(SafeFileHandle device, byte[] reportBuffer, uint reportBufferLength);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetFeature(SafeFileHandle device, [Out] byte[] reportBuffer, uint reportBufferLength);

    private static string[] EnumerateHidPaths()
    {
        HidD_GetHidGuid(out var hidGuid);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var length, hidGuid, 0, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || length <= 1)
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

    private static SafeFileHandle? TryOpenHandle(ushort vendorId, ushort productId, ushort usagePage, out int featureLength)
    {
        featureLength = 0;
        foreach (var path in EnumerateHidPaths())
        {
            var handle = OpenHid(path);
            if (handle is null) continue;

            if (MatchesCollection(handle, vendorId, productId, usagePage, out featureLength))
                return handle;

            handle.Dispose();
        }

        return null;
    }

    private static SafeFileHandle? OpenHid(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();

        handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    private static bool MatchesCollection(SafeFileHandle handle, ushort vendorId, ushort productId, ushort usagePage, out int featureReportLength)
    {
        featureReportLength = 0;
        var attrs = new HiddAttributes { Size = (uint)sizeof(HiddAttributes) };
        if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != vendorId || attrs.ProductID != productId)
            return false;

        if (!HidD_GetPreparsedData(handle, out var preparsed))
            return false;

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS)
                return false;

            if (caps.UsagePage != usagePage || caps.FeatureReportByteLength < DefaultReportLength)
                return false;

            featureReportLength = caps.FeatureReportByteLength;
            return true;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }
}
