using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Tsume.Core.Ipc;

namespace Tsume.Helper;

/// <summary>
/// Listens on the helper pipe and answers one request per connection, strictly one at a time:
/// firmware calls must not overlap.
/// </summary>
public sealed class PipeServer(RequestHandler handler)
{
    private static readonly TimeSpan NameTakenPause = TimeSpan.FromSeconds(5);

    /// <summary>How long a caller may take to send its request, and again to read the answer.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>In characters. Real requests are a few hundred.</summary>
    private const int LongestRequest = 4096;

    public async Task RunAsync(CancellationToken stop)
    {
        Log.Write("Helper started.");

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stop);
                await AnswerAsync(pipe, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException error)
            {
                // The client went away mid-conversation; carry on with the next one.
                Log.Write($"Connection dropped: {error.Message}");
            }
            catch (UnauthorizedAccessException)
            {
                // Another program holds a pipe with our name. Never share it: wait for the name to come free.
                Log.Write("The pipe name is taken by another program. Waiting.");
                try { await Task.Delay(NameTakenPause, stop); }
                catch (OperationCanceledException) { break; }
            }
        }

        Log.Write("Helper stopped.");
    }

    private async Task AnswerAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            // A caller that connects and then says nothing, or never reads the answer, must not hold up the rest.
            using var asking = CancellationTokenSource.CreateLinkedTokenSource(stop);
            asking.CancelAfter(Patience);

            var line = await ReadLineAsync(reader, asking.Token);
            if (line is null) return;

            var reply = Parse(line) is { } request
                ? handler.Handle(request)
                : HelperReply.Failure("The request could not be understood.");

            using var answering = CancellationTokenSource.CreateLinkedTokenSource(stop);
            answering.CancelAfter(Patience);

            await writer.WriteLineAsync(HelperJson.Write(reply).AsMemory(), answering.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            Log.Write("A caller took too long and was dropped.");
        }
    }

    /// <summary>One line of at most <see cref="LongestRequest"/> characters; null if the caller sent nothing or too much.</summary>
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancel)
    {
        var line = new StringBuilder();
        var buffer = new char[256];

        while (line.Length <= LongestRequest)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancel);
            if (read == 0) return line.Length > 0 ? line.ToString() : null;

            var end = Array.IndexOf(buffer, '\n', 0, read);
            if (end >= 0) return line.Append(buffer, 0, end).ToString().TrimEnd('\r');

            line.Append(buffer, 0, read);
        }

        return null;
    }

    private static HelperRequest? Parse(string line)
    {
        try { return HelperJson.ReadRequest(line); }
        catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// Creates the pipe with an explicit ACL: as a service, anyone signed in at the screen may talk to it;
    /// started by hand, only the person who started it. Network callers are denied.
    /// </summary>
    // FirstPipeInstance + a single instance: no other program can open a second server end and pose as the helper.
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent();

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var atTheScreen = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        var caller = me.IsSystem ? atTheScreen : me.User!;

        security.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(caller, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!me.IsSystem)
            security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            HelperPipe.Name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }
}
