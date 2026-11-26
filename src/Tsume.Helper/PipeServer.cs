using System.IO.Pipes;
using System.Runtime.InteropServices;
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

            var parsed = Parse(line);
            var reply = parsed is not null
                ? Answer(pipe, parsed)
                : HelperReply.Failure("The request could not be understood.");

            using var answering = CancellationTokenSource.CreateLinkedTokenSource(stop);
            answering.CancelAfter(parsed is GetHardwareInventory or GetHardwareState or RunFirmwareCheck ? TimeSpan.FromSeconds(15) : Patience);

            await writer.WriteLineAsync(HelperJson.Write(reply).AsMemory(), answering.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            Log.Write("A caller took too long and was dropped.");
        }
    }

    /// <summary>
    /// Answers one request. Starting the panel and reading the helper's log are handled here, not in
    /// <see cref="RequestHandler"/>: neither touches the device (both work on a machine Tsume does not know),
    /// and starting the panel must run in the caller's own session, read from the pipe — never named by the caller.
    /// </summary>
    private HelperReply Answer(NamedPipeServerStream pipe, HelperRequest request)
    {
        if (request is StartPanel start)
        {
            var session = CallerSession(pipe);
            if (session < 0) return HelperReply.Failure("Could not tell which session asked.");

            return PanelStarter.StartForRequest(session, start)
                ? new HelperReply { Ok = true }
                : HelperReply.Failure("The panel could not be started.");
        }

        if (request is GetLog)
            return new HelperReply { Ok = true, Log = Log.Read() ?? "" };

        if (request is SetRtssProfile rtss)
            return RtssProfileWriter.Apply(rtss);

        if (request is SetRtssExclusions exclusions)
            return RtssProfileWriter.SetExclusions(exclusions);

        if (request is StopRtss)
            return RtssProfileWriter.Stop();

        if (request is GetRtssStartup)
            return RtssProfileWriter.GetStartup(CallerUserSid(pipe));

        if (request is SetRtssStartup startup)
            return RtssProfileWriter.SetStartup(startup.Enabled, CallerUserSid(pipe));

        if (request is StartRtss)
            return RtssProfileWriter.Start(CallerSession(pipe));

        if (request is SetRtssOverlay overlay)
            return RtssOverlayWriter.Apply(overlay);

        if (request is GetHardwareInventory inv)
            return DiscoveryWorker.HandleInventory(inv);

        if (request is GetHardwareState state)
            return DiscoveryWorker.HandleState(state);

        if (request is RunFirmwareCheck check)
            return DiscoveryWorker.HandleFirmwareCheck(check);

        if (request is GetCpuBoost)
        {
            try
            {
                var enabled = Hardware.CpuBoostControl.Read();
                return enabled is { } active
                    ? new HelperReply { Ok = true, CpuBoostEnabled = active }
                    : HelperReply.Failure("CPU boost state could not be read.");
            }
            catch (Exception error)
            {
                Log.Write($"GetCpuBoost failed: {error.Message}");
                return HelperReply.Failure(error.Message);
            }
        }

        if (request is SetCpuBoost boost)
        {
            try
            {
                var result = Hardware.CpuBoostControl.Set(boost.Enabled);
                if (!result.Ok)
                    Log.Write($"SetCpuBoost({boost.Enabled}) failed: {result.Error}");

                return new HelperReply
                {
                    Ok = result.Ok,
                    Error = result.Error,
                    CpuBoostEnabled = result.Enabled,
                };
            }
            catch (Exception error)
            {
                Log.Write($"SetCpuBoost({boost.Enabled}) failed: {error.Message}");
                return HelperReply.Failure(error.Message);
            }
        }

        return handler.Handle(request);
    }

    /// <summary>The Windows session of whoever is on the other end of the pipe, or -1 if it cannot be read.</summary>
    private static int CallerSession(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var client)) return -1;
        return ProcessIdToSessionId(client, out var session) ? (int)session : -1;
    }

    /// <summary>The Windows user SID of whoever is on the other end of the pipe, or null if it cannot be read.</summary>
    private static string? CallerUserSid(NamedPipeServerStream pipe)
    {
        string? sid = null;
        try
        {
            pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }

        if (sid is not null) return sid;

        var session = CallerSession(pipe);
        return session >= 0 ? PanelStarter.UserSidForSession(session) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

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
