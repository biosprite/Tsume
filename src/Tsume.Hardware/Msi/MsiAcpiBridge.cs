using System.Management;

namespace Tsume.Hardware.Msi;

/// <summary>
/// The only way into an MSI board's firmware: the <c>MSI_ACPI</c> WMI class. Every call sends and returns a
/// 32-byte package.
/// </summary>
/// <param name="machine">The board being talked to.</param>
/// <param name="verified">The board's checked map, or null — and then only reads are allowed.</param>
// request   [0] selector   [1..31] payload
// response  [0] status     [1..31] data        (status 0 = firmware refused)
// Selector: an EC register address for Get_Data/Set_Data; a sub-table index for Get_Fan and the like.
public sealed class MsiAcpiBridge(Board machine, MsiBoard? verified) : IMsiFirmware
{
    private const string Scope = @"root\WMI";
    private const string Instance = @"MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'";
    private const string PackageClass = "Package_32";
    private const int PackageSize = 32;

    /// <summary>Serialises all <c>MSI_ACPI</c> WMI calls process-wide so device control and read-only discovery never overlap.</summary>
    internal static readonly Lock AcpiCallGate = new();

    private ManagementObject _acpi = new(Scope, Instance, null);

    /// <summary>Opens the bridge on this machine, verified or not: reads are allowed anywhere so the probe can learn a new board.</summary>
    public static MsiAcpiBridge Open()
    {
        var machine = Board.Detect();
        return new MsiAcpiBridge(machine, MsiBoards.Find(machine));
    }

    public Board Machine => machine;

    /// <summary>The machine's checked map; null on a board Tsume does not know.</summary>
    public MsiBoard? Verified => verified;

    public FirmwareReply Get(string method) => Invoke(method, package: null);

    public FirmwareReply Get(string method, byte selector)
    {
        var package = new byte[PackageSize];
        package[0] = selector;
        return Invoke(method, package);
    }

    public FirmwareReply Set(string method, byte selector, ReadOnlySpan<byte> payload)
    {
        // Every write to an MSI board passes through here, so this is where an unverified machine is refused.
        if (verified is null)
            throw new FirmwareException($"Tsume does not write to this device ({machine}): its firmware has not been verified.");

        if (payload.Length > PackageSize - 1)
            throw new ArgumentException($"Payload is limited to {PackageSize - 1} bytes.", nameof(payload));

        var package = new byte[PackageSize];
        package[0] = selector;
        payload.CopyTo(package.AsSpan(1));
        return Invoke(method, package);
    }

    private FirmwareReply Invoke(string method, byte[]? package)
    {
        lock (AcpiCallGate)
        {
            try
            {
                return InvokeCore(method, package);
            }
            catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException)
            {
                // WMI proxies can go stale after hibernation or a Winmgmt restart; reconnect once before failing.
                ReconnectLocked();
                return InvokeCore(method, package);
            }
        }
    }

    private FirmwareReply InvokeCore(string method, byte[]? package)
    {
        using var output = package is null
            ? _acpi.InvokeMethod(method, null, null)
            : InvokeWithPackage(method, package);

        return output?["Data"] is ManagementBaseObject { } data && data["Bytes"] is byte[] { Length: > 1 } bytes
            ? new FirmwareReply(Accepted: bytes[0] != 0, Data: bytes[1..])
            : FirmwareReply.Empty;
    }

    private void ReconnectLocked()
    {
        try { _acpi.Dispose(); }
        catch (Exception) { }
        _acpi = new ManagementObject(Scope, Instance, null);
    }

    private ManagementBaseObject InvokeWithPackage(string method, byte[] package)
    {
        using var input = _acpi.GetMethodParameters(method);
        using var packageClass = new ManagementClass(Scope, PackageClass, null);
        using var instance = packageClass.CreateInstance();

        instance["Bytes"] = package;
        input["Data"] = instance;

        return _acpi.InvokeMethod(method, input, null);
    }

    public void Dispose()
    {
        lock (AcpiCallGate) _acpi.Dispose();
    }
}
