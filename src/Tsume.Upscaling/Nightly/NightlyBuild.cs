using System.Globalization;
using System.Text.RegularExpressions;

namespace Tsume.Upscaling.Nightly;

/// <summary>One nightly build of OptiScaler, as GitHub lists it.</summary>
/// <param name="Tag">"nightly-20261111" — also the build's folder name once downloaded.</param>
/// <param name="AssetName">"OptiScaler_v10.0.0-pre1_20261111.7z"</param>
/// <param name="Sha256">The archive's hash as GitHub publishes it, lower-case hex.</param>
public sealed partial record NightlyBuild(string Tag, string AssetName, string Url, long Size, string Sha256)
{
    /// <summary>The night it was built, read from the tag.</summary>
    public DateOnly? Date =>
        DateOnly.TryParseExact(Tag[(Tag.LastIndexOf('-') + 1)..], "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    /// <summary>"10.0.0-pre1", read from the file name; empty if it is not there.</summary>
    public string Version => VersionInName().Match(AssetName) is { Success: true } found ? found.Groups[1].Value : "";

    [GeneratedRegex(@"_v(\d[\w.\-]*?)_")]
    private static partial Regex VersionInName();
}

/// <summary>A build that is on this machine, unpacked and ready to copy from.</summary>
/// <param name="Folder">Holds the build's files exactly as the archive laid them out.</param>
/// <param name="Files">Every file in it, with its hash — taken once, when the build was unpacked.</param>
public sealed record StoredBuild(NightlyBuild Build, string Folder, IReadOnlyList<BuildFile> Files);

/// <param name="Path">Relative to the build's folder.</param>
public sealed record BuildFile(string Path, long Length, string Sha256);

/// <summary>Something in the Builds folder that is no longer needed: an older build, or a download's left-overs.</summary>
/// <param name="Size">In bytes.</param>
public sealed record OldBuild(string Path, long Size);
