using System.Reflection;
using Tsume.Upscaling;

namespace Tsume.Tests;

public sealed class GpuVendorTests
{
    private const uint VendorIntel = 0x8086;
    private const uint VendorAmd = 0x1002;
    private const uint VendorNvidia = 0x10DE;

    private static readonly MethodInfo ClassifyMethod = typeof(GpuVendor).GetMethod(
        "Classify",
        BindingFlags.Static | BindingFlags.NonPublic,
        binder: null,
        types: [typeof(uint), typeof(uint), typeof(string)],
        modifiers: null)
        ?? throw new InvalidOperationException("GpuVendor.Classify(uint, uint, string?) not found.");

    private static (bool Intel, bool IntelArc, bool IntelXmx, bool Amd, bool AmdRdna3Plus) Classify(
        uint vendorId, uint deviceId, string? description)
    {
        var boxed = ClassifyMethod.Invoke(null, [vendorId, deviceId, description])!;
        var type = boxed.GetType();
        return (
            (bool)type.GetProperty("Intel")!.GetValue(boxed)!,
            (bool)type.GetProperty("IntelArc")!.GetValue(boxed)!,
            (bool)type.GetProperty("IntelXmx")!.GetValue(boxed)!,
            (bool)type.GetProperty("Amd")!.GetValue(boxed)!,
            (bool)type.GetProperty("AmdRdna3Plus")!.GetValue(boxed)!);
    }

    [Theory]
    [InlineData(0x9A49u, "Intel(R) Iris(R) Xe Graphics", false, false)]
    [InlineData(0x46A6u, "Intel(R) Iris(R) Xe Graphics", false, false)]
    [InlineData(0x7D40u, "Intel(R) Graphics", false, false)]
    [InlineData(0x7D55u, "Intel(R) Arc(TM) Graphics", true, false)]
    [InlineData(0x7D55u, null, true, false)]
    [InlineData(0x6420u, "Intel(R) Arc(TM) 130V GPU", true, true)]
    [InlineData(0x64A0u, "Intel(R) Arc(TM) 140V GPU", true, true)]
    [InlineData(0x7D51u, "Intel(R) Arc(TM) 140T GPU", true, true)]
    [InlineData(0xB080u, "Intel(R) Arc(TM) B390 GPU", true, true)]
    [InlineData(0xB081u, "Intel(R) Arc(TM) B370 GPU", true, true)]
    [InlineData(0xB090u, "Intel(R) Graphics", true, true)]
    [InlineData(0xFFFFu, "Intel(R) Arc(TM) B580 Graphics", true, true)]
    [InlineData(0xFFFFu, "Intel(R) Arc(TM) Graphics", true, false)]
    [InlineData(0xFFFFu, "Intel(R) UHD Graphics", false, false)]
    public void Classify_IntelGpus(uint deviceId, string? description, bool expectedArc, bool expectedXmx)
    {
        var result = Classify(VendorIntel, deviceId, description);

        Assert.True(result.Intel);
        Assert.Equal(expectedArc, result.IntelArc);
        Assert.Equal(expectedXmx, result.IntelXmx);
        Assert.False(result.Amd);
        Assert.False(result.AmdRdna3Plus);
    }

    [Theory]
    [InlineData(0x15D8u, "AMD Radeon(TM) Vega 8 Graphics", false)]
    [InlineData(0x1681u, "AMD Radeon(TM) 680M", false)]
    [InlineData(0x163Fu, "AMD Radeon(TM) Graphics", false)]
    [InlineData(0x15BFu, "AMD Radeon(TM) 780M", true)]
    [InlineData(0x1900u, "AMD Radeon(TM) 780M", true)]
    [InlineData(0x150Eu, "AMD Radeon(TM) 890M", true)]
    [InlineData(0x1586u, "AMD Radeon(TM) 8060S", true)]
    [InlineData(0xFFFFu, "AMD Radeon(TM) 660M", false)]
    [InlineData(0xFFFFu, "AMD Radeon Vega 6", false)]
    [InlineData(0xFFFFu, "AMD Radeon 900M", true)]
    [InlineData(0xFFFFu, null, true)]
    public void Classify_AmdGpus(uint deviceId, string? description, bool expectedRdna3Plus)
    {
        var result = Classify(VendorAmd, deviceId, description);

        Assert.False(result.Intel);
        Assert.False(result.IntelArc);
        Assert.False(result.IntelXmx);
        Assert.True(result.Amd);
        Assert.Equal(expectedRdna3Plus, result.AmdRdna3Plus);
    }

    [Fact]
    public void Classify_OtherVendorReturnsAllFalse()
    {
        var result = Classify(VendorNvidia, 0x2684, "NVIDIA GeForce RTX 4090");

        Assert.False(result.Intel);
        Assert.False(result.IntelArc);
        Assert.False(result.IntelXmx);
        Assert.False(result.Amd);
        Assert.False(result.AmdRdna3Plus);
    }
}
