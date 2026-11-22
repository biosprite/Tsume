using System.Diagnostics;
using System.Text;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Games;
using Tsume.Upscaling.Install;
using Tsume.Upscaling.Nightly;
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

        Check(ini.Get("Upscalers", "Dx12Upscaler") == "ffx" && ini.Get("FSR", "UpscalerIndex") == "0"
              && ini.Get("FSR", "Fsr4GpuSupport") == "2" && ini.Get("FSR", "Fsr4Model") == "1",
            "FSR 4 with the INT8 network is written as ffx / index 0 / support 2 / model 1");
        Check(ini.Get("FrameGen", "FGOutput") == "xefg" && ini.Get("XeFG", "InterpolationCount") == "2",
            "XeFG at 3× is written as xefg / 2 made-up frames");
        Check(new UpscalerSettings().CorrectedBy(ini) == chosen, "and reads back as the same settings");
        Check(ini.ToString().Split('\n').Length == blank.Split('\n').Length, "without adding or dropping a line");

        // GPU-based defaults: XeSS on Intel, FSR 4 on AMD, Auto elsewhere.
        Check(UpscalerSettings.DefaultUpscaler(hasIntelGpu: true, hasAmdGpu: false) == UpscalerChoice.XeSS,
            "Intel devices default to XeSS");
        Check(UpscalerSettings.DefaultUpscaler(hasIntelGpu: false, hasAmdGpu: true) == UpscalerChoice.Fsr4,
            "AMD devices default to FSR 4");
        Check(UpscalerSettings.DefaultUpscaler(hasIntelGpu: false, hasAmdGpu: false) == UpscalerChoice.Auto,
            "other devices default to Auto");

        var defaults = new UpscalerSettings();
        var expectedDefault = UpscalerSettings.DefaultUpscaler(GpuVendor.HasIntelGpu(), GpuVendor.HasAmdGpu());
        Check(defaults.Upscaler == expectedDefault, $"on this machine the default upscaler is {expectedDefault}");

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

        var without = xmxSettings.Fitting(hasXmx: false);
        Check(without is { Upscaler: UpscalerChoice.Fsr4, Model: Fsr4Model.Int8 } && xmxSettings.Fitting(hasXmx: true) == xmxSettings,
            "for a game without the XMX upscaler in it, plain FSR 4 stands in");

        var fsr4Lite = new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Lite };
        Check(fsr4Lite.Fitting(hasXmx: true) is { Upscaler: UpscalerChoice.Fsr4, Model: Fsr4Model.Int8 },
            "plain FSR 4 clamps Lite to INT8 because AMD's DX12 upscaler has no Lite network");

        var xefgMfg = new UpscalerSettings { FrameGen = FrameGenChoice.XeFG, Multiplier = 4 };
        Check(xefgMfg.Fitting(hasXmx: true, hasIntelGpu: true).Multiplier == 4
              && xefgMfg.Fitting(hasXmx: true, hasIntelGpu: false).Multiplier == 2,
            "XeFG 3×/4× stays on Intel GPUs and clamps to 2× on non-Intel GPUs");

        var dlssgFg = new UpscalerSettings { FrameGen = FrameGenChoice.XeFG, Source = FrameGenSource.Dlssg, Multiplier = 2 };
        ini = IniFile.Parse(blank);
        dlssgFg.WriteTo(ini, ueIntelAtomics: true);
        Check(ini.Get("FrameGen", "Enabled") == "auto" && ini.Get("FrameGen", "FGInput") == "dlssg"
              && ini.Get("FrameGen", "FGOutput") == "xefg" && ini.Get("Spoofing", "UEIntelAtomics") == "true",
            "DLSS-FG input leaves FrameGen.Enabled on auto and UE Intel spoofing enables UEIntelAtomics");
        Check(new UpscalerSettings().CorrectedBy(ini) == dlssgFg, "and DLSS-FG reads back cleanly");
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
                UpscalerChoice.Fsr4 => "ffx",
                _                   => "auto",
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

            Installer.WriteSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.Fsr4, SpoofNvidia = false });
            Check(IniFile.Load(Path.Combine(twin.Folder, "OptiScaler.ini")).Get("Upscalers", "Dx12Upscaler") == "xess"
                  && IniFile.Load(Path.Combine(a.Folder, "OptiScaler.ini")).Get("Upscalers", "Dx12Upscaler") == "ffx",
                "two games keep different settings");
            Check(Files.Hash(Path.Combine(build.Folder, "OptiScaler.ini")) == blankHash, "and the store's blank settings file is unchanged");
            Installer.WriteSettings(manifest, new UpscalerSettings());

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

            Installer.WriteSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.XeSS });
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

            manifest = installer.Install(b, bFacts, build, new UpscalerSettings());
            Check(manifest.Proxy == "winmm.dll", "left to choose, Tsume took the next free name: winmm.dll");
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

            Say("D. A game that already has OptiScaler from another tool");
            var (d, dFacts) = MakeGame(bench, "Taken", folder =>
            {
                File.Copy(Path.Combine(build.Folder, "OptiScaler.dll"), Path.Combine(folder, "winmm.dll"));
                File.WriteAllText(Path.Combine(folder, "OptiScaler.ini"), "[Menu]\nShowFps=true\n");
            });
            before = Snapshot(d.Folder);

            Check(dFacts.ExistingProxy == "winmm.dll", "inspected: found it, as winmm.dll");

            manifest = installer.Install(d, dFacts, build, new UpscalerSettings { Proxy = "dxgi.dll" });
            Check(manifest.Proxy == "winmm.dll", "Tsume took over that name rather than adding a second OptiScaler");

            installer.Remove(d.Id);
            Check(Same(before, Snapshot(d.Folder)), "removed: the other tool's files and settings are back as they were");
            Say("");

            Say("E. An install that fails half-way");
            var (e, eFacts) = MakeGame(bench, "Stuck", folder =>
            {
                // A folder where a file has to go, so the copy fails there.
                Directory.CreateDirectory(Path.Combine(folder, "OptiScaler", "libxess.dll"));
                File.WriteAllText(Path.Combine(folder, "dxgi.dll"), "the game's own");
            });
            before = Snapshot(e.Folder);

            var failed = false;
            try { installer.Install(e, eFacts, build, new UpscalerSettings { Proxy = "dxgi.dll" }); }
            catch (Exception error) when (error is UpscalingException or IOException or UnauthorizedAccessException) { failed = true; }

            Check(failed, "the install reported failure");
            Check(Same(before, Snapshot(e.Folder)), "and undid itself: the folder is as it was found");
            Check(installer.Find(e.Id) is null, "no record remains");
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
                var (f, fFacts) = MakeGame(bench, "Matrix", folder => { });
                before = Snapshot(f.Folder);

                // Before Tsume has the upscaler: asking for it must not leave the game without one.
                manifest = installer.Install(f, fFacts, build, xmxSettings);
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
                Check(!installer.IsCurrent(manifest, build), "the game installed earlier now reads as due an update");

                installer.Remove(f.Id);
                manifest = installer.Install(f, fFacts, build, xmxSettings);
                live = IniFile.Load(Path.Combine(f.Folder, "OptiScaler.ini"));

                var upscaler = Path.Combine(f.Folder, XmxLayout.Upscaler);
                var weights = Path.Combine(f.Folder, XmxLayout.Weights);
                var amd = Path.Combine(f.Folder, XmxLayout.WeightsInBuild);

                Check(File.Exists(upscaler) && Files.Hash(upscaler) == real.Sha256, $"installed: the XMX upscaler is at {XmxLayout.Upscaler}");
                Check(File.Exists(weights) && Files.Hash(weights) == Files.Hash(amd), "with AMD's upscaler beside it as fsr-original.dll");
                Check(Files.Names(weights) >= 2 && Files.Names(weights) == Files.Names(amd), "as a second name for the same bytes, not a second copy");
                Check(live.Get("Upscalers", "Dx12Upscaler") == XmxLayout.Name && live.Get(XmxLayout.Name, "UpscalerIndex") == "3",
                    "and the settings file selects it, with 4.1.2 Lite");
                Check(Installer.HasXmx(manifest) && installer.IsCurrent(manifest, build), "the install reads as up to date");
                Check(Installer.ReadSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.XeSS }) == xmxSettings,
                    "its settings read back as XMX 4.1.2 Lite");

                Installer.WriteSettings(manifest, new UpscalerSettings { Upscaler = UpscalerChoice.XeSS });
                live = IniFile.Load(Path.Combine(f.Folder, "OptiScaler.ini"));
                Check(live.Get("Upscalers", "Dx12Upscaler") == "xess" && live.Get(XmxLayout.Name, "UpscalerIndex") == "3",
                    "switching to XeSS changes the upscaler and leaves the XMX version be");

                installer.Remove(f.Id);
                Check(Same(before, Snapshot(f.Folder)), "removed: the folder is as it was found");
            }

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
