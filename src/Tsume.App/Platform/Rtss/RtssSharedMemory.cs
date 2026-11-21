using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Tsume.App.Platform.Rtss;

/// <summary>Live frame telemetry read from RTSS's active 3D application slot.</summary>
/// <param name="Fps">Current frame rate, or null if no 3D app is active.</param>
/// <param name="FrameTimeMs">Most recent frame time in milliseconds, or null if unavailable.</param>
public readonly record struct RtssAppStats(float? Fps, float? FrameTimeMs);

/// <summary>
/// Direct reader/writer for <c>RTSSSharedMemoryV2</c>.
/// Claims a dedicated <c>"Tsume"</c> OSD slot (never slot 0) so other monitoring tools stay untouched,
/// embeds RTSS's native hardware frametime graph for Level 4, and requests immediate live profile
/// reloads (<c>APPFLAG_PROFILE_UPDATE_REQUESTED</c>) across hooked 3D applications.
/// </summary>
public static class RtssSharedMemory
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const string OwnerName = "Tsume";
    private const uint RtssSignature = 0x52545353; // 'RTSS'
    private const uint GraphSignature = 0x47523030; // 'GR00'

    private const uint VersionExText = 0x00020007;      // 2.7+: szOSDEx[4096]
    private const uint VersionBuffer = 0x0002000C;      // 2.12+: buffer[262144]
    private const uint VersionBusy = 0x0002000E;        // 2.14+: dwBusy spinlock
    private const uint VersionLastFg = 0x00020010;      // 2.16+: dwLastForegroundApp

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
    private const int OffLastFgApp = 64;

    // OSD entry field byte offsets in RTSS_SHARED_MEMORY_OSD_ENTRY
    private const int OsdTextOffset = 0;
    private const int OsdTextCapacity = 256;
    private const int OsdOwnerOffset = 256;
    private const int OsdOwnerCapacity = 256;
    private const int OsdTextExOffset = 512;
    private const int OsdTextExCapacity = 4096;
    private const int OsdBufferOffset = 4608;
    private const int OsdBufferCapacity = 262144;

    // App entry field byte offsets in RTSS_SHARED_MEMORY_APP_ENTRY
    private const int AppPidOffset = 0;
    private const int AppFlagsOffset = 264;
    private const int AppTime0Offset = 268;
    private const int AppTime1Offset = 272;
    private const int AppFramesOffset = 276;
    private const int AppFrameTimeOffset = 280;

    private const uint AppFlagProfileUpdateRequested = 0x10000000;

    // RTSS_EMBEDDED_OBJECT_GRAPH flags
    private const uint GraphFlagFilled = 1;
    private const uint GraphFlagFrametime = 4;

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

    /// <summary>Reads FPS and frametime from the foreground (or most active) 3D application tracked by RTSS.</summary>
    public static RtssAppStats ReadActiveApp()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(OffSignature) != RtssSignature) return default;
            var version = view.ReadUInt32(OffVersion);
            if (version < 0x00020000) return default;

            var appEntrySize = view.ReadUInt32(OffAppEntrySize);
            var appArrOffset = view.ReadUInt32(OffAppArrOffset);
            var appArrSize = view.ReadUInt32(OffAppArrSize);
            if (appEntrySize < 284 || appArrOffset == 0 || appArrSize == 0) return default;

            var capacity = view.Capacity;

            if (version >= VersionLastFg && OffLastFgApp + 4 <= capacity)
            {
                var lastFg = view.ReadUInt32(OffLastFgApp);
                if (lastFg < appArrSize)
                {
                    var fg = ReadAppEntry(view, appArrOffset + (long)lastFg * appEntrySize, capacity);
                    if (fg.Fps is not null || fg.FrameTimeMs is not null) return fg;
                }
            }

            RtssAppStats best = default;
            var limit = Math.Min(appArrSize, 256u);
            for (uint i = 0; i < limit; i++)
            {
                var entry = ReadAppEntry(view, appArrOffset + (long)i * appEntrySize, capacity);
                if (entry.Fps is > 0 && (best.Fps is null || entry.Fps > best.Fps))
                    best = entry;
            }

            return best;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return default;
        }
    }

    /// <summary>
    /// Marks every hooked 3D application entry with <c>APPFLAG_PROFILE_UPDATE_REQUESTED</c> and bumps
    /// <c>dwOSDFrame</c> so running games immediately reload position, scale, fill and frame-rate cap
    /// from RTSS's Global profile on the next frame.
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

    /// <summary>
    /// Writes <paramref name="text"/> (with RTSS hypertext tags) into Tsume's dedicated OSD slot,
    /// optionally embedding RTSS's native per-frame frametime graph at buffer offset 0 (<c>&lt;OBJ=00000000&gt;</c>).
    /// </summary>
    public static bool UpdateSlot(string text, bool embedNativeFrametimeGraph = false)
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
            var slotOffset = FindOrClaimSlot(view, osdArrOffset, osdEntrySize, osdArrSize, capacity, createIfMissing: true);
            if (slotOffset < 0) return false;

            var useBusyLock = version >= VersionBusy && OffBusy + 4 <= capacity;
            if (useBusyLock && !TryAcquireBusy(view)) return false;

            try
            {
                WriteAnsiString(view, slotOffset + OsdOwnerOffset, OsdOwnerCapacity, OwnerName);

                var supportsEx = version >= VersionExText && osdEntrySize >= OsdTextExOffset + OsdTextExCapacity
                    && slotOffset + OsdTextExOffset + OsdTextExCapacity <= capacity;
                var supportsBuffer = version >= VersionBuffer && osdEntrySize >= OsdBufferOffset + OsdBufferCapacity
                    && slotOffset + OsdBufferOffset + OsdBufferCapacity <= capacity;

                if (supportsBuffer)
                {
                    if (embedNativeFrametimeGraph)
                        WriteNativeFrametimeGraph(view, slotOffset + OsdBufferOffset);
                    else
                        view.Write(slotOffset + OsdBufferOffset, 0u);
                }

                if (supportsEx)
                {
                    WriteAnsiString(view, slotOffset + OsdTextExOffset, OsdTextExCapacity, text);
                    WriteAnsiString(view, slotOffset + OsdTextOffset, OsdTextCapacity, "");
                }
                else
                {
                    WriteAnsiString(view, slotOffset + OsdTextOffset, OsdTextCapacity, text);
                }

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

    /// <summary>Clears and releases Tsume's OSD slot without disturbing any other application's slot.</summary>
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
            var slotOffset = FindOrClaimSlot(view, osdArrOffset, osdEntrySize, osdArrSize, capacity, createIfMissing: false);
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

    private static RtssAppStats ReadAppEntry(MemoryMappedViewAccessor view, long offset, long capacity)
    {
        if (offset < 0 || offset + 284 > capacity) return default;

        var pid = view.ReadUInt32(offset + AppPidOffset);
        if (pid == 0) return default;

        var flags = view.ReadUInt32(offset + AppFlagsOffset);
        if ((flags & 0x0000FFFF) == 0) return default;

        var time0 = view.ReadUInt32(offset + AppTime0Offset);
        var time1 = view.ReadUInt32(offset + AppTime1Offset);
        var frames = view.ReadUInt32(offset + AppFramesOffset);
        var frameTimeUs = view.ReadUInt32(offset + AppFrameTimeOffset);

        float? frameTimeMs = frameTimeUs is >= 500 and <= 2_000_000 ? frameTimeUs / 1000f : null;

        float? fps = null;
        if (time1 > time0 && frames > 0)
            fps = 1000f * frames / (time1 - time0);
        else if (frameTimeMs is > 0)
            fps = 1000f / frameTimeMs.Value;

        return new RtssAppStats(fps, frameTimeMs);
    }

    private static long FindOrClaimSlot(
        MemoryMappedViewAccessor view, uint arrOffset, uint entrySize, uint arrSize, long capacity, bool createIfMissing)
    {
        var limit = Math.Min(arrSize, 64u);
        long firstFree = -1;

        for (uint i = 0; i < limit; i++)
        {
            var offset = arrOffset + (long)i * entrySize;
            if (offset + OsdOwnerOffset + OsdOwnerCapacity > capacity) break;

            var owner = ReadAnsiString(view, offset + OsdOwnerOffset, OsdOwnerCapacity);
            if (owner.Equals(OwnerName, StringComparison.Ordinal)) return offset;

            // Leave slot 0 for RTSS / MSI Afterburner; claim the first free slot from index 1 onward.
            if (createIfMissing && firstFree < 0 && i >= 1 && owner.Length == 0)
                firstFree = offset;
        }

        return createIfMissing ? firstFree : -1;
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

    /// <summary>
    /// Configures RTSS's built-in per-frame frametime graph (0–50,000 µs = 0–50 ms), rendered
    /// natively at full game frame rate without polling.
    /// </summary>
    private static void WriteNativeFrametimeGraph(MemoryMappedViewAccessor view, long bufferOffset)
    {
        var header = new EmbeddedGraphHeader
        {
            Signature = GraphSignature,
            Size = (uint)Marshal.SizeOf<EmbeddedGraphHeader>(),
            Width = -24,   // 24 character cells wide (scales cleanly with ZoomRatio)
            Height = -2,   // 2 character cells tall
            Margin = 1,
            Flags = GraphFlagFrametime | GraphFlagFilled,
            Min = 0f,
            Max = 50000f,  // microseconds (0..50 ms), per RTSS SDK RTSSSharedMemorySampleDlg.cpp
            DataCount = 0, // 0 tells RTSS to pull its own per-frame frametime history directly
        };

        view.Write(bufferOffset, ref header);
    }

    private static string ReadAnsiString(MemoryMappedViewAccessor view, long offset, int maxBytes)
    {
        var bytes = new byte[maxBytes];
        view.ReadArray(offset, bytes, 0, maxBytes);

        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = maxBytes;
        return length == 0 ? "" : Encoding.Latin1.GetString(bytes, 0, length);
    }

    private static void WriteAnsiString(MemoryMappedViewAccessor view, long offset, int capacity, string text)
    {
        var encoded = Encoding.Latin1.GetBytes(text);
        var length = Math.Min(encoded.Length, capacity - 1);

        if (length > 0) view.WriteArray(offset, encoded, 0, length);
        view.Write(offset + length, (byte)0);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct EmbeddedGraphHeader
    {
        public uint Signature;
        public uint Size;
        public int Width;
        public int Height;
        public int Margin;
        public uint Flags;
        public float Min;
        public float Max;
        public uint DataCount;
    }
}
