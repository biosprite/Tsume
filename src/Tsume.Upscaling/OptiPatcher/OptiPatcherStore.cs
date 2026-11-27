using System.Text.Json;

namespace Tsume.Upscaling.OptiPatcher;

/// <summary>
/// Downloads and verifies OptiPatcher into <c>OptiPatcher\&lt;tag&gt;\OptiPatcher.asi</c>, with <c>patcher.json</c>
/// written last so a folder that has one is whole. Games hold hard links to the stored file where the disk allows,
/// so a newer release replaces the folder rather than overwriting the file in place.
/// </summary>
public sealed class OptiPatcherStore(UpscalingPaths paths)
{
    private const string Record = "patcher.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>The OptiPatcher release that is downloaded and whole, if there is one.</summary>
    public StoredOptiPatcher? Current()
    {
        if (!Directory.Exists(paths.OptiPatcher)) return null;

        StoredOptiPatcher? newest = null;

        foreach (var folder in Directory.GetDirectories(paths.OptiPatcher)
                     .Where(dir => !Path.GetFileName(dir).EndsWith(".new", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var record = Path.Combine(folder, Record);
                var plugin = Path.Combine(folder, OptiPatcherLayout.FileName);
                if (!File.Exists(record) || !File.Exists(plugin)) continue;

                if (JsonSerializer.Deserialize<StoredOptiPatcher>(File.ReadAllText(record), Format) is { Release: not null } stored)
                {
                    var candidate = stored with { File = plugin };
                    if (newest is null || candidate.DownloadedAt > newest.DownloadedAt)
                        newest = candidate;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // A release whose record cannot be read is not one to install from.
            }
        }

        return newest;
    }

    /// <summary>
    /// Downloads <paramref name="release"/> unless the same release and hash are already here,
    /// checks it against GitHub's published SHA-256 hash, and verifies it is a Windows DLL/ASI binary.
    /// </summary>
    /// <param name="progress">0–1 while downloading.</param>
    public async Task<StoredOptiPatcher> FetchAsync(OptiPatcherRelease release, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        if (Current() is { } here
            && here.Release.Tag == release.Tag
            && here.Release.Sha256 == release.Sha256
            && Files.Hash(here.File) == release.Sha256)
        {
            return here;
        }

        var home = Files.SafeChildFolder(paths.OptiPatcher, release.Tag);
        var making = home + ".new";

        Directory.CreateDirectory(paths.OptiPatcher);
        Files.Discard(making);

        try
        {
            Directory.CreateDirectory(making);
            var staged = Path.Combine(making, OptiPatcherLayout.FileName);

            await RemoteStore.DownloadFileAsync(release.Url, staged, release.Size, release.Sha256, progress, cancel)
                .ConfigureAwait(false);
            CheckBinary(staged);

            var stored = new StoredOptiPatcher(release, staged, DateTimeOffset.Now);
            cancel.ThrowIfCancellationRequested();
            Files.WriteWhole(Path.Combine(making, Record), JsonSerializer.Serialize(stored, Format));

            // If a running game holds the previous same-tag folder open via a hard link,
            // retire its record first and fall back to a hash-qualified folder so the download still succeeds.
            var finalHome = RemoteStore.PromoteFolder(making, home, Record, release.Sha256);

            RemoteStore.ForgetOtherFolders(paths.OptiPatcher, finalHome, Record);
            return stored with { File = Path.Combine(finalHome, OptiPatcherLayout.FileName) };
        }
        finally
        {
            Files.Discard(making);
        }
    }

    /// <summary>
    /// Checks that the stored file still matches the hash that was downloaded.
    /// If not, stops it counting as downloaded and throws <see cref="Trouble.OptiPatcherDamaged"/>.
    /// </summary>
    public static string Verified(StoredOptiPatcher stored)
    {
        if (!File.Exists(stored.File) || Files.Hash(stored.File) != stored.Release.Sha256)
        {
            Distrust(stored);
            throw new UpscalingException(Trouble.OptiPatcherDamaged, stored.Release.Tag);
        }

        return stored.File;
    }

    /// <summary>
    /// Stops <paramref name="stored"/> counting as downloaded, because the file on disk has changed.
    /// </summary>
    public static void Distrust(StoredOptiPatcher stored) =>
        RemoteStore.Distrust(Path.GetDirectoryName(stored.File)!, Record);

    private static void CheckBinary(string path)
    {
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length < 1024)
            throw new UpscalingException(Trouble.OptiPatcherInvalid, info.Name);

        Span<byte> header = stackalloc byte[2];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new UpscalingException(Trouble.OptiPatcherInvalid, info.Name);
    }
}
