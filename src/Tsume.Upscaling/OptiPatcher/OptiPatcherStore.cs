using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Tsume.Upscaling.Nightly;

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

        var tag = Path.GetFileName(release.Tag);   // strips any path from the tag: it is used as a folder name
        var home = Path.Combine(paths.OptiPatcher, tag);
        var making = home + ".new";

        Directory.CreateDirectory(paths.OptiPatcher);
        Files.Discard(making);

        try
        {
            Directory.CreateDirectory(making);
            var staged = Path.Combine(making, OptiPatcherLayout.FileName);

            await DownloadAsync(release, staged, progress, cancel).ConfigureAwait(false);
            CheckBinary(staged);

            var stored = new StoredOptiPatcher(release, staged, DateTimeOffset.Now);
            cancel.ThrowIfCancellationRequested();
            Files.WriteWhole(Path.Combine(making, Record), JsonSerializer.Serialize(stored, Format));

            // If a running game holds the previous same-tag folder open via a hard link,
            // retire its record first and fall back to a hash-qualified folder so the download still succeeds.
            DiscardFolder(home);
            var finalHome = home;
            if (Directory.Exists(finalHome))
            {
                var suffix = release.Sha256.Length >= 8 ? release.Sha256[..8] : Guid.NewGuid().ToString("N")[..8];
                finalHome = $"{home}-{suffix}";
                DiscardFolder(finalHome);
                if (Directory.Exists(finalHome))
                    throw new UpscalingException(Trouble.FileInUse, finalHome);
            }

            Directory.Move(making, finalHome);

            ForgetOthers(finalHome);
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
    public static void Distrust(StoredOptiPatcher stored)
    {
        try { Files.Delete(Path.Combine(Path.GetDirectoryName(stored.File)!, Record)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static async Task DownloadAsync(OptiPatcherRelease release, string target, IProgress<double>? progress,
        CancellationToken cancel)
    {
        try
        {
            using var headersLimit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            headersLimit.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await NightlyFeed.Web
                .GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, headersLimit.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16];
                long done = 0;

                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    stall.CancelAfter(TimeSpan.FromSeconds(30));

                    var read = await from.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    hash.AppendData(buffer, 0, read);
                    await to.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);

                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != release.Sha256)
                throw new UpscalingException(Trouble.DownloadDamaged, $"expected {release.Sha256}, got {actual}");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new UpscalingException(Trouble.Offline, error.Message, error);
        }
        catch (IOException error) when (BuildStore.IsDiskFull(error))
        {
            throw new UpscalingException(Trouble.DiskFull, target, error);
        }
    }

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

    private void ForgetOthers(string keep)
    {
        foreach (var folder in Directory.GetDirectories(paths.OptiPatcher))
            if (!folder.Equals(keep, StringComparison.OrdinalIgnoreCase)) DiscardFolder(folder);
    }

    /// <summary>
    /// Removes <c>patcher.json</c> before discarding the folder so an old release whose <c>.asi</c> is locked by a
    /// running game can never be picked up again by <see cref="Current"/>.
    /// </summary>
    private static void DiscardFolder(string folder)
    {
        try { Files.Delete(Path.Combine(folder, Record)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        Files.Discard(folder);
    }
}
