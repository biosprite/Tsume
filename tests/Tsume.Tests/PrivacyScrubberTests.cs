using Tsume.Core.Diagnostics;

namespace Tsume.Tests;

public sealed class PrivacyScrubberTests
{
    [Fact]
    public void ScrubText_RedactsUsersPathsAndHostIdentity()
    {
        Assert.Equal(string.Empty, PrivacyScrubber.ScrubText(null));
        Assert.Equal(string.Empty, PrivacyScrubber.ScrubText(""));

        var scrubbedPath = PrivacyScrubber.ScrubText(@"C:\Users\Alice\AppData\Local\Tsume\log.txt");
        Assert.Equal(@"C:\Users\<USER>\AppData\Local\Tsume\log.txt", scrubbedPath);

        if (Environment.MachineName.Length > 1)
        {
            var scrubbedHost = PrivacyScrubber.ScrubText($"Host is {Environment.MachineName} online");
            Assert.Equal("Host is <HOST> online", scrubbedHost);
        }
    }

    [Fact]
    public void ScrubDevicePath_RedactsInstanceTailButPreservesAcpiIds()
    {
        Assert.Equal(string.Empty, PrivacyScrubber.ScrubDevicePath(null));
        Assert.Equal(string.Empty, PrivacyScrubber.ScrubDevicePath("   "));

        var hid = @"\\?\hid#vid_0db0&pid_1901&mi_03&col02#8&1a2b3c4d&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(
            @"\\?\hid#vid_0db0&pid_1901&mi_03&col02#<INSTANCE>#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            PrivacyScrubber.ScrubDevicePath(hid));

        var pnp = @"USB\VID_0DB0&PID_1901\UNITSERIAL999";
        Assert.Equal(@"USB\VID_0DB0&PID_1901\<INSTANCE>", PrivacyScrubber.ScrubDevicePath(pnp));

        var acpi = @"ACPI\PNP0C14\0_0";
        Assert.Equal(acpi, PrivacyScrubber.ScrubDevicePath(acpi));
    }

    [Theory]
    [InlineData("FanMode", true)]
    [InlineData("ThermalProfile", true)]
    [InlineData("AccessToken", false)]
    [InlineData("UserPassword", false)]
    [InlineData("MachineGuid", false)]
    [InlineData("SerialNumber", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsSafeRegistryValueName_FiltersSensitiveNames(string name, bool expected)
    {
        Assert.Equal(expected, PrivacyScrubber.IsSafeRegistryValueName(name));
    }

    [Theory]
    [InlineData("FanCurve", true)]
    [InlineData("TdpLimit", true)]
    [InlineData("ChargeMode", true)]
    [InlineData("RgbBrightness", true)]
    [InlineData("WindowPosition", false)]
    [InlineData("FanSerialNumber", false)]
    [InlineData("ProfilePassword", false)]
    public void IsHardwareRelevantRegistryName_RequiresSafeAndRelevantName(string name, bool expected)
    {
        Assert.Equal(expected, PrivacyScrubber.IsHardwareRelevantRegistryName(name));
    }
}
