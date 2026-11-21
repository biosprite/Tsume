using System.Text.Json;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;
using Tsume.Hardware.Diagnostics;

namespace Tsume.Helper;

/// <summary>
/// Runs strictly read-only hardware discovery scans on a background thread so <see cref="PipeServer"/>
/// returns immediately (&lt;5 ms) and never starves the panel's live readings or performance requests.
/// </summary>
internal static class DiscoveryWorker
{
    private static readonly JobSlot InventorySlot = new("GetHardwareInventory");
    private static readonly JobSlot StateSlot = new("GetHardwareState");

    public static HelperReply HandleInventory(GetHardwareInventory request) =>
        InventorySlot.Handle(
            pollOnly: request.PollOnly,
            run: () => JsonSerializer.Serialize(
                HardwareDiscoveryCollector.CollectInventory(request.IncludeTimedLoad),
                DiscoveryJson.Compact));

    public static HelperReply HandleState(GetHardwareState request) =>
        StateSlot.Handle(
            pollOnly: request.PollOnly,
            run: () => JsonSerializer.Serialize(
                HardwareDiscoveryCollector.CollectFastState(),
                DiscoveryJson.Compact));

    private sealed class JobSlot(string name)
    {
        private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(2);

        private readonly Lock _gate = new();
        private bool _running;
        private string? _payload;
        private string? _error;
        private DateTime _completedUtc;

        public HelperReply Handle(bool pollOnly, Func<string> run)
        {
            lock (_gate)
            {
                if (_running)
                    return new HelperReply { Ok = true, Pending = true };

                if ((_payload is not null || _error is not null) && DateTime.UtcNow - _completedUtc > ResultLifetime)
                {
                    _payload = null;
                    _error = null;
                }

                if (pollOnly)
                {
                    if (_payload is not null)
                        return new HelperReply { Ok = true, PayloadJson = _payload };

                    if (_error is not null)
                        return HelperReply.Failure(_error);

                    return HelperReply.Failure($"{name} has no active job.");
                }

                // Start a fresh background scan.
                _running = true;
                _payload = null;
                _error = null;

                _ = Task.Run(() =>
                {
                    try
                    {
                        var json = run();
                        lock (_gate)
                        {
                            _payload = json;
                            _completedUtc = DateTime.UtcNow;
                            _running = false;
                        }
                    }
                    catch (Exception error)
                    {
                        Log.Write($"{name} failed: {error.Message}");
                        lock (_gate)
                        {
                            _error = error.Message;
                            _completedUtc = DateTime.UtcNow;
                            _running = false;
                        }
                    }
                });

                return new HelperReply { Ok = true, Pending = true };
            }
        }
    }
}
