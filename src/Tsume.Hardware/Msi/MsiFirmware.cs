namespace Tsume.Hardware.Msi;

/// <summary>
/// The calls an MSI board's firmware answers. The real one is <see cref="MsiAcpiBridge"/>; the interface lets
/// <see cref="MsiDevice"/>'s writes be tested against a fake, without administrator rights or a real machine.
/// </summary>
public interface IMsiFirmware : IDisposable
{
    /// <summary>Calls a method that takes no selector, such as <c>Get_WMI</c> or <c>Get_EC</c>.</summary>
    FirmwareReply Get(string method);

    /// <summary>Calls a <c>Get_*</c> method with a selector.</summary>
    FirmwareReply Get(string method, byte selector);

    /// <summary>Calls a <c>Set_*</c> method with a selector and payload.</summary>
    FirmwareReply Set(string method, byte selector, ReadOnlySpan<byte> payload);
}

/// <summary>What the firmware answered. <see cref="Data"/> excludes the status byte.</summary>
public readonly record struct FirmwareReply(bool Accepted, byte[] Data)
{
    public static FirmwareReply Empty { get; } = new(Accepted: false, Data: new byte[31]);
}
