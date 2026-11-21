using System.IO.Pipes;
using System.Text;

namespace Tsume.Core.Ipc;

/// <summary>
/// The app's side of the helper pipe. Opens one short-lived connection per request,
/// so it recovers by itself if the helper restarts.
/// </summary>
public sealed class HelperClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Sends one request and waits for the reply. Never throws for an absent or
    /// misbehaving helper; it returns a failed reply instead.
    /// </summary>
    public Task<HelperReply> SendAsync(HelperRequest request, CancellationToken cancel = default) =>
        SendAsync(request, replyTimeout: null, cancel);

    public async Task<HelperReply> SendAsync(HelperRequest request, TimeSpan? replyTimeout, CancellationToken cancel = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", HelperPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(ConnectTimeout, cancel);

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
                ? HelperReply.Failure("The helper closed the connection without answering.")
                : HelperJson.ReadReply(line) ?? HelperReply.Failure("The helper sent an empty reply.");
        }
        catch (TimeoutException)
        {
            return HelperReply.Failure("The helper is not running.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return HelperReply.Failure(error.Message);
        }
    }
}
