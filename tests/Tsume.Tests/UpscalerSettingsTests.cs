using Tsume.Upscaling.Config;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Xmx;

namespace Tsume.Tests;

public sealed class UpscalerSettingsTests
{
    [Theory]
    [InlineData(true, false, true, UpscalerChoice.XeSS)]
    [InlineData(false, true, true, UpscalerChoice.Fsr4)]
    [InlineData(false, true, false, UpscalerChoice.Fsr3)]
    [InlineData(false, false, false, UpscalerChoice.Auto)]
    public void DefaultUpscaler_SelectsExpectedBackendForGpu(
        bool hasIntel, bool hasAmd, bool hasAmdRdna3Plus, UpscalerChoice expected)
    {
        Assert.Equal(expected, UpscalerSettings.DefaultUpscaler(hasIntel, hasAmd, hasAmdRdna3Plus));
    }

    [Theory]
    [InlineData("WINMM.DLL", "winmm.dll")]
    [InlineData("optiscaler.asi", ProxyNames.AsiName)]
    [InlineData("unknown.dll", ProxyNames.Default)]
    [InlineData("", ProxyNames.Default)]
    public void Proxy_NormalizesOnInit(string input, string expected)
    {
        var settings = new UpscalerSettings { Proxy = input };
        Assert.Equal(expected, settings.Proxy);
    }

    [Fact]
    public void Fitting_RejectsXmxWithoutHardwareOrArchiveAndClampsMultiplier()
    {
        var xmxSettings = new UpscalerSettings
        {
            Upscaler = UpscalerChoice.Fsr4Xmx,
            Model = Fsr4Model.Lite,
            OptiPatcher = true,
            Multiplier = 4,
        };

        var noXmxGpu = xmxSettings.Fitting(hasXmx: true, hasOptiPatcher: false, hasIntelArcGpu: false, hasIntelXmxGpu: false);
        Assert.NotEqual(UpscalerChoice.Fsr4Xmx, noXmxGpu.Upscaler);
        Assert.Equal(Fsr4Model.Int8, noXmxGpu.Model);
        Assert.False(noXmxGpu.OptiPatcher);
        Assert.Equal(2, noXmxGpu.Multiplier);

        var missingArchive = xmxSettings.Fitting(hasXmx: false, hasOptiPatcher: true, hasIntelArcGpu: true, hasIntelXmxGpu: true);
        Assert.Equal(UpscalerChoice.Fsr4, missingArchive.Upscaler);
        Assert.Equal(Fsr4Model.Int8, missingArchive.Model);
        Assert.True(missingArchive.OptiPatcher);
        Assert.Equal(4, missingArchive.Multiplier);

        var supported = xmxSettings.Fitting(hasXmx: true, hasOptiPatcher: true, hasIntelArcGpu: true, hasIntelXmxGpu: true);
        Assert.Equal(UpscalerChoice.Fsr4Xmx, supported.Upscaler);
        Assert.Equal(Fsr4Model.Lite, supported.Model);
        Assert.True(supported.OptiPatcher);
        Assert.Equal(4, supported.Multiplier);
    }

    [Fact]
    public void Fitting_PinsPlainFsr4ToInt8()
    {
        var settings = new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Lite };
        var fitted = settings.Fitting(hasXmx: false, hasOptiPatcher: false, hasIntelArcGpu: false, hasIntelXmxGpu: false);
        Assert.Equal(Fsr4Model.Int8, fitted.Model);
    }

    [Fact]
    public void PreservingUnavailable_RestoresRememberedXmxAndOptiPatcher()
    {
        var previous = new UpscalerSettings
        {
            Upscaler = UpscalerChoice.Fsr4Xmx,
            Model = Fsr4Model.Lite,
            OptiPatcher = true,
        };

        var edited = new UpscalerSettings
        {
            Upscaler = UpscalerChoice.Fsr4,
            Model = Fsr4Model.Int8,
            OptiPatcher = false,
            SpoofNvidia = false,
        };

        var preserved = edited.PreservingUnavailable(previous, hasXmx: false, hasOptiPatcher: false, hasIntelXmxGpu: true);

        Assert.Equal(UpscalerChoice.Fsr4Xmx, preserved.Upscaler);
        Assert.Equal(Fsr4Model.Lite, preserved.Model);
        Assert.True(preserved.OptiPatcher);
        Assert.False(preserved.SpoofNvidia);
    }

    [Fact]
    public void WriteToAndCorrectedBy_RoundTripFsr4AndXeFgSettings()
    {
        var settings = new UpscalerSettings
        {
            Upscaler = UpscalerChoice.Fsr4,
            Model = Fsr4Model.Int8,
            FrameGen = FrameGenChoice.XeFG,
            Source = FrameGenSource.Upscaler,
            Multiplier = 3,
            SpoofNvidia = true,
            OptiPatcher = true,
        };

        var ini = IniFile.Parse("");
        settings.WriteTo(ini, ueIntelAtomics: true, hasOptiPatcher: true, keepOtherAsiPlugins: false, hasIntelArcGpu: true);

        Assert.Equal("fsr31", ini.Get("Upscalers", "Dx11Upscaler"));
        Assert.Equal("ffx", ini.Get("Upscalers", "Dx12Upscaler"));
        Assert.Equal("ffx", ini.Get("Upscalers", "VulkanUpscaler"));
        Assert.Equal("0", ini.Get("FSR", "UpscalerIndex"));
        Assert.Equal("2", ini.Get("FSR", "Fsr4GpuSupport"));
        Assert.Equal("1", ini.Get("FSR", "Fsr4Model"));
        Assert.Equal("true", ini.Get("FrameGen", "Enabled"));
        Assert.Equal("xefg", ini.Get("FrameGen", "FGOutput"));
        Assert.Equal("upscaler", ini.Get("FrameGen", "FGInput"));
        Assert.Equal("false", ini.Get("Hotfix", "DisableOverlays"));
        Assert.Equal("2", ini.Get("XeFG", "InterpolationCount"));
        Assert.Equal("auto", ini.Get("Spoofing", "Dxgi"));
        Assert.Equal("true", ini.Get("Spoofing", "Vulkan"));
        Assert.Equal("true", ini.Get("Spoofing", "VulkanExtensionSpoofing"));
        Assert.Equal("true", ini.Get("Spoofing", "UEIntelAtomics"));
        Assert.Equal("true", ini.Get("Plugins", "LoadAsiPlugins"));

        var roundTripped = new UpscalerSettings().CorrectedBy(ini, hasOptiPatcher: true);
        Assert.Equal(settings.Upscaler, roundTripped.Upscaler);
        Assert.Equal(settings.Model, roundTripped.Model);
        Assert.Equal(settings.FrameGen, roundTripped.FrameGen);
        Assert.Equal(settings.Source, roundTripped.Source);
        Assert.Equal(settings.Multiplier, roundTripped.Multiplier);
        Assert.Equal(settings.SpoofNvidia, roundTripped.SpoofNvidia);
        Assert.Equal(settings.OptiPatcher, roundTripped.OptiPatcher);
    }

    [Fact]
    public void WriteToAndCorrectedBy_HandlesFsr4XmxAndDlssgOverlayVariants()
    {
        var ini = IniFile.Parse("""
            [FrameGen]
            FGInput=nvngxfg
            [Plugins]
            LoadAsiPlugins=true
            """);

        var settings = new UpscalerSettings
        {
            Upscaler = UpscalerChoice.Fsr4Xmx,
            Model = Fsr4Model.Lite,
            FrameGen = FrameGenChoice.FsrFG,
            Source = FrameGenSource.Dlssg,
            SpoofNvidia = false,
            OptiPatcher = false,
        };

        settings.WriteTo(ini, hasOptiPatcher: true, keepOtherAsiPlugins: true, hasIntelArcGpu: true);

        Assert.Equal("xess", ini.Get("Upscalers", "Dx11Upscaler"));
        Assert.Equal(XmxLayout.Name, ini.Get("Upscalers", "Dx12Upscaler"));
        Assert.Equal("3", ini.Get(XmxLayout.Name, "UpscalerIndex"));
        Assert.Equal("false", ini.Get("Inputs", "EnableXeSSInputs"));
        Assert.Equal("nvngxfg", ini.Get("FrameGen", "FGInput"));
        Assert.Equal("true", ini.Get("FrameGen", "Enabled"));
        Assert.Equal("false", ini.Get("Spoofing", "Dxgi"));
        // keepOtherAsiPlugins: true leaves LoadAsiPlugins intact when OptiPatcher is false.
        Assert.Equal("true", ini.Get("Plugins", "LoadAsiPlugins"));

        var readBack = new UpscalerSettings().CorrectedBy(ini, hasOptiPatcher: false);
        Assert.Equal(UpscalerChoice.Fsr4Xmx, readBack.Upscaler);
        Assert.Equal(Fsr4Model.Lite, readBack.Model);
        Assert.Equal(FrameGenChoice.FsrFG, readBack.FrameGen);
        Assert.Equal(FrameGenSource.Dlssg, readBack.Source);
        Assert.False(readBack.SpoofNvidia);
        Assert.False(readBack.OptiPatcher);
    }

    [Fact]
    public void WriteTo_UsesXess12ForDx11OnNonArcGpu()
    {
        var ini = IniFile.Parse("");
        var settings = new UpscalerSettings { Upscaler = UpscalerChoice.XeSS };

        settings.WriteTo(ini, hasIntelArcGpu: false);

        Assert.Equal("xess_12", ini.Get("Upscalers", "Dx11Upscaler"));
        Assert.Equal("xess", ini.Get("Upscalers", "Dx12Upscaler"));
    }
}
