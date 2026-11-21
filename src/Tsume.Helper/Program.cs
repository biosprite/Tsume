using System.Text.Json;
using Tsume.Core.Diagnostics;
using Tsume.Hardware;
using Tsume.Hardware.Diagnostics;
using Tsume.Hardware.Msi;
using Tsume.Hardware.Msi.Diagnostics;
using Tsume.Helper;
using Tsume.Helper.Oem;

// Everything in Tsume that needs administrator rights. No arguments: serve hardware requests on the named pipe.
// --probe, --write-check and --mode-check write a report to a file and exit (MSI boards only).

switch (args)
{
    case []:
        using (var stop = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await ServeAsync(stop.Token);
        }
        return 0;

    case ["--service"]:
        System.ServiceProcess.ServiceBase.Run(new HelperService());
        return 0;

    // For tests: an unelevated script cannot stop an elevated helper, so it stops itself.
    case ["--serve-for", var text] when int.TryParse(text, out var seconds) && seconds is > 0 and <= 600:
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            await ServeAsync(stop.Token);
        return 0;

    case ["--probe", var outputFile]:
        using (var acpi = MsiAcpiBridge.Open())
            File.WriteAllText(outputFile, FirmwareProbe.Run(acpi));
        return 0;

    case ["--write-check", var outputFile]:
        using (var acpi = MsiAcpiBridge.Open())
            File.WriteAllText(outputFile, WriteCheck.Run(acpi));
        return 0;

    case ["--mode-check", var outputFile]:
        using (var acpi = MsiAcpiBridge.Open())
            File.WriteAllText(outputFile, ModeCheck.Run(acpi));
        return 0;

    case ["--msi-center", "pause"]:
        MsiCenterControl.Pause();
        return 0;

    case ["--msi-center", "resume"]:
        MsiCenterControl.Resume();
        return 0;

    case ["--hardware-inventory", var outputFile]:
        File.WriteAllText(outputFile, JsonSerializer.Serialize(
            HardwareDiscoveryCollector.CollectInventory(includeTimedLoad: true), DiscoveryJson.Indented));
        return 0;

    case ["--hardware-inventory-fast", var outputFile]:
        File.WriteAllText(outputFile, JsonSerializer.Serialize(
            HardwareDiscoveryCollector.CollectInventory(includeTimedLoad: false), DiscoveryJson.Indented));
        return 0;

    case ["--hardware-state", var outputFile]:
        File.WriteAllText(outputFile, JsonSerializer.Serialize(
            HardwareDiscoveryCollector.CollectFastState(), DiscoveryJson.Indented));
        return 0;

    default:
        Console.Error.WriteLine("Usage: Tsume.Helper [--serve-for <seconds> | --probe <file> | --write-check <file> | --mode-check <file> | --hardware-inventory <file> | --hardware-state <file> | --msi-center pause|resume]");
        return 64;
}

static async Task ServeAsync(CancellationToken stop)
{
    var (machine, found) = DeviceCatalog.Open();
    using var device = found;

    await new PipeServer(new RequestHandler(
        machine, device,
        device is null ? null : new ModeKeeper(device),
        device is null ? null : new ChargeKeeper(device))).RunAsync(stop);
}
