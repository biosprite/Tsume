using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Tsume.App.Platform.Rtss;

/// <summary>
/// Minimal access to <c>RTSSSharedMemoryV2</c>: checks RTSS is up, requests immediate live profile reloads
/// (<c>APPFLAG_PROFILE_UPDATE_REQUESTED</c>) across hooked 3D applications, and clears the <c>"Tsume"</c> OSD slot
/// that older Tsume versions drew their own overlay into (the overlay is now drawn by RTSS's OverlayEditor plugin).
/// </summary>
public static class RtssSharedMemory
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const string OwnerName = "Tsume";
    private const uint RtssSignature = 0x52545353; // 'RTSS'

    private const uint VersionExText = 0x00020007;      // 2.7+: szOSDEx[4096]
    private const uint VersionApiFlags = 0x0002000A;    // 2.10+: APPFLAG_API_USAGE_MASK
    private const uint VersionBuffer = 0x0002000C;      // 2.12+: buffer[262144]
    private const uint VersionBusy = 0x0002000E;        // 2.14+: dwBusy spinlock

    // Header field byte offsets in RTSS_SHARED_MEMORY
    private const int OffSignature = 0;
    private const int OffVersion = 4;
    private const int OffAppEntrySize = 8;
    private const int OffAppArrOffset = 12;
    private const int OffAppArrSize = 16;
    private const int OffOsdEntrySize = 20;
    private const int OffOsdArrOffset = 24;
    private const int OffOsdArrSize = 28;
    private const int OffOsdFrame = 32;
    private const int OffBusy = 36;

    // OSD entry field byte offsets in RTSS_SHARED_MEMORY_OSD_ENTRY
    private const int OsdTextOffset = 0;
    private const int OsdOwnerOffset = 256;
    private const int OsdOwnerCapacity = 256;
    private const int OsdTextExOffset = 512;
    private const int OsdBufferOffset = 4608;

    // App entry field byte offsets in RTSS_SHARED_MEMORY_APP_ENTRY
    private const int AppPidOffset = 0;
    private const int AppFlagsOffset = 264;

    private const uint AppFlagApiUsageMask = 0x0000FFFF;
    private const uint AppFlagProfileUpdateRequested = 0x10000000;

    /// <summary>True when RTSS's shared memory exists and carries a valid signature.</summary>
    public static bool IsAvailable()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read);
            return view.ReadUInt32(OffSignature) == RtssSignature && view.ReadUInt32(OffVersion) >= 0x00020000;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fast, allocation-free check for whether <paramref name="processId"/> is currently hooked by RTSS as a 3D app.
    /// </summary>
    public static bool IsProcessHooked(uint processId)
    {
        if (processId == 0) return false;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(OffSignature) != RtssSignature) return false;
            var version = view.ReadUInt32(OffVersion);
            if (version < 0x00020000) return false;

            var appEntrySize = view.ReadUInt32(OffAppEntrySize);
            var appArrOffset = view.ReadUInt32(OffAppArrOffset);
            var appArrSize = view.ReadUInt32(OffAppArrSize);
            var capacity = view.Capacity;

            if (appEntrySize >= 284 && appArrOffset > 0 && appArrSize > 0)
            {
                var limit = Math.Min(appArrSize, 256u);
                for (uint i = 0; i < limit; i++)
                {
                    var offset = appArrOffset + (long)i * appEntrySize;
                    if (offset + AppFlagsOffset + 4 > capacity) break;

                    if (view.ReadUInt32(offset + AppPidOffset) != processId) continue;

                    var flags = view.ReadUInt32(offset + AppFlagsOffset);
                    var hasApi = version >= VersionApiFlags ? (flags & AppFlagApiUsageMask) != 0 : flags != 0;
                    if (hasApi) return true;
                }
            }

            return false;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the process IDs of 3D applications currently hooked by RTSS, or null if RTSS shared memory
    /// is unavailable.
    /// </summary>
    public static HashSet<uint>? GetHookedProcessIds()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(OffSignature) != RtssSignature) return null;
            var version = view.ReadUInt32(OffVersion);
            if (version < 0x00020000) return null;

            var appEntrySize = view.ReadUInt32(OffAppEntrySize);
            var appArrOffset = view.ReadUInt32(OffAppArrOffset);
            var appArrSize = view.ReadUInt32(OffAppArrSize);
            var capacity = view.Capacity;

            var pids = new HashSet<uint>();
            if (appEntrySize >= 284 && appArrOffset > 0 && appArrSize > 0)
            {
                var limit = Math.Min(appArrSize, 256u);
                for (uint i = 0; i < limit; i++)
                {
                    var offset = appArrOffset + (long)i * appEntrySize;
                    if (offset + AppFlagsOffset + 4 > capacity) break;

                    var pid = view.ReadUInt32(offset + AppPidOffset);
                    if (pid == 0) continue;

                    var flags = view.ReadUInt32(offset + AppFlagsOffset);
                    var hasApi = version >= VersionApiFlags ? (flags & AppFlagApiUsageMask) != 0 : flags != 0;
                    if (hasApi) pids.Add(pid);
                }
            }

            return pids;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Marks every hooked 3D application entry with <c>APPFLAG_PROFILE_UPDATE_REQUESTED</c> and bumps
    /// <c>dwOSDFrame</c> so running games immediately reload the frame-rate cap and other Global profile
    /// values on the next frame.
    /// </summary>
    public static bool RequestProfileUpdate()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            if (view.ReadUInt32(OffSignature) != RtssSignature) return false;
            var version = view.ReadUInt32(OffVersion);
            if (version < 0x00020000) return false;

            var appEntrySize = view.ReadUInt32(OffAppEntrySize);
            var appArrOffset = view.ReadUInt32(OffAppArrOffset);
            var appArrSize = view.ReadUInt32(OffAppArrSize);
            var capacity = view.Capacity;

            if (appEntrySize >= 284 && appArrOffset > 0 && appArrSize > 0)
            {
                var limit = Math.Min(appArrSize, 256u);
                for (uint i = 0; i < limit; i++)
                {
                    var offset = appArrOffset + (long)i * appEntrySize;
                    if (offset + AppFlagsOffset + 4 > capacity) break;

                    var pid = view.ReadUInt32(offset + AppPidOffset);
                    if (pid == 0) continue;

                    var flags = view.ReadUInt32(offset + AppFlagsOffset);
                    view.Write(offset + AppFlagsOffset, flags | AppFlagProfileUpdateRequested);
                }
            }

            var frame = view.ReadUInt32(OffOsdFrame);
            view.Write(OffOsdFrame, unchecked(frame + 1));
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Clears and releases Tsume's OSD slot (if one exists) without disturbing any other application's slot.</summary>
    public static bool ClearSlot()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            if (view.ReadUInt32(OffSignature) != RtssSignature) return false;
            var version = view.ReadUInt32(OffVersion);
            if (version < 0x00020000) return false;

            var osdEntrySize = view.ReadUInt32(OffOsdEntrySize);
            var osdArrOffset = view.ReadUInt32(OffOsdArrOffset);
            var osdArrSize = view.ReadUInt32(OffOsdArrSize);
            if (osdEntrySize < 512 || osdArrOffset == 0 || osdArrSize == 0) return false;

            var capacity = view.Capacity;
            var slotOffset = FindSlot(view, osdArrOffset, osdEntrySize, osdArrSize, capacity);
            if (slotOffset < 0) return true;

            var useBusyLock = version >= VersionBusy && OffBusy + 4 <= capacity;
            if (useBusyLock && !TryAcquireBusy(view)) return false;

            try
            {
                view.Write(slotOffset + OsdTextOffset, (byte)0);
                view.Write(slotOffset + OsdOwnerOffset, (byte)0);

                if (version >= VersionExText && osdEntrySize >= OsdTextExOffset + 1 && slotOffset + OsdTextExOffset < capacity)
                    view.Write(slotOffset + OsdTextExOffset, (byte)0);

                if (version >= VersionBuffer && osdEntrySize >= OsdBufferOffset + 4 && slotOffset + OsdBufferOffset + 4 <= capacity)
                    view.Write(slotOffset + OsdBufferOffset, 0u);

                var frame = view.ReadUInt32(OffOsdFrame);
                view.Write(OffOsdFrame, unchecked(frame + 1));
                return true;
            }
            finally
            {
                if (useBusyLock) view.Write(OffBusy, 0u);
            }
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static long FindSlot(MemoryMappedViewAccessor view, uint arrOffset, uint entrySize, uint arrSize, long capacity)
    {
        var limit = Math.Min(arrSize, 64u);
        for (uint i = 0; i < limit; i++)
        {
            var offset = arrOffset + (long)i * entrySize;
            if (offset + OsdOwnerOffset + OsdOwnerCapacity > capacity) break;

            if (ReadAnsiString(view, offset + OsdOwnerOffset, OsdOwnerCapacity).Equals(OwnerName, StringComparison.Ordinal))
                return offset;
        }

        return -1;
    }

    private static bool TryAcquireBusy(MemoryMappedViewAccessor view)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (view.ReadUInt32(OffBusy) == 0)
            {
                view.Write(OffBusy, 1u);
                return true;
            }
            Thread.Sleep(1);
        }

        return false;
    }

    private static string ReadAnsiString(MemoryMappedViewAccessor view, long offset, int maxBytes)
    {
        var bytes = new byte[maxBytes];
        view.ReadArray(offset, bytes, 0, maxBytes);

        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = maxBytes;
        return length == 0 ? "" : Encoding.Latin1.GetString(bytes, 0, length);
    }
}
