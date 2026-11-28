using System.IO;
using Microsoft.Win32.SafeHandles;
using Tsume.App.Platform.Interop;
using Tsume.Core.Controller;
using Tsume.Core.Devices;
using Tsume.Core.Lighting;

namespace Tsume.App.Platform;

/// <summary>
/// The Claw gamepad's vendor HID channel (see <c>docs/hardware/controller.md</c>); needs no elevation.
/// Deliberately narrow: reads the profile and mode, writes only the lighting block, switches only between the two <see cref="PadMode"/> modes.
/// </summary>
public sealed class ClawController : IDisposable
{
    public const ushort MsiVendorId = 0x0DB0;
    public static readonly IReadOnlyList<ushort> MsiProductIds = [0x1901, 0x1902];

    public static readonly IReadOnlyList<ushort> AcerVendorIds = [0x0502, 0x0489];
    public static readonly IReadOnlyList<ushort> AcerProductIds = [0xF900, 0xF901, 0xF909, 0xF90A, 0xF310, 0xF311, 0xF319, 0xF31A];

    /// <summary>All known HJC/JieLi handheld controller USB ID families (MSI Claw + Acer Predator Atlas 8).</summary>
    public static readonly IReadOnlyList<(IReadOnlyList<ushort> VendorIds, IReadOnlyList<ushort> ProductIds)> KnownFamilies =
    [
        ([MsiVendorId], MsiProductIds),
        (AcerVendorIds, AcerProductIds),
    ];

    private const byte ReportId = 0x0F;
    private const int ReportLength = 64;

    /// <summary>The largest data a single profile read or write carries.</summary>
    public const int MaxChunk = 55;

    private const byte ReadProfile = 0x04;
    private const byte ReadProfileAck = 0x05;
    private const byte ReadRgbStatusAck = 0x0A;
    private const byte WriteProfile = 0x21;
    private const byte SyncToRom = 0x22;
    private const byte SwitchMode = 0x24;
    private const byte ReadGamepadMode = 0x26;
    private const byte GamepadModeAck = 0x27;
    private const byte MotionDataAck = 0x30;
    private const byte Unknown = 0xF4;

    /// <summary>Incoming report byte that carries the command.</summary>
    private const int CommandAt = 4;

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(1000);
    private const int Attempts = 3;

    /// <summary>Serialises vendor HID exchanges across all handles in this process so replies never interleave.</summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;

    private ClawController(SafeFileHandle handle, ushort version)
    {
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        FirmwareVersionRaw = version;
        FirmwareVersion = $"{version >> 8:X}.{version & 0xFF:X2}";
    }

    /// <summary>Raw USB <c>bcdDevice</c> firmware version, e.g. <c>0x0414</c> or <c>0x0217</c>.</summary>
    public ushort FirmwareVersionRaw { get; }

    /// <summary>"4.14" — the controller firmware, from the USB descriptor.</summary>
    public string FirmwareVersion { get; }

    /// <summary>
    /// True when the controller firmware version is unknown (<c>0</c>) or an early launch firmware
    /// (A1M &lt;= 1.63, Claw 8 AI+ 2.xx &lt; 2.17) whose profile map sits at legacy offsets.
    /// Prefer <see cref="IsUnsupportedProfileFirmware"/> when the board profile is known.
    /// </summary>
    public bool UsesLegacyProfileLayout =>
        IsUnsupportedProfileFirmware(minFirmwareVersion: null, boardKnown: false);

    /// <summary>
    /// True when the controller's firmware version cannot be trusted with the 1478-byte profile map.
    /// Version <c>0</c> (unreadable descriptor) is always rejected. When the board declares
    /// <paramref name="minFirmwareVersion"/>, checks against that board's cutoff; when null on a known board
    /// (e.g. Claw 8 EX AI+ or Claw A8), all non-zero versions are accepted. When the board is not yet known,
    /// falls back to the conservative A1M / A2VM launch-firmware cutoffs.
    /// </summary>
    public bool IsUnsupportedProfileFirmware(ushort? minFirmwareVersion, bool boardKnown)
    {
        if (FirmwareVersionRaw == 0) return true;
        if (minFirmwareVersion is { } min) return FirmwareVersionRaw < min;
        if (boardKnown) return false;
        return FirmwareVersionRaw <= 0x0163 || (FirmwareVersionRaw >= 0x0200 && FirmwareVersionRaw < 0x0217);
    }

    /// <summary>
    /// Opens the built-in handheld controller's HJC vendor collection (MSI Claw or Acer Predator Atlas 8),
    /// or returns null if there is none. Recognised by output report 0x0F with 64-byte input/output reports.
    /// </summary>
    public static ClawController? Find()
    {
        foreach (var (vendorIds, productIds) in KnownFamilies)
            if (Find(vendorIds, productIds) is { } found) return found;

        return null;
    }

    public static ClawController? Find(ushort vendorId, IReadOnlyList<ushort> productIds) =>
        Find([vendorId], productIds);

    public static ClawController? Find(IReadOnlyList<ushort> vendorIds, IReadOnlyList<ushort> productIds)
    {
        if (vendorIds.Count == 0 || productIds.Count == 0) return null;

        foreach (var path in Hid.DevicePaths())
        {
            if (!MatchesUsbIds(path, vendorIds, productIds)) continue;

            var handle = Hid.OpenReadWrite(path);
            if (handle is null) continue;

            var description = Hid.Describe(handle);
            if (description is { } d
                && d.OutputReports.Contains(ReportId)
                && d.Caps.OutputReportByteLength == ReportLength
                && d.Caps.InputReportByteLength == ReportLength)
            {
                try
                {
                    return new ClawController(handle, Hid.Version(handle) ?? 0);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // Device disconnected or access was blocked (e.g. HidHide) mid-open; skip it.
                }
            }

            handle.Dispose();
        }

        return null;
    }

    public static bool IsClaw(string path)
    {
        foreach (var (vendorIds, productIds) in KnownFamilies)
            if (MatchesUsbIds(path, vendorIds, productIds)) return true;

        return false;
    }

    private static bool MatchesUsbIds(string path, IReadOnlyList<ushort> vendorIds, IReadOnlyList<ushort> productIds)
    {
        var lower = path.ToLowerInvariant();
        var vidMatch = false;
        foreach (var vid in vendorIds)
        {
            if (lower.Contains($"vid_{vid:x4}"))
            {
                vidMatch = true;
                break;
            }
        }

        if (!vidMatch) return false;

        foreach (var id in productIds)
            if (lower.Contains($"pid_{id:x4}")) return true;

        return false;
    }

    /// <summary>Up to <see cref="MaxChunk"/> bytes of the profile, or null if the controller did not answer.</summary>
    public Task<byte[]?> ReadProfileAsync(int offset, int length) =>
        ReadProfileAsync(offset, length, ProfileMap.Length);

    public async Task<byte[]?> ReadProfileAsync(int offset, int length, int profileLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, profileLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxChunk);

        var reply = await ExchangeAsync(
            Packet(ReadProfile, 0x00, (byte)(offset >> 8), (byte)offset, (byte)length),
            incoming => incoming[CommandAt] == ReadProfileAck
                     && incoming[6] == (byte)(offset >> 8)
                     && incoming[7] == (byte)offset
                     && incoming[8] == length);

        return reply?[9..(9 + length)];
    }

    /// <summary>A span of the profile of any length, read in chunks. Null if any chunk went unanswered.</summary>
    public async Task<byte[]?> ReadProfileRangeAsync(int offset, int length)
    {
        var result = new byte[length];

        for (var done = 0; done < length; done += MaxChunk)
        {
            var size = Math.Min(MaxChunk, length - done);
            var chunk = await ReadProfileAsync(offset + done, size);
            if (chunk is null) return null;

            chunk.CopyTo(result, done);
        }

        return result;
    }

    /// <summary>The gamepad mode and what M1/M2 do, as raw codes; null if unanswered.</summary>
    public async Task<(byte Mode, byte MKeys)?> ReadModeAsync()
    {
        var reply = await ExchangeAsync(Packet(ReadGamepadMode), incoming => incoming[CommandAt] == GamepadModeAck);
        return reply is null ? null : (reply[5], reply[6]);
    }

    /// <summary>The highest "what M1/M2 do" code the protocol has.</summary>
    private const byte MaxMKeys = 2;

    /// <summary>
    /// Asks the pad to become a gamepad or a mouse; pass <paramref name="mKeys"/> as <see cref="ReadModeAsync"/> gave it.
    /// True means sent, not applied: there is no reply, so read the mode back (as <see cref="ControllerMode"/> does).
    /// </summary>
    public async Task<bool> SwitchModeAsync(PadMode mode, byte mKeys)
    {
        // Safety guard: only the two everyday modes may ever be sent.
        if (mode is not (PadMode.Gamepad or PadMode.Cursor))
            throw new ArgumentOutOfRangeException(nameof(mode), "Only gamepad and cursor mode may be asked for.");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mKeys, MaxMKeys);

        await OneAtATime.WaitAsync();
        try
        {
            await _stream.WriteAsync(Packet(SwitchMode, (byte)mode, mKeys));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>
    /// Writes bytes of the lighting block into the controller's working memory.
    /// The lights change at once; nothing is kept over a power cycle until <see cref="SaveAsync"/>.
    /// </summary>
    public async Task<bool> WriteLightingAsync(int offset, ReadOnlyMemory<byte> data, ushort? minFirmwareVersion = null, bool boardKnown = false)
    {
        if (IsUnsupportedProfileFirmware(minFirmwareVersion, boardKnown)) return false;

        // Safety guard: nothing outside the lighting block may ever be written.
        if (offset < ProfileMap.LightingStart || offset + data.Length > ProfileMap.LightingEnd)
            throw new ArgumentOutOfRangeException(nameof(offset), "Only the lighting block may be written.");

        return await WriteProfileChunkedAsync(offset, data);
    }

    /// <summary>Writes a back button's remap block to RAM; rejects offsets outside <paramref name="profile"/>.</summary>
    public Task<bool> WriteBackButtonAsync(BackButtonsProfile profile, BackButtonDefinition button, ReadOnlyMemory<byte> data)
    {
        if (IsUnsupportedProfileFirmware(profile.MinFirmwareVersion, boardKnown: true)) return Task.FromResult(false);

        if (!profile.Buttons.Contains(button))
            throw new ArgumentOutOfRangeException(nameof(button), "Back button is not declared in this device's verified profile.");
        if (data.Length != profile.BlockLength)
            throw new ArgumentOutOfRangeException(nameof(data), $"Back-button profile must be {profile.BlockLength} bytes.");
        if (button.Offset < 0 || button.Offset + data.Length > profile.ProfileLength)
            throw new ArgumentOutOfRangeException(nameof(button), "Back-button offset lies outside the controller profile.");

        return WriteProfileChunkedAsync(button.Offset, data);
    }

    private async Task<bool> WriteProfileChunkedAsync(int offset, ReadOnlyMemory<byte> data)
    {
        for (var done = 0; done < data.Length; done += MaxChunk)
        {
            var size = Math.Min(MaxChunk, data.Length - done);
            var at = offset + done;

            var payload = new byte[4 + size];
            payload[0] = 0x01;   // profile index; MSI writes with 1 and reads with 0
            payload[1] = (byte)(at >> 8);
            payload[2] = (byte)at;
            payload[3] = (byte)size;
            data.Span.Slice(done, size).CopyTo(payload.AsSpan(4));

            if (await ExchangeAsync(Packet(WriteProfile, payload), IsPlainAck) is null) return false;
        }

        return true;
    }

    /// <summary>Copies the controller's working memory to flash, so the lighting survives a restart. A flash write: use sparingly.</summary>
    public async Task<bool> SaveAsync() => await ExchangeAsync(Packet(SyncToRom), IsPlainAck) is not null;

    /// <summary>MSI accepts any report as the answer to a write, except these, which arrive on their own.</summary>
    private static bool IsPlainAck(byte[] incoming) =>
        incoming[CommandAt] is not (ReadRgbStatusAck or GamepadModeAck or MotionDataAck or Unknown);

    private static byte[] Packet(byte command, params byte[] payload)
    {
        var packet = new byte[ReportLength];
        packet[0] = ReportId;
        packet[3] = 0x3C;
        packet[4] = command;
        payload.CopyTo(packet, 5);
        return packet;
    }

    /// <summary>Sends a packet and waits for the matching reply, retrying if none comes.</summary>
    private async Task<byte[]?> ExchangeAsync(byte[] packet, Func<byte[], bool> isReply)
    {
        await OneAtATime.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                Hid.HidD_FlushQueue(_handle);
                await _stream.WriteAsync(packet);

                using var timeout = new CancellationTokenSource(ReplyTimeout);
                try
                {
                    while (true)
                    {
                        var incoming = new byte[ReportLength];
                        var read = await _stream.ReadAsync(incoming, timeout.Token);
                        if (read > CommandAt && isReply(incoming)) return incoming;
                    }
                }
                catch (OperationCanceledException)
                {
                    // No answer in time; send again.
                }
            }

            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            // Unplugged or asleep, or this handle was disposed while the exchange waited its turn.
            return null;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }
}
