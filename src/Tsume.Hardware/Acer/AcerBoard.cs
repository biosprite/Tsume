using Tsume.Core.Devices;

namespace Tsume.Hardware.Acer;

/// <summary>
/// What differs between Acer handheld boards, as data: board/system identifiers, EC HID endpoint,
/// WMI fan IDs, and what the board offers. <see cref="AcerDevice"/> is the same code for all of them.
/// </summary>
public sealed record AcerBoard
{
    /// <summary>Baseboard product string (e.g. <c>"F900_PTG"</c>; on Atlas 8 <c>Win32_BaseBoard.Manufacturer</c> is <c>"PTL"</c>).</summary>
    public required string Model { get; init; }

    /// <summary>Fallback prefix matched against <c>Win32_ComputerSystem.Model</c> when manufacturer contains <c>"Acer"</c>.</summary>
    public string? SystemModelPrefix { get; init; }

    /// <summary>What the panel is told this machine can do.</summary>
    public required DeviceProfile Profile { get; init; }

    /// <summary>EC HID-over-I2C USB Vendor ID used for power mode and OC profile feature reports (<c>0x1025</c>).</summary>
    public ushort EcHidVendorId { get; init; } = 0x1025;

    /// <summary>EC HID-over-I2C Product ID (<c>0x174B</c> on Atlas 8).</summary>
    public ushort EcHidProductId { get; init; } = 0x174B;

    /// <summary>Top-level HID usage page carrying feature report <c>0xA0</c> (<c>0xFF05</c>).</summary>
    public ushort EcHidUsagePage { get; init; } = 0xFF05;

    /// <summary>
    /// WMI <c>AcerGamingFunction</c> fan IDs controlled by this board (e.g. <c>[2, 3]</c> for System Fan 1 &amp; 2 on Atlas 8).
    /// </summary>
    public IReadOnlyList<byte> FanIds { get; init; } = [2, 3];

    /// <summary>True only once verified end-to-end on a physical unit.</summary>
    public bool CheckedOnDevice { get; init; }
}
