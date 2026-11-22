using System.Globalization;

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
    /// (see <see cref="Xmx.XmxStore"/>). DirectX 12 games only.
    /// </summary>
    Fsr4Xmx,
}

/// <summary>Which of FSR 4's networks to run. The INT8 ones are those that run on Intel Arc.</summary>
public enum Fsr4Model
{
    /// <summary>OptiScaler picks the best one it believes the GPU supports.</summary>
    Auto,

    /// <summary>FSR 4.1.2's full network.</summary>
    Int8,

    /// <summary>FSR 4.1.2's smaller network, made for integrated graphics.</summary>
    Lite,
}

public enum FrameGenChoice
{
    Off,

    /// <summary>Intel's frame generation.</summary>
    XeFG,

    /// <summary>AMD's frame generation.</summary>
    FsrFG,
}

/// <summary>Where frame generation gets what it needs from the game.</summary>
public enum FrameGenSource
{
    /// <summary>From the upscaler: works in any game that upscales, though the HUD may shimmer.</summary>
    Upscaler,

    /// <summary>From the game's own DLSS frame generation, switched on in the game.</summary>
    Dlssg,

    /// <summary>From the game's own FSR frame generation, switched on in the game.</summary>
    FsrFg,
}

/// <summary>
/// The OptiScaler settings Tsume manages, for one game or as the defaults.
/// Everything else in OptiScaler.ini is left untouched, for the in-game overlay.
/// </summary>
public sealed record UpscalerSettings
{
    /// <summary>
    /// The default upscaler for the detected GPU: XeSS on Intel devices, FSR 4 on AMD devices,
    /// and Auto elsewhere.
    /// </summary>
    public static UpscalerChoice DefaultUpscaler(bool hasIntelGpu, bool hasAmdGpu) =>
        hasAmdGpu ? UpscalerChoice.Fsr4
        : hasIntelGpu ? UpscalerChoice.XeSS
        : UpscalerChoice.Auto;

    public static UpscalerChoice DefaultUpscaler() =>
        DefaultUpscaler(GpuVendor.HasIntelGpu(), GpuVendor.HasAmdGpu());

    public UpscalerChoice Upscaler { get; init; } = DefaultUpscaler();
    public Fsr4Model Model { get; init; } = Fsr4Model.Int8;

    public FrameGenChoice FrameGen { get; init; } = FrameGenChoice.Off;
    public FrameGenSource Source { get; init; } = FrameGenSource.Upscaler;

    /// <summary>Frames shown per frame drawn, 2–4. XeFG only; FSR's is always 2.</summary>
    public int Multiplier { get; init; } = 2;

    /// <summary>Tell the game an Nvidia card is fitted, so it offers DLSS for OptiScaler to take over.</summary>
    public bool SpoofNvidia { get; init; } = true;

    /// <summary>The file name OptiScaler is installed under. Empty: Tsume chooses.</summary>
    public string Proxy { get; init; } = "";

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
    /// Adjusts settings for what the target game actually supports: falls back to plain FSR 4 (INT8) when XMX
    /// is chosen but not in the game's folder, and maps plain FSR 4's Lite choice to INT8 (AMD's DX12 upscaler
    /// does not offer the Lite network).
    /// </summary>
    public UpscalerSettings Fitting(bool hasXmx)
    {
        if (Upscaler == UpscalerChoice.Fsr4Xmx && !hasXmx)
            return this with { Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Int8 };

        if (Upscaler == UpscalerChoice.Fsr4 && Model == Fsr4Model.Lite)
            return this with { Model = Fsr4Model.Int8 };

        return this;
    }

    /// <summary>Writes these settings into the file, and only these.</summary>
    /// <param name="ueIntelAtomics">
    /// True for an Unreal Engine game on an Intel GPU: sets <c>UEIntelAtomics=true</c> while Nvidia spoofing is on
    /// so Unreal does not fail with a DirectX 12 unsupported error.
    /// </param>
    public void WriteTo(IniFile ini, bool ueIntelAtomics = false)
    {
        // Each graphics API names its upscalers differently. The XMX upscaler is DirectX 12 only; XeSS covers the other two.
        // Native DX11 XeSS is Intel Arc only (non-Intel GPUs use dx11on12); native DX11 FSR 3.1 avoids dx11on12 instability.
        var dx11Xess = GpuVendor.HasIntelGpu() ? "xess" : "xess_12";
        var (dx11, dx12, vulkan, backend) = Upscaler switch
        {
            UpscalerChoice.XeSS    => (dx11Xess, "xess", "xess", Auto),
            UpscalerChoice.Fsr4    => ("ffx_12", "ffx", "ffx_12", "0"),
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

        ini.Set("Upscalers", "Dx11Upscaler", dx11);
        ini.Set("Upscalers", "Dx12Upscaler", dx12);
        ini.Set("Upscalers", "VulkanUpscaler", vulkan);
        ini.Set("FSR", "UpscalerIndex", backend);

        // FSR 4 checks for a Radeon; Fsr4GpuSupport overrides that for the INT8 network, and Fsr4Model names it.
        // AMD's DX12 upscaler does not include INT8 Lite (only Vulkan driver FSR does), so Lite maps to INT8.
        var (support, model) = (Upscaler, Model) switch
        {
            (UpscalerChoice.Fsr4, Fsr4Model.Int8 or Fsr4Model.Lite) => ("2", "1"),
            _                                                       => (Auto, Auto),
        };

        // Fed from a game's XeSS inputs, the XMX upscaler hung the graphics driver and the game then crashed at every
        // start (Crimson Desert). With this off, XeSS picked in the game runs natively; FSR and DLSS are still taken over.
        ini.Set("Inputs", "EnableXeSSInputs", Upscaler == UpscalerChoice.Fsr4Xmx ? "false" : Auto);

        ini.Set("FSR", "Fsr4GpuSupport", support);
        ini.Set("FSR", "Fsr4Model", model);

        var generating = FrameGen != FrameGenChoice.Off;
        // With dlssg FG input, auto follows the game's own DLSS Frame Generation toggle instead of forcing it on.
        var followsGameFg = generating && Source == FrameGenSource.Dlssg;

        ini.Set("FrameGen", "Enabled", !generating || followsGameFg ? Auto : "true");
        ini.Set("FrameGen", "FGOutput", FrameGen switch
        {
            FrameGenChoice.XeFG  => "xefg",
            FrameGenChoice.FsrFG => "fsrfg",
            _                    => Auto,
        });
        ini.Set("FrameGen", "FGInput", !generating ? Auto : Source switch
        {
            FrameGenSource.Dlssg => "dlssg",
            FrameGenSource.FsrFg => "fsrfg",
            _                    => "upscaler",
        });

        // OptiScaler counts generated frames, not frames shown: 2× is 1.
        ini.Set("XeFG", "InterpolationCount", FrameGen == FrameGenChoice.XeFG
            ? (Math.Clamp(Multiplier, 2, 4) - 1).ToString(CultureInfo.InvariantCulture)
            : Auto);

        ini.Set("Spoofing", "Dxgi", SpoofNvidia ? Auto : "false");
        if (ueIntelAtomics) ini.Set("Spoofing", "UEIntelAtomics", SpoofNvidia ? "true" : Auto);
    }

    /// <summary>
    /// These settings updated from the file, which the in-game overlay may have changed. Unknown values read as Auto;
    /// what the file cannot say (the FSR 4 model under XeSS, the multiplier with frame generation off) is kept.
    /// </summary>
    public UpscalerSettings CorrectedBy(IniFile ini)
    {
        var upscaler = ini.Get("Upscalers", "Dx12Upscaler")?.ToLowerInvariant() switch
        {
            "xess"               => UpscalerChoice.XeSS,
            "ffx"                => ini.Get("FSR", "UpscalerIndex") == "0" ? UpscalerChoice.Fsr4 : UpscalerChoice.Fsr3,
            Xmx.XmxLayout.Name   => UpscalerChoice.Fsr4Xmx,
            _                    => UpscalerChoice.Auto,
        };

        var fgEnabled = ini.Get("FrameGen", "Enabled");
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
                UpscalerChoice.Fsr4 => ini.Get("FSR", "Fsr4Model") switch
                {
                    "1" or "3" => Fsr4Model.Int8,
                    _          => Fsr4Model.Auto,
                },
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
                "dlssg" => FrameGenSource.Dlssg,
                "fsrfg" => FrameGenSource.FsrFg,
                _       => FrameGenSource.Upscaler,
            },

            Multiplier = frameGen == FrameGenChoice.XeFG && int.TryParse(ini.Get("XeFG", "InterpolationCount"), out var made)
                ? Math.Clamp(made + 1, 2, 4)
                : Multiplier,

            SpoofNvidia = !Is(ini.Get("Spoofing", "Dxgi"), "false"),
        };

        static bool Is(string? value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);
    }
}
