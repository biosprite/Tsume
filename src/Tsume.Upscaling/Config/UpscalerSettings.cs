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
    public UpscalerChoice Upscaler { get; init; } = UpscalerChoice.Fsr4Xmx;
    public Fsr4Model Model { get; init; } = Fsr4Model.Lite;

    public FrameGenChoice FrameGen { get; init; } = FrameGenChoice.Off;
    public FrameGenSource Source { get; init; } = FrameGenSource.Upscaler;

    /// <summary>Frames shown per frame drawn, 2–4. XeFG only; FSR's is always 2.</summary>
    public int Multiplier { get; init; } = 2;

    /// <summary>Tell the game an Nvidia card is fitted, so it offers DLSS for OptiScaler to take over.</summary>
    public bool SpoofNvidia { get; init; } = true;

    public bool ShowFps { get; init; }

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
    /// Falls back to plain FSR 4 when the XMX upscaler is chosen but not in the game's folder. Its full network
    /// is used because AMD's upscaler does not offer the Lite one to DirectX 12 games.
    /// </summary>
    public UpscalerSettings Fitting(bool hasXmx) => Upscaler == UpscalerChoice.Fsr4Xmx && !hasXmx
        ? this with { Upscaler = UpscalerChoice.Fsr4, Model = Fsr4Model.Int8 }
        : this;

    /// <summary>Writes these settings into the file, and only these.</summary>
    public void WriteTo(IniFile ini)
    {
        // Each graphics API names its upscalers differently. The XMX upscaler is DirectX 12 only; XeSS covers the other two.
        var (dx11, dx12, vulkan, backend) = Upscaler switch
        {
            UpscalerChoice.XeSS    => ("xess", "xess", "xess", Auto),
            UpscalerChoice.Fsr4    => ("ffx_12", "ffx", "ffx_12", "0"),
            UpscalerChoice.Fsr3    => ("ffx_12", "ffx", "ffx", "1"),
            UpscalerChoice.Fsr4Xmx => ("xess", Xmx.XmxLayout.Name, "xess", Auto),
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

        // FSR 4 checks for a Radeon; Fsr4GpuSupport overrides that for the INT8 networks, and Fsr4Model names the one to run.
        var (support, model) = (Upscaler, Model) switch
        {
            (UpscalerChoice.Fsr4, Fsr4Model.Int8) => ("2", "1"),
            (UpscalerChoice.Fsr4, Fsr4Model.Lite) => ("4", "3"),
            _                                     => (Auto, Auto),
        };

        // Fed from a game's XeSS inputs, the XMX upscaler hung the graphics driver and the game then crashed at every
        // start (Crimson Desert). With this off, XeSS picked in the game runs natively; FSR and DLSS are still taken over.
        ini.Set("Inputs", "EnableXeSSInputs", Upscaler == UpscalerChoice.Fsr4Xmx ? "false" : Auto);

        ini.Set("FSR", "Fsr4GpuSupport", support);
        ini.Set("FSR", "Fsr4Model", model);

        var generating = FrameGen != FrameGenChoice.Off;

        ini.Set("FrameGen", "Enabled", generating ? "true" : Auto);
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
        ini.Set("Menu", "ShowFps", ShowFps ? "true" : Auto);
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

        var frameGen = !Is(ini.Get("FrameGen", "Enabled"), "true") ? FrameGenChoice.Off
            : ini.Get("FrameGen", "FGOutput") switch
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
                    "1" => Fsr4Model.Int8,
                    "3" => Fsr4Model.Lite,
                    _   => Fsr4Model.Auto,
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

            Source = frameGen == FrameGenChoice.Off ? Source : ini.Get("FrameGen", "FGInput") switch
            {
                "dlssg" => FrameGenSource.Dlssg,
                "fsrfg" => FrameGenSource.FsrFg,
                _       => FrameGenSource.Upscaler,
            },

            Multiplier = frameGen == FrameGenChoice.XeFG && int.TryParse(ini.Get("XeFG", "InterpolationCount"), out var made)
                ? Math.Clamp(made + 1, 2, 4)
                : Multiplier,

            SpoofNvidia = !Is(ini.Get("Spoofing", "Dxgi"), "false"),
            ShowFps = Is(ini.Get("Menu", "ShowFps"), "true"),
        };

        static bool Is(string? value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);
    }
}
