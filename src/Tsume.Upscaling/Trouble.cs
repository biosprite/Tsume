namespace Tsume.Upscaling;

/// <summary>Why something could not be done. The panel words the message; nothing in this project does.</summary>
public enum Trouble
{
    /// <summary>GitHub could not be reached.</summary>
    Offline,

    /// <summary>GitHub answered, but with no nightly build Tsume can use.</summary>
    NoBuildPublished,

    /// <summary>The download does not match the hash published beside it.</summary>
    DownloadDamaged,

    /// <summary>Windows could not unpack the archive.</summary>
    CannotUnpack,

    /// <summary>The archive unpacked, but OptiScaler's own files are not in it.</summary>
    BuildIncomplete,

    /// <summary>A file of the downloaded build is no longer what was downloaded. The build has been set aside.</summary>
    BuildDamaged,

    /// <summary>No build has been downloaded yet.</summary>
    NoBuild,

    GameRunning,
    FolderMissing,

    /// <summary>The game has an anti-cheat, which would detect OptiScaler being loaded into the game.</summary>
    AntiCheat,

    /// <summary>The game's folder needs administrator rights, which the panel does not have.</summary>
    NotWritable,

    DiskFull,
    FileInUse,
    AlreadyInstalled,
    NotInstalled,

    /// <summary>The archive offered as the XMX upscaler does not hold it.</summary>
    NotXmx,
}

/// <summary>A failure the panel can explain.</summary>
public sealed class UpscalingException(Trouble trouble, string? detail = null, Exception? inner = null)
    : Exception(detail is null ? trouble.ToString() : $"{trouble}: {detail}", inner)
{
    public Trouble Trouble { get; } = trouble;

    /// <summary>A file name, a path, or the system's error text; meant for the log.</summary>
    public string? Detail { get; } = detail;
}
