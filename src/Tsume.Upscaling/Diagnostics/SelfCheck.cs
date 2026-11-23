using System.Diagnostics;
using System.Text;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Nightly;
using Tsume.Upscaling.OptiPatcher;
using Tsume.Upscaling.Xmx;

namespace Tsume.Upscaling.Diagnostics;

/// <summary>
/// <c>Tsume.exe --optiscaler-check &lt;file&gt;</c>: fetches the real nightly, installs it into made-up game folders
/// under %TEMP% and checks by hash that removal leaves each folder as found. Real games are only read, never touched.
/// </summary>
public static class SelfCheck
{
    public static async Task<string> RunAsync()
    {
        var report = new StringBuilder();
        var failures = 0;

        void Say(string line) => report.AppendLine(line);
        void Check(bool passed, string what)
        {
            if (!passed) failures++;
            Say($"  {(passed ? "ok  " : "FAIL")}  {what}");
        }

        Say($"Tsume — OptiScaler self-check, {DateTime.Now:yyyy-MM-dd HH:mm}");
        Say("");

        var store = new BuildStore(new UpscalingPaths());   // the real store, so the download is kept
        var newest = await NightlyFeed.NewestAsync();

        Say("Newest nightly");
        Say($"  {newest.Tag}   version {newest.Version}   {newest.Size / 1e6:0.0} MB   sha256 {newest.Sha256[..16]}…");

        var clock = Stopwatch.StartNew();
        var wasHere = store.Current()?.Build.Tag == newest.Tag;
        var build = await store.FetchAsync(newest);

        Say($"  {(wasHere ? "already on disk" : $"downloaded, checked and unpacked in {clock.Elapsed.TotalSeconds:0.0} s")}: " +
            $"{build.Files.Count} files, {build.Files.Sum(file => file.Length) / 1e6:0} MB");
        Check(store.Current()?.Build.Tag == newest.Tag, "the build on disk is the newest");
        Say("");

        Say("OptiScaler.ini");
        var blank = File.ReadAllText(Path.Combine(build.Folder, "OptiScaler.ini"));
        var ini = IniFile.Parse(blank);

        Check(ini.ToString() == blank, "read and written back, it is byte for byte the same");

        var chosen = new UpscalerSettings
        {
            Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Int8,
            FrameGen = FrameGenChoice.XeFG, Multiplier = 3, SpoofNvidia = false,
        };
        chosen.WriteTo(ini);

        Check(ini.Get("Upscalers", "Dx11Upscaler") == "fsr31" && ini.Get("Upscalers", "Dx12Upscaler") == "ffx"
              && ini.Get("Upscalers", "VulkanUpscaler") == "ffx" && ini.Get("FSR", "UpscalerIndex") == "0"
              && ini.Get("FSR", "Fsr4GpuSupport") == "2" && ini.Get("FSR", "Fsr4Model") == "1",
            "FSR 4 writes native fsr31/ffx for DX11/Vulkan and ffx / index 0 / support 2 / model 1 for DX12 INT8");
        Check(ini.Get("FrameGen", "FGOutput") == "xefg" && ini.Get("XeFG", "InterpolationCount") == "2"
              && ini.Get("Hotfix", "DisableOverlays") == "false",
            "XeFG at 3× with OptiFG writes xefg / 2 made-up frames and keeps Steam Input active (DisableOverlays=false)");
        Check(ini.Get("Spoofing", "Dxgi") == "false" && ini.Get("Spoofing", "Vulkan") == "false"
              && ini.Get("Spoofing", "VulkanExtensionSpoofing") == "false",
            "turning Nvidia spoofing off disables both DXGI and Vulkan spoofing");
        Check(new UpscalerSettings().CorrectedBy(ini) == chosen, "and reads back as the same settings");
        Check(ini.ToString().Split('\n').Length == blank.Split('\n').Length, "without adding or dropping a line");

        // Plain FSR 4 always forces INT8 on handhelds (never leaves Fsr4GpuSupport on auto to fall back to FSR 3.1).
        var fsr4AutoModel = new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Auto };
        ini = IniFile.Parse(blank);
        fsr4AutoModel.WriteTo(ini);
        Check(ini.Get("FSR", "Fsr4GpuSupport") == "2" && ini.Get("FSR", "Fsr4Model") == "1"
              && ini.Get("Hotfix", "DisableOverlays") == "auto"
              && ini.Get("Spoofing", "Vulkan") == "true" && ini.Get("Spoofing", "VulkanExtensionSpoofing") == "true",
            "FSR 4 always writes INT8 support/model, enables Vulkan spoofing when SpoofNvidia is on, and resets DisableOverlays when OptiFG is off");

        // GPU-based defaults: XeSS on Intel, FSR 4 on RDNA 3/3.5+ AMD (Z1E/Z2E), FSR 3 on RDNA 2 AMD (Z2 Go/Z2 A), Auto elsewhere.
        Check(UpscalerSettings.DefaultUpscaler(hasIntelGpu: true, hasAmdGpu: false) == UpscalerChoice.XeSS,
            "Intel devices default to XeSS");
        Check(UpscalerSettings.DefaultUpscaler(hasIntelGpu: false, hasAmdGpu: true, hasAmdRdna3PlusGpu: true) == UpscalerChoice.Fsr4,
            "RDNA 3/3.5+ AMD devices (Z1E/Z2E/HX 370) default to FSR 4");
        Check(UpscalerSettings.DefaultUpscaler(hasIntelGpu: false, hasAmdGpu: true, hasAmdRdna3PlusGpu: false) == UpscalerChoice.Fsr3,
            "RDNA 2 AMD APUs (Z2 Go/Z2 A/680M) default to FSR 3");
        Check(UpscalerSettings.DefaultUpscaler(hasIntelGpu: false, hasAmdGpu: false) == UpscalerChoice.Auto,
            "other devices default to Auto");

        var defaults = new UpscalerSettings();
        var expectedDefault = UpscalerSettings.DefaultUpscaler();
        Say($"  detected primary GPU: Intel={GpuVendor.HasIntelGpu()}, IntelArc={GpuVendor.HasIntelArcGpu()}, IntelXmx={GpuVendor.HasIntelXmxGpu()}, Amd={GpuVendor.HasAmdGpu()}, AmdRdna3Plus={GpuVendor.HasAmdRdna3PlusGpu()}");
        Check(defaults.Upscaler == expectedDefault && defaults.Proxy == ProxyNames.Default,
            $"on this machine the default upscaler is {expectedDefault} and default proxy is {ProxyNames.Default}");

        // GPU classification: separates Intel XMX (Lunar Lake / Arrow Lake-H / Core G3) from Meteor Lake (Claw A1M),
        // and AMD RDNA 3/3.5+ (Z1E / Z2E) from RDNA 2 APUs (Z2 Go / Z2 A).
        Check(GpuVendor.Classify(0x8086, 0x64A0, "Intel(R) Arc(TM) 140V GPU") == new GpuVendor.GpuCapabilities(true, true, true, false, false)
              && GpuVendor.Classify(0x8086, 0xB081, "Intel(R) Arc(TM) B370 GPU") == new GpuVendor.GpuCapabilities(true, true, true, false, false)
              && GpuVendor.Classify(0x8086, 0xB080, "Intel(R) Arc(TM) B390 GPU") == new GpuVendor.GpuCapabilities(true, true, true, false, false)
              && GpuVendor.Classify(0x8086, 0x7D51, "Intel(R) Arc(TM) 140T GPU") == new GpuVendor.GpuCapabilities(true, true, true, false, false)
              && GpuVendor.Classify(0x8086, 0x7D55, "Intel(R) Arc(TM) Graphics") == new GpuVendor.GpuCapabilities(true, true, false, false, false)
              && GpuVendor.Classify(0x8086, 0x7D41, "Intel(R) Graphics") == new GpuVendor.GpuCapabilities(true, false, false, false, false)
              && GpuVendor.Classify(0x8086, 0x7D67, "Intel(R) Graphics") == new GpuVendor.GpuCapabilities(true, false, false, false, false)
              && GpuVendor.Classify(0x8086, 0x9A49, "Intel(R) Iris(R) Xe Graphics") == new GpuVendor.GpuCapabilities(true, false, false, false, false)
              && GpuVendor.Classify(0x8086, 0x46A6, "Intel(R) Iris(R) Xe Graphics") == new GpuVendor.GpuCapabilities(true, false, false, false, false)
              && GpuVendor.Classify(0x8086, 0, "Intel(R) UHD Graphics 770") == new GpuVendor.GpuCapabilities(true, false, false, false, false)
              && GpuVendor.Classify(0x8086, 0, "Intel(R) Iris(R) Xe Graphics") == new GpuVendor.GpuCapabilities(true, false, false, false, false)
              && GpuVendor.Classify(0x1002, 0x15BF, "AMD Radeon 780M Graphics") == new GpuVendor.GpuCapabilities(false, false, false, true, true)
              && GpuVendor.Classify(0x1002, 0x150E, "AMD Radeon(TM) 890M Graphics") == new GpuVendor.GpuCapabilities(false, false, false, true, true)
              && GpuVendor.Classify(0x1002, 0x1681, "AMD Radeon(TM) Graphics") == new GpuVendor.GpuCapabilities(false, false, false, true, false)
              && GpuVendor.Classify(0x1002, 0x163F, "AMD Radeon(TM) Graphics") == new GpuVendor.GpuCapabilities(false, false, false, true, false)
              && GpuVendor.Classify(0x10DE, 0, "NVIDIA GeForce RTX 4090") == new GpuVendor.GpuCapabilities(false, false, false, false, false),
            "GPU classification separates Intel XMX from Meteor Lake/Iris Xe, and AMD RDNA 3/3.5+ from RDNA 2 APUs");

        ini = IniFile.Parse(blank);
        new UpscalerSettings { Upscaler = UpscalerChoice.XeSS }.WriteTo(ini, hasIntelArcGpu: true);
        var dx11Arc = ini.Get("Upscalers", "Dx11Upscaler");
        new UpscalerSettings { Upscaler = UpscalerChoice.XeSS }.WriteTo(ini, hasIntelArcGpu: false);
        var dx11NonArc = ini.Get("Upscalers", "Dx11Upscaler");
        Check(dx11Arc == "xess" && dx11NonArc == "xess_12",
            "DX11 XeSS uses native xess only on Intel Arc and xess_12 elsewhere");

        // FSR 4.1.2 Lite on the XMX upscaler.
        var xmxSettings = new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4Xmx, Model = Fsr4Model.Lite };
        ini = IniFile.Parse(blank);
        xmxSettings.WriteTo(ini);

        Check(ini.Get("Upscalers", "Dx12Upscaler") == XmxLayout.Name && ini.Get(XmxLayout.Name, "UpscalerIndex") == "3"
              && ini.Get("FSR", "Fsr4GpuSupport") == "auto" && ini.Get("FSR", "UpscalerIndex") == "auto",
            $"XMX 4.1.2 Lite is written as Dx12Upscaler={XmxLayout.Name} and [{XmxLayout.Name}] UpscalerIndex=3, with the stock FSR 4 lines left on auto");
        Check(ini.Get(XmxLayout.Section, "Model") is null, "and nothing is written to the XMX upscaler's own Run-as setting");
        Check(ini.Get("Inputs", "EnableXeSSInputs") == "false", "with it, a game's XeSS is left to the game (EnableXeSSInputs=false)");

        (xmxSettings with { Upscaler = UpscalerChoice.Fsr4 }).WriteTo(ini);
        Check(ini.Get("Inputs", "EnableXeSSInputs") == "auto", "and taken over again once another upscaler is chosen");
        xmxSettings.WriteTo(ini);
        Check(new UpscalerSettings { Upscaler = UpscalerChoice.XeSS }.CorrectedBy(ini) == xmxSettings, "and read back as the same settings");

        (xmxSettings with { Model = Fsr4Model.Int8 }).WriteTo(ini);
        Check(ini.Get(XmxLayout.Name, "UpscalerIndex") == "2", "the full 4.1.2 network is UpscalerIndex=2");

        ini.Set(XmxLayout.Name, "UpscalerIndex", "8");   // a DLSS model, picked in the overlay
        (xmxSettings with { Model = Fsr4Model.Auto, SpoofNvidia = false }).WriteTo(ini);
        Check(ini.Get(XmxLayout.Name, "UpscalerIndex") == "8", "a version picked in the overlay is not written over by another change in Tsume");

        // A file as Tsume 0.5.1 wrote it (Run as only), then as the overlay saved it in a real game.
        ini = IniFile.Parse(blank);
        ini.Set("Upscalers", "Dx12Upscaler", XmxLayout.Name);
        ini.Set(XmxLayout.Section, "Model", "3");
        Check(xmxSettings.CorrectedBy(ini).Model == Fsr4Model.Auto, "a file from 0.5.1, which set only Run as, reads as no version chosen");

        ini.Set(XmxLayout.Name, "UpscalerIndex", "3");
        Check(xmxSettings.CorrectedBy(ini) == xmxSettings, "once 4.1.2X Lite is picked in the overlay it reads as 4.1.2 Lite");

        xmxSettings.WriteTo(ini);
        Check(ini.Get(XmxLayout.Section, "Model") == "0" && ini.Get(XmxLayout.Name, "UpscalerIndex") == "3",
            "and Tsume's next write puts Run as back to the game's choice, leaving the version");

        var without = xmxSettings.Fitting(hasXmx: false, hasIntelXmxGpu: true);
        var withoutXmxGpu = xmxSettings.Fitting(hasXmx: true, hasIntelXmxGpu: false);
        Check(without is { Upscaler: UpscalerChoice.Fsr4, Model: Fsr4Model.Int8 }
              && xmxSettings.Fitting(hasXmx: true, hasIntelXmxGpu: true) == xmxSettings
              && withoutXmxGpu.Upscaler != UpscalerChoice.Fsr4Xmx,
            "XMX falls back to FSR 4 on XMX GPUs without the archive, and is rejected on non-XMX GPUs");

        var fsr4Lite = new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Lite };
        Check(fsr4Lite.Fitting(hasXmx: true) is { Upscaler: UpscalerChoice.Fsr4, Model: Fsr4Model.Int8 },
            "plain FSR 4 pins Lite/Auto to INT8");

        var xefgMfg = new UpscalerSettings { FrameGen = FrameGenChoice.XeFG, Multiplier = 4 };
        Check(xefgMfg.Fitting(hasXmx: true, hasIntelArcGpu: true).Multiplier == 4
              && xefgMfg.Fitting(hasXmx: true, hasIntelArcGpu: false).Multiplier == 2,
            "XeFG 3×/4× stays on Intel Arc GPUs and clamps to 2× on non-Arc GPUs");

        // Remembered XMX (on Intel XMX GPUs) and OptiPatcher preferences survive edits to unrelated settings while unavailable.
        var rememberedUnavailable = new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4Xmx, Model = Fsr4Model.Lite, OptiPatcher = true, SpoofNvidia = true };
        var editedWhileUnavailable = rememberedUnavailable.Fitting(hasXmx: false, hasOptiPatcher: false, hasIntelXmxGpu: true) with { SpoofNvidia = false };
        var preserved = editedWhileUnavailable.PreservingUnavailable(rememberedUnavailable, hasXmx: false, hasOptiPatcher: false, hasIntelXmxGpu: true);
        var notPreservedNonXmx = editedWhileUnavailable.PreservingUnavailable(rememberedUnavailable, hasXmx: false, hasOptiPatcher: false, hasIntelXmxGpu: false);
        var switchedExplicitly = (editedWhileUnavailable with { Upscaler = UpscalerChoice.XeSS })
            .PreservingUnavailable(rememberedUnavailable, hasXmx: false, hasOptiPatcher: false, hasIntelXmxGpu: true);
        Check(preserved is { Upscaler: UpscalerChoice.Fsr4Xmx, Model: Fsr4Model.Lite, OptiPatcher: true, SpoofNvidia: false }
              && notPreservedNonXmx.Upscaler == UpscalerChoice.Fsr4
              && switchedExplicitly is { Upscaler: UpscalerChoice.XeSS, OptiPatcher: true },
            "editing an unrelated setting preserves unavailable XMX on Intel XMX GPUs (never non-XMX) while explicit upscaler changes win");

        // Overlay preservation: Dx12Upscaler=ffx + UpscalerIndex=auto, custom VulkanUpscaler, XeFG input, and fsrfg30/nvngxfg inputs.
        ini = IniFile.Parse(blank);
        ini.Set("Upscalers", "Dx12Upscaler", "ffx");
        ini.Set("Upscalers", "VulkanUpscaler", "xess");
        ini.Set("FrameGen", "Enabled", "true");
        ini.Set("FrameGen", "FGOutput", "fsrfg");
        ini.Set("FrameGen", "FGInput", "fsrfg30");
        var readOverlay = new UpscalerSettings().CorrectedBy(ini);
        Check(readOverlay is { Upscaler: UpscalerChoice.Fsr4, FrameGen: FrameGenChoice.FsrFG, Source: FrameGenSource.FsrFg },
            "overlay ffx with UpscalerIndex=auto reads as FSR 4, and fsrfg30 reads under FSR-FG");
        (readOverlay with { SpoofNvidia = false }).WriteTo(ini);
        Check(ini.Get("Upscalers", "VulkanUpscaler") == "xess" && ini.Get("FrameGen", "FGInput") == "fsrfg30",
            "editing an unrelated setting in Tsume preserves overlay VulkanUpscaler and fsrfg30 FGInput");
        var xefgSource = new UpscalerSettings { FrameGen = FrameGenChoice.FsrFG, Source = FrameGenSource.XeFg };
        xefgSource.WriteTo(ini);
        Check(ini.Get("FrameGen", "FGInput") == "xefg" && new UpscalerSettings().CorrectedBy(ini).Source == FrameGenSource.XeFg,
            "XeSS-FG input writes FGInput=xefg and reads back cleanly");

        var dlssgFg = new UpscalerSettings { FrameGen = FrameGenChoice.XeFG, Source = FrameGenSource.Dlssg, Multiplier = 2 };
        ini = IniFile.Parse(blank);
        dlssgFg.WriteTo(ini, ueIntelAtomics: true);
        Check(ini.Get("FrameGen", "Enabled") == "auto" && ini.Get("FrameGen", "FGInput") == "dlssg"
              && ini.Get("FrameGen", "FGOutput") == "xefg" && ini.Get("Spoofing", "UEIntelAtomics") == "true",
            "DLSS-FG input leaves FrameGen.Enabled on auto and UE Intel spoofing enables UEIntelAtomics");
        Check(new UpscalerSettings().CorrectedBy(ini) == dlssgFg, "and DLSS-FG reads back cleanly");

        var opOn = new UpscalerSettings { OptiPatcher = true };
        ini = IniFile.Parse(blank);
        opOn.WriteTo(ini);
        Check(ini.Get("Plugins", "LoadAsiPlugins") == "true" && new UpscalerSettings().CorrectedBy(ini) == opOn,
            "OptiPatcher on writes [Plugins] LoadAsiPlugins=true and reads back cleanly");
        Check(ini.ToString().Split('\n').Length == blank.Split('\n').Length,
            "OptiPatcher edits LoadAsiPlugins in place without adding or dropping lines");
        Check(!opOn.Fitting(hasXmx: true, hasOptiPatcher: false).OptiPatcher
              && opOn.Fitting(hasXmx: true, hasOptiPatcher: true).OptiPatcher,
            "OptiPatcher clamps to off when OptiPatcher.asi is not present");

        // The user's own ASI plugins: LoadAsiPlugins=true set by hand survives a settings write in a game without OptiPatcher,
        // and does not report OptiPatcher as enabled.
        ini = IniFile.Parse(blank);
        ini.Set("Plugins", "LoadAsiPlugins", "true");
        var readWithoutOp = new UpscalerSettings().CorrectedBy(ini, hasOptiPatcher: false);
        readWithoutOp.Fitting(hasXmx: true, hasOptiPatcher: false).WriteTo(ini, hasOptiPatcher: false);
        Check(!readWithoutOp.OptiPatcher && ini.Get("Plugins", "LoadAsiPlugins") == "true",
            "without OptiPatcher, LoadAsiPlugins=true does not read as OptiPatcher and is kept for the user's own plugins");
        Say("");

        Say("Games the stores report (read only)");
        clock.Restart();
        var library = GameLibrary.Find();

        foreach (var game in library)
        {
            var facts = GameInspector.Inspect(game);
            Say($"  [{game.Store}] {game.Name}");
            Say(facts is null
                ? "        nothing to run was found"
                : $"        {Path.GetRelativePath(game.Folder, facts.Executable)}   " +
                  $"upscalers: {(facts.Upscalers.Count > 0 ? string.Join(", ", facts.Upscalers) : "none")}" +
                  $"{(facts.AntiCheat is null ? "" : $"   ANTI-CHEAT: {facts.AntiCheat}")}" +
                  $"{(facts.ExistingProxy is null ? "" : $"   OptiScaler already here as {facts.ExistingProxy}")}");
        }

        Say($"  {library.Count} games, looked through in {clock.Elapsed.TotalSeconds:0.0} s");
        Say("");

        var bench = Path.Combine(Path.GetTempPath(), "Tsume-optiscaler-check-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new UpscalingPaths(Path.Combine(bench, "Data"));
        var installer = new Installer(paths);

        try
        {
            // A store manifest pointing at a root stub exe should still resolve to the real game exe next to upscaler DLLs.
            var hintedRoot = Path.Combine(bench, "Hinted");
            var hintedBin = Path.Combine(hintedRoot, "bin", "x64");
            Directory.CreateDirectory(hintedBin);
            var stubExe = Path.Combine(hintedRoot, "StartGame.exe");
            var realExe = Path.Combine(hintedBin, "Hinted.exe");
            File.WriteAllBytes(stubExe, new byte[4096]);
            File.WriteAllBytes(realExe, new byte[4096]);
            File.WriteAllText(Path.Combine(hintedBin, "nvngx_dlss.dll"), "stand-in for DLSS");
            var hintedGame = Game.At(hintedRoot, "Hinted", GameStore.Epic, stubExe);
            Check(string.Equals(GameInspector.Inspect(hintedGame)?.Executable, realExe, StringComparison.OrdinalIgnoreCase),
                "store manifest hint in root does not override a real game exe beside upscaler DLLs");

            // Non-Shipping Unreal game + subfolder root inference + anti-cheat/upscaler signatures.
            var uePlainRoot = Path.Combine(bench, "PhoenixRoot");
            var uePlainBin = Path.Combine(uePlainRoot, "Phoenix", "Binaries", "Win64");
            var uePlainDlss = Path.Combine(uePlainRoot, "Engine", "Plugins", "Runtime", "Nvidia", "DLSS", "Binaries", "ThirdParty", "Win64");
            Directory.CreateDirectory(uePlainBin);
            Directory.CreateDirectory(uePlainDlss);
            var ueRootStub = Path.Combine(uePlainRoot, "PhoenixRoot.exe");
            var ueRealExe = Path.Combine(uePlainBin, "Phoenix.exe");
            File.WriteAllBytes(ueRootStub, new byte[4096]);
            File.WriteAllBytes(ueRealExe, new byte[32768]);
            File.WriteAllText(Path.Combine(uePlainDlss, "sl.dlss.dll"), "stand-in for Streamline DLSS");
            var customUeFromStub = Game.At(uePlainRoot, "PhoenixRoot", GameStore.Custom, ueRootStub);
            var customUeFacts = GameInspector.Inspect(customUeFromStub);
            Check(customUeFacts is not null
                  && string.Equals(customUeFacts.Executable, ueRealExe, StringComparison.OrdinalIgnoreCase)
                  && GameInspector.IsUnrealTarget(customUeFacts.Target)
                  && customUeFacts.Upscalers.Contains("DLSS"),
                "Unreal games without -Shipping resolve to Binaries\\Win64 (enabling UEIntelAtomics) and detect sl.dlss.dll");

            File.WriteAllText(Path.Combine(uePlainRoot, "randgrid.sys"), "stand-in for RICOCHET");
            var inferredRoot = GameInspector.InferRootFolder(ueRealExe);
            var inferredFacts = GameInspector.Inspect(Game.At(inferredRoot, "Phoenix", GameStore.Custom, ueRealExe));
            Check(string.Equals(inferredRoot, uePlainRoot, StringComparison.OrdinalIgnoreCase)
                  && inferredFacts?.AntiCheat == "RICOCHET Anti-Cheat",
                "adding a subfolder Binaries\\Win64 exe infers the game root and catches root-level anti-cheat (RICOCHET)");

            Say("A. An ordinary game");
            var (a, aFacts) = MakeGame(bench, "Plain", folder => { });
            var before = Snapshot(a.Folder);

            Check(aFacts.Upscalers.SequenceEqual(["DLSS"]) && aFacts.ExistingProxy is null, "inspected: DLSS found, no OptiScaler yet");

            clock.Restart();
            var manifest = installer.Install(a, aFacts, build, new UpscalerSettings());
            Say($"        installed in {clock.Elapsed.TotalSeconds:0.0} s");

            Check(manifest.Proxy == "dxgi.dll" && GameInspector.ExistingProxy(a.Folder) == "dxgi.dll", "OptiScaler is in as dxgi.dll");
            Check(File.Exists(Path.Combine(a.Folder, "OptiScaler", "libxess.dll")), "with its companion files");
            Check(!File.Exists(Path.Combine(a.Folder, "setup_windows.bat")), "and without the setup scripts");
            Check(Installer.Check(installer.Find(a.Id)) == InstallState.Healthy, "the install reads as healthy");
            var expectedDx12 = defaults.Upscaler switch
            {
                UpscalerChoice.XeSS => "xess",
                UpscalerChoice.Fsr4 or UpscalerChoice.Fsr3 => "ffx",
                _ => "auto",
            };
            Check(IniFile.Load(Path.Combine(a.Folder, "OptiScaler.ini")).Get("Upscalers", "Dx12Upscaler") == expectedDx12,
                "its settings file carries Tsume's settings");

            // The big libraries are shared with the store where the disk can link; the two files that get written to never are.
            var shared = Path.Combine(a.Folder, "OptiScaler", "libxess.dll");
            var inStore = Path.Combine(build.Folder, "OptiScaler", "libxess.dll");
            var namesBefore = Files.Names(inStore);

            if (manifest.Linked == true)
            {
                Check(Files.Names(shared) >= 2, "its libraries are second names for the store's files, not copies");
                Check(Files.Hash(shared) == Files.Hash(inStore), "with the store's bytes");
            }
            else
            {
                Say("        this disk cannot link to the store: the libraries were copied");
                Check(Files.Names(shared) == 1, "its libraries are plain copies");
            }

            Check(Files.Names(Path.Combine(a.Folder, "dxgi.dll")) == 1 && Files.Names(Path.Combine(a.Folder, "OptiScaler.ini")) == 1,
                "OptiScaler itself and the settings file are the game's own copies");

            // A second game: its settings are its own, and the store's blank settings file is never touched.
            var blankHash = Files.Hash(Path.Combine(build.Folder, "OptiScaler.ini"));
            var (twin, twinFacts) = MakeGame(bench, "Twin", folder => { });
            var twinManifest = installer.Install(twin, twinFacts, build, new UpscalerSettings { Upscaler = UpscalerChoice.XeSS });

            installer.WriteSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4, SpoofNvidia = false });
            Check(IniFile.Load(Path.Combine(twin.Folder, "OptiScaler.ini")).Get("Upscalers", "Dx12Upscaler") == "xess"
                  && IniFile.Load(Path.Combine(a.Folder, "OptiScaler.ini")).Get("Upscalers", "Dx12Upscaler") == "ffx",
                "two games keep different settings");
            Check(Files.Hash(Path.Combine(build.Folder, "OptiScaler.ini")) == blankHash, "and the store's blank settings file is unchanged");
            installer.WriteSettings(manifest, new UpscalerSettings());

            installer.Remove(twin.Id);
            Check(File.Exists(shared) && Files.Hash(shared) == Files.Hash(inStore) && Files.Names(inStore) == namesBefore,
                "removing one game leaves the other's files, and the store's, as they were");

            // Simulate what happens while the game is played.
            File.WriteAllText(Path.Combine(a.Folder, "OptiScaler.log"), "log");
            Directory.CreateDirectory(Path.Combine(a.Folder, "OptiScaler", "plugins"));
            File.WriteAllText(Path.Combine(a.Folder, "OptiScaler", "plugins", "mine.asi"), "a plugin added by hand");

            var live = IniFile.Load(Path.Combine(a.Folder, "OptiScaler.ini"));
            live.Set("Sharpness", "Sharpness", "0.55");   // tuned in the overlay; not a setting Tsume manages
            live.Set("Spoofing", "Dxgi", "false");        // one that it does
            live.Save(Path.Combine(a.Folder, "OptiScaler.ini"));

            Check(!Installer.ReadSettings(manifest, new UpscalerSettings()).SpoofNvidia, "a change made in the overlay is read back");

            installer.WriteSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.XeSS });
            live = IniFile.Load(Path.Combine(a.Folder, "OptiScaler.ini"));
            Check(live.Get("Upscalers", "Dx12Upscaler") == "xess" && live.Get("Sharpness", "Sharpness") == "0.55",
                "changing a setting in Tsume leaves the overlay's other tuning alone");

            var removal = installer.Remove(a.Id, keepSettings: true);
            var after = Snapshot(a.Folder);
            var mine = Path.Combine("OptiScaler", "plugins", "mine.asi");

            Check(removal.LeftAlone.Count == 0, "removed, nothing reported as left alone");
            Check(after.ContainsKey(mine), "the plugin added by hand is still there");
            Check(Same(before, after.Where(file => file.Key != mine)), "everything else is exactly as before the install");
            Check(!File.Exists(Path.Combine(a.Folder, "OptiScaler.log")), "OptiScaler's log went with it");
            Check(installer.Find(a.Id) is null, "no record remains");

            var saved = Path.Combine(paths.InstallFolder(a.Id), "Saved", "OptiScaler.ini");
            Check(File.Exists(saved) && IniFile.Load(saved).Get("Sharpness", "Sharpness") == "0.55", "an update removal saves the settings file");

            manifest = installer.Install(a, GameInspector.Inspect(a)!, build, new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4 });
            live = IniFile.Load(Path.Combine(a.Folder, "OptiScaler.ini"));
            Check(live.Get("Sharpness", "Sharpness") == "0.55" && live.Get("Upscalers", "Dx12Upscaler") == "ffx",
                "installed again: the saved tuning is back, under the settings chosen now");

            installer.Remove(a.Id);
            Check(!File.Exists(saved), "a full removal clears Saved\\OptiScaler.ini so broken tuning does not stick");
            File.Delete(Path.Combine(a.Folder, mine));
            Directory.Delete(Path.Combine(a.Folder, "OptiScaler", "plugins"));
            Directory.Delete(Path.Combine(a.Folder, "OptiScaler"));
            Check(Same(before, Snapshot(a.Folder)), "removed again: the folder is as it was found");
            Say("");

            Say("B. A game with a dxgi.dll of its own (ReShade, say)");
            var (b, bFacts) = MakeGame(bench, "Shaded", folder => File.WriteAllText(Path.Combine(folder, "dxgi.dll"), "not OptiScaler"));
            before = Snapshot(b.Folder);

            Check(bFacts.PresentProxies is { } occupied && occupied.SequenceEqual(["dxgi.dll"]),
                "inspected: reports dxgi.dll as already occupied in the folder");

            manifest = installer.Install(b, bFacts, build, new UpscalerSettings { Proxy = "winmm.dll" });
            Check(manifest.Proxy == "winmm.dll", "told to use winmm.dll alongside ReShade, it did");
            Check(Snapshot(b.Folder)["dxgi.dll"] == before["dxgi.dll"], "the game's dxgi.dll was not touched");

            installer.Remove(b.Id);
            Check(Same(before, Snapshot(b.Folder)), "removed: the folder is as it was found");

            manifest = installer.Install(b, bFacts, build, new UpscalerSettings { Proxy = "dxgi.dll" });
            Check(manifest.Proxy == "dxgi.dll" && GameInspector.ExistingProxy(b.Folder) == "dxgi.dll", "told to use dxgi.dll, it did");
            Check(File.Exists(Path.Combine(paths.InstallFolder(b.Id), "Originals", "dxgi.dll")), "after moving the game's own aside");

            installer.Remove(b.Id);
            Check(Same(before, Snapshot(b.Folder)), "removed: the game's dxgi.dll is back, byte for byte");
            Say("");

            Say("C. A game update replaces OptiScaler's file");
            manifest = installer.Install(b, bFacts, build, new UpscalerSettings { Proxy = "dxgi.dll" });
            File.WriteAllText(Path.Combine(b.Folder, "dxgi.dll"), "the game's new dxgi.dll, from an update");

            Check(Installer.Check(installer.Find(b.Id)) == InstallState.Replaced, "the install reads as replaced");

            removal = installer.Remove(b.Id);
            Check(File.ReadAllText(Path.Combine(b.Folder, "dxgi.dll")).StartsWith("the game's new"), "removal left the new file alone");
            Check(removal.LeftAlone.SequenceEqual(["dxgi.dll"]), "and said so");
            Check(removal.KeptOriginals is { } kept && Files.Hash(Path.Combine(kept, "dxgi.dll")) == before["dxgi.dll"],
                "the older original was kept rather than forced back or thrown away");
            Check(Same(before.Where(file => file.Key != "dxgi.dll"), Snapshot(b.Folder).Where(file => file.Key != "dxgi.dll")),
                "everything else is as it was found");
            Say("");

            Say("D. A game that already has OptiScaler from another tool (plus pre-0.9 files / OptiScaler.asi)");
            var (d, dFacts) = MakeGame(bench, "Taken", folder =>
            {
                File.Copy(Path.Combine(build.Folder, "OptiScaler.dll"), Path.Combine(folder, "winmm.dll"));
                File.WriteAllText(Path.Combine(folder, "OptiScaler.asi"), "old asi copy");
                File.WriteAllText(Path.Combine(folder, "nvngx.dll"), "legacy pre-0.9 nvngx");
                File.WriteAllText(Path.Combine(folder, "nvapi64.dll"), "legacy pre-0.9 fakenvapi");
                File.WriteAllText(Path.Combine(folder, "OptiScaler.ini"), "[Menu]\nShowFps=true\n");
            });
            before = Snapshot(d.Folder);

            Check(dFacts.ExistingProxy == "winmm.dll", "inspected: found it, as winmm.dll");

            manifest = installer.Install(d, dFacts, build, new UpscalerSettings { Proxy = "dxgi.dll" });
            Check(manifest.Proxy == "winmm.dll", "Tsume took over that name rather than adding a second OptiScaler");
            Check(!File.Exists(Path.Combine(d.Folder, "OptiScaler.asi"))
                  && !File.Exists(Path.Combine(d.Folder, "nvngx.dll"))
                  && !File.Exists(Path.Combine(d.Folder, "nvapi64.dll")),
                "secondary OptiScaler.asi and conflicting pre-0.9 nvngx.dll / nvapi64.dll were moved out of the way");

            installer.Remove(d.Id);
            Check(Same(before, Snapshot(d.Folder)), "removed: the other tool's files, legacy DLLs, and settings are back as they were");
            Say("");

            Say("E. An install that fails half-way");
            var (e, eFacts) = MakeGame(bench, "Stuck", folder =>
            {
                // A folder where a file has to go, so the copy fails there.
                Directory.CreateDirectory(Path.Combine(folder, "OptiScaler", "libxess.dll"));
                File.WriteAllText(Path.Combine(folder, "dxgi.dll"), "the game's own");
            });
            before = Snapshot(e.Folder);

            var savedE = Path.Combine(paths.InstallFolder(e.Id), "Saved", "OptiScaler.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(savedE)!);
            File.WriteAllText(savedE, blank.Replace("Sharpness=auto", "Sharpness=0.77"));

            var failed = false;
            try { installer.Install(e, eFacts, build, new UpscalerSettings { Proxy = "dxgi.dll" }); }
            catch (Exception error) when (error is UpscalingException or IOException or UnauthorizedAccessException) { failed = true; }

            Check(failed, "the install reported failure");
            Check(Same(before, Snapshot(e.Folder)), "and undid itself: the folder is as it was found");
            Check(installer.Find(e.Id) is null, "no record remains");
            Check(File.Exists(savedE) && IniFile.Load(savedE).Get("Sharpness", "Sharpness") == "0.77",
                "and Saved\\OptiScaler.ini survives a failed update/install");
            Say("");

            Say("F. The XMX upscaler");
            var held = new XmxStore(new UpscalingPaths());   // the real store, for the user's archive

            if (held.Current() is not { } real)
            {
                Say("        skipped: Tsume has not been given the XMX upscaler");
            }
            else
            {
                var xmx = new XmxStore(paths);
                var xmxInstaller = new Installer(paths, hasIntelXmxGpu: true);
                var nonXmxInstaller = new Installer(paths, hasIntelXmxGpu: false);
                var (f, fFacts) = MakeGame(bench, "Matrix", folder => { });
                before = Snapshot(f.Folder);

                // Before Tsume has the upscaler: asking for it must not leave the game without one.
                manifest = xmxInstaller.Install(f, fFacts, build, xmxSettings);
                Check(!Installer.HasXmx(manifest) && IniFile.Load(Path.Combine(f.Folder, "OptiScaler.ini")).Get("Upscalers", "Dx12Upscaler") == "ffx",
                    "installed before Tsume has it: plain FSR 4 was written instead");

                // A file with the right name that is not the XMX upscaler.
                var fake = Path.Combine(bench, "fake");
                Directory.CreateDirectory(fake);
                File.Copy(Path.Combine(build.Folder, XmxLayout.WeightsInBuild), Path.Combine(fake, XmxLayout.FileName));
                System.IO.Compression.ZipFile.CreateFromDirectory(fake, Path.Combine(bench, "fake.zip"));

                var refused = false;
                try { xmx.Add(Path.Combine(bench, "fake.zip")); }
                catch (UpscalingException error) when (error.Trouble == Trouble.NotXmx) { refused = true; }
                Check(refused && xmx.Current() is null, "AMD's own upscaler, offered as the XMX one, is refused");

                clock.Restart();
                var added = xmx.Add(held.ArchiveFile);
                Say($"        version {added.Version}, {added.Length / 1e6:0.0} MB unpacked; checked and stored in {clock.Elapsed.TotalSeconds:0.0} s");

                Check(added.Sha256 == real.Sha256 && Files.Hash(xmx.ArchiveFile) == Files.Hash(held.ArchiveFile),
                    "added: the archive is kept as it came, zipped");
                Check(Directory.GetFiles(paths.Xmx, "*.dll", SearchOption.AllDirectories).Length == 0, "and nothing is unpacked beside it until a game needs it");
                Check(!xmxInstaller.IsCurrent(manifest, build) && nonXmxInstaller.IsCurrent(manifest, build),
                    "the game installed earlier reads as due an update on Intel XMX GPUs, and current on non-XMX GPUs");

                xmxInstaller.Remove(f.Id);

                var nonXmxManifest = nonXmxInstaller.Install(f, fFacts, build, xmxSettings);
                Check(!Installer.HasXmx(nonXmxManifest), "non-XMX GPUs skip placing XMX files even when stored");
                nonXmxInstaller.Remove(f.Id);

                manifest = xmxInstaller.Install(f, fFacts, build, xmxSettings);
                live = IniFile.Load(Path.Combine(f.Folder, "OptiScaler.ini"));

                var upscaler = Path.Combine(f.Folder, XmxLayout.Upscaler);
                var weights = Path.Combine(f.Folder, XmxLayout.Weights);
                var amd = Path.Combine(f.Folder, XmxLayout.WeightsInBuild);

                Check(File.Exists(upscaler) && Files.Hash(upscaler) == real.Sha256, $"installed: the XMX upscaler is at {XmxLayout.Upscaler}");
                Check(File.Exists(weights) && Files.Hash(weights) == Files.Hash(amd), "with AMD's upscaler beside it as fsr-original.dll");
                Check(Files.Names(weights) >= 2 && Files.Names(weights) == Files.Names(amd), "as a second name for the same bytes, not a second copy");
                Check(live.Get("Upscalers", "Dx12Upscaler") == XmxLayout.Name && live.Get(XmxLayout.Name, "UpscalerIndex") == "3",
                    "and the settings file selects it, with 4.1.2 Lite");
                Check(Installer.HasXmx(manifest) && xmxInstaller.IsCurrent(manifest, build), "the install reads as up to date");
                Check(Installer.ReadSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.XeSS }) == xmxSettings,
                    "its settings read back as XMX 4.1.2 Lite");

                xmxInstaller.WriteSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.XeSS });
                live = IniFile.Load(Path.Combine(f.Folder, "OptiScaler.ini"));
                Check(live.Get("Upscalers", "Dx12Upscaler") == "xess" && live.Get(XmxLayout.Name, "UpscalerIndex") == "3",
                    "switching to XeSS changes the upscaler and leaves the XMX version be");

                xmxInstaller.Remove(f.Id);
                Check(Same(before, Snapshot(f.Folder)), "removed: the folder is as it was found");
            }

            Say("");

            Say("G. OptiPatcher");
            var (g, gFacts) = MakeGame(bench, "Patched", folder => { });
            before = Snapshot(g.Folder);

            // Installed before OptiPatcher is downloaded: LoadAsiPlugins stays auto.
            manifest = installer.Install(g, gFacts, build, new UpscalerSettings { OptiPatcher = true });
            Check(!Installer.HasOptiPatcher(manifest) && !Installer.IsOptiPatcherEnabled(manifest)
                  && IniFile.Load(Path.Combine(g.Folder, "OptiScaler.ini")).Get("Plugins", "LoadAsiPlugins") == "auto",
                "installed before OptiPatcher is downloaded: LoadAsiPlugins stays auto");

            var patcherStore = new OptiPatcherStore(paths);
            clock.Restart();
            var newestPatcher = await OptiPatcherFeed.NewestAsync();
            var storedPatcher = await patcherStore.FetchAsync(newestPatcher);
            Say($"        {newestPatcher.Tag} ({newestPatcher.AssetName}, {newestPatcher.Size / 1024} KB) fetched and verified in {clock.Elapsed.TotalSeconds:0.0} s");

            Check(patcherStore.Current()?.Release.Tag == newestPatcher.Tag, "OptiPatcher is stored and readable");
            Check(installer.IsCurrent(manifest, build), "a game without OptiPatcher stays up to date after OptiPatcher is downloaded");

            // User's own plugin in OptiScaler\plugins\: stays isolated from OptiPatcher.
            var userPlugin = Path.Combine(g.Folder, "OptiScaler", "plugins", "mine.asi");
            Directory.CreateDirectory(Path.GetDirectoryName(userPlugin)!);
            File.WriteAllText(userPlugin, "user asi plugin");
            live = IniFile.Load(Path.Combine(g.Folder, "OptiScaler.ini"));
            live.Set("Plugins", "LoadAsiPlugins", "true");
            live.Save(Path.Combine(g.Folder, "OptiScaler.ini"));

            Check(!Installer.IsOptiPatcherEnabled(manifest) && !Installer.ReadSettings(manifest, new UpscalerSettings()).OptiPatcher,
                "LoadAsiPlugins=true for a user plugin does not show OptiPatcher as enabled");

            // Turning OptiPatcher on dynamically places OptiPatcher.asi without reinstalling OptiScaler.
            var pluginPath = Path.Combine(g.Folder, OptiPatcherLayout.Plugin);
            installer.WriteSettings(manifest, new UpscalerSettings { OptiPatcher = true });
            Check(File.Exists(pluginPath) && Files.Hash(pluginPath) == newestPatcher.Sha256,
                $"toggled on: OptiPatcher is placed at {OptiPatcherLayout.Plugin} with verified hash");
            Check(manifest.OptiPatcher == newestPatcher.Tag && Installer.HasOptiPatcher(manifest)
                  && Installer.IsOptiPatcherEnabled(manifest) && installer.IsCurrent(manifest, build),
                "manifest records the OptiPatcher tag, reports it enabled, and reads as up to date");

            // Turning OptiPatcher off while user's mine.asi is present removes OptiPatcher.asi but keeps LoadAsiPlugins=true.
            installer.WriteSettings(manifest, new UpscalerSettings { OptiPatcher = false });
            Check(!File.Exists(pluginPath) && !Installer.HasOptiPatcher(manifest) && !Installer.IsOptiPatcherEnabled(manifest)
                  && IniFile.Load(Path.Combine(g.Folder, "OptiScaler.ini")).Get("Plugins", "LoadAsiPlugins") == "true",
                "turning OptiPatcher off removes OptiPatcher.asi while keeping LoadAsiPlugins=true for the user's own plugin");

            // Without other user plugins, turning OptiPatcher off resets LoadAsiPlugins=auto.
            File.Delete(userPlugin);
            installer.WriteSettings(manifest, new UpscalerSettings { OptiPatcher = true });
            Check(Installer.IsOptiPatcherEnabled(manifest) && File.Exists(pluginPath),
                "turning OptiPatcher back on places OptiPatcher.asi and enables LoadAsiPlugins=true");
            installer.WriteSettings(manifest, new UpscalerSettings { OptiPatcher = false });
            Check(!File.Exists(pluginPath) && !Installer.IsOptiPatcherEnabled(manifest)
                  && IniFile.Load(Path.Combine(g.Folder, "OptiScaler.ini")).Get("Plugins", "LoadAsiPlugins") == "auto",
                "turning OptiPatcher off with no other plugins resets LoadAsiPlugins=auto");

            installer.Remove(g.Id);
            Check(Same(before, Snapshot(g.Folder)), "removed: the game folder is as it was found");

            // A damaged OptiPatcher file does not block installs that do not use OptiPatcher, but fails when OptiPatcher is requested.
            File.WriteAllText(storedPatcher.File, "corrupted");
            manifest = installer.Install(g, gFacts, build, new UpscalerSettings { OptiPatcher = false });
            Check(!Installer.HasOptiPatcher(manifest), "damaged OptiPatcher in store does not block installs with OptiPatcher off");
            installer.Remove(g.Id);

            var patcherFailed = false;
            try { installer.Install(g, gFacts, build, new UpscalerSettings { OptiPatcher = true }); }
            catch (UpscalingException error) when (error.Trouble == Trouble.OptiPatcherDamaged) { patcherFailed = true; }
            Check(patcherFailed && patcherStore.Current() is null,
                "requesting OptiPatcher with a damaged file fails cleanly and sets the damaged file aside");

            Say("");

            Say("H. Custom game folders");
            var customLib = Path.Combine(bench, "CustomLibrary");
            var (libFlat, _) = MakeGame(customLib, "AlphaGame", _ => { });
            var libUe = Path.Combine(customLib, "UnrealGame");
            var libUeBin = Path.Combine(libUe, "UnrealGame", "Binaries", "Win64");
            Directory.CreateDirectory(Path.Combine(libUe, "Engine", "Binaries"));
            Directory.CreateDirectory(libUeBin);
            File.WriteAllBytes(Path.Combine(libUeBin, "UnrealGame-Win64-Shipping.exe"), new byte[4096]);
            File.WriteAllText(Path.Combine(libUeBin, "libxess.dll"), "stand-in for XeSS");

            var libPlain = Path.Combine(customLib, "NoUpscalerGame");
            Directory.CreateDirectory(libPlain);
            File.WriteAllBytes(Path.Combine(libPlain, "NoUpscalerGame.exe"), new byte[4096]);

            var folderList = new GameList();
            Check(folderList.AddFolder(customLib + Path.DirectorySeparatorChar)
                  && !folderList.AddFolder(customLib)
                  && folderList.HasFolder(customLib)
                  && folderList.Folders.SequenceEqual([customLib]),
                "custom folders normalize trailing slashes and avoid duplicates");

            var fromLib = GameLibrary.FromFolder(customLib).ToList();
            var libSupported = fromLib
                .Where(game => GameInspector.Inspect(game) is { Upscalers.Count: > 0 })
                .Select(game => game.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Check(libSupported.SequenceEqual(["AlphaGame", "UnrealGame"])
                  && fromLib.All(game => game.Store == GameStore.Folder),
                "scanning a library folder finds supported games and filters out games without upscalers");

            var singleFromUe = GameLibrary.FromFolder(libUe).ToList();
            Check(singleFromUe.Count == 1 && singleFromUe[0].Name == "UnrealGame"
                  && string.Equals(singleFromUe[0].Folder, libUe, StringComparison.OrdinalIgnoreCase),
                "adding a single Unreal game folder directly detects it by its folder name rather than Engine");

            var singleFromFlat = GameLibrary.FromFolder(libFlat.Folder).ToList();
            Check(singleFromFlat.Count == 1 && singleFromFlat[0].Name == "AlphaGame",
                "adding a single flat game folder directly detects it as one game");

            var drives = Path.Combine(bench, "Drive");
            var steamApps = Path.Combine(drives, "SteamLibrary", "steamapps");
            MakeGame(Path.Combine(steamApps, "common"), "BetaGame", _ => { });
            Directory.CreateDirectory(Path.Combine(steamApps, "common", "Steamworks Common Redistributables"));
            Directory.CreateDirectory(Path.Combine(steamApps, "shadercache", "123"));
            Check(new[] { drives, Path.Combine(drives, "SteamLibrary"), steamApps }.All(folder =>
                    GameLibrary.FromFolder(folder).Select(game => game.Name).SequenceEqual(["BetaGame"])),
                "a Steam library is looked into for its games, whether added from above it, itself, or its steamapps");

            var emptyKeep = new HashSet<string>();
            folderList.Absorb(fromLib.Where(game => GameInspector.Inspect(game) is { Upscalers.Count: > 0 }), emptyKeep);
            Check(folderList.Games.Select(listed => listed.Game.Name).SequenceEqual(["AlphaGame", "UnrealGame"]),
                "Absorb adds discovered folder games to the list");

            Check(folderList.RemoveFolder(customLib) && !folderList.HasFolder(customLib),
                "removing a saved folder clears it from the folder list");
            folderList.Absorb([], new HashSet<string> { singleFromFlat[0].Id });
            Check(folderList.Games.Select(listed => listed.Game.Name).SequenceEqual(["AlphaGame"]),
                "after removing a folder, uninstalled folder games drop while installed ones stay removable");

            Say("");
        }
        finally
        {
            Files.Discard(bench);
        }

        Say(failures == 0 ? "All checks passed." : $"{failures} CHECK(S) FAILED.");
        return report.ToString();
    }

    /// <summary>Makes a stand-in game folder: a program, a DLSS file, some data.</summary>
    private static (Game, GameFacts) MakeGame(string bench, string name, Action<string> extra)
    {
        var folder = Path.Combine(bench, name);
        Directory.CreateDirectory(Path.Combine(folder, "data"));

        File.WriteAllBytes(Path.Combine(folder, $"{name}.exe"), new byte[4096]);
        File.WriteAllText(Path.Combine(folder, "nvngx_dlss.dll"), "stand-in for DLSS");
        File.WriteAllText(Path.Combine(folder, "data", "level.pak"), "game data");
        extra(folder);

        var game = Game.At(folder, name, GameStore.Custom, Path.Combine(folder, $"{name}.exe"));
        return (game, GameInspector.Inspect(game)!);
    }

    /// <summary>Every file under a folder, with its hash.</summary>
    private static Dictionary<string, string> Snapshot(string folder) => Directory
        .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(folder, file), Files.Hash, StringComparer.OrdinalIgnoreCase);

    private static bool Same(IEnumerable<KeyValuePair<string, string>> before, IEnumerable<KeyValuePair<string, string>> after) =>
        before.OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(after.OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase));
}
