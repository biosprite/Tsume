using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Tsume.App.Platform.Rtss;

/// <summary>Snapshot of the foreground window's RTSS hook / frame state and whether a previously cached PID is still hooked.</summary>
public readonly record struct RtssFrameQueryResult(
    bool ForegroundIsHooked,
    uint ForegroundPid,
    int FrameWidth,
    int FrameHeight,
    bool CachedPidStillHooked,
    bool HasOverlayEditorSlot = false,
    bool HasAnyHookedApps = false)
{
    public bool HasFrame => FrameWidth > 0 && FrameHeight > 0;
}

/// <summary>
/// Minimal access to <c>RTSSSharedMemoryV2</c>: checks RTSS is up, requests immediate live profile reloads
/// (<c>APPFLAG_PROFILE_UPDATE_REQUESTED</c>) across hooked 3D applications, and clears the <c>"Tsume"</c> OSD slot
/// that older Tsume versions drew their own overlay into (the overlay is now drawn by RTSS's OverlayEditor plugin).
/// </summary>
public static class RtssSharedMemory
{
    private const string MapName = "RTSSSharedMemoryV2";
    private const string OwnerName = "Tsume";
    private const string OverlayEditorOwnerName = "RTSSOverlayEditor";
    private const uint RtssSignature = 0x52545353; // 'RTSS'

    private const uint VersionExText = 0x00020007;      // 2.7+: szOSDEx[4096]
    private const uint VersionApiFlags = 0x0002000A;    // 2.10+: APPFLAG_API_USAGE_MASK
    private const uint VersionBuffer = 0x0002000C;      // 2.12+: buffer[262144]
    private const uint VersionBusy = 0x0002000E;        // 2.14+: dwBusy spinlock
    private const uint VersionResolution = 0x00020014;  // 2.20+: dwResolutionX / dwResolutionY & szOSDEx2[32768]

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
    private const int OsdTextExCapacity = 4096;
    private const int OsdBufferOffset = 4608;
    private const int OsdTextEx2Offset = OsdBufferOffset + 262144;
    private const int OsdTextEx2Capacity = 32768;

    // App entry field byte offsets in RTSS_SHARED_MEMORY_APP_ENTRY
    private const int AppPidOffset = 0;
    private const int AppFlagsOffset = 264;
    private const int AppResolutionXOffset = 9224;
    private const int AppResolutionYOffset = 9228;
    public const int MinFrameSide = 240;
    private const uint MaxFrameSide = 16384;

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
    /// Fast, allocation-free check for whether RTSS is running and currently hooking at least one 3D application.
    /// </summary>
    public static bool HasHookedApps()
    {
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

                    if (view.ReadUInt32(offset + AppPidOffset) == 0) continue;

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
    /// Inspects <c>RTSSSharedMemoryV2</c> in a single pass to find the rendered frame size for any PID in
    /// <paramref name="foregroundPids"/> (ignoring tiny helper swapchains below <see cref="MinFrameSide"/>)
    /// and to check whether <paramref name="cachedPid"/> is still hooked by RTSS.
    /// </summary>
    public static RtssFrameQueryResult QueryFrameState(
        ReadOnlySpan<uint> foregroundPids, uint cachedPid, bool checkOverlayEditorSlot = false, bool checkAnyHookedApps = false)
    {
        if (foregroundPids.IsEmpty && cachedPid == 0 && !checkOverlayEditorSlot && !checkAnyHookedApps) return default;
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
            var capacity = view.Capacity;

            var fgHooked = false;
            var matchedFgPid = 0u;
            var frameW = 0;
            var frameH = 0;
            var cachedStillHooked = false;
            var anyHooked = false;

            if (appEntrySize >= 284 && appArrOffset > 0 && appArrSize > 0)
            {
                var canReadResolution = version >= VersionResolution && appEntrySize >= AppResolutionYOffset + 4;
                var limit = Math.Min(appArrSize, 256u);
                for (uint i = 0; i < limit; i++)
                {
                    var offset = appArrOffset + (long)i * appEntrySize;
                    if (offset + AppFlagsOffset + 4 > capacity) break;

                    var pid = view.ReadUInt32(offset + AppPidOffset);
                    if (pid == 0) continue;

                    var flags = view.ReadUInt32(offset + AppFlagsOffset);
                    var hasApi = version >= VersionApiFlags ? (flags & AppFlagApiUsageMask) != 0 : flags != 0;
                    if (!hasApi) continue;

                    anyHooked = true;

                    if (cachedPid != 0 && pid == cachedPid)
                        cachedStillHooked = true;

                    if (!foregroundPids.IsEmpty && foregroundPids.Contains(pid))
                    {
                        fgHooked = true;
                        if (matchedFgPid == 0) matchedFgPid = pid;

                        if (frameW == 0 && canReadResolution && offset + AppResolutionYOffset + 4 <= capacity)
                        {
                            var width = view.ReadUInt32(offset + AppResolutionXOffset);
                            var height = view.ReadUInt32(offset + AppResolutionYOffset);
                            if (width is >= MinFrameSide and <= MaxFrameSide && height is >= MinFrameSide and <= MaxFrameSide)
                            {
                                matchedFgPid = pid;
                                frameW = (int)width;
                                frameH = (int)height;
                                break;
                            }
                        }
                    }
                }
            }

            var hasOverlayEditorSlot = false;
            if (checkOverlayEditorSlot)
            {
                var osdEntrySize = view.ReadUInt32(OffOsdEntrySize);
                var osdArrOffset = view.ReadUInt32(OffOsdArrOffset);
                var osdArrSize = view.ReadUInt32(OffOsdArrSize);
                if (osdEntrySize >= 512 && osdArrOffset > 0 && osdArrSize > 0)
                    hasOverlayEditorSlot = FindSlot(view, osdArrOffset, osdEntrySize, osdArrSize, capacity, OverlayEditorOwnerName) >= 0;
            }

            return new RtssFrameQueryResult(
                fgHooked, matchedFgPid, frameW, frameH, cachedStillHooked, hasOverlayEditorSlot, anyHooked);
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return default;
        }
    }

    /// <summary>
    /// Size in pixels of the frame <paramref name="processId"/> renders, as RTSS reports it for a hooked 3D app.
    /// That is the space RTSS positions the overlay in, and it is smaller than the screen when a borderless game
    /// renders below native resolution. Null when the process is not hooked or this RTSS does not report it.
    /// </summary>
    public static (int Width, int Height)? GetFrameSize(uint processId)
    {
        if (processId == 0) return null;
        var result = QueryFrameState([processId], cachedPid: 0);
        return result.HasFrame ? (result.FrameWidth, result.FrameHeight) : null;
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

            IncrementOsdFrame(view);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>True when RTSS's OverlayEditor plugin currently owns an active OSD slot in shared memory.</summary>
    public static bool HasOverlayEditorSlot()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(OffSignature) != RtssSignature) return false;
            if (view.ReadUInt32(OffVersion) < 0x00020000) return false;

            var osdEntrySize = view.ReadUInt32(OffOsdEntrySize);
            var osdArrOffset = view.ReadUInt32(OffOsdArrOffset);
            var osdArrSize = view.ReadUInt32(OffOsdArrSize);
            if (osdEntrySize < 512 || osdArrOffset == 0 || osdArrSize == 0) return false;

            return FindSlot(view, osdArrOffset, osdEntrySize, osdArrSize, view.Capacity, OverlayEditorOwnerName) >= 0;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether RTSS's OverlayEditor plugin has already published a panel layer at
    /// <c>&lt;P=x,y&gt;&lt;L...&gt;&lt;E=panelWidth,panelHeight&gt;</c> into its shared-memory OSD slot.
    /// </summary>
    public static bool IsOverlayEditorAtPosition(int x, int y, int panelWidth, int panelHeight)
    {
        if (panelWidth <= 0 || panelHeight <= 0) return false;

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (!TryResolveOverlayEditorTextSlot(view, out _, out var textOffset, out var maxBytes))
                return false;

            var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                view.ReadArray(textOffset, buffer, 0, maxBytes);
                var length = Array.IndexOf(buffer, (byte)0, 0, maxBytes);
                if (length < 0) length = maxBytes;
                if (length == 0) return false;

                return TryFindPanelPosition(buffer.AsSpan(0, length), panelWidth, panelHeight, out var currentX, out var currentY)
                    && currentX == x
                    && currentY == y;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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
            var slotOffset = FindSlot(view, osdArrOffset, osdEntrySize, osdArrSize, capacity, OwnerName);
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
            }
            finally
            {
                if (useBusyLock) ReleaseBusy(view);
            }

            IncrementOsdFrame(view);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool TryResolveOverlayEditorTextSlot(
        MemoryMappedViewAccessor view, out uint version, out long textOffset, out int maxBytes)
    {
        version = 0;
        textOffset = 0;
        maxBytes = 0;

        if (view.ReadUInt32(OffSignature) != RtssSignature) return false;
        version = view.ReadUInt32(OffVersion);
        if (version < VersionExText) return false;

        var osdEntrySize = view.ReadUInt32(OffOsdEntrySize);
        var osdArrOffset = view.ReadUInt32(OffOsdArrOffset);
        var osdArrSize = view.ReadUInt32(OffOsdArrSize);
        if (osdEntrySize < 512 || osdArrOffset == 0 || osdArrSize == 0) return false;

        var capacity = view.Capacity;
        var slotOffset = FindSlot(view, osdArrOffset, osdEntrySize, osdArrSize, capacity, OverlayEditorOwnerName);
        if (slotOffset < 0) return false;

        if (version >= VersionResolution && osdEntrySize > OsdTextEx2Offset)
        {
            textOffset = slotOffset + OsdTextEx2Offset;
            maxBytes = (int)Math.Min(OsdTextEx2Capacity, Math.Min(osdEntrySize - OsdTextEx2Offset, capacity - textOffset));
        }
        else if (osdEntrySize > OsdTextExOffset)
        {
            textOffset = slotOffset + OsdTextExOffset;
            maxBytes = (int)Math.Min(OsdTextExCapacity, Math.Min(osdEntrySize - OsdTextExOffset, capacity - textOffset));
        }

        return maxBytes > 1;
    }

    private static bool TryFindPanelPosition(
        ReadOnlySpan<byte> text, int panelWidth, int panelHeight, out int x, out int y)
    {
        x = 0;
        y = 0;

        Span<byte> extentTag = stackalloc byte[32];
        if (!TryFormatExtentTag(extentTag, out var extentLen, panelWidth, panelHeight))
            return false;
        var extentSpan = extentTag[..extentLen];

        var cursor = 0;
        while (cursor < text.Length)
        {
            var rel = text[cursor..].IndexOf((byte)'<');
            if (rel < 0) return false;

            var tagStart = cursor + rel;
            if (TryParsePosLayerTag(text, tagStart, out var px, out var py, out _, out var tagEnd))
            {
                if (text[tagEnd..].StartsWith(extentSpan))
                {
                    x = px;
                    y = py;
                    return true;
                }

                cursor = tagEnd;
                continue;
            }

            cursor = tagStart + 1;
        }

        return false;
    }

    private static bool TryParsePosLayerTag(
        ReadOnlySpan<byte> text, int start, out int x, out int y, out int layerId, out int end)
    {
        x = 0;
        y = 0;
        layerId = 0;
        end = 0;

        if (start + 11 > text.Length
            || text[start] != (byte)'<'
            || text[start + 1] != (byte)'P'
            || text[start + 2] != (byte)'=')
        {
            return false;
        }

        var pos = start + 3;
        if (!TryParseInt(text, ref pos, out x)) return false;
        if (pos >= text.Length || text[pos] != (byte)',') return false;
        pos++;
        if (!TryParseInt(text, ref pos, out y)) return false;

        if (pos + 4 > text.Length
            || text[pos] != (byte)'>'
            || text[pos + 1] != (byte)'<'
            || text[pos + 2] != (byte)'L')
        {
            return false;
        }

        pos += 3;
        if (!TryParseNonNegativeInt(text, ref pos, out layerId)) return false;
        if (pos >= text.Length || text[pos] != (byte)'>') return false;

        end = pos + 1;
        return true;
    }

    private static bool TryParseInt(ReadOnlySpan<byte> text, ref int pos, out int value)
    {
        value = 0;
        if (pos >= text.Length) return false;

        var negative = false;
        if (text[pos] == (byte)'-')
        {
            negative = true;
            pos++;
        }

        if (!TryParseNonNegativeInt(text, ref pos, out var magnitude)) return false;
        value = negative ? -magnitude : magnitude;
        return true;
    }

    private static bool TryParseNonNegativeInt(ReadOnlySpan<byte> text, ref int pos, out int value)
    {
        value = 0;
        var digitsStart = pos;
        while (pos < text.Length && text[pos] is >= (byte)'0' and <= (byte)'9')
        {
            if (value > 100_000_000) return false;
            value = value * 10 + (text[pos] - (byte)'0');
            pos++;
        }

        return pos > digitsStart;
    }

    private static bool TryFormatExtentTag(Span<byte> dest, out int written, int width, int height)
    {
        written = 0;
        if (dest.Length < 8) return false;

        dest[0] = (byte)'<';
        dest[1] = (byte)'E';
        dest[2] = (byte)'=';
        var pos = 3;

        if (!Utf8Formatter.TryFormat(width, dest[pos..], out var wLen)) return false;
        pos += wLen;
        if (pos >= dest.Length) return false;
        dest[pos++] = (byte)',';

        if (!Utf8Formatter.TryFormat(height, dest[pos..], out var hLen)) return false;
        pos += hLen;
        if (pos >= dest.Length) return false;
        dest[pos++] = (byte)'>';

        written = pos;
        return true;
    }

    private static long FindSlot(MemoryMappedViewAccessor view, uint arrOffset, uint entrySize, uint arrSize, long capacity, string ownerName)
    {
        var limit = Math.Min(arrSize, 64u);
        for (uint i = 0; i < limit; i++)
        {
            var offset = arrOffset + (long)i * entrySize;
            if (offset + OsdOwnerOffset + OsdOwnerCapacity > capacity) break;

            if (ReadAnsiString(view, offset + OsdOwnerOffset, OsdOwnerCapacity).Equals(ownerName, StringComparison.Ordinal))
                return offset;
        }

        return -1;
    }

    private static unsafe bool TryAcquireBusy(MemoryMappedViewAccessor view)
    {
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            if (basePtr == null) return false;
            ref var busy = ref *(int*)(basePtr + view.PointerOffset + OffBusy);

            for (var attempt = 0; attempt < 40; attempt++)
            {
                if ((Interlocked.Or(ref busy, 1) & 1) == 0)
                    return true;
                Thread.Sleep(1);
            }

            return false;
        }
        finally
        {
            if (basePtr != null)
                view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe void ReleaseBusy(MemoryMappedViewAccessor view)
    {
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            if (basePtr != null)
                Volatile.Write(ref *(int*)(basePtr + view.PointerOffset + OffBusy), 0);
        }
        finally
        {
            if (basePtr != null)
                view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe void IncrementOsdFrame(MemoryMappedViewAccessor view)
    {
        if (OffOsdFrame + 4 > view.Capacity) return;

        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            if (basePtr != null)
                Interlocked.Increment(ref *(int*)(basePtr + view.PointerOffset + OffOsdFrame));
        }
        finally
        {
            if (basePtr != null)
                view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
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
