using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tsume.Upscaling.Nightly;

/// <summary>
/// Downloads, verifies and unpacks the nightly into Builds\&lt;tag&gt;\files\, and reports which build is here.
/// build.json (the build and every file's hash) is written last, so a folder that has one is whole. Only one build is kept.
/// Games hold hard links to these files where the disk allows (see Installer), so nothing here is ever written to:
/// a build is replaced by deleting its folder, which leaves the games' files as they are.
/// </summary>
public sealed class BuildStore(UpscalingPaths paths)
{
    private const string Record = "build.json";
    private const string Contents = "files";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>The build that is downloaded and whole, if there is one.</summary>
    public StoredBuild? Current()
    {
        if (!Directory.Exists(paths.Builds)) return null;

        foreach (var folder in Directory.GetDirectories(paths.Builds, "nightly-*").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                var record = Path.Combine(folder, Record);
                if (!File.Exists(record)) continue;

                if (JsonSerializer.Deserialize<StoredBuild>(File.ReadAllText(record), Format) is { } build)
                    return build with { Folder = Path.Combine(folder, Contents) };
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                // A build whose record cannot be read is not one to install from.
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads <paramref name="build"/> unless it is already here, checks it against GitHub's published hash,
    /// unpacks it and hashes its files.
    /// </summary>
    /// <param name="progress">0–1 while downloading.</param>
    public async Task<StoredBuild> FetchAsync(NightlyBuild build, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        if (Current() is { } here && here.Build.Tag == build.Tag) return here;

        var tag = Path.GetFileName(build.Tag);   // strips any path from the tag: it is used as a folder name
        var home = Path.Combine(paths.Builds, tag);
        var making = home + ".new";
        var archive = home + ".7z";

        Directory.CreateDirectory(paths.Builds);
        Files.Discard(making);

        try
        {
            await DownloadAsync(build, archive, progress, cancel).ConfigureAwait(false);

            var contents = Path.Combine(making, Contents);
            Directory.CreateDirectory(contents);
            await UnpackAsync(archive, contents, cancel).ConfigureAwait(false);

            var stored = new StoredBuild(build, contents, TakeStock(contents));
            cancel.ThrowIfCancellationRequested();
            Files.WriteWhole(Path.Combine(making, Record), JsonSerializer.Serialize(stored, Format));

            Files.Discard(home);
            Directory.Move(making, home);

            ForgetOtherBuilds(home);
            return stored with { Folder = Path.Combine(home, Contents) };
        }
        finally
        {
            Files.Discard(making);
            try { File.Delete(archive); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task DownloadAsync(NightlyBuild build, string archive, IProgress<double>? progress,
        CancellationToken cancel)
    {
        try
        {
            using var response = await NightlyFeed.Web
                .GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? build.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var to = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17))
            {
                var buffer = new byte[1 << 17];
                long done = 0;

                while (true)
                {
                    // Give up on a connection that stalls for 30 s.
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
            if (actual != build.Sha256)
                throw new UpscalingException(Trouble.DownloadDamaged, $"expected {build.Sha256}, got {actual}");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new UpscalingException(Trouble.Offline, error.Message, error);
        }
        catch (IOException error) when (IsDiskFull(error))
        {
            throw new UpscalingException(Trouble.DiskFull, archive, error);
        }
    }

    /// <summary>
    /// Unpacks with Windows 11's own <c>tar</c>, which reads .7z and itself refuses entries that would land
    /// outside the target folder. The result is checked again in <see cref="TakeStock"/>.
    /// </summary>
    internal static async Task UnpackAsync(string archive, string into, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar)) throw new UpscalingException(Trouble.CannotUnpack, "Windows has no tar.exe");

        var start = new ProcessStartInfo(tar)
        {
            ArgumentList = { "-xf", archive, "-C", into },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        try
        {
            using var unpack = Process.Start(start)!;
            var complaints = unpack.StandardError.ReadToEndAsync(cancel);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromMinutes(5));

            try
            {
                await unpack.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!unpack.HasExited)
                    {
                        unpack.Kill();
                        unpack.WaitForExit(3000);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw;
            }

            if (unpack.ExitCode != 0)
                throw new UpscalingException(Trouble.CannotUnpack, (await complaints.ConfigureAwait(false)).Trim());
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new UpscalingException(Trouble.CannotUnpack, error.Message, error);
        }
    }

    /// <summary>Lists and hashes what was unpacked, and makes sure it is OptiScaler.</summary>
    private static List<BuildFile> TakeStock(string contents)
    {
        var files = new List<BuildFile>();

        foreach (var file in Directory.EnumerateFiles(contents, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);

            // Reject links: one could point anywhere on the disk.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UpscalingException(Trouble.BuildIncomplete, $"{info.Name} is a link");

            files.Add(new BuildFile(Path.GetRelativePath(contents, file), info.Length, Files.Hash(file)));
        }

        var program = Path.Combine(contents, "OptiScaler.dll");
        var isOptiScaler = File.Exists(program)
            && File.Exists(Path.Combine(contents, "OptiScaler.ini"))
            && FileVersionInfo.GetVersionInfo(program).OriginalFilename == "OptiScaler.dll";

        if (!isOptiScaler) throw new UpscalingException(Trouble.BuildIncomplete);

        return files;
    }

    /// <summary>
    /// Stops <paramref name="build"/> counting as downloaded, because a file in it has changed. Its folder stays
    /// until the next download replaces it.
    /// </summary>
    public static void Distrust(StoredBuild build)
    {
        try { Files.Delete(Path.Combine(Path.GetDirectoryName(build.Folder)!, Record)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Everything in Builds that is not the current build and that no game in <paramref name="inUse"/> was installed from:
    /// older nightlies, and the left-overs of a download that was cut short.
    /// </summary>
    public IReadOnlyList<OldBuild> Old(IReadOnlySet<string> inUse)
    {
        if (!Directory.Exists(paths.Builds)) return [];

        var current = Current() is { } build ? Path.GetDirectoryName(build.Folder) : null;

        return [.. Directory.GetFileSystemEntries(paths.Builds)
            .Where(entry => !entry.Equals(current, StringComparison.OrdinalIgnoreCase) && !inUse.Contains(Path.GetFileName(entry)))
            .Select(entry => new OldBuild(entry, SizeOf(entry)))];
    }

    /// <summary>Deletes them. A build that cannot be deleted (its files are open) stays.</summary>
    /// <returns>The bytes freed.</returns>
    public long RemoveOld(IReadOnlySet<string> inUse)
    {
        long freed = 0;

        foreach (var old in Old(inUse))
        {
            if (Directory.Exists(old.Path)) Files.Discard(old.Path);
            else try { File.Delete(old.Path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

            if (!Path.Exists(old.Path)) freed += old.Size;
        }

        return freed;
    }

    private static long SizeOf(string entry)
    {
        try
        {
            return Directory.Exists(entry)
                ? new DirectoryInfo(entry).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                : new FileInfo(entry).Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void ForgetOtherBuilds(string keep)
    {
        foreach (var folder in Directory.GetDirectories(paths.Builds))
            if (!folder.Equals(keep, StringComparison.OrdinalIgnoreCase)) Files.Discard(folder);

        foreach (var archive in Directory.GetFiles(paths.Builds, "*.7z"))
            try { File.Delete(archive); } catch (IOException) { }
    }

    internal static bool IsDiskFull(IOException error) =>
        (error.HResult & 0xFFFF) is 0x70 or 0x27;   // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
}
