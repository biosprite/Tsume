using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Tsume.App.Language;

/// <summary>
/// Every UI string, by name; the text lives in <c>Strings.resx</c> (English) and <c>Strings.&lt;language&gt;.resx</c>.
/// To add one: a <c>&lt;data&gt;</c> entry in Strings.resx and a member here with the same name; XAML uses <c>{x:Static lang:Strings.Name}</c>.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Words = new(typeof(Strings).FullName!, typeof(Strings).Assembly);

    /// <summary>A date pattern such as "dddd, MMMM d".</summary>
    public static string HeaderDate => Find();

    public static string PerformanceHeading => Find();
    public static string ModeEndurance => Find();
    public static string ModeBalanced => Find();
    public static string ModePerformance => Find();
    public static string ModeManual => Find();
    public static string Sustained => Find();
    public static string Boost => Find();
    public static string HelperOffline => Find();
    public static string DeviceNotSupported => Find();
    public static string OnlineGameCaution => Find();

    public static string Cpu => Find();
    public static string Fan => Find();
    public static string NoReading => Find();

    /// <summary>"25 W"</summary>
    public static string Watts(int watts) => Fill(Find(), watts);

    /// <summary>"61 °C"</summary>
    public static string Temperature(int celsius) => Fill(Find(), celsius);

    /// <summary>"3,200 rpm"</summary>
    public static string FanSpeed(int rpm) => Fill(Find(), rpm.ToString("N0", CultureInfo.CurrentCulture));

    public static string RefreshRate => Find();
    public static string Fans => Find();
    public static string FanAuto => Find();
    public static string FanMax => Find();
    public static string FanCustom => Find();
    public static string FanUnavailable => Find();
    public static string Keyboard => Find();
    public static string Screenshot => Find();
    public static string TaskManager => Find();
    public static string Desktop => Find();
    public static string Controller => Find();
    public static string PadGamepad => Find();
    public static string PadCursor => Find();
    public static string PadUnavailable => Find();

    /// <summary>"120 Hz"</summary>
    public static string RefreshRateValue(int hertz) => Fill(Find(), hertz);

    public static string Settings => Find();
    public static string Power => Find();
    public static string Back => Find();

    public static string PowerHeading => Find();
    public static string Hibernate => Find();
    public static string Lock => Find();
    public static string Restart => Find();
    public static string ShutDown => Find();

    public static string StartupHeading => Find();
    public static string StartWithWindows => Find();
    public static string StartWithWindowsDetail => Find();
    public static string ShowInStartMenu => Find();
    public static string ShowInStartMenuDetail => Find();

    public static string BatteryHeading => Find();
    public static string ChargeLimit => Find();

    /// <summary>"80 %"</summary>
    public static string Percent(int percent) => Fill(Find(), percent);

    public static string ChargeLimitUnavailable => Find();
    public static string ChargeLimitFull => Find();
    public static string ChargeLimitStops(int percent) => Fill(Find(), percent);
    public static string ChargeResting => Find();
    public static string ChargeCharging(int percent) => Fill(Find(), percent);
    public static string ChargeDraining => Find();
    public static string ChargeOnBattery(int percent) => Fill(Find(), percent);

    public static string MsiCenterHeading => Find();
    public static string PauseMsiCenter => Find();
    public static string MsiCenterPaused => Find();
    public static string MsiCenterRunning => Find();
    public static string MsiCenterNoHelper => Find();

    public static string QuitTsume => Find();

    public static string Lighting => Find();
    public static string EffectHeading => Find();
    public static string EffectOff => Find();
    public static string EffectSteady => Find();
    public static string EffectBreathe => Find();
    public static string EffectRainbow => Find();
    public static string EffectSpin => Find();
    public static string LightBrightness => Find();
    public static string LightSpeed => Find();
    public static string SticksHeading => Find();
    public static string ButtonsHeading => Find();
    public static string StickBrightness => Find();
    public static string ButtonBrightness => Find();
    public static string LeftStick => Find();
    public static string RightStick => Find();
    public static string FaceButtons => Find();
    public static string Hue => Find();
    public static string Saturation => Find();
    public static string SameOnBothSticks => Find();
    public static string SameOnBothSticksDetail => Find();
    public static string StickLights => Find();
    public static string StickLightsDetail => Find();
    public static string ButtonLights => Find();
    public static string ButtonLightsDetail => Find();
    public static string ButtonsSteady => Find();
    public static string ButtonsSteadyDetail => Find();
    public static string BothSticks => Find();
    public static string ControllerNotFound => Find();
    public static string ControllerNotAnswering => Find();
    public static string LightingMsiCenterRuns => Find();
    public static string LightingSaved => Find();

    public static string QuitHeading => Find();
    public static string QuitExplanation => Find();
    public static string KeepRunning => Find();

    public static string OptiScaler => Find();
    public static string FeaturesHeading => Find();

    public static string AppearanceHeading => Find();
    public static string Customization => Find();
    public static string CustomizationDetail => Find();
    public static string CustomizationSaved => Find();
    public static string PresetsHeading => Find();
    public static string ColoursHeading => Find();
    public static string ColourAccent => Find();
    public static string ColourBackground => Find();
    public static string ColourText => Find();
    public static string AccentBrightness => Find();
    public static string BackgroundBrightness => Find();
    public static string TextBrightness => Find();
    public static string PresetDefault => Find();
    public static string PresetEmber => Find();
    public static string PresetLeaf => Find();
    public static string PresetViolet => Find();
    public static string PresetGold => Find();
    public static string PresetRose => Find();
    public static string PresetMono => Find();
    public static string PresetDaylight => Find();
    public static string ShowOptiScaler => Find();
    public static string ShowOptiScalerDetail => Find();
    public static string Experimental => Find();

    public static string PerformancePresets => Find();
    public static string PerformancePresetsDetail => Find();
    public static string PerformancePresetsSaved => Find();
    public static string PresetsReset => Find();

    /// <summary>"Default 25 W / 37 W"</summary>
    public static string PresetStock(int sustained, int boost) => Fill(Find(), sustained, boost);

    /// <summary>"Balanced: Sustained" — a slider's name for a screen reader.</summary>
    public static string PresetLimit(string mode, string limit) => Fill(Find(), mode, limit);

    /// <summary>"3 games"</summary>
    public static string GamesWithOptiScaler(int count) => count == 1 ? Find("OneGameWithOptiScaler") : Fill(Find(), count);

    public static string GamesHeading => Find();
    public static string ToolsHeading => Find();
    public static string NoGamesYet => Find();
    public static string LookingForGames => Find();
    public static string AddGame => Find();
    public static string Defaults => Find();
    public static string OptiScalerBuild => Find();
    public static string OpenGameMenu => Find();
    public static string OpenGameMenuDetail => Find();

    /// <summary>"Installed · nightly of 11 November"</summary>
    public static string GameInstalled(string night) => Fill(Find(), night);
    public static string GameUpdateReady => Find();
    public static string GameNeedsRepair => Find();
    public static string GameCutShort => Find();
    public static string GameFolderGone => Find();
    public static string GameNothingToRun => Find();
    public static string GameNoUpscaler => Find();

    /// <summary>"Easy Anti-Cheat — not offered"</summary>
    public static string GameBlocked(string antiCheat) => Fill(Find(), antiCheat);

    public static string NotInstalled => Find();

    /// <summary>"Installed · nightly of 11 November · loads as dxgi.dll"</summary>
    public static string StateInstalled(string night, string proxy) => Fill(Find(), night, proxy);
    public static string StateReplaced => Find();
    public static string StateCutShort => Find();
    public static string StateFolderGone => Find();
    public static string StateBlocked(string antiCheat) => Fill(Find(), antiCheat);
    public static string StateNothingToRun => Find();

    /// <summary>"Found: DLSS, FSR 3"</summary>
    public static string FoundUpscalers(string list) => Fill(Find(), list);
    public static string FoundNoUpscalers => Find();
    public static string SomeoneElsesOptiScaler(string proxy) => Fill(Find(), proxy);
    public static string NewerBuildReady(string night) => Fill(Find(), night);

    public static string InstallOptiScaler => Find();
    public static string UpdateOptiScaler => Find();
    public static string RepairOptiScaler => Find();
    public static string RemoveOptiScaler => Find();
    public static string ForgetInstall => Find();
    public static string TakeOffList => Find();

    public static string CheckingBuild => Find();

    /// <summary>"Downloading the build… 43 %"</summary>
    public static string Downloading(int percent) => Fill(Find(), percent);
    public static string Unpacking => Find();
    public static string Installing => Find();
    public static string Removing => Find();
    public static string InstalledDone => Find();
    public static string RemovedDone => Find();
    public static string RemovedButLeft(int count) => Fill(Find(), count);
    public static string Forgotten => Find();
    public static string SettingsSaved => Find();

    public static string UpscalerHeading => Find();
    public static string ChoiceAuto => Find();
    public static string UpscalerAutoDetail => Find();
    public static string UpscalerXessDetail => Find();
    public static string UpscalerFsr4Detail => Find();
    public static string UpscalerFsr3Detail => Find();
    public static string ModelAutoDetail => Find();
    public static string ModelInt8Detail => Find();
    public static string ModelLiteDetail => Find();
    public static string UpscalerXmxDetail => Find();
    public static string UpscalerXmxMissing => Find();
    public static string XmxModelAutoDetail => Find();
    public static string XmxModelFullDetail => Find();
    public static string XmxModelLiteDetail => Find();

    public static string FrameGenHeading => Find();
    public static string ChoiceOff => Find();
    public static string SourceAny => Find();
    public static string SourceAnyDetail => Find();
    public static string SourceDlssgDetail => Find();
    public static string SourceFsrDetail => Find();

    public static string InGameHeading => Find();
    public static string SpoofNvidia => Find();
    public static string SpoofNvidiaDetail => Find();
    public static string ShowFps => Find();
    public static string ShowFpsDetail => Find();

    /// <summary>"Loads as winmm.dll"</summary>
    public static string LoadsAs(string proxy) => Fill(Find(), proxy);
    public static string LoadsAsAuto => Find();
    public static string LoadsAsDetail => Find();

    /// <summary>"Nightly of 11 November"</summary>
    public static string NightlyOf(string night) => Fill(Find(), night);
    public static string NoBuildOnDisk => Find();
    public static string NightlyExplained => Find();
    public static string GetNewestBuild => Find();
    public static string HaveNewest => Find();
    public static string GotNewer(string night) => Fill(Find(), night);

    /// <summary>"Update 2 games"</summary>
    public static string UpdateGames(int count) => count == 1 ? Find("UpdateOneGame") : Fill(Find(), count);
    public static string UpdateAllGames => Find();
    public static string AllGamesCurrent => Find();
    public static string RemoveOldBuilds => Find();
    public static string RemoveOldBuildsSized(long bytes) => Fill(Find(), Size(bytes));
    public static string NoOldBuilds => Find();
    public static string RemovedOldBuilds(long bytes) => Fill(Find(), Size(bytes));
    public static string OldBuildsInUse => Find();

    /// <summary>"283 MB", "1.4 GB".</summary>
    private static string Size(long bytes) => bytes >= 1L << 30
        ? Fill(Find("SizeGB"), bytes / (double)(1L << 30))
        : Fill(Find("SizeMB"), Math.Max(1, bytes >> 20));
    public static string GamesUpToDate => Find();
    public static string UpdatedGames(int count) => Fill(Find(), count);

    /// <summary>"Witchfire: The game is running. Close it first."</summary>
    public static string CouldNotUpdate(string game, string why) => Fill(Find(), game, why);

    public static string ExtrasHeading => Find();
    public static string ExtrasExplained => Find();
    public static string OpenExtras => Find();
    public static string ExtrasEmpty => Find();
    public static string ExtrasCount(int count) => count == 1 ? Find("ExtrasOne") : Fill(Find(), count);

    public static string XmxHeading => Find();
    public static string XmxExplained => Find();
    public static string AddXmx => Find();
    public static string ReplaceXmx => Find();
    public static string XmxNotAdded => Find();

    /// <summary>"Version 4.0.2.1 · added 11 November"</summary>
    public static string XmxHeld(string version, string day) => Fill(Find(), version, day);
    public static string XmxNotFound => Find();
    public static string XmxAdded => Find();
    public static string XmxSame => Find();
    public static string XmxReady => Find();
    public static string XmxNotInGame => Find();

    public static string DefaultsExplained => Find();
    public static string BrowseHint => Find();
    public static string BrowseUp => Find();
    public static string ThisPC => Find();
    public static string CannotOpenFolder => Find();

    /// <summary>What went wrong, in a sentence.</summary>
    public static string Explain(Exception error) => error is Tsume.Upscaling.UpscalingException known
        ? Find("Trouble" + known.Trouble)
        : Fill(Find("TroubleOther"), error.Message);

    public static string ShortcutDescription => Find();

    public static string UninstallTitle => Find();
    public static string RestoreGames => Find();
    public static string RestoreGamesDetail(int count) => count == 1 ? Find("RestoreOneGameDetail") : Fill(Find(), count);
    public static string RestoreGamesExplained => Find();
    public static string RestoreGamesRetry => Find();
    public static string ContinueUninstall => Find();

    public static string RestoreAllGames => Find();
    public static string RestoreAllDetail => Find();
    public static string RestoreAllHeading => Find();
    public static string KeepOptiScaler => Find();
    public static string RestoredGames(int count) => Fill(Find(), count);

    /// <summary>The string named after the calling member; a name with no entry is shown as the name itself.</summary>
    private static string Find([CallerMemberName] string name = "") =>
        Words.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Fill(string pattern, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, pattern, values);
}
