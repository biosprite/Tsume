using Tsume.App.Platform;
using Tsume.Upscaling;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
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
            FrameRateLimit = 60,
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
        Assert.Equal("60", ini.Get("Framerate", "FramerateLimit"));
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
        Assert.Equal(settings.FrameRateLimit, roundTripped.FrameRateLimit);
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

    [Theory]
    [InlineData("auto", 0)]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    [InlineData("60", 60)]
    [InlineData("60.0", 60)]
    [InlineData("58.9", 58.9)]
    [InlineData("115.8", 115.8)]
    [InlineData("59.64", 59.6)]
    [InlineData("5", UpscalerSettings.MinFrameRateLimit)]
    [InlineData("500", UpscalerSettings.MaxFrameRateLimit)]
    [InlineData("1e20", UpscalerSettings.MaxFrameRateLimit)]
    [InlineData("Infinity", 0)]
    [InlineData("-Infinity", 0)]
    [InlineData("NaN", 0)]
    public void CorrectedBy_ParsesAndClampsFrameRateLimit(string raw, double expected)
    {
        var ini = IniFile.Parse($"[Framerate]\nFramerateLimit={raw}\n");
        var read = new UpscalerSettings().CorrectedBy(ini);
        Assert.Equal(expected, read.FrameRateLimit);
    }

    [Fact]
    public void WriteTo_PreservesOneDecimalForFractionalFrameRateLimit()
    {
        var ini = IniFile.Parse("");
        var settings = new UpscalerSettings { FrameRateLimit = 115.8 };

        settings.WriteTo(ini);

        Assert.Equal("115.8", ini.Get("Framerate", "FramerateLimit"));
        Assert.Equal(115.8, new UpscalerSettings().CorrectedBy(ini).FrameRateLimit);
    }

    [Fact]
    public void WriteTo_WritesAutoWhenFrameRateLimitIsOff()
    {
        var ini = IniFile.Parse("[Framerate]\nFramerateLimit=60.0\n");
        var settings = new UpscalerSettings { FrameRateLimit = 0 };

        settings.WriteTo(ini);

        Assert.Equal("auto", ini.Get("Framerate", "FramerateLimit"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(14, 0)]
    [InlineData(48, 47)]
    [InlineData(60, 59)]
    [InlineData(90, 87)]
    [InlineData(120, 116)]
    [InlineData(144, 138)]
    [InlineData(165, 157)]
    [InlineData(240, 224)]
    public void VrrFrameRateLimit_UsesReflexCeilingFormula(int refreshHz, int expected)
    {
        Assert.Equal(expected, DisplayMode.VrrFrameRateLimit(refreshHz));
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(14, false, 0)]
    [InlineData(48, false, 47.3)]
    [InlineData(48, true, 47)]
    [InlineData(60, false, 58.9)]
    [InlineData(60, true, 59)]
    [InlineData(90, false, 87.6)]
    [InlineData(90, true, 88)]
    [InlineData(120, false, 115.8)]
    [InlineData(120, true, 116)]
    [InlineData(144, false, 138)]
    [InlineData(144, true, 138)]
    [InlineData(165, false, 157.2)]
    [InlineData(165, true, 157)]
    [InlineData(180, false, 170.8)]
    [InlineData(180, true, 171)]
    [InlineData(240, false, 223.9)]
    [InlineData(240, true, 224)]
    public void OptiScalerVrrFrameRateLimit_MatchesOptiScalerCalculator(int refreshHz, bool roundToWholeFps, double expected)
    {
        Assert.Equal(expected, DisplayMode.OptiScalerVrrFrameRateLimit(refreshHz, roundToWholeFps));
    }

    [Fact]
    public void ListedGame_CanAutoInstall_RespectsFactsAntiCheatAndExistingProxy()
    {
        var game = Game.At(@"C:\Games\Sample", "Sample", GameStore.Steam);
        var cleanFacts = new GameFacts(@"C:\Games\Sample", @"C:\Games\Sample\Sample.exe", ["DLSS"], null, null);
        var antiCheatFacts = cleanFacts with { AntiCheat = "Easy Anti-Cheat" };
        var existingProxyFacts = cleanFacts with { ExistingProxy = "dxgi.dll" };

        Assert.False(new ListedGame { Game = game, Facts = null }.CanAutoInstall);
        Assert.True(new ListedGame { Game = game, Facts = cleanFacts }.CanAutoInstall);
        Assert.False(new ListedGame { Game = game, Facts = existingProxyFacts }.CanAutoInstall);
        Assert.False(new ListedGame { Game = game, Facts = antiCheatFacts }.CanAutoInstall);
        Assert.True(new ListedGame { Game = game, Facts = antiCheatFacts, AntiCheatAccepted = true }.CanAutoInstall);
        Assert.False(new ListedGame { Game = game, Facts = existingProxyFacts with { AntiCheat = "Easy Anti-Cheat" }, AntiCheatAccepted = true }.CanAutoInstall);
    }

    [Fact]
    public void GameList_Absorb_ReturnsOnlyNewlyAddedGames()
    {
        var first = Game.At(@"C:\Games\First", "First", GameStore.Steam);
        var second = Game.At(@"C:\Games\Second", "Second", GameStore.Steam);
        var list = new GameList { Games = [new ListedGame { Game = first }] };

        var added = list.Absorb([first, second], new HashSet<string>());

        var only = Assert.Single(added);
        Assert.Equal(second.Id, only.Game.Id);
        Assert.Empty(list.Absorb([first, second], new HashSet<string>()));
    }

    [Fact]
    public void GameList_AutoInstall_RoundTripsThroughDisk()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "tsume-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new UpscalingPaths(tempRoot);
            var game = Game.At(@"C:\Games\Sample", "Sample", GameStore.Steam);
            var list = new GameList
            {
                AutoInstall = true,
                Games = [new ListedGame { Game = game }],
            };

            Assert.True(GameList.Save(paths, list.Serialize()));

            var loaded = GameList.Load(paths);
            Assert.True(loaded.AutoInstall);
            Assert.Single(loaded.Games);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }
}
