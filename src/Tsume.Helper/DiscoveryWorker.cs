using System.Text.Json;
using Tsume.Core.Diagnostics;
using Tsume.Core.Ipc;
using Tsume.Hardware.Diagnostics;
using Tsume.Hardware.Msi;
using Tsume.Hardware.Msi.Diagnostics;

namespace Tsume.Helper;

/// <summary>
/// Runs hardware discovery scans and firmware verification checks on a background thread so <see cref="PipeServer"/>
/// returns immediately (&lt;5 ms) and never starves the panel's live readings or performance requests.
/// </summary>
internal static class DiscoveryWorker
{
    private static readonly JobSlot InventorySlot = new("GetHardwareInventory");
    private static readonly JobSlot StateSlot = new("GetHardwareState");
    private static readonly FirmwareCheckSlot FirmwareSlot = new();

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

    /// <summary>
    /// Held briefly by <see cref="ModeKeeper"/> and <see cref="ChargeKeeper"/> during restore checks so a mutating
    /// firmware check (<c>WriteCheck</c> / <c>ModeCheck</c>) never overlaps with an in-flight keeper restore.
    /// </summary>
    internal static readonly Lock KeeperGate = new();

    /// <summary>True while a mutating firmware check (<c>WriteCheck</c> or <c>ModeCheck</c>) owns the EC registers.</summary>
    public static bool IsMutatingFirmwareCheckRunning => FirmwareSlot.IsMutatingRunning;

    public static HelperReply HandleFirmwareCheck(RunFirmwareCheck request) =>
        FirmwareSlot.Handle(request.Kind, request.PollOnly);

    private sealed class FirmwareCheckSlot
    {
        private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(2);

        private readonly Lock _gate = new();
        private bool _running;
        private volatile bool _mutatingRunning;
        private FirmwareCheckKind _activeKind;
        private string? _report;
        private string? _error;
        private DateTime _completedUtc;

        public bool IsMutatingRunning => _mutatingRunning;

        public HelperReply Handle(FirmwareCheckKind kind, bool pollOnly)
        {
            lock (_gate)
            {
                if (_running)
                {
                    if (_activeKind == kind)
                        return new HelperReply { Ok = true, Pending = true };

                    return HelperReply.Failure(pollOnly
                        ? $"Firmware check ({kind}) has no active job."
                        : $"A firmware check ({_activeKind}) is already running.");
                }

                if ((_report is not null || _error is not null) && DateTime.UtcNow - _completedUtc > ResultLifetime)
                {
                    _report = null;
                    _error = null;
                }

                if (pollOnly)
                {
                    if (_activeKind != kind)
                        return HelperReply.Failure($"Firmware check ({kind}) has no active job.");

                    if (_report is not null)
                        return new HelperReply { Ok = true, Report = _report };

                    if (_error is not null)
                        return HelperReply.Failure(_error);

                    return HelperReply.Failure($"Firmware check ({kind}) has no active job.");
                }

                var isMutating = kind is FirmwareCheckKind.WriteCheck or FirmwareCheckKind.ModeCheck;
                _running = true;
                _mutatingRunning = isMutating;
                _activeKind = kind;
                _report = null;
                _error = null;

                _ = Task.Run(() =>
                {
                    Log.Write($"Firmware check ({kind}) started.");
                    try
                    {
                        // Wait for any already-in-flight ModeKeeper/ChargeKeeper restore to finish before touching EC registers.
                        if (isMutating) lock (KeeperGate) { }

                        using var acpi = MsiAcpiBridge.Open();
                        var text = kind switch
                        {
                            FirmwareCheckKind.Probe      => FirmwareProbe.Run(acpi),
                            FirmwareCheckKind.WriteCheck => WriteCheck.Run(acpi),
                            FirmwareCheckKind.ModeCheck  => ModeCheck.Run(acpi),
                            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
                        };

                        Log.Write($"Firmware check ({kind}) finished ({text.Length} chars).");
                        lock (_gate)
                        {
                            _report = text;
                            _completedUtc = DateTime.UtcNow;
                            _mutatingRunning = false;
                            _running = false;
                        }
                    }
                    catch (Exception error)
                    {
                        Log.Write($"Firmware check ({kind}) failed: {error.Message}");
                        lock (_gate)
                        {
                            _error = error.Message;
                            _completedUtc = DateTime.UtcNow;
                            _mutatingRunning = false;
                            _running = false;
                        }
                    }
                });

                return new HelperReply { Ok = true, Pending = true };
            }
        }
    }

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
