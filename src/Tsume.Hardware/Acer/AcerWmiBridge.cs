using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace Tsume.Hardware.Acer;

/// <summary>
/// Talks to Acer's <c>root\WMI</c> <c>AcerGamingFunction</c> and <c>BatteryControl</c> BIOS WMI classes.
/// Every call is serialized under <see cref="WmiCallGate"/> because AML <c>WMBH</c> acquires <c>MU01</c> without
/// an explicit release, and SMM calls enforce PredatorSense's 30 ms spacing.
/// </summary>
public sealed class AcerWmiBridge : IAcerWmi
{
    private const string Scope = @"root\WMI";
    private const int SmmThrottleMs = 30;

    /// <summary>Serializes all Acer WMI calls across both normal operation and hardware diagnostics.</summary>
    internal static readonly Lock WmiCallGate = new();

    private static long _lastSmmCallTicks;

    private readonly ManagementObject _gaming;
    private readonly ManagementObject? _battery;
    private bool _disposed;

    private AcerWmiBridge(ManagementObject gaming, ManagementObject? battery)
    {
        _gaming = gaming;
        _battery = battery;
    }

    /// <summary>
    /// Opens <c>AcerGamingFunction</c> (and optional <c>BatteryControl</c>) and verifies that
    /// <c>GetGamingSysInfo(0)</c> answers with status 0 before claiming the interface works.
    /// </summary>
    public static AcerWmiBridge? TryOpen()
    {
        var gaming = FirstInstance("AcerGamingFunction");
        if (gaming is null) return null;

        var battery = FirstInstance("BatteryControl");
        var bridge = new AcerWmiBridge(gaming, battery);

        if (bridge.GetGamingSysInfo(0x0000) is null)
        {
            bridge.Dispose();
            return null;
        }

        return bridge;
    }

    public ulong? GetGamingSysInfo(uint input) => InvokeGamingGet("GetGamingSysInfo", input, entersSmm: false);

    public bool SetGamingFanBehavior(ulong input) => InvokeGamingSet("SetGamingFanBehavior", input);

    public ulong? GetGamingFanBehavior(uint fanMask) => InvokeGamingGet("GetGamingFanBehavior", fanMask, entersSmm: true);

    public bool SetGamingFanSpeed(ulong input) => InvokeGamingSet("SetGamingFanSpeed", input);

    public ulong? GetGamingFanSpeed(uint fanId) => InvokeGamingGet("GetGamingFanSpeed", fanId, entersSmm: true);

    public bool? GetBatteryHealthControl()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_battery is null) return null;

        try
        {
            lock (WmiCallGate)
            {
                ThrottleSmmLocked();
                using var inParams = _battery.GetMethodParameters("GetBatteryHealthControlStatus");
                inParams["uBatteryNo"] = (byte)1;
                inParams["uFunctionQuery"] = (byte)1;
                inParams["uReserved"] = new byte[2];

                using var outParams = _battery.InvokeMethod("GetBatteryHealthControlStatus", inParams, null);
                MarkSmmCallLocked();
                if (outParams is null) return null;

                if (outParams["uReturn"] is byte[] { Length: > 0 } ret && ret[0] != 0)
                    return null;

                if (outParams["uFunctionStatus"] is byte[] { Length: > 0 } status)
                    return (status[0] & 0x01) != 0;

                return null;
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool SetBatteryHealthControl(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_battery is null) return false;

        try
        {
            lock (WmiCallGate)
            {
                ThrottleSmmLocked();
                using var inParams = _battery.GetMethodParameters("SetBatteryHealthControl");
                inParams["uBatteryNo"] = (byte)1;
                inParams["uFunctionMask"] = (byte)1;
                inParams["uFunctionStatus"] = enabled ? (byte)1 : (byte)0;
                inParams["uReservedIn"] = new byte[5];

                using var outParams = _battery.InvokeMethod("SetBatteryHealthControl", inParams, null);
                MarkSmmCallLocked();
                if (outParams is null) return false;

                return Convert.ToUInt16(outParams["uReturn"]) == 0;
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException)
        {
            return false;
        }
    }

    public string? ReadBiosVersion()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            using var results = search.Get();
            foreach (var row in results)
            {
                using (row)
                {
                    var text = $"{row["SMBIOSBIOSVersion"]}".Trim();
                    return text.Length > 0 ? text : null;
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private ulong? InvokeGamingGet(string method, uint input, bool entersSmm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            lock (WmiCallGate)
            {
                if (entersSmm) ThrottleSmmLocked();
                using var inParams = _gaming.GetMethodParameters(method);
                inParams["gmInput"] = input;
                using var outParams = _gaming.InvokeMethod(method, inParams, null);
                if (entersSmm) MarkSmmCallLocked();
                if (outParams is null) return null;

                var raw = Convert.ToUInt64(outParams["gmOutput"]);
                return (raw & 0xFF) == 0 ? raw : null;
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }

    private bool InvokeGamingSet(string method, ulong input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            lock (WmiCallGate)
            {
                ThrottleSmmLocked();
                using var inParams = _gaming.GetMethodParameters(method);
                inParams["gmInput"] = input;
                using var outParams = _gaming.InvokeMethod(method, inParams, null);
                MarkSmmCallLocked();
                if (outParams is null) return false;

                var raw = Convert.ToUInt64(outParams["gmOutput"]);
                return (raw & 0xFF) == 0;
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException)
        {
            return false;
        }
    }

    private static void ThrottleSmmLocked()
    {
        if (_lastSmmCallTicks == 0) return;
        var elapsedMs = Stopwatch.GetElapsedTime(_lastSmmCallTicks).TotalMilliseconds;
        var waitMs = (int)Math.Ceiling(SmmThrottleMs - elapsedMs);
        if (waitMs > 0) Thread.Sleep(waitMs);
    }

    private static void MarkSmmCallLocked() => _lastSmmCallTicks = Stopwatch.GetTimestamp();

    private static ManagementObject? FirstInstance(string className)
    {
        try
        {
            using var search = new ManagementObjectSearcher(Scope, $"SELECT * FROM {className}");
            using var results = search.Get();
            foreach (ManagementObject obj in results) return obj;
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gaming.Dispose();
        _battery?.Dispose();
    }
}
