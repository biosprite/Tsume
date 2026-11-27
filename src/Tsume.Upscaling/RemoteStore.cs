using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tsume.Upscaling;

/// <summary>
/// Shared HTTP, GitHub release asset parsing, streaming SHA-256 download, and atomic store-folder helpers
/// used by <see cref="Nightly.BuildStore"/>, <see cref="Nightly.NightlyFeed"/>,
/// <see cref="OptiPatcher.OptiPatcherStore"/>, and <see cref="OptiPatcher.OptiPatcherFeed"/>.
/// </summary>
internal static class RemoteStore
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    internal static readonly HttpClient Web = NewClient();

    /// <summary>Fetches a UTF-8 response body with a 20 s timeout, mapping network/timeout failures to <see cref="Trouble.Offline"/>.</summary>
    public static async Task<string> GetStringAsync(string url, CancellationToken cancel)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(RequestTimeout);

            return await Web.GetStringAsync(url, limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new UpscalingException(Trouble.Offline, error.Message, error);
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>, verifying its SHA-256 against
    /// <paramref name="expectedSha256"/> while streaming.
    /// </summary>
    public static async Task DownloadFileAsync(
        string url,
        string destination,
        long expectedSize,
        string expectedSha256,
        IProgress<double>? progress,
        CancellationToken cancel)
    {
        try
        {
            using var headersLimit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            headersLimit.CancelAfter(RequestTimeout);

            using var response = await Web
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersLimit.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? expectedSize;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var to = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17))
            {
                var buffer = new byte[1 << 17];
                long done = 0;

                while (true)
                {
                    // Give up on a connection that stalls for 30 s.
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    stall.CancelAfter(StallTimeout);

                    var read = await from.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    hash.AppendData(buffer, 0, read);
                    await to.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);

                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != expectedSha256)
                throw new UpscalingException(Trouble.DownloadDamaged, $"expected {expectedSha256}, got {actual}");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new UpscalingException(Trouble.Offline, error.Message, error);
        }
        catch (IOException error) when (IsDiskFull(error))
        {
            throw new UpscalingException(Trouble.DiskFull, destination, error);
        }
    }

    /// <summary>
    /// Extracts a verified GitHub release asset (matching name, allowed download URL prefix, and published SHA-256 digest).
    /// </summary>
    public static bool TryReadVerifiedAsset(
        JsonElement asset,
        string allowedUrlPrefix,
        Func<string, bool> nameMatches,
        out string name,
        out string url,
        out long size,
        out string sha256)
    {
        name = asset.GetProperty("name").GetString() ?? "";
        url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var digest = asset.TryGetProperty("digest", out var given) ? given.GetString() : null;

        // No published hash, no download: there would be nothing to check it against.
        if (!nameMatches(name)
            || !url.StartsWith(allowedUrlPrefix, StringComparison.Ordinal)
            || digest is null
            || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            size = 0;
            sha256 = "";
            return false;
        }

        size = asset.GetProperty("size").GetInt64();
        sha256 = digest["sha256:".Length..].ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Removes <paramref name="recordName"/> before discarding <paramref name="folder"/> so an old folder whose files
    /// are locked by a running game can never be picked up again by <c>Current()</c>.
    /// </summary>
    public static void DiscardFolder(string folder, string recordName)
    {
        Distrust(folder, recordName);
        Files.Discard(folder);
    }

    /// <summary>
    /// Moves a freshly staged <paramref name="making"/> folder into <paramref name="home"/>.
    /// If <paramref name="home"/> is locked by a running game holding a hard-linked file open, retires its record first
    /// and falls back to a hash-qualified folder so the download still succeeds.
    /// </summary>
    public static string PromoteFolder(string making, string home, string recordName, string sha256)
    {
        DiscardFolder(home, recordName);

        var finalHome = home;
        if (Directory.Exists(finalHome))
        {
            var suffix = sha256.Length >= 8 ? sha256[..8] : Guid.NewGuid().ToString("N")[..8];
            finalHome = $"{home}-{suffix}";
            DiscardFolder(finalHome, recordName);
            if (Directory.Exists(finalHome))
                throw new UpscalingException(Trouble.FileInUse, finalHome);
        }

        Directory.Move(making, finalHome);
        return finalHome;
    }

    /// <summary>Retires and discards every subdirectory in <paramref name="root"/> other than <paramref name="keep"/>.</summary>
    public static void ForgetOtherFolders(string root, string keep, string recordName)
    {
        foreach (var folder in Directory.GetDirectories(root))
            if (!folder.Equals(keep, StringComparison.OrdinalIgnoreCase)) DiscardFolder(folder, recordName);
    }

    /// <summary>Deletes the store record file inside <paramref name="folder"/> so the folder stops counting as whole.</summary>
    public static void Distrust(string folder, string recordName)
    {
        try { Files.Delete(Path.Combine(folder, recordName)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsDiskFull(IOException error) =>
        (error.HResult & 0xFFFF) is 0x70 or 0x27;   // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // each request sets its own limit

        // GitHub turns away requests that do not say what is asking.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tsume", "1"));

        return client;
    }
}
