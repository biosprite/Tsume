using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.Upscaling;
using Tsume.Upscaling.Config;
using Tsume.Upscaling.Install;

namespace Tsume.App.ViewModels;

/// <summary>One choice in the "Install as" proxy DLL dropdown.</summary>
public sealed record ProxyOption(string Value, string Label, string Hint);

/// <summary>
/// Controls for the OptiScaler settings Tsume manages (upscaler, frame generation, switches, proxy DLL).
/// Used on each game's page and the defaults page; it edits settings and raises Changed, but does not store them.
/// </summary>
public sealed class UpscalerTuningViewModel : Observable
{
    private static readonly IReadOnlySet<int> DisabledXmx = new HashSet<int> { (int)UpscalerChoice.Fsr4Xmx };
    private static readonly IReadOnlySet<int> DisabledMultiFrame = new HashSet<int> { 1, 2 };
    private static readonly ProxyOption AsiProxyOption = new(ProxyNames.AsiName, ProxyNames.AsiName, Strings.ProxyAsiHint);

    private readonly List<ProxyOption> _standardProxyOptions;
    private readonly IReadOnlyList<ProxyOption> _asiProxyOptions;
    private bool _loading;
    private string? _lockedProxy;
    private HashSet<string>? _occupiedProxies;
    private bool _xmxAvailableToAdd;

    public UpscalerTuningViewModel()
    {
        ToggleSpoof = new Command(() => Change(Settings with { SpoofNvidia = !Settings.SpoofNvidia }));
        ToggleOptiPatcher = new Command(() =>
        {
            if (!HasOptiPatcher) return;
            Change(Settings with { OptiPatcher = !Settings.OptiPatcher });
        });

        _standardProxyOptions = BuildProxyOptions();
        _asiProxyOptions = [.. _standardProxyOptions, AsiProxyOption];

        Upscalers = SupportsXmx
            ? [Strings.ChoiceAuto, "XeSS", "FSR 4", "FSR 3", "XMX"]
            : [Strings.ChoiceAuto, "XeSS", "FSR 4", "FSR 3"];
    }

    /// <summary>Raised when a control here changes a setting — not when settings are loaded.</summary>
    public event Action? Changed;

    public UpscalerSettings Settings { get; private set; } = new();

    /// <summary>True on Intel GPUs with hardware XMX matrix units; non-XMX and AMD devices hide XMX options everywhere.</summary>
    public bool SupportsXmx => GpuVendor.HasIntelXmxGpu();

    /// <param name="hasXmx">True if the XMX upscaler is in the game, or would go in with an install.</param>
    /// <param name="hasOptiPatcher">True if OptiPatcher.asi is in the game, or would go in with an install.</param>
    /// <param name="isInstalled">True once OptiScaler is cleanly installed in this game (locks install-time choices).</param>
    /// <param name="lockedProxy">
    /// The fixed proxy DLL name when already installed (or already placed by another tool); null when editable.
    /// </param>
    /// <param name="xmxAvailableToAdd">
    /// True when the game is installed without XMX while Tsume holds an XMX archive that Update can add.
    /// </param>
    /// <param name="occupiedProxies">
    /// Non-OptiScaler proxy DLL names already sitting in the game's folder (e.g. ReShade's dxgi.dll).
    /// </param>
    public void Load(UpscalerSettings settings, bool hasXmx, bool hasOptiPatcher = false, bool isInstalled = false,
        string? lockedProxy = null, bool xmxAvailableToAdd = false, bool showOptiPatcher = true,
        IReadOnlyList<string>? occupiedProxies = null)
    {
        _loading = true;
        HasXmx = SupportsXmx && hasXmx;
        HasOptiPatcher = hasOptiPatcher;
        ShowOptiPatcher = showOptiPatcher;
        IsInstalled = isInstalled;
        _lockedProxy = lockedProxy;
        _xmxAvailableToAdd = SupportsXmx && xmxAvailableToAdd;
        _occupiedProxies = occupiedProxies is { Count: > 0 }
            ? occupiedProxies.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        Settings = settings.Fitting(HasXmx, hasOptiPatcher);
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

    public IReadOnlyList<string> Upscalers { get; }

    /// <summary>Greys out XMX on Intel XMX GPUs when Tsume has no XMX archive or the installed game does not carry XMX yet.</summary>
    public IReadOnlySet<int>? DisabledUpscalers => !SupportsXmx || HasXmx ? null : DisabledXmx;

    public int UpscalerIndex
    {
        get => !SupportsXmx && Settings.Upscaler == UpscalerChoice.Fsr4Xmx
            ? (int)UpscalerSettings.DefaultUpscaler()
            : (int)Settings.Upscaler;
        set
        {
            var choice = (UpscalerChoice)value;
            if (choice == UpscalerChoice.Fsr4Xmx && (!SupportsXmx || !HasXmx)) return;

            var model = choice == UpscalerChoice.Fsr4 ? Fsr4Model.Int8 : Settings.Model;
            Change(Settings with { Upscaler = choice, Model = model });
        }
    }

    public string UpscalerDetail => Settings.Upscaler switch
    {
        UpscalerChoice.XeSS    => Strings.UpscalerXessDetail,
        UpscalerChoice.Fsr4    => Strings.UpscalerFsr4Detail,
        UpscalerChoice.Fsr3    => Strings.UpscalerFsr3Detail,
        UpscalerChoice.Fsr4Xmx => HasXmx ? Strings.UpscalerXmxDetail : Strings.UpscalerXmxMissing,
        _                      => Strings.UpscalerAutoDetail,
    };

    /// <summary>Explains why XMX is greyed out on Intel XMX devices when it is not yet in the build/game.</summary>
    public string XmxHint => !SupportsXmx || HasXmx ? "" : _xmxAvailableToAdd ? Strings.XmxLockedUpdate : Strings.XmxLockedBuild;

    public Visibility XmxHintShown => XmxHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>False while Tsume has no XMX upscaler to put in, or the game was installed without one.</summary>
    public bool HasXmx { get; private set; }

    /// <summary>True once OptiScaler is installed in the game.</summary>
    public bool IsInstalled { get; private set; }

    /// <summary>The XMX upscaler runs both 4.1.2 networks (or defers to the overlay on Auto).</summary>
    public bool ShowsXmxModel => SupportsXmx && Settings.Upscaler == UpscalerChoice.Fsr4Xmx;

    public IReadOnlyList<string> XmxModels { get; } = [Strings.ChoiceAuto, "4.1.2", "4.1.2 Lite"];

    public int ModelIndex
    {
        get => (int)Settings.Model;
        set => Change(Settings with { Model = (Fsr4Model)value });
    }

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
        set
        {
            var choice = (FrameGenChoice)value;
            var multiplier = choice == FrameGenChoice.XeFG && !SupportsMultiFrame && Settings.Multiplier > 2
                ? 2
                : Settings.Multiplier;
            Change(Settings with { FrameGen = choice, Multiplier = multiplier });
        }
    }

    public string FrameGenDetail => Settings.FrameGen switch
    {
        FrameGenChoice.XeFG  => Strings.FrameGenXefgDetail,
        FrameGenChoice.FsrFG => Strings.FrameGenFsrDetail,
        _                    => "",
    };

    public Visibility FrameGenDetailShown => FrameGenDetail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool ShowsSource => Settings.FrameGen != FrameGenChoice.Off;

    public IReadOnlyList<string> Sources { get; } = [Strings.SourceAny, "DLSS-FG", "FSR-FG", "XeSS-FG"];

    public int SourceIndex
    {
        get => (int)Settings.Source;
        set => Change(Settings with { Source = (FrameGenSource)value });
    }

    public string SourceDetail => Settings.Source switch
    {
        FrameGenSource.Dlssg => Strings.SourceDlssgDetail,
        FrameGenSource.FsrFg => Strings.SourceFsrDetail,
        FrameGenSource.XeFg  => Strings.SourceXefgDetail,
        _                    => Strings.SourceAnyDetail,
    };

    /// <summary>Only Intel's frame generation makes more than one frame in between.</summary>
    public bool ShowsMultiplier => Settings.FrameGen == FrameGenChoice.XeFG;

    /// <summary>True on Intel Arc GPUs, which support XeFG multi-frame generation (3× and 4×).</summary>
    public bool SupportsMultiFrame => GpuVendor.HasIntelArcGpu();

    /// <summary>Greys out 3× and 4× on non-Intel GPUs, where XeFG only supports 2×.</summary>
    public IReadOnlySet<int>? DisabledMultipliers => SupportsMultiFrame ? null : DisabledMultiFrame;

    public IReadOnlyList<string> Multipliers { get; } = ["2×", "3×", "4×"];

    public int MultiplierIndex
    {
        get => SupportsMultiFrame ? Math.Clamp(Settings.Multiplier, 2, 4) - 2 : 0;
        set
        {
            if (!SupportsMultiFrame && value > 0) return;
            Change(Settings with { Multiplier = Math.Clamp(value + 2, 2, 4) });
        }
    }

    public string MultiplierHint => SupportsMultiFrame ? "" : Strings.MultiplierIntelOnly;

    public Visibility MultiplierHintShown => MultiplierHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public ICommand ToggleSpoof { get; }

    public bool SpoofNvidia => Settings.SpoofNvidia;

    public ICommand ToggleOptiPatcher { get; }

    /// <summary>False on Global Defaults because OptiPatcher is strictly per game.</summary>
    public bool ShowOptiPatcher { get; private set; } = true;

    public Visibility OptiPatcherShown => ShowOptiPatcher ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>False while Tsume has not downloaded OptiPatcher, or the game was installed without it.</summary>
    public bool HasOptiPatcher { get; private set; }

    public bool OptiPatcher => HasOptiPatcher && Settings.OptiPatcher;

    public string OptiPatcherDetail => !HasOptiPatcher
        ? Strings.OptiPatcherLockedBuild
        : Settings.OptiPatcher
            ? Strings.OptiPatcherDetailOn
            : Strings.OptiPatcherDetailOff;

    public IReadOnlyList<ProxyOption> ProxyOptions =>
        string.Equals(_lockedProxy, ProxyNames.AsiName, StringComparison.OrdinalIgnoreCase)
            ? _asiProxyOptions
            : _standardProxyOptions;

    /// <summary>True only before install and when no other OptiScaler proxy is already present.</summary>
    public bool IsProxyEditable => !IsInstalled && _lockedProxy is null;

    public string ProxyDetail =>
        IsInstalled && _lockedProxy is not null ? Strings.LoadsAsLockedInstalled(_lockedProxy)
        : _lockedProxy is not null ? Strings.LoadsAsLockedExisting(_lockedProxy)
        : _occupiedProxies?.Contains(SelectedProxyOption.Value) == true ? Strings.LoadsAsOccupied(SelectedProxyOption.Value)
        : Strings.LoadsAsDetail;

    public ProxyOption SelectedProxyOption
    {
        get
        {
            var active = _lockedProxy ?? Settings.Proxy;
            if (string.Equals(active, ProxyNames.AsiName, StringComparison.OrdinalIgnoreCase))
                return AsiProxyOption;

            return _standardProxyOptions.FirstOrDefault(option => option.Value.Equals(active, StringComparison.OrdinalIgnoreCase))
                ?? _standardProxyOptions[0];
        }
        set
        {
            if (_loading || !IsProxyEditable || value is null) return;
            Change(Settings with { Proxy = value.Value });
        }
    }

    private static List<ProxyOption> BuildProxyOptions()
    {
        var list = new List<ProxyOption>(ProxyNames.All.Count);

        foreach (var name in ProxyNames.All)
        {
            var hint = name.ToLowerInvariant() switch
            {
                "dxgi.dll"    => Strings.ProxyDxgiHint,
                "winmm.dll"   => Strings.ProxyWinmmHint,
                "version.dll" => Strings.ProxyVersionHint,
                "dbghelp.dll" => Strings.ProxyDbghelpHint,
                "d3d12.dll"   => Strings.ProxyD3d12Hint,
                "wininet.dll" => Strings.ProxyWininetHint,
                "winhttp.dll" => Strings.ProxyWinhttpHint,
                _             => "",
            };
            list.Add(new ProxyOption(name, name, hint));
        }

        return list;
    }
}
