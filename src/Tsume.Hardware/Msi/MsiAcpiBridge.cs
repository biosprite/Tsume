using System.Management;

namespace Tsume.Hardware.Msi;

/// <summary>
/// The only way into an MSI board's firmware: the <c>MSI_ACPI</c> WMI class. Every call sends and returns a
/// 32-byte package.
/// </summary>
/// <param name="machine">The board being talked to.</param>
/// <param name="verified">The board's map from <see cref="MsiBoards"/>, or null — and then only reads are allowed.</param>
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

    /// <summary>The machine's map from <see cref="MsiBoards"/>; null on a board Tsume does not know.</summary>
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

        if (!verified.CheckedOnDevice)
            RequireRegisterInterface();

        if (payload.Length > PackageSize - 1)
            throw new ArgumentException($"Payload is limited to {PackageSize - 1} bytes.", nameof(payload));

        var package = new byte[PackageSize];
        package[0] = selector;
        payload.CopyTo(package.AsSpan(1));
        return Invoke(method, package);
    }

    /// <summary>
    /// The WMI interface version <c>Get_WMI</c> reported, as major &lt;&lt; 8 | minor; -1 until it has answered.
    /// A definitive answer holds for the bridge's lifetime; a failed call is not remembered, so the next write asks again.
    /// </summary>
    private int _interfaceVersion = -1;

    /// <summary>
    /// MSI Center M uses the <c>Get_Data</c>/<c>Set_Data</c> register path only on WMI interface major version 2
    /// (<c>isNew_EC = WmiMajorVersion &gt; 1</c>; version 3 has a different method set). On a board never checked
    /// on a real unit, refuses every write unless the firmware reports exactly that.
    /// </summary>
    // Get_WMI reply (DSDT WMAM, MAJR/MINR): status, then data byte 0 = major, data byte 1 = minor.
    // Called before Set takes AcpiCallGate: Get takes the gate itself, so the two never nest. Callers may hold
    // MsiDevice's own lock; Get_WMI does not take that one.
    private void RequireRegisterInterface()
    {
        var version = Volatile.Read(ref _interfaceVersion);
        if (version < 0)
        {
            FirmwareReply reply;
            try
            {
                reply = Get("Get_WMI");
            }
            catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException)
            {
                throw new FirmwareException($"Tsume did not write to this device ({machine}): asking its firmware for the WMI interface version failed ({error.Message}).");
            }

            // Major 0 is not a version (an all-zero package can come back early in boot), so it is not remembered either.
            if (!reply.Accepted || reply.Data.Length == 0 || reply.Data[0] == 0)
                throw new FirmwareException($"Tsume did not write to this device ({machine}): its firmware did not report a WMI interface version.");

            version = (reply.Data[0] << 8) | (reply.Data.Length > 1 ? reply.Data[1] : 0);
            Volatile.Write(ref _interfaceVersion, version);
        }

        if (version >> 8 != 2)
            throw new FirmwareException(
                $"Tsume does not write to this device ({machine}): its firmware reports WMI interface version {version >> 8}.{version & 0xFF}, " +
                "and Tsume's register writes are only known to work on version 2.");
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
