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
    public static string ChargeRestingHint(int resumeBelow) => Fill(Find(), resumeBelow);
    public static string ChargeCharging(int percent) => Fill(Find(), percent);
    public static string ChargeDraining => Find();
    public static string ChargeOnBattery(int percent) => Fill(Find(), percent);

    public static string MsiCenterHeading => Find();
    public static string PauseMsiCenter => Find();
    public static string MsiCenterPaused => Find();
    public static string MsiCenterRunning => Find();
    public static string MsiCenterNoHelper => Find();

    public static string TroubleshootingHeading => Find();
    public static string ShareLogs => Find();
    public static string ShareLogsDetail => Find();
    public static string ShareLogsBusy => Find();
    public static string ShareLogsFailed(string why) => Fill(Find(), why);
    public static string OpenDataFolder => Find();
    public static string OpenDataFolderDetail => Find();

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
    public static string ControllerFirmwareTooOld(string version) => Fill(Find(), version);
    public static string LightingMsiCenterRuns => Find();
    public static string LightingSaved => Find();

    public static string QuitHeading => Find();
    public static string QuitExplanation => Find();
    public static string KeepRunning => Find();

    public static string OptiScaler => Find();

    public static string CustomizationHeading => Find();
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
    public static string RescanGames => Find();
    public static string ScanningGames => Find();
    public static string ScanFoundGames(int count) => count switch
    {
        0 => Find("ScanFoundNoGames"),
        1 => Find("ScanFoundOneGame"),
        _ => Fill(Find(), count),
    };
    public static string AddGame => Find();
    public static string AddFolder => Find();
    public static string FolderCount(int count) => count == 1 ? Find("OneFolder") : Fill(Find(), count);
    public static string Defaults => Find();
    public static string DefaultsDetail => Find();
    public static string OptiScalerBuild => Find();
    public static string OpenGameMenu => Find();
    public static string OpenGameMenuDetail => Find();

    /// <summary>"Installed · nightly of 11 November"</summary>
    public static string TagUpdateReady => Find();
    public static string TagBlocked => Find();
    public static string GameInstalled(string night) => Fill(Find(), night);
    public static string GameInstalledWithOptiPatcher(string night) => Fill(Find(), night);
    public static string GameUpdateReady => Find();
    public static string GameUpdateReadyWithOptiPatcher => Find();
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
    public static string OpenGameFolder => Find();

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

    public static string FrameGenOutputHeading => Find();
    public static string FrameGenXefgDetail => Find();
    public static string FrameGenFsrDetail => Find();
    public static string FrameGenInputHeading => Find();
    public static string FrameGenMultiplierHeading => Find();
    public static string ChoiceOff => Find();
    public static string SourceAny => Find();
    public static string SourceAnyDetail => Find();
    public static string SourceDlssgDetail => Find();
    public static string SourceFsrDetail => Find();
    public static string SourceXefgDetail => Find();

    public static string InGameHeading => Find();
    public static string SpoofNvidia => Find();
    public static string SpoofNvidiaDetail => Find();
    public static string OptiPatcher => Find();
    public static string OptiPatcherDetailOn => Find();
    public static string OptiPatcherDetailOff => Find();
    public static string OptiPatcherLockedBuild => Find();
    public static string OptiPatcherBadge => Find();
    public static string OptiPatcherBadgeVersion(string tag) => Fill(Find(), tag);

    public static string InstallAs => Find();

    /// <summary>"Loads as winmm.dll"</summary>
    public static string LoadsAs(string proxy) => Fill(Find(), proxy);
    public static string LoadsAsDetail => Find();
    public static string LoadsAsOccupied(string proxy) => Fill(Find(), proxy);
    public static string LoadsAsLockedInstalled(string proxy) => Fill(Find(), proxy);
    public static string LoadsAsLockedExisting(string proxy) => Fill(Find(), proxy);
    public static string ProxyDxgiHint => Find();
    public static string ProxyWinmmHint => Find();
    public static string ProxyVersionHint => Find();
    public static string ProxyDbghelpHint => Find();
    public static string ProxyD3d12Hint => Find();
    public static string ProxyWininetHint => Find();
    public static string ProxyWinhttpHint => Find();
    public static string ProxyAsiHint => Find();
    public static string XmxLockedBuild => Find();
    public static string XmxLockedUpdate => Find();
    public static string MultiplierIntelOnly => Find();

    /// <summary>"Nightly of 11 November"</summary>
    public static string NightlyOf(string night) => Fill(Find(), night);
    public static string NoBuildOnDisk => Find();
    public static string NightlyExplained => Find();
    public static string CheckForNewerBuild => Find();
    public static string CheckingNewerBuild => Find();
    public static string NewerBuildAvailableHeading => Find();
    public static string BuildAvailableHeading => Find();
    public static string DownloadingBuildHeading => Find();
    public static string DownloadBuild => Find();
    public static string Cancel => Find();
    public static string CancelDownload => Find();
    public static string CheckCancelled => Find();
    public static string DownloadCancelled => Find();
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
    public static string FormatSize(long bytes) => Size(bytes);

    private static string Size(long bytes) => bytes >= 1L << 30
        ? Fill(Find("SizeGB"), bytes / (double)(1L << 30))
        : bytes >= 1L << 20
            ? Fill(Find("SizeMB"), bytes >> 20)
            : Fill(Find("SizeKB"), Math.Max(1, bytes >> 10));
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

    public static string OptiPatcherHeading => Find();
    public static string OptiPatcherSupportedOnly => Find();
    public static string OptiPatcherExplained => Find();
    public static string CompatibilityHeading => Find();
    public static string OptiScalerCompatibility => Find();
    public static string OptiPatcherCompatibility => Find();
    public static string DownloadOptiPatcher => Find();
    public static string CheckForNewerOptiPatcher => Find();
    public static string OptiPatcherNotDownloaded => Find();
    public static string OptiPatcherHeld(string tag, string day) => Fill(Find(), tag, day);
    public static string CheckingOptiPatcher => Find();
    public static string DownloadingOptiPatcher(string tag) => Fill(Find(), tag);
    public static string DownloadingOptiPatcherProgress(string tag, int percent) => Fill(Find(), tag, percent);
    public static string NewerOptiPatcherAvailableHeading => Find();
    public static string DownloadingOptiPatcherHeading => Find();
    public static string HaveNewestOptiPatcher(string tag) => Fill(Find(), tag);
    public static string GotOptiPatcher(string tag) => Fill(Find(), tag);
    public static string OptiPatcherUpdateReady(string tag) => Fill(Find(), tag);
    public static string OptiPatcherNotInGame => Find();
    public static string BuildLineWithOptiPatcher(string buildLine, string tag) => Fill(Find(), buildLine, tag);

    public static string DefaultsExplained => Find();
    public static string BrowseHint => Find();
    public static string BrowseFolderHint => Find();
    public static string BrowseFolderSavedHint => Find();
    public static string UseThisFolder => Find();
    public static string RemoveThisFolder => Find();
    public static string SavedFoldersHeading => Find();
    public static string DrivesHeading => Find();
    public static string RemoveFolder => Find();
    public static string FolderMissingDetail(string folder) => Fill(Find(), folder);
    public static string NoSubfolders => Find();
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

    public static string Rtss => Find();
    public static string RtssNotInstalledTitle => Find();
    public static string RtssNotInstalledExplained => Find();
    public static string RtssDownloadOfficial => Find();
    public static string RtssDownloadOfficialDetail => Find();
    public static string RtssRunInstaller => Find();
    public static string RtssRunInstallerDetail(string fileName) => Fill(Find(), fileName);
    public static string RtssCheckAgain => Find();
    public static string RtssCheckAgainDetail => Find();
    public static string RtssStillNotFound => Find();
    public static string RtssOpeningDownload => Find();
    public static string RtssInstallerFailed => Find();
    public static string RtssInstallAuto => Find();
    public static string RtssInstallAutoDetail => Find();
    public static string RtssInstallAutoBusy => Find();
    public static string RtssInstallStagePreparing => Find();
    public static string RtssInstallStageDownloading => Find();
    public static string RtssInstallStageInstalling => Find();
    public static string RtssInstallStageFinishing => Find();
    public static string RtssInstallAlreadyRunning => Find();
    public static string RtssInstallOffline => Find();
    public static string RtssInstallNoWinget => Find();
    public static string RtssInstallTimedOut => Find();
    public static string RtssInstallFailed(int exitCode) => Fill(Find(), $"0x{exitCode:X8}");
    public static string RtssInstallNotFoundAfter => Find();
    public static string RtssInstallDone(string version) => string.IsNullOrWhiteSpace(version) ? Find("RtssInstallDonePlain") : Fill(Find(), version);
    public static string RtssAlreadyInstalled => Find();

    public static string RtssDownloadTitle => Find();
    public static string RtssDownloadPage => Find();
    public static string RtssDownloadPageDetail => Find();
    public static string RtssDownloadPageInstalled => Find();
    public static string RtssDownloadPageInstalledDetail => Find();
    public static string RtssDownloadPageUpdateDetail(string version) => Fill(Find(), version);
    public static string RtssUpdateHeading => Find();
    public static string RtssUpdateChecking => Find();
    public static string RtssUpdateUpToDate => Find();
    public static string RtssUpdateCheckFailed => Find();
    public static string RtssUpdate(string version) => Fill(Find(), version);
    public static string RtssUpdateDetail(string version) => string.IsNullOrWhiteSpace(version) ? Find("RtssUpdateDetailPlain") : Fill(Find(), version);
    public static string RtssUpdateBusy => Find();
    public static string RtssUpdateStageInstalling => Find();
    public static string RtssUpdateDone(string version) => Fill(Find(), version);
    public static string RtssUpdateFailed(int exitCode) => Fill(Find(), $"0x{exitCode:X8}");
    public static string RtssUpdateTimedOut => Find();
    public static string RtssUpdateStillRunning => Find();
    public static string RtssAutomaticHeading => Find();
    public static string RtssManualHeading => Find();
    public static string RtssUninstallHeading => Find();
    public static string RtssUninstall => Find();
    public static string RtssUninstallDetail(string version) => string.IsNullOrWhiteSpace(version) ? Find("RtssUninstallDetailPlain") : Fill(Find(), version);
    public static string RtssUninstallConfirm => Find();
    public static string RtssUninstallConfirmDetail => Find();
    public static string RtssUninstallBusy => Find();
    public static string RtssUninstalling => Find();
    public static string RtssUninstallDone => Find();
    public static string RtssUninstallFailed => Find();
    public static string RtssUninstallCancelled => Find();
    public static string RtssUninstallStillRunning => Find();
    public static string RtssUninstallNotFound => Find();

    public static string RtssOverlayHeading => Find();
    public static string RtssUseTsumeOverlay => Find();
    public static string RtssUseTsumeOverlayOn => Find();
    public static string RtssUseTsumeOverlayOff => Find();

    public static string RtssLevelOff => Find();
    public static string RtssLevelFps => Find();
    public static string RtssLevelBar => Find();
    public static string RtssLevelDetailed => Find();
    public static string RtssLevelFull => Find();

    public static string RtssLevelOffDetail => Find();
    public static string RtssLevelFpsDetail => Find();
    public static string RtssLevelBarDetail => Find();
    public static string RtssLevelDetailedDetail => Find();
    public static string RtssLevelFullDetail => Find();

    public static string RtssPositionHeading(string levelName) => Fill(Find(), levelName);
    public static string RtssAlignTopLeft => Find();
    public static string RtssAlignTopCenter => Find();
    public static string RtssAlignTopRight => Find();
    public static string RtssAlignMiddleLeft => Find();
    public static string RtssAlignCenter => Find();
    public static string RtssAlignMiddleRight => Find();
    public static string RtssAlignBottomLeft => Find();
    public static string RtssAlignBottomCenter => Find();
    public static string RtssAlignBottomRight => Find();
    public static string RtssAlignCustom => Find();
    public static string RtssTouchDrag => Find();
    public static string RtssTouchDragDetail => Find();
    public static string RtssAidaConflictAida64 => Find();
    public static string RtssAidaConflictWinhanced => Find();
    public static string RtssAidaConflictOther => Find();

    public static string RtssShowOwnOsd => Find();
    public static string RtssShowOwnOsdDetail => Find();

    public static string RtssFrameCapHeading => Find();
    public static string RtssCapCustom => Find();
    public static string RtssCapSlider => Find();
    public static string RtssCapNote => Find();
    public static string RtssFpsShort(int fps) => Fill(Find(), fps);
    public static string RtssOsdOn => Find();

    public static string RtssOnlineWarning => Find();
    public static string RtssAppHeading => Find();
    public static string RtssStart => Find();
    public static string RtssOpenMenu => Find();
    public static string RtssOpenMenuDetail => Find();
    public static string RtssClose => Find();
    public static string RtssCloseDetail => Find();
    public static string RtssClosing => Find();
    public static string RtssClosed => Find();
    public static string RtssCloseFailed => Find();
    public static string RtssOpenMenuFailed => Find();
    public static string RtssStartWithWindowsDetail => Find();
    public static string RtssStartWithWindowsFailed => Find();
    public static string RtssSkipAntiCheat => Find();
    public static string RtssSkipAntiCheatOn(int count) => count == 1 ? Find("RtssSkipAntiCheatOne") : Fill(Find(), count);
    public static string RtssSkipAntiCheatNone => Find();
    public static string RtssSkipAntiCheatOff => Find();
    public static string RtssSkipAntiCheatBusy => Find();
    public static string RtssStatusRunning(string version) => string.IsNullOrWhiteSpace(version) ? Find("RtssRunningPlain") : Fill(Find(), version);
    public static string RtssStatusStopped(string version) => string.IsNullOrWhiteSpace(version) ? Find("RtssStoppedPlain") : Fill(Find(), version);
    public static string RtssStarting => Find();
    public static string RtssStartFailed => Find();
    public static string RtssNotRunningNote => Find();
    public static string RtssSavedNote => Find();
    public static string RtssRunningSavedNote(string version) => string.IsNullOrWhiteSpace(version) ? Find("RtssSavedNote") : Fill(Find(), version);

    public static string ControlsAndLayout => Find();
    public static string ControlsAndLayoutDetail => Find();
    public static string PanelPositionHeading => Find();
    public static string OpenOnRight => Find();
    public static string OpenOnRightDetail => Find();
    public static string MainTilesHeading => Find();
    public static string MainTilesTitle => Find();
    public static string MainTilesSummary(int shown, int total, int columns) => Fill(Find(), shown, total, columns);
    public static string MainTilesSummaryNone => Find();
    public static string MainTilesCountChip(int shown, int total) => Fill(Find(), shown, total);
    public static string MainTilesColumnsChip(int columns) => Fill(Find(), columns);
    public static string MainTilesPreviewHeading => Find();
    public static string MainTilesCountBadge(int shown, int total) => Fill(Find(), shown, total);
    public static string MainTilesEmptyPreview => Find();
    public static string MainTilesSelectedPosition(int position, int total) => Fill(Find(), position, total);
    public static string MainTilesMoveLeft => Find();
    public static string MainTilesMoveRight => Find();
    public static string MainTilesHide => Find();
    public static string MainTilesRemoveTile => Find();
    public static string MainTilesColumnsHeading => Find();
    public static string MainTilesColumnsChoice(int columns) => Fill(Find(), columns);
    public static string MainTilesAvailableHeading => Find();
    public static string MainTilesAvailableHint => Find();
    public static string MainTilesAllShown => Find();
    public static string MainTilesReset => Find();
    public static string MainTilesFooterHint => Find();
    public static string TileMute => Find();
    public static string TileMuted => Find();
    public static string TileSoundOn => Find();
    public static string TileSteam => Find();
    public static string TileGameBar => Find();
    public static string HardwareButtonsHeading => Find();
    public static string GameBarButton => Find();
    public static string GameBarDefault => Find();
    public static string GameBarOpenTsume => Find();
    public static string GameBarSteam => Find();
    public static string GameBarTaskManager => Find();
    public static string GameBarTouchKeyboard => Find();
    public static string GameBarScreenshot => Find();
    public static string GameBarShowDesktop => Find();
    public static string GameBarDisabled => Find();
    public static string GameBarDefaultDetail => Find();
    public static string GameBarNonNativeDefaultDetail => Find();
    public static string GameBarCustomDetail => Find();
    public static string GameBarMsiCenterWarning => Find();
    public static string MButton => Find();
    public static string MButtonDefault => Find();
    public static string MButtonXboxGameBar => Find();
    public static string MButtonDetailTsume => Find();
    public static string MButtonDetailCustom => Find();
    public static string MButtonDetailDisabled => Find();
    public static string MButtonMsiCenterWarning => Find();
    public static string BackButtonsHeading => Find();
    public static string BackButtonUnbound => Find();
    public static string BackButtonMacro => Find();
    public static string BackButtonKeySlot(int number) => Fill(Find(), number);
    public static string BackButtonAddKey => Find();
    public static string BackButtonRemoveKey => Find();
    public static string BackButtonMultiHint(int maxKeys) => Fill(Find(), maxKeys);
    public static string DedicatedActionsHeading => Find();
    public static string DedicatedActionsTitle => Find();
    public static string DedicatedActionsNoneActive => Find();
    public static string DedicatedActionsActiveSummary(int count, string names) => Fill(Find(), count, names);
    public static string DedicatedActionsActiveChip(int count) => Fill(Find(), count);
    public static string DedicatedActionsTabActive => Find();
    public static string DedicatedActionsTabTsume => Find();
    public static string DedicatedActionsTabSystem => Find();
    public static string DedicatedActionsBrowseHint => Find();
    public static string DedicatedActionsSystemButtonHint => Find();
    public static string DedicatedActionsFooterHint(int maxKeys) => Fill(Find(), maxKeys);
    public static string SystemButtonsHeading => Find();
    public static string SystemButtonsTitle => Find();
    public static string SystemButtonsDetailAll => Find();
    public static string SystemButtonsDetailHardware => Find();
    public static string SystemButtonsDetailBack => Find();
    public static string SystemButtonCustomDetail => Find();
    public static string DedicatedActionOpenTsume => Find();
    public static string DedicatedActionOpenTsumeDetail => Find();
    public static string DedicatedActionFpsOverlay => Find();
    public static string DedicatedActionFpsOverlayDetail => Find();
    public static string DedicatedActionCyclePerformance => Find();
    public static string DedicatedActionCyclePerformanceDetail => Find();
    public static string DedicatedActionCycleRefreshRate => Find();
    public static string DedicatedActionCycleRefreshRateDetail => Find();
    public static string DedicatedActionToggleFanBoost => Find();
    public static string DedicatedActionToggleFanBoostDetail => Find();
    public static string DedicatedActionToggleControllerMode => Find();
    public static string DedicatedActionToggleControllerModeDetail => Find();
    public static string DedicatedActionScreenshot => Find();
    public static string DedicatedActionScreenshotDetail => Find();
    public static string DedicatedActionTouchKeyboard => Find();
    public static string DedicatedActionTouchKeyboardDetail => Find();
    public static string DedicatedActionShowDesktop => Find();
    public static string DedicatedActionShowDesktopDetail => Find();
    public static string DedicatedActionTaskManager => Find();
    public static string DedicatedActionTaskManagerDetail => Find();
    public static string DedicatedActionToggleMute => Find();
    public static string DedicatedActionToggleMuteDetail => Find();
    public static string DedicatedActionSteamBigPicture => Find();
    public static string DedicatedActionSteamBigPictureDetail => Find();
    public static string DedicatedActionXboxGameBar => Find();
    public static string DedicatedActionXboxGameBarDetail => Find();
    public static string ControlsSaved => Find();
    public static string ControlsSavedPanelOnly => Find();
    public static string ControlsMsiCenterRuns => Find();

    public static string DeveloperOptions => Find();
    public static string DeveloperOptionsDetail => Find();
    public static string DeveloperDiscoveryHeading => Find();
    public static string OpenHardwareDiscovery => Find();
    public static string OpenHardwareDiscoveryDetail => Find();
    public static string QuickHardwareSnapshot => Find();
    public static string QuickHardwareSnapshotDetail => Find();
    public static string QuickHardwareSnapshotBusy => Find();
    public static string QuickHardwareSnapshotDone => Find();
    public static string QuickHardwareSnapshotFailed(string why) => Fill(Find(), why);
    public static string DeveloperDiagnosticsHeading => Find();
    public static string CheckEnvironment => Find();
    public static string CheckEnvironmentDetail => Find();
    public static string CheckEnvironmentBusy => Find();
    public static string IncludeHardwareInLogs => Find();
    public static string IncludeHardwareInLogsDetail => Find();
    public static string DeveloperFirmwareChecksHeading => Find();
    public static string RunFirmwareProbe => Find();
    public static string RunFirmwareProbeDetail => Find();
    public static string RunFirmwareProbeBusy => Find();
    public static string RunWriteCheck => Find();
    public static string RunWriteCheckDetail => Find();
    public static string RunWriteCheckBusy => Find();
    public static string RunModeCheck => Find();
    public static string RunModeCheckDetail => Find();
    public static string RunModeCheckBusy => Find();
    public static string FirmwareCheckLogged => Find();
    public static string FirmwareCheckLoggedMsiWarning => Find();
    public static string FirmwareCheckFailed(string why) => Fill(Find(), why);
    public static string DeveloperShareLogsDetail => Find();
    public static string HardwareDiscoveryTitle => Find();
    public static string HardwareDiscoverySubtitle => Find();

    /// <summary>The string named after the calling member; a name with no entry is shown as the name itself.</summary>
    private static string Find([CallerMemberName] string name = "") =>
        Words.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Fill(string pattern, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, pattern, values);
}
