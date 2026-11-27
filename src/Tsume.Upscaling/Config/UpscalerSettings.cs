using System.Globalization;
using Tsume.Upscaling.Install;

namespace Tsume.Upscaling.Config;

/// <summary>The upscaler OptiScaler runs in place of the game's own.</summary>
public enum UpscalerChoice
{
    /// <summary>OptiScaler decides. On Intel Arc that is XeSS.</summary>
    Auto,
    XeSS,
    Fsr4,
    Fsr3,

    /// <summary>
    /// FSR 4 on an Intel Arc's matrix units (XMX), via a separate upscaler that must be added first
    /// (see <see cref="Xmx.XmxStore"/>). DirectX 12 games on Intel XMX GPUs only.
    /// </summary>
    Fsr4Xmx,
}

/// <summary>Which of FSR 4's networks to run on the XMX upscaler (plain FSR 4 on handhelds always uses INT8).</summary>
public enum Fsr4Model
{
    /// <summary>Uses the model chosen in OptiScaler's overlay.</summary>
    Auto,

    /// <summary>FSR 4.1.2's full INT8 network.</summary>
    Int8,

    /// <summary>FSR 4.1.2's smaller network, made for integrated graphics.</summary>
    Lite,
}

public enum FrameGenChoice
{
    Off,

    /// <summary>Intel's frame generation (DirectX 12 only).</summary>
    XeFG,

    /// <summary>AMD's frame generation (DirectX 12 and Vulkan).</summary>
    FsrFG,
}

/// <summary>Where frame generation gets what it needs from the game.</summary>
public enum FrameGenSource
{
    /// <summary>From the upscaler (OptiFG): works in any game that upscales, though the HUD may shimmer.</summary>
    Upscaler,

    /// <summary>From the game's own DLSS frame generation, switched on in the game.</summary>
    Dlssg,

    /// <summary>From the game's own FSR frame generation, switched on in the game.</summary>
    FsrFg,

    /// <summary>From the game's own XeSS frame generation, switched on in the game.</summary>
    XeFg,
}

/// <summary>
/// The OptiScaler settings Tsume manages, for one game or as the defaults.
/// Everything else in OptiScaler.ini is left untouched, for the in-game overlay.
/// </summary>
public sealed record UpscalerSettings
{
    /// <summary>
    /// The default upscaler for the detected handheld GPU: XeSS on Intel devices, FSR 4 on RDNA 3/3.5+ AMD devices
    /// (e.g. Z1E, Z2E, HX 370), FSR 3 on older RDNA 2 AMD APUs (e.g. Z2 Go, Z2 A), and Auto elsewhere.
    /// </summary>
    public static UpscalerChoice DefaultUpscaler(bool hasIntelGpu, bool hasAmdGpu, bool hasAmdRdna3PlusGpu = true) =>
        hasAmdGpu ? (hasAmdRdna3PlusGpu ? UpscalerChoice.Fsr4 : UpscalerChoice.Fsr3)
        : hasIntelGpu ? UpscalerChoice.XeSS
        : UpscalerChoice.Auto;

    public static UpscalerChoice DefaultUpscaler() =>
        DefaultUpscaler(GpuVendor.HasIntelGpu(), GpuVendor.HasAmdGpu(), GpuVendor.HasAmdRdna3PlusGpu());

    public UpscalerChoice Upscaler { get; init; } = DefaultUpscaler();
    public Fsr4Model Model { get; init; } = Fsr4Model.Int8;

    public FrameGenChoice FrameGen { get; init; } = FrameGenChoice.Off;
    public FrameGenSource Source { get; init; } = FrameGenSource.Upscaler;

    public const int MinFrameRateLimit = 15;
    public const int MaxFrameRateLimit = 240;

    /// <summary>Frames shown per frame drawn, 2–4. XeFG only; FSR's is always 2.</summary>
    public int Multiplier { get; init; } = 2;

    /// <summary>OptiScaler's frame-rate cap (<c>[Framerate] FramerateLimit</c>, 0.1 FPS precision). 0 means off (<c>auto</c>).</summary>
    public double FrameRateLimit { get; init; }

    /// <summary>Tell the game an Nvidia card is fitted, so it offers DLSS for OptiScaler to take over.</summary>
    public bool SpoofNvidia { get; init; } = true;

    /// <summary>
    /// Enable OptiScaler's ASI plugin loader (<c>[Plugins] LoadAsiPlugins=true</c>) so <c>OptiPatcher.asi</c>
    /// unlocks DLSS/DLSS-FG inputs in supported games without GPU spoofing.
    /// </summary>
    public bool OptiPatcher { get; init; }

    private readonly string _proxy = ProxyNames.Default;

    /// <summary>The DLL name OptiScaler is installed under (defaults to <c>dxgi.dll</c>).</summary>
    public string Proxy
    {
        get => _proxy;
        init => _proxy = ProxyNames.Normalize(value);
    }

    private const string Auto = "auto";

    /// <summary>
    /// The ini key (in a section named after the custom upscaler's folder) that picks a version by index, 0 when unset.
    /// The XMX upscaler's list: 0 = 4.0.2X, 1 = 4.0.2X Hybrid, 2 = 4.1.2X, 3 = 4.1.2X Lite, then its DLSS models.
    /// </summary>
    private const string XmxVersion = "UpscalerIndex";
    private const string Xmx412 = "2";
    private const string Xmx412Lite = "3";

    /// <summary>
    /// The XMX upscaler's own "Run as" setting, which overrides the version OptiScaler asked for.
    /// Not used to choose the model: it did not take effect in a game.
    /// </summary>
    private const string XmxRunAs = "Model";

    /// <summary>
    /// Adjusts settings for what the target game and this GPU actually support: rejects XMX on GPUs without Intel XMX
    /// hardware, falls back to plain FSR 4 (INT8) when XMX is chosen on an XMX GPU before the XMX archive is in the
    /// game's folder, pins plain FSR 4 to the INT8 network, turns off OptiPatcher when <c>OptiPatcher.asi</c> is absent,
    /// and clamps XeFG's multiplier to 2× on non-Arc GPUs (3×/4× require Intel Arc).
    /// </summary>
    public UpscalerSettings Fitting(
        bool hasXmx,
        bool hasOptiPatcher = false,
        bool? hasIntelArcGpu = null,
        bool? hasIntelXmxGpu = null)
    {
        var fitted = this;
        var intelArc = hasIntelArcGpu ?? GpuVendor.HasIntelArcGpu();
        var intelXmx = hasIntelXmxGpu ?? GpuVendor.HasIntelXmxGpu();

        if (fitted.Upscaler == UpscalerChoice.Fsr4Xmx && !intelXmx)
            fitted = fitted with { Upscaler = DefaultUpscaler(), Model = Fsr4Model.Int8 };
        else if (fitted.Upscaler == UpscalerChoice.Fsr4Xmx && !hasXmx)
            fitted = fitted with { Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Int8 };
        else if (fitted.Upscaler == UpscalerChoice.Fsr4 && fitted.Model != Fsr4Model.Int8)
            fitted = fitted with { Model = Fsr4Model.Int8 };

        if (fitted.OptiPatcher && !hasOptiPatcher)
            fitted = fitted with { OptiPatcher = false };

        if (!intelArc && fitted.Multiplier > 2)
            fitted = fitted with { Multiplier = 2 };

        var fittedLimit = double.IsFinite(fitted.FrameRateLimit) && fitted.FrameRateLimit > 0
            ? Math.Round(Math.Clamp(fitted.FrameRateLimit, MinFrameRateLimit, MaxFrameRateLimit), 1, MidpointRounding.AwayFromZero)
            : 0;
        if (fittedLimit != fitted.FrameRateLimit)
            fitted = fitted with { FrameRateLimit = fittedLimit };

        return fitted;
    }

    /// <summary>
    /// Restores a previously remembered XMX or OptiPatcher preference when that feature is currently unavailable on disk
    /// (so editing an unrelated setting does not discard the preference before an install or update can activate it).
    /// </summary>
    public UpscalerSettings PreservingUnavailable(
        UpscalerSettings previous,
        bool hasXmx,
        bool hasOptiPatcher,
        bool? hasIntelXmxGpu = null)
    {
        var result = this;
        var intelXmx = hasIntelXmxGpu ?? GpuVendor.HasIntelXmxGpu();

        if (intelXmx
            && !hasXmx
            && previous.Upscaler == UpscalerChoice.Fsr4Xmx
            && result.Upscaler == UpscalerChoice.Fsr4)
        {
            result = result with { Upscaler = UpscalerChoice.Fsr4Xmx, Model = previous.Model };
        }

        if (!hasOptiPatcher && previous.OptiPatcher)
            result = result with { OptiPatcher = true };

        return result;
    }

    /// <summary>Writes these settings into the file, and only these.</summary>
    /// <param name="ueIntelAtomics">
    /// True for an Unreal Engine game on an Intel GPU: sets <c>UEIntelAtomics=true</c> while Nvidia spoofing is on
    /// so Unreal does not fail with a DirectX 12 unsupported error.
    /// </param>
    /// <param name="hasOptiPatcher">
    /// False when <c>OptiPatcher.asi</c> is not managed for this game: <c>LoadAsiPlugins</c> is then left as the
    /// file has it, so a value set for the user's own ASI plugins is kept.
    /// </param>
    /// <param name="keepOtherAsiPlugins">
    /// True when the game's plugins folder holds other <c>*.asi</c> files: turning OptiPatcher off leaves
    /// <c>LoadAsiPlugins</c> untouched so those user plugins keep loading.
    /// </param>
    /// <param name="hasIntelArcGpu">Overrides Intel Arc detection (used by self-checks).</param>
    public void WriteTo(IniFile ini, bool ueIntelAtomics = false, bool hasOptiPatcher = true,
        bool keepOtherAsiPlugins = false, bool? hasIntelArcGpu = null)
    {
        // Each graphics API names its upscalers differently.
        // Native DX11 XeSS is Intel Arc only (non-Arc GPUs use dx11on12); native DX11 FSR 3.1 and native Vulkan FSR 3.1
        // avoid dx11on12 / VKon12 interop crashes while DX12 runs FSR 4 INT8 (or XMX).
        var dx11Xess = (hasIntelArcGpu ?? GpuVendor.HasIntelArcGpu()) ? "xess" : "xess_12";
        var (dx11, dx12, vulkan, backend) = Upscaler switch
        {
            UpscalerChoice.XeSS    => (dx11Xess, "xess", "xess", Auto),
            UpscalerChoice.Fsr4    => ("fsr31", "ffx", "ffx", "0"),
            UpscalerChoice.Fsr3    => ("fsr31", "ffx", "ffx", "1"),
            UpscalerChoice.Fsr4Xmx => (dx11Xess, Xmx.XmxLayout.Name, "xess", Auto),
            _                      => (Auto, Auto, Auto, Auto),
        };

        if (Upscaler == UpscalerChoice.Fsr4Xmx)
        {
            var now = ini.Get(Xmx.XmxLayout.Name, XmxVersion);

            // Auto resets the index only if Tsume set it; a version picked in the overlay stays.
            if (Model != Fsr4Model.Auto) ini.Set(Xmx.XmxLayout.Name, XmxVersion, Model == Fsr4Model.Lite ? Xmx412Lite : Xmx412);
            else if (now is Xmx412 or Xmx412Lite) ini.Set(Xmx.XmxLayout.Name, XmxVersion, Auto);

            // Tsume 0.5.1 wrote 2 or 3 here; reset to 0 ("the game's choice") so the version above is what runs.
            if (ini.Get(Xmx.XmxLayout.Section, XmxRunAs) is "2" or "3") ini.Set(Xmx.XmxLayout.Section, XmxRunAs, "0");
        }

        // Keep per-API DX11 / Vulkan upscalers chosen in the in-game overlay while the DX12 upscaler choice is unchanged.
        var existingDx12 = ini.Get("Upscalers", "Dx12Upscaler");
        var existingBackend = ini.Get("FSR", "UpscalerIndex");
        var sameUpscaler = Is(existingDx12, dx12)
            && (dx12 != "ffx" || (backend == "0" ? IsAutoOr(existingBackend, "0") : Is(existingBackend, backend)));

        if (sameUpscaler)
        {
            var existingDx11 = ini.Get("Upscalers", "Dx11Upscaler");
            if (!IsNullOrAuto(existingDx11)
                && !(Upscaler is UpscalerChoice.XeSS or UpscalerChoice.Fsr4Xmx && (Is(existingDx11, "xess") || Is(existingDx11, "xess_12"))))
            {
                dx11 = existingDx11!;
            }

            var existingVulkan = ini.Get("Upscalers", "VulkanUpscaler");
            if (!IsNullOrAuto(existingVulkan))
                vulkan = existingVulkan!;
        }

        ini.Set("Upscalers", "Dx11Upscaler", dx11);
        ini.Set("Upscalers", "Dx12Upscaler", dx12);
        ini.Set("Upscalers", "VulkanUpscaler", vulkan);
        ini.Set("FSR", "UpscalerIndex", backend);

        // No Windows handheld GPU has native RDNA 4 FP8: whenever plain FSR 4 is selected, always enable the INT8
        // override (Fsr4GpuSupport=2, Fsr4Model=1) so OptiScaler never drops back to FSR 3.1.
        var (support, model) = Upscaler == UpscalerChoice.Fsr4 ? ("2", "1") : (Auto, Auto);

        // Fed from a game's XeSS inputs, the XMX upscaler hung the graphics driver and the game then crashed at every
        // start (Crimson Desert). With this off, XeSS picked in the game runs natively; FSR and DLSS are still taken over.
        ini.Set("Inputs", "EnableXeSSInputs", Upscaler == UpscalerChoice.Fsr4Xmx ? "false" : Auto);

        ini.Set("FSR", "Fsr4GpuSupport", support);
        ini.Set("FSR", "Fsr4Model", model);

        var generating = FrameGen != FrameGenChoice.Off;
        var existingFgInput = ini.Get("FrameGen", "FGInput")?.ToLowerInvariant();

        // Preserve overlay-only variants (nvngxfg under DLSS-FG, fsrfg30 under FSR-FG) until another source is picked.
        var fgInput = !generating ? Auto : Source switch
        {
            FrameGenSource.Dlssg => existingFgInput == "nvngxfg" ? "nvngxfg" : "dlssg",
            FrameGenSource.FsrFg => existingFgInput == "fsrfg30" ? "fsrfg30" : "fsrfg",
            FrameGenSource.XeFg  => "xefg",
            _                    => "upscaler",
        };

        // With dlssg FG input, auto follows the game's own DLSS Frame Generation toggle instead of forcing it on.
        var followsGameFg = generating && fgInput == "dlssg";

        ini.Set("FrameGen", "Enabled", !generating || followsGameFg ? Auto : "true");
        ini.Set("FrameGen", "FGOutput", FrameGen switch
        {
            FrameGenChoice.XeFG  => "xefg",
            FrameGenChoice.FsrFG => "fsrfg",
            _                    => Auto,
        });
        ini.Set("FrameGen", "FGInput", fgInput);

        // OptiFG (FGInput=upscaler) defaults DisableOverlays=auto to true, which blocks Steam Overlay and breaks
        // Steam Input controller support on handhelds. Keep overlays enabled unless the user explicitly forced "true".
        var currentDisableOverlays = ini.Get("Hotfix", "DisableOverlays");
        if (generating && fgInput == "upscaler")
        {
            if (IsNullOrAuto(currentDisableOverlays))
                ini.Set("Hotfix", "DisableOverlays", "false");
        }
        else if (Is(currentDisableOverlays, "false"))
        {
            ini.Set("Hotfix", "DisableOverlays", Auto);
        }

        // OptiScaler counts generated frames, not frames shown: 2× is 1.
        ini.Set("XeFG", "InterpolationCount", FrameGen == FrameGenChoice.XeFG
            ? (Math.Clamp(Multiplier, 2, 4) - 1).ToString(CultureInfo.InvariantCulture)
            : Auto);

        var limit = double.IsFinite(FrameRateLimit) && FrameRateLimit > 0
            ? Math.Round(Math.Clamp(FrameRateLimit, MinFrameRateLimit, MaxFrameRateLimit), 1, MidpointRounding.AwayFromZero)
            : 0;
        ini.Set("Framerate", "FramerateLimit", limit > 0
            ? limit.ToString("0.#", CultureInfo.InvariantCulture)
            : Auto);

        // Spoof Nvidia across both DXGI and Vulkan so DLSS options unlock in Vulkan games as well as DirectX games.
        ini.Set("Spoofing", "Dxgi", SpoofNvidia ? Auto : "false");
        ini.Set("Spoofing", "Vulkan", SpoofNvidia ? "true" : "false");
        ini.Set("Spoofing", "VulkanExtensionSpoofing", SpoofNvidia ? "true" : "false");
        if (ueIntelAtomics) ini.Set("Spoofing", "UEIntelAtomics", SpoofNvidia ? "true" : Auto);

        if (hasOptiPatcher)
        {
            if (OptiPatcher) ini.Set("Plugins", "LoadAsiPlugins", "true");
            else if (!keepOtherAsiPlugins) ini.Set("Plugins", "LoadAsiPlugins", Auto);
        }

        static bool Is(string? value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);
        static bool IsNullOrAuto(string? value) => string.IsNullOrEmpty(value) || Is(value, Auto);
        static bool IsAutoOr(string? value, string word) => IsNullOrAuto(value) || Is(value, word);
    }

    /// <summary>
    /// These settings updated from the file, which the in-game overlay may have changed. Unknown values read as Auto;
    /// what the file cannot say (the XMX model under XeSS, the multiplier with frame generation off) is kept.
    /// </summary>
    /// <param name="hasOptiPatcher">
    /// False when <c>OptiPatcher.asi</c> is not installed in the game: <c>LoadAsiPlugins=true</c> then belongs to
    /// the user's own ASI plugins and does not count as OptiPatcher being enabled.
    /// </param>
    public UpscalerSettings CorrectedBy(IniFile ini, bool hasOptiPatcher = true)
    {
        var upscalerIndex = ini.Get("FSR", "UpscalerIndex");
        var upscaler = ini.Get("Upscalers", "Dx12Upscaler")?.ToLowerInvariant() switch
        {
            "xess"               => UpscalerChoice.XeSS,
            "ffx"                => string.IsNullOrEmpty(upscalerIndex) || Is(upscalerIndex, Auto) || upscalerIndex == "0"
                                        ? UpscalerChoice.Fsr4
                                        : UpscalerChoice.Fsr3,
            Xmx.XmxLayout.Name   => UpscalerChoice.Fsr4Xmx,
            _                    => UpscalerChoice.Auto,
        };

        var fgEnabled = ini.Get("FrameGen", "FGOutput") is not null ? ini.Get("FrameGen", "Enabled") : null;
        var fgInput = ini.Get("FrameGen", "FGInput")?.ToLowerInvariant();
        var fgActive = Is(fgEnabled, "true") || (Is(fgEnabled, Auto) && fgInput == "dlssg");

        var frameGen = !fgActive ? FrameGenChoice.Off
            : ini.Get("FrameGen", "FGOutput")?.ToLowerInvariant() switch
            {
                "xefg"  => FrameGenChoice.XeFG,
                "fsrfg" => FrameGenChoice.FsrFG,
                _       => FrameGenChoice.Off,
            };

        return this with
        {
            Upscaler = upscaler,

            Model = upscaler switch
            {
                UpscalerChoice.Fsr4 => Fsr4Model.Int8,
                UpscalerChoice.Fsr4Xmx => ini.Get(Xmx.XmxLayout.Name, XmxVersion) switch
                {
                    Xmx412     => Fsr4Model.Int8,
                    Xmx412Lite => Fsr4Model.Lite,
                    _          => Fsr4Model.Auto,
                },
                _ => Model,
            },

            FrameGen = frameGen,

            Source = frameGen == FrameGenChoice.Off ? Source : fgInput switch
            {
                "dlssg" or "nvngxfg" => FrameGenSource.Dlssg,
                "fsrfg" or "fsrfg30" => FrameGenSource.FsrFg,
                "xefg"               => FrameGenSource.XeFg,
                "upscaler"           => FrameGenSource.Upscaler,
                _                    => Source,
            },

            Multiplier = frameGen == FrameGenChoice.XeFG && int.TryParse(ini.Get("XeFG", "InterpolationCount"), out var made)
                ? Math.Clamp(made + 1, 2, 4)
                : Multiplier,

            FrameRateLimit = double.TryParse(ini.Get("Framerate", "FramerateLimit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLimit)
                && double.IsFinite(parsedLimit)
                && parsedLimit >= 1.0
                ? Math.Round(Math.Clamp(parsedLimit, MinFrameRateLimit, MaxFrameRateLimit), 1, MidpointRounding.AwayFromZero)
                : 0,

            SpoofNvidia = !Is(ini.Get("Spoofing", "Dxgi"), "false"),

            OptiPatcher = hasOptiPatcher && Is(ini.Get("Plugins", "LoadAsiPlugins"), "true"),
        };

        static bool Is(string? value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);
    }
}
