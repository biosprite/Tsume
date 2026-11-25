using System.Text;

namespace Tsume.Hardware.Msi.Diagnostics;

/// <summary>
/// Rewrites each register Tsume ever changes with its own current value and reads it back. Nothing changes;
/// it shows whether the firmware accepts writes at all.
/// </summary>
public static class WriteCheck
{
    public static string Run(MsiAcpiBridge acpi)
    {
        var report = new StringBuilder();
        var passed = 0;

        if (acpi.Verified is not { } board)
            return report.AppendLine($"  {acpi.Machine} is not a verified device. Nothing was written.").ToString();

        var registers = board.Registers.Written.ToList();

        report.AppendLine("── Write check: each register is rewritten with its own value ──");
        report.AppendLine("  register                    before   write      after    verdict");

        foreach (var (name, register) in registers)
        {
            var before = acpi.Get("Get_Data", register);
            if (!before.Accepted)
            {
                report.AppendLine($"  0x{register:X2}  {name,-20}  --       skipped    --       could not read");
                continue;
            }

            var value = before.Data[0];
            FirmwareReply write;
            try
            {
                write = acpi.Set("Set_Data", register, [value]);
            }
            catch (FirmwareException refused)
            {
                report.AppendLine($"  Stopped: {refused.Message}");
                break;
            }

            var after = acpi.Get("Get_Data", register);

            var unchanged = after.Accepted && after.Data[0] == value;
            var ok = write.Accepted && unchanged;
            if (ok) passed++;

            report.AppendLine(
                $"  0x{register:X2}  {name,-20}  0x{value:X2}    {(write.Accepted ? "accepted" : "REFUSED "),-9}  " +
                $"{(after.Accepted ? $"0x{after.Data[0]:X2}" : "--  ")}     {(ok ? "ok" : unchanged ? "refused, value intact" : "VALUE CHANGED")}");
        }

        report.AppendLine().AppendLine($"  {passed} of {registers.Count} registers accepted a write.");
        return report.ToString();
    }
}
