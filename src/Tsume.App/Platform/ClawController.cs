using System.IO;
using Microsoft.Win32.SafeHandles;
using Tsume.App.Platform.Interop;
using Tsume.Core.Controller;
using Tsume.Core.Lighting;

namespace Tsume.App.Platform;

/// <summary>
/// The Claw gamepad's vendor HID channel (see <c>docs/hardware/controller.md</c>); needs no elevation.
/// Deliberately narrow: reads the profile and mode, writes only the lighting block, switches only between the two <see cref="PadMode"/> modes.
/// </summary>
public sealed class ClawController : IDisposable
{
    private const ushort VendorId = 0x0DB0;
    private static readonly ushort[] ProductIds = [0x1901, 0x1902];

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
        FirmwareVersion = $"{version >> 8:X}.{version & 0xFF:X2}";
    }

    /// <summary>"4.14" — the controller firmware, from the USB descriptor.</summary>
    public string FirmwareVersion { get; }

    /// <summary>
    /// Opens the Claw controller's vendor collection, or returns null if there is none.
    /// Recognised as MSI does: it accepts output report 0x0F, with 64-byte reports.
    /// </summary>
    public static ClawController? Find()
    {
        foreach (var path in Hid.DevicePaths())
        {
            if (!IsClaw(path)) continue;

            var handle = Hid.OpenReadWrite(path);
            if (handle is null) continue;

            var description = Hid.Describe(handle);
            if (description is { } d
                && d.OutputReports.Contains(ReportId)
                && d.Caps.OutputReportByteLength == ReportLength
                && d.Caps.InputReportByteLength == ReportLength)
            {
                return new ClawController(handle, Hid.Version(handle) ?? 0);
            }

            handle.Dispose();
        }

        return null;
    }

    internal static bool IsClaw(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains($"vid_{VendorId:x4}")
            && ProductIds.Any(id => lower.Contains($"pid_{id:x4}"));
    }

    /// <summary>Up to <see cref="MaxChunk"/> bytes of the profile, or null if the controller did not answer.</summary>
    public async Task<byte[]?> ReadProfileAsync(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, ProfileMap.Length);
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
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
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
    public async Task<bool> WriteLightingAsync(int offset, ReadOnlyMemory<byte> data)
    {
        // Safety guard: nothing outside the lighting block may ever be written.
        if (offset < ProfileMap.LightingStart || offset + data.Length > ProfileMap.LightingEnd)
            throw new ArgumentOutOfRangeException(nameof(offset), "Only the lighting block may be written.");

        return await WriteProfileChunkedAsync(offset, data);
    }

    /// <summary>
    /// Writes the 8-byte remap profile for M1 or M2 into the controller's working memory.
    /// The binding takes effect at once; call <see cref="SaveAsync"/> to persist to flash.
    /// </summary>
    public Task<bool> WriteBackButtonAsync(BackButton button, ReadOnlyMemory<byte> data)
    {
        // Safety guard: only the exact 8-byte M1 or M2 basic remap block may be written.
        if (data.Length != ProfileMap.BackButtonProfileLength)
            throw new ArgumentOutOfRangeException(nameof(data), $"Back-button profile must be {ProfileMap.BackButtonProfileLength} bytes.");

        var offset = button switch
        {
            BackButton.M1 => ProfileMap.M1ProfileStart,
            BackButton.M2 => ProfileMap.M2ProfileStart,
            _             => throw new ArgumentOutOfRangeException(nameof(button)),
        };

        return WriteProfileChunkedAsync(offset, data);
    }

    /// <summary>
    /// Ensures the controller's M-key function is set to custom remap/macro mode (0) rather than
    /// combination shortcuts (1) or disabled (2), leaving the active gamepad/cursor mode untouched.
    /// </summary>
    public async Task<bool> EnsureBackButtonsActiveAsync()
    {
        var current = await ReadModeAsync();
        if (current is not { } now) return false;
        if (now.MKeys == 0) return true;

        var padMode = now.Mode == (byte)PadMode.Cursor ? PadMode.Cursor : PadMode.Gamepad;
        return await SwitchModeAsync(padMode, mKeys: 0);
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
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
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
