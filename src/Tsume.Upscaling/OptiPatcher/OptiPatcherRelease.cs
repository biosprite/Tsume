namespace Tsume.Upscaling.OptiPatcher;

/// <summary>
/// Where OptiPatcher goes in a game. OptiScaler nightly builds load <c>*.asi</c> plugins from
/// <c>OptiScaler\plugins\</c> when <c>[Plugins] LoadAsiPlugins=true</c> in <c>OptiScaler.ini</c>.
/// </summary>
public static class OptiPatcherLayout
{
    public const string FileName = "OptiPatcher.asi";

    public static string Plugin { get; } = Path.Combine("OptiScaler", "plugins", FileName);
}

/// <summary>One release of OptiPatcher, as GitHub lists it.</summary>
/// <param name="Tag">"v0.42" — also the release's folder name once downloaded.</param>
/// <param name="AssetName">"OptiPatcher_v0.42.asi"</param>
/// <param name="Sha256">The file's hash as GitHub publishes it, lower-case hex.</param>
public sealed record OptiPatcherRelease(string Tag, string AssetName, string Url, long Size, string Sha256);

/// <summary>An OptiPatcher plugin stored on this machine, verified and ready to link or copy into games.</summary>
/// <param name="File">Full path to the stored <c>OptiPatcher.asi</c>.</param>
public sealed record StoredOptiPatcher(OptiPatcherRelease Release, string File, DateTimeOffset DownloadedAt);
