using System.Diagnostics;
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

        foreach (var folder in Directory.GetDirectories(paths.Builds, "nightly-*")
                     .Where(dir => !Path.GetFileName(dir).EndsWith(".new", StringComparison.OrdinalIgnoreCase))
                     .OrderDescending(StringComparer.Ordinal))
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

        var home = Files.SafeChildFolder(paths.Builds, build.Tag);
        var making = home + ".new";
        var archive = home + ".7z";

        Directory.CreateDirectory(paths.Builds);
        Files.Discard(making);

        try
        {
            await RemoteStore.DownloadFileAsync(build.Url, archive, build.Size, build.Sha256, progress, cancel)
                .ConfigureAwait(false);

            var contents = Path.Combine(making, Contents);
            Directory.CreateDirectory(contents);
            await UnpackAsync(archive, contents, cancel).ConfigureAwait(false);

            var stored = new StoredBuild(build, contents, TakeStock(contents));
            cancel.ThrowIfCancellationRequested();
            Files.WriteWhole(Path.Combine(making, Record), JsonSerializer.Serialize(stored, Format));

            // If a running game holds the previous same-tag folder open via a hard link,
            // retire its record first and fall back to a hash-qualified folder so the download still succeeds.
            var finalHome = RemoteStore.PromoteFolder(making, home, Record, build.Sha256);

            ForgetOtherBuilds(finalHome);
            return stored with { Folder = Path.Combine(finalHome, Contents) };
        }
        finally
        {
            Files.Discard(making);
            try { File.Delete(archive); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
        // Reject file and directory reparse points (symlinks/junctions) before enumerating files.
        if (Files.FindReparsePoint(contents) is { } link)
            throw new UpscalingException(Trouble.BuildIncomplete, $"{link} is a link");

        var files = new List<BuildFile>();

        foreach (var file in Directory.EnumerateFiles(contents, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
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
    public static void Distrust(StoredBuild build) =>
        RemoteStore.Distrust(Path.GetDirectoryName(build.Folder)!, Record);

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
            if (Directory.Exists(old.Path)) RemoteStore.DiscardFolder(old.Path, Record);
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
        RemoteStore.ForgetOtherFolders(paths.Builds, keep, Record);

        foreach (var archive in Directory.GetFiles(paths.Builds, "*.7z"))
            try { File.Delete(archive); } catch (IOException) { }
    }
}
