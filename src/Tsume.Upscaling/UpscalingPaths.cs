namespace Tsume.Upscaling;

/// <summary>
/// Everything kept for OptiScaler, all under %LOCALAPPDATA%\Tsume\OptiScaler (no administrator rights needed).
/// <code>
///   games.json            the games Tsume knows, and the settings chosen for each
///   Builds\&lt;tag&gt;\         the nightly build, downloaded and unpacked
///   OptiPatcher\&lt;tag&gt;\    OptiPatcher.asi, downloaded and verified
///   Extras\               your own files, laid over the build at every install
///   Xmx\                  the XMX build of FSR 4, kept as the archive it came in
///   Installs\&lt;game&gt;\
///      manifest.json      what was put in the game's folder
///      Originals\         the game's own files that had to make way
///      Saved\             the game's OptiScaler.ini, kept across removals
///      Kept\              originals that could not go back (see Installer)
/// </code>
/// </summary>
public sealed class UpscalingPaths(string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(
        Core.DataFolder.Path, "OptiScaler");

    public string GameList => Path.Combine(Root, "games.json");
    public string Builds => Path.Combine(Root, "Builds");
    public string OptiPatcher => Path.Combine(Root, "OptiPatcher");
    public string Extras => Path.Combine(Root, "Extras");
    public string Xmx => Path.Combine(Root, "Xmx");

    public string InstallFolder(string gameId) => Path.Combine(Root, "Installs", gameId);
}
