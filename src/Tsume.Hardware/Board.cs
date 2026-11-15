using System.Management;

namespace Tsume.Hardware;

/// <summary>
/// Which machine this is, by its mainboard's maker and model. <see cref="DeviceCatalog"/> matches devices on it.
/// </summary>
public sealed record Board(string Maker, string Model)
{
    public static Board Detect()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            using var results = search.Get();

            foreach (var row in results)
                using (row)
                    return new Board($"{row["Manufacturer"]}".Trim(), $"{row["Product"]}".Trim());
        }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Falls through: an unnamed board is treated as unknown.
        }

        return Unnamed;
    }

    private static readonly Board Unnamed = new("unknown", "unknown");

    /// <summary>False when Windows would not say what the board is — as when WMI is not up yet, early in a boot.</summary>
    public bool IsNamed => this != Unnamed;

    public override string ToString() => $"{Maker} {Model}";
}
