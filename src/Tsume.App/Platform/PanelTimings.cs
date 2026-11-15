using Microsoft.Win32;

namespace Tsume.App.Platform;

/// <summary>One way of driving a screen: its pixel clock and the size of a whole frame, blanking included.</summary>
public readonly record struct PanelTiming(uint Width, uint Height, uint TotalWidth, uint TotalHeight, uint PixelClock)
{
    public uint PixelsPerFrame => TotalWidth * TotalHeight;

    public double Hertz => (double)PixelClock / PixelsPerFrame;

    /// <summary>The rate as people say it: 60, not 60.001.</summary>
    public int Rate => (int)Math.Round(Hertz);
}

/// <summary>The detailed timings a screen declares in its EDID: the rates it can really be driven at.</summary>
public static class PanelTimings
{
    private const int BlockSize = 128;
    private const int TimingSize = 18;

    /// <summary>Byte offsets of the four timing slots in the base block.</summary>
    private static readonly int[] BaseSlots = [54, 72, 90, 108];

    private const byte ExtensionWithTimings = 0x02;   // CTA-861
    private const byte Interlaced = 0x80;

    /// <param name="devicePath">As Windows names the monitor: <c>\\?\DISPLAY#CSW0801#4&amp;1de9…#{…}</c>.</param>
    public static IReadOnlyList<PanelTiming> Of(string devicePath) =>
        ReadEdid(devicePath) is { } edid ? Parse(edid) : [];

    public static IReadOnlyList<PanelTiming> Parse(ReadOnlySpan<byte> edid)
    {
        var timings = new List<PanelTiming>();
        if (edid.Length < BlockSize) return timings;

        foreach (var slot in BaseSlots) Add(edid.Slice(slot, TimingSize));

        for (var block = BlockSize; block + BlockSize <= edid.Length; block += BlockSize)
        {
            var extension = edid.Slice(block, BlockSize);
            if (extension[0] != ExtensionWithTimings || extension[2] < 4) continue;

            // Timings run from the offset in byte 2 up to the checksum; a zero clock ends them.
            for (var at = (int)extension[2]; at + TimingSize <= BlockSize - 1; at += TimingSize)
            {
                if (!Add(extension.Slice(at, TimingSize))) break;
            }
        }

        return timings;

        bool Add(ReadOnlySpan<byte> t)
        {
            var clock = (uint)(t[0] | t[1] << 8) * 10_000;   // stored in units of 10 kHz
            if (clock == 0) return false;                    // a text or range descriptor, not a timing
            if ((t[17] & Interlaced) != 0) return true;

            var width = (uint)(t[2] | (t[4] >> 4) << 8);
            var blankWidth = (uint)(t[3] | (t[4] & 0x0F) << 8);
            var height = (uint)(t[5] | (t[7] >> 4) << 8);
            var blankHeight = (uint)(t[6] | (t[7] & 0x0F) << 8);

            if (width > 0 && height > 0)
                timings.Add(new PanelTiming(width, height, width + blankWidth, height + blankHeight, clock));

            return true;
        }
    }

    /// <summary>The monitor's EDID from the registry. An EDID_OVERRIDE (one value per block, named 0, 1, …) wins, as it does in Windows.</summary>
    private static byte[]? ReadEdid(string devicePath)
    {
        // \\?\DISPLAY#<model>#<instance>#{interface}
        if (devicePath.Split('#') is not [_, var model, var instance, ..]) return null;

        try
        {
            using var parameters = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{model}\{instance}\Device Parameters");
            if (parameters is null) return null;

            using var replaced = parameters.OpenSubKey("EDID_OVERRIDE");
            if (replaced?.GetValue("0") is byte[] first)
            {
                var blocks = new List<byte>(first);
                for (var index = 1; replaced.GetValue(index.ToString()) is byte[] next; index++) blocks.AddRange(next);
                return [.. blocks];
            }

            return parameters.GetValue("EDID") as byte[];
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }
}
