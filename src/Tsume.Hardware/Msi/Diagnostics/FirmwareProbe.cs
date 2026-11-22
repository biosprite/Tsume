using System.Text;

namespace Tsume.Hardware.Msi.Diagnostics;

/// <summary>
/// A strictly read-only report of what this machine's firmware answers. Run with <c>Tsume.Helper --probe</c>.
/// </summary>
public static class FirmwareProbe
{
    /// <summary>Every sub-table the read methods are known to expose on WMI interface v2.</summary>
    private static readonly (string Method, byte[] Selectors)[] Tables =
    [
        ("Get_Fan",         [0, 1, 2, 3]),
        ("Get_Temperature", [0, 1, 2]),
        ("Get_Thermal",     [0, 1, 2, 3]),
        ("Get_AP",          [0, 1, 2, 3]),
        ("Get_Device",      [0, 1]),
        ("Get_Power",       [0, 1, 2, 3]),
    ];

    public static string Run(MsiAcpiBridge acpi)
    {
        var report = new StringBuilder();

        report.AppendLine("── Identity ─────────────────────────────────────────────");
        report.AppendLine($"  Board                {acpi.Machine}");
        Describe(report, "Get_WMI", acpi.Get("Get_WMI"));
        Describe(report, "Get_EC ", acpi.Get("Get_EC"), asText: true);

        report.AppendLine().AppendLine("── Registers Tsume uses ─────────────────────────────────");
        if (acpi.Verified is null)
            report.AppendLine($"  {acpi.Machine} has no verified map yet — the memory dump below is where one starts.");

        foreach (var (name, address) in acpi.Verified?.Registers.All ?? [])
        {
            var reply = acpi.Get("Get_Data", address);
            var value = reply.Accepted ? $"0x{reply.Data[0]:X2}  ({reply.Data[0]})" : "refused";
            report.AppendLine($"  0x{address:X2}  {name,-20} {value}");
        }

        report.AppendLine().AppendLine("── Tables ───────────────────────────────────────────────");
        foreach (var (method, selectors) in Tables)
            foreach (var selector in selectors)
                Describe(report, $"{method}({selector})", acpi.Get(method, selector));

        report.AppendLine().AppendLine("── Embedded-controller memory ───────────────────────────");
        DumpMemory(report, acpi);

        return report.ToString();
    }

    /// <summary>
    /// All 256 bytes of EC memory as a hex grid. Two dumps a few seconds apart show which bytes are live values.
    /// </summary>
    private static void DumpMemory(StringBuilder report, MsiAcpiBridge acpi)
    {
        report.AppendLine("        00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F");

        for (var row = 0; row < 16; row++)
        {
            report.Append($"  0x{row:X}0  ");

            for (var column = 0; column < 16; column++)
            {
                var reply = acpi.Get("Get_Data", (byte)(row * 16 + column));
                report.Append(reply.Accepted ? $"{reply.Data[0]:X2} " : "-- ");
                if (column == 7) report.Append(' ');
            }

            report.AppendLine();
        }
    }

    private static void Describe(StringBuilder report, string label, FirmwareReply reply, bool asText = false)
    {
        var status = reply.Accepted ? "ok     " : "refused";
        var bytes = Convert.ToHexString(reply.Data.AsSpan(0, 16)).Chunk(2).Select(pair => new string(pair));
        report.Append($"  {label,-20} {status}  {string.Join(' ', bytes)}");

        if (asText)
            report.Append($"   \"{Encoding.ASCII.GetString(reply.Data, 1, 28).TrimEnd('\0')}\"");

        report.AppendLine();
    }
}
