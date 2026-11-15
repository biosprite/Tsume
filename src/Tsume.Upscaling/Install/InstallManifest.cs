namespace Tsume.Upscaling.Install;

/// <summary>
/// Every file Tsume put in one game's folder, and every game file it displaced. Written before the first
/// file is touched, and the only thing removal acts on — so Tsume never deletes a file it did not place.
/// </summary>
public sealed class InstallManifest
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";

    /// <summary>The folder OptiScaler went into: the one holding the game's real program.</summary>
    public string Target { get; set; } = "";

    /// <summary>That program — so removal can tell whether the game is running.</summary>
    public string Executable { get; set; } = "";

    /// <summary>The file name OptiScaler was given: dxgi.dll, winmm.dll …</summary>
    public string Proxy { get; set; } = "";

    /// <summary>The nightly it came from: "nightly-20261111".</summary>
    public string Build { get; set; } = "";

    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>False until the last file is in place; a manifest found false means the install was cut short.</summary>
    public bool Complete { get; set; }

    /// <summary>
    /// True if the big libraries are links to Tsume's own files rather than copies; false if the disk could not link.
    /// Null in a manifest written before 0.7.1, when everything was copied.
    /// </summary>
    public bool? Linked { get; set; }

    public List<PlacedFile> Files { get; set; } = [];

    /// <summary>Folders that did not exist before, deepest last. Removed again only if left empty.</summary>
    public List<string> NewFolders { get; set; } = [];

    /// <summary>
    /// Files OptiScaler writes by itself once the game runs (its log), listed only where no such file
    /// existed beforehand, so removal may delete them.
    /// </summary>
    public List<string> Leavings { get; set; } = [];
}

/// <summary>One file Tsume put — or was about to put — in a game's folder.</summary>
public sealed class PlacedFile
{
    /// <summary>Relative to the manifest's target folder.</summary>
    public string Path { get; set; } = "";

    /// <summary>The hash of what Tsume put here: how removal knows the file is still Tsume's.</summary>
    public string Sha256 { get; set; } = "";

    public long Length { get; set; }

    /// <summary>OptiScaler.ini. Its overlay rewrites it, so it is recognised by name, never by hash.</summary>
    public bool IsSettings { get; set; }

    /// <summary>Hash of the file this one displaced, if any; a copy is under Originals\ at the same relative path.</summary>
    public string? OriginalSha256 { get; set; }
}

public enum InstallState
{
    None,
    Healthy,

    /// <summary>The proxy file is gone or is no longer OptiScaler — a game update or a "verify files" does this.</summary>
    Replaced,

    /// <summary>The install never finished.</summary>
    CutShort,

    FolderGone,
}

/// <summary>What removal deliberately did not undo.</summary>
/// <param name="LeftAlone">Files changed since Tsume placed them; not touched.</param>
/// <param name="KeptOriginals">Where the game's originals for those files were kept instead of being put back; null if there were none.</param>
public sealed record RemovalReport(IReadOnlyList<string> LeftAlone, string? KeptOriginals);
