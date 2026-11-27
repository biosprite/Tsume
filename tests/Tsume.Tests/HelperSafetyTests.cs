using System.Text;
using System.Text.Json;
using Tsume.Core.Ipc;

namespace Tsume.Tests;

public sealed class HelperSafetyTests
{
    // Matches PipeServer.LongestRequest (131,072 characters).
    private const int PipeLongestRequest = 131_072;

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not-json")]
    [InlineData("{\"op\":\"unknown.operation\"}")]
    [InlineData("{\"op\":123}")]
    [InlineData("[]")]
    public void ReadRequest_ThrowsJsonExceptionOnMalformedOrUnknownPayloads(string payload)
    {
        Assert.ThrowsAny<JsonException>(() => HelperJson.ReadRequest(payload));
    }

    [Fact]
    public void ReadRequest_ThrowsNotSupportedExceptionWhenDiscriminatorMissing()
    {
        Assert.Throws<NotSupportedException>(() => HelperJson.ReadRequest("{}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void ReadReply_ThrowsJsonExceptionOnMalformedPayloads(string payload)
    {
        Assert.ThrowsAny<JsonException>(() => HelperJson.ReadReply(payload));
    }

    [Fact]
    public void SetRtssExclusions_Large512ExecutablePayloadFitsWellUnderPipeLimitAndRoundTrips()
    {
        var executables = Enumerable.Range(0, 512)
            .Select(i => $"game_{i:D3}_anticheat_client_win64_shipping.exe")
            .ToArray();

        var request = new SetRtssExclusions(executables);
        var json = HelperJson.Write(request);
        var utf8Bytes = Encoding.UTF8.GetByteCount(json);

        Assert.True(json.Length < PipeLongestRequest, $"Expected JSON length ({json.Length}) < {PipeLongestRequest}.");
        Assert.True(utf8Bytes < PipeLongestRequest, $"Expected UTF-8 byte length ({utf8Bytes}) < {PipeLongestRequest}.");

        var roundTripped = Assert.IsType<SetRtssExclusions>(HelperJson.ReadRequest(json));
        Assert.Equal(512, roundTripped.Executables.Count);
        Assert.Equal(executables, roundTripped.Executables);
    }
}
