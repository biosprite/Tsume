using Tsume.Core.Diagnostics;

namespace Tsume.Tests;

public sealed class DiscoveryDiffEngineTests
{
    [Fact]
    public void Compute_IdenticalSnapshotsProduceZeroChanges()
    {
        var snap = new CombinedStateSnapshot
        {
            Elevated = new LiveHardwareState
            {
                FirmwareValues = new Dictionary<string, string> { ["mode"] = "Balanced" },
                EcMemoryHex = "01 02 03 04",
                PowerSchemeGuid = "scheme-a",
                PowerOverlayGuid = "overlay-a",
                OemRegistry = new Dictionary<string, string> { ["FanMode"] = "Auto" },
            },
            Hid = new HidTopologySnapshot
            {
                XInputActiveSlots = [0],
            },
        };

        var diff = DiscoveryDiffEngine.Compute("step-1", "Baseline", "guided", snap, snap);

        Assert.Equal(0, diff.NonVolatileChangeCount);
        Assert.Empty(diff.FirmwareChanges);
        Assert.Empty(diff.EcByteChanges);
        Assert.Empty(diff.PowerChanges);
        Assert.Empty(diff.RegistryChanges);
        Assert.Empty(diff.HidAdded);
        Assert.Empty(diff.HidRemoved);
    }

    [Fact]
    public void Compute_DetectsFirmwareEcPowerRegistryHidAndControllerDiffs()
    {
        var baseline = new CombinedStateSnapshot
        {
            Elevated = new LiveHardwareState
            {
                FirmwareValues = new Dictionary<string, string>
                {
                    ["mode"] = "Balanced",
                    ["temp"] = "55",
                },
                EcMemoryHex = "10-20-30-40",
                PowerSchemeGuid = "scheme-1",
                PowerOverlayGuid = "overlay-1",
                OemRegistry = new Dictionary<string, string> { ["FanMode"] = "Auto" },
            },
            Hid = new HidTopologySnapshot
            {
                XInputActiveSlots = [0],
                Collections =
                [
                    new HidCollectionInfo { VendorId = 0x0DB0, ProductId = 0x1901, UsagePage = 0xFF00, Usage = 0x0001 },
                ],
                VendorControllers =
                [
                    new VendorControllerSnapshot
                    {
                        Protocol = "msi",
                        VendorId = 0x0DB0,
                        ProductId = 0x1901,
                        ModeByte = 0x01,
                        MKeysByte = 0x00,
                        ProfileHex = "AA BB CC",
                    },
                ],
            },
        };

        var current = new CombinedStateSnapshot
        {
            Elevated = new LiveHardwareState
            {
                FirmwareValues = new Dictionary<string, string>
                {
                    ["mode"] = "Performance",
                    ["temp"] = "60",
                },
                EcMemoryHex = "10 25 30 41",
                PowerSchemeGuid = "scheme-2",
                PowerOverlayGuid = "overlay-1",
                OemRegistry = new Dictionary<string, string>
                {
                    ["FanMode"] = "Max",
                    ["NewKey"] = "1",
                },
            },
            Hid = new HidTopologySnapshot
            {
                XInputActiveSlots = [0, 1],
                Collections =
                [
                    new HidCollectionInfo { VendorId = 0x0DB0, ProductId = 0x1901, UsagePage = 0xFF00, Usage = 0x0001 },
                    new HidCollectionInfo { VendorId = 0x045E, ProductId = 0x028E, UsagePage = 0x0001, Usage = 0x0005 },
                ],
                VendorControllers =
                [
                    new VendorControllerSnapshot
                    {
                        Protocol = "msi",
                        VendorId = 0x0DB0,
                        ProductId = 0x1901,
                        ModeByte = 0x02,
                        MKeysByte = 0x00,
                        ProfileHex = "AA FF CC",
                    },
                ],
            },
        };

        var diff = DiscoveryDiffEngine.Compute(
            "step-perf",
            "Switch to Performance",
            "guided",
            baseline,
            current,
            volatileFirmwareKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "temp" },
            volatileEcOffsets: new HashSet<int> { 3 },
            userNote: "Pressed mode toggle");

        // Firmware: mode (non-volatile) + temp (volatile)
        Assert.Equal(2, diff.FirmwareChanges.Count);
        Assert.Single(diff.FirmwareChanges, c => c.Key == "mode" && !c.IsVolatile);
        Assert.Single(diff.FirmwareChanges, c => c.Key == "temp" && c.IsVolatile);

        // EC: offset 1 (0x20 -> 0x25, non-volatile) + offset 3 (0x40 -> 0x41, volatile)
        Assert.Equal(2, diff.EcByteChanges.Count);
        Assert.Single(diff.EcByteChanges, e => e.Offset == 1 && e.OffsetHex == "0x01" && e.Before == 0x20 && e.After == 0x25 && !e.IsVolatile);
        Assert.Single(diff.EcByteChanges, e => e.Offset == 3 && e.IsVolatile);

        // Power: PowerSchemeGuid changed
        Assert.Single(diff.PowerChanges);
        Assert.Equal("PowerSchemeGuid", diff.PowerChanges[0].Key);

        // Registry: FanMode changed, NewKey added
        Assert.Equal(2, diff.RegistryChanges.Count);

        // HID & XInput
        Assert.Single(diff.HidAdded);
        Assert.Empty(diff.HidRemoved);
        Assert.Equal("0", diff.XInputBefore);
        Assert.Equal("0,1", diff.XInputAfter);

        // Controller meta & profile byte
        Assert.Single(diff.ControllerMetaChanges);
        Assert.Equal("msi:0DB0:1901.mode", diff.ControllerMetaChanges[0].Key);
        Assert.Single(diff.ControllerProfileChanges);
        Assert.Equal(1, diff.ControllerProfileChanges[0].Offset);
        Assert.Equal(0xBB, diff.ControllerProfileChanges[0].Before);
        Assert.Equal(0xFF, diff.ControllerProfileChanges[0].After);

        // Non-volatile count: 1 fw + 1 ec + 1 power + 2 reg + 1 hid + 1 meta + 1 profile + 1 xinput = 9
        Assert.Equal(9, diff.NonVolatileChangeCount);
    }

    [Fact]
    public void BuildMarkdown_RendersCoreSections()
    {
        var bundle = new DeviceSupportBundle
        {
            TsumeVersion = "1.2",
            Inventory = new ElevatedInventory
            {
                Elevated = true,
                Identity = new SystemIdentityInfo
                {
                    SystemManufacturer = "MSI",
                    SystemModel = "Claw 8 AI+",
                },
            },
        };

        var markdown = DiscoveryReportBuilder.BuildMarkdown(bundle);

        Assert.Contains("# Tsume Device Support Report", markdown, StringComparison.Ordinal);
        Assert.Contains("## 1. System Identity", markdown, StringComparison.Ordinal);
        Assert.Contains("Claw 8 AI+", markdown, StringComparison.Ordinal);
    }
}
