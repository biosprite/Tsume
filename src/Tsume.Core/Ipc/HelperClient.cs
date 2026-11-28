using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Tsume.Core.Ipc;

/// <summary>
/// The app's side of the helper pipe. Opens one short-lived connection per request,
/// so it recovers by itself if the helper restarts.
/// </summary>
public sealed class HelperClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan StartPanelConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Sends one request and waits for the reply. Never throws for an absent or
    /// misbehaving helper; it returns a failed reply instead.
    /// </summary>
    public Task<HelperReply> SendAsync(HelperRequest request, CancellationToken cancel = default) =>
        SendAsync(request, replyTimeout: null, cancel);

    public async Task<HelperReply> SendAsync(HelperRequest request, TimeSpan? replyTimeout, CancellationToken cancel = default)
    {
        var connected = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", HelperPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);

            var connectTimeout = request is StartPanel ? StartPanelConnectTimeout : ConnectTimeout;
            await pipe.ConnectAsync(connectTimeout, cancel);
            connected = true;

            var effectiveTimeout = replyTimeout ?? request switch
            {
                GetHardwareInventory => TimeSpan.FromSeconds(35),
                GetHardwareState     => TimeSpan.FromSeconds(10),
                _                    => ReplyTimeout,
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(effectiveTimeout);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync(HelperJson.Write(request).AsMemory(), timeout.Token);
            var line = await reader.ReadLineAsync(timeout.Token);

            return line is null
                ? new HelperReply { Ok = false, Unreachable = true, Error = "The helper closed the connection without answering." }
                : HelperJson.ReadReply(line) ?? HelperReply.Failure("The helper sent an empty reply.");
        }
        catch (TimeoutException)
        {
            return new HelperReply { Ok = false, Unreachable = true, Error = "The helper is not running." };
        }
        catch (OperationCanceledException error) when (!cancel.IsCancellationRequested)
        {
            return new HelperReply { Ok = false, Unreachable = !connected, Error = error.Message };
        }
        catch (OperationCanceledException error)
        {
            return HelperReply.Failure(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HelperReply { Ok = false, Unreachable = !connected || error is IOException, Error = error.Message };
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            return HelperReply.Failure($"The helper sent an invalid reply: {error.Message}");
        }
    }
}
