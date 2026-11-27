using System.Text;
using Tsume.Helper;

namespace Tsume.Tests;

public sealed class PipeServerTests
{
    private static async Task<(string? Line, bool TooLong)> ReadAsync(string payload)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await PipeServer.ReadLineAsync(reader, CancellationToken.None);
    }

    [Fact]
    public async Task ReadLineAsync_HandlesEmptyStream()
    {
        var (line, tooLong) = await ReadAsync("");
        Assert.Null(line);
        Assert.False(tooLong);
    }

    [Fact]
    public async Task ReadLineAsync_ReadsLfCrlfAndEofTerminatedLines()
    {
        var lf = await ReadAsync("{\"type\":\"ping\"}\n");
        Assert.Equal(("{\"type\":\"ping\"}", false), lf);

        var crlf = await ReadAsync("{\"type\":\"ping\"}\r\n");
        Assert.Equal(("{\"type\":\"ping\"}", false), crlf);

        var eof = await ReadAsync("{\"type\":\"ping\"}");
        Assert.Equal(("{\"type\":\"ping\"}", false), eof);

        var eofWithCr = await ReadAsync("{\"type\":\"ping\"}\r");
        Assert.Equal(("{\"type\":\"ping\"}", false), eofWithCr);
    }

    [Fact]
    public async Task ReadLineAsync_AcceptsExactMaximumLengthWithLfCrlfAndSplitCrlf()
    {
        var max = new string('a', PipeServer.LongestRequest);

        Assert.Equal((max, false), await ReadAsync(max + "\n"));
        Assert.Equal((max, false), await ReadAsync(max + "\r\n"));
        Assert.Equal((max, false), await ReadAsync(max));
        Assert.Equal((max, false), await ReadAsync(max + "\r"));

        // Force '\r' to land at the end of a read at LongestRequest + 1 and '\n' at index 0 of the next read.
        using var stream = new SplitBoundaryStream(Encoding.UTF8.GetBytes(max + "\r\n"), splitAt: PipeServer.LongestRequest + 1);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal((max, false), await PipeServer.ReadLineAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task ReadLineAsync_RejectsPayloadsExceedingMaximumLength()
    {
        var over = new string('a', PipeServer.LongestRequest + 1);
        var max = new string('a', PipeServer.LongestRequest);

        Assert.Equal((null, true), await ReadAsync(over + "\n"));
        Assert.Equal((null, true), await ReadAsync(over + "\r\n"));
        Assert.Equal((null, true), await ReadAsync(over));
        Assert.Equal((null, true), await ReadAsync(max + "\rx\n"));
        Assert.Equal((null, true), await ReadAsync(max + "\r\r\n"));

        // Split read where '\r' sits at LongestRequest + 1 and next read starts with a non-'\n' byte.
        using var stream = new SplitBoundaryStream(Encoding.UTF8.GetBytes(max + "\rx\n"), splitAt: PipeServer.LongestRequest + 1);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal((null, true), await PipeServer.ReadLineAsync(reader, CancellationToken.None));
    }

    /// <summary>Splits reads right at <paramref name="splitAt"/> so CRLF pairs can be tested across read boundaries.</summary>
    private sealed class SplitBoundaryStream(byte[] data, int splitAt) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        private int NextTake(int requested)
        {
            var limit = _offset < splitAt ? Math.Min(splitAt, data.Length) : data.Length;
            var remaining = limit - _offset;
            return remaining <= 0 ? 0 : Math.Min(remaining, requested);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = NextTake(count);
            if (take == 0) return 0;

            Buffer.BlockCopy(data, _offset, buffer, offset, take);
            _offset += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = NextTake(buffer.Length);
            if (take == 0) return ValueTask.FromResult(0);

            data.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
