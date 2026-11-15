using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Install;

namespace Tsume.App.ViewModels;

/// <summary>
/// Controls for the OptiScaler settings Tsume manages (upscaler, frame generation, switches).
/// Used on each game's page and the defaults page; it edits settings and raises Changed, but does not store them.
/// </summary>
public sealed class UpscalerTuningViewModel : Observable
{
    private bool _loading;

    public UpscalerTuningViewModel()
    {
        ToggleSpoof = new Command(() => Change(Settings with { SpoofNvidia = !Settings.SpoofNvidia }));
        ToggleFps = new Command(() => Change(Settings with { ShowFps = !Settings.ShowFps }));
        NextProxy = new Command(() =>
        {
            // Tsume chooses → dxgi.dll → winmm.dll → … → Tsume chooses
            var at = ProxyNames.All.ToList().FindIndex(name => name.Equals(Settings.Proxy, StringComparison.OrdinalIgnoreCase));
            Change(Settings with { Proxy = at + 1 < ProxyNames.All.Count ? ProxyNames.All[at + 1] : "" });
        });
    }

    /// <summary>Raised when a control here changes a setting — not when settings are loaded.</summary>
    public event Action? Changed;

    public UpscalerSettings Settings { get; private set; } = new();

    /// <param name="canChooseName">False once OptiScaler is in the game: its file name is settled then.</param>
    /// <param name="hasXmx">True if the XMX upscaler is in the game, or would go in with an install.</param>
    public void Load(UpscalerSettings settings, bool canChooseName, bool hasXmx)
    {
        _loading = true;
        ShowsProxy = canChooseName;
        HasXmx = hasXmx;
        Settings = settings;
        Notify("");   // every control reads its value again
        _loading = false;
    }

    private void Change(UpscalerSettings next)
    {
        if (_loading || next == Settings) return;

        Settings = next;
        Notify("");
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Upscalers { get; } = [Strings.ChoiceAuto, "XeSS", "FSR 4", "FSR 3", "XMX"];

    public int UpscalerIndex
    {
        get => (int)Settings.Upscaler;
        set => Change(Settings with { Upscaler = (UpscalerChoice)value });
    }

    public string UpscalerDetail => Settings.Upscaler switch
    {
        UpscalerChoice.XeSS => Strings.UpscalerXessDetail,
        UpscalerChoice.Fsr4 => Strings.UpscalerFsr4Detail,
        UpscalerChoice.Fsr3 => Strings.UpscalerFsr3Detail,
        UpscalerChoice.Fsr4Xmx => HasXmx ? Strings.UpscalerXmxDetail : Strings.UpscalerXmxMissing,
        _                   => Strings.UpscalerAutoDetail,
    };

    /// <summary>False while Tsume has no XMX upscaler to put in, or the game was installed without one.</summary>
    public bool HasXmx { get; private set; }

    /// <summary>FSR 4 comes as several networks; the others have nothing to choose.</summary>
    public bool ShowsModel => Settings.Upscaler == UpscalerChoice.Fsr4;

    public IReadOnlyList<string> Models { get; } = [Strings.ChoiceAuto, "INT8", "INT8 Lite"];

    public int ModelIndex
    {
        get => (int)Settings.Model;
        set => Change(Settings with { Model = (Fsr4Model)value });
    }

    public string ModelDetail => Settings.Model switch
    {
        Fsr4Model.Int8 => Strings.ModelInt8Detail,
        Fsr4Model.Lite => Strings.ModelLiteDetail,
        _              => Strings.ModelAutoDetail,
    };

    /// <summary>The XMX upscaler runs the same two networks, under their version's name.</summary>
    public bool ShowsXmxModel => Settings.Upscaler == UpscalerChoice.Fsr4Xmx;

    public IReadOnlyList<string> XmxModels { get; } = [Strings.ChoiceAuto, "4.1.2", "4.1.2 Lite"];

    public string XmxModelDetail => Settings.Model switch
    {
        Fsr4Model.Int8 => Strings.XmxModelFullDetail,
        Fsr4Model.Lite => Strings.XmxModelLiteDetail,
        _              => Strings.XmxModelAutoDetail,
    };

    public IReadOnlyList<string> FrameGens { get; } = [Strings.ChoiceOff, "XeFG", "FSR FG"];

    public int FrameGenIndex
    {
        get => (int)Settings.FrameGen;
        set => Change(Settings with { FrameGen = (FrameGenChoice)value });
    }

    public bool ShowsSource => Settings.FrameGen != FrameGenChoice.Off;

    public IReadOnlyList<string> Sources { get; } = [Strings.SourceAny, "DLSS-FG", "FSR-FG"];

    public int SourceIndex
    {
        get => (int)Settings.Source;
        set => Change(Settings with { Source = (FrameGenSource)value });
    }

    public string SourceDetail => Settings.Source switch
    {
        FrameGenSource.Dlssg => Strings.SourceDlssgDetail,
        FrameGenSource.FsrFg => Strings.SourceFsrDetail,
        _                    => Strings.SourceAnyDetail,
    };

    /// <summary>Only Intel's frame generation makes more than one frame in between.</summary>
    public bool ShowsMultiplier => Settings.FrameGen == FrameGenChoice.XeFG;

    public IReadOnlyList<string> Multipliers { get; } = ["2×", "3×", "4×"];

    public int MultiplierIndex
    {
        get => Math.Clamp(Settings.Multiplier, 2, 4) - 2;
        set => Change(Settings with { Multiplier = value + 2 });
    }

    public ICommand ToggleSpoof { get; }
    public ICommand ToggleFps { get; }
    public ICommand NextProxy { get; }

    public bool SpoofNvidia => Settings.SpoofNvidia;
    public bool ShowFps => Settings.ShowFps;

    public bool ShowsProxy { get; private set; } = true;

    public string ProxyLabel => Settings.Proxy.Length == 0 ? Strings.LoadsAsAuto : Strings.LoadsAs(Settings.Proxy);
}
