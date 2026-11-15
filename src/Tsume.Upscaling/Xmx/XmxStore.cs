using System.Diagnostics;
using System.Text.Json;
using Tsume.Upscaling.Nightly;

namespace Tsume.Upscaling.Xmx;

/// <summary>
/// Where the XMX build of FSR 4 goes in a game. OptiScaler loads any upscaler found under
/// <c>OptiScaler\custom\&lt;name&gt;\ffx\</c> and runs it when <c>Dx12Upscaler</c> is set to that name.
/// </summary>
public static class XmxLayout
{
    /// <summary>The folder's name, and the value of <c>Dx12Upscaler</c>.</summary>
    public const string Name = "xmxsr";

    /// <summary>
    /// The OptiScaler.ini section holding the XMX upscaler's own settings.
    /// (OptiScaler's settings about it are in a section named <see cref="Name"/>.)
    /// </summary>
    public const string Section = "Plugin." + Name;

    public const string FileName = "amd_fidelityfx_upscaler_dx12.dll";

    public static string Upscaler { get; } = Path.Combine("OptiScaler", "custom", Name, "ffx", FileName);
    // AMD's own upscaler under another name; the XMX one reads FSR 4.1.2's weights out of it.
    public static string Weights { get; } = Path.Combine("OptiScaler", "custom", Name, "ffx", "fsr-original.dll");

    /// <summary>AMD's upscaler as the nightly build carries it.</summary>
    public static string WeightsInBuild { get; } = Path.Combine("OptiScaler", FileName);
}

/// <summary>Record of the stored XMX archive's contents.</summary>
/// <param name="Version">The file's own version number.</param>
/// <param name="Length">Of the upscaler once unpacked.</param>
/// <param name="Sha256">Of the upscaler once unpacked.</param>
public sealed record XmxUpscaler(string Version, long Length, string Sha256, DateTimeOffset Added);

/// <summary>
/// Holds the XMX build of FSR 4, a test build that is not in the nightly and is never downloaded: the user supplies
/// the archive. It is kept as Xmx\upscaler.7z, with upscaler.json (written last) describing it, and unpacked once, at the first install.
/// </summary>
public sealed class XmxStore(UpscalingPaths paths)
{
    private const string Archive = "upscaler.7z";
    private const string Record = "upscaler.json";

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    internal string ArchiveFile => Path.Combine(paths.Xmx, Archive);

    /// <summary>The stored upscaler, or null.</summary>
    public XmxUpscaler? Current()
    {
        try
        {
            var record = Path.Combine(paths.Xmx, Record);
            if (!File.Exists(record) || !File.Exists(Path.Combine(paths.Xmx, Archive))) return null;

            return JsonSerializer.Deserialize<XmxUpscaler>(File.ReadAllText(record), Format);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Archives on the Desktop and in Downloads that look like the XMX upscaler, newest first.</summary>
    public static IReadOnlyList<string> Lying()
    {
        // Asked of Windows for whoever is signed in: either folder may have been moved (OneDrive, another drive).
        string?[] places =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Downloads(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ];

        return [.. places
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(place => Directory.EnumerateFiles(place, "amd_fidelityfx_upscaler_dx12*"))
            .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".7z" or ".zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)];
    }

    /// <summary>The signed-in user's Downloads folder, wherever it has been moved to; null if Windows will not say.</summary>
    private static string? Downloads()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        return SHGetKnownFolderPath(in downloads, 0, 0, out var path) == 0 ? path : null;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string? path);

    /// <summary>Copies <paramref name="archive"/> into the store after checking it holds the XMX upscaler.</summary>
    public XmxUpscaler Add(string archive)
    {
        var checking = Path.Combine(paths.Xmx, ".checking");
        Files.Discard(checking);

        try
        {
            var found = Unpack(archive, checking);
            var version = FileVersionInfo.GetVersionInfo(found);

            // AMD's own file has the same name; only the XMX build has "XMX" in its ProductName.
            if (version.ProductName?.Contains("XMX", StringComparison.OrdinalIgnoreCase) != true)
                throw new UpscalingException(Trouble.NotXmx, Path.GetFileName(archive));

            var upscaler = new XmxUpscaler(version.FileVersion ?? "", new FileInfo(found).Length, Files.Hash(found), DateTimeOffset.Now);

            Files.Delete(Path.Combine(paths.Xmx, Record));   // the store counts as empty until the record is rewritten
            Files.Place(archive, Path.Combine(paths.Xmx, Archive));
            Files.WriteWhole(Path.Combine(paths.Xmx, Record), JsonSerializer.Serialize(upscaler, Format));

            return upscaler;
        }
        finally
        {
            Files.Discard(checking);
        }
    }

    /// <summary>
    /// The stored upscaler, unpacked once into Xmx\shared\ for games to link to. Unpacked again if it is missing
    /// or is not the one recorded; the old file is replaced, not written over, so games linked to it keep theirs.
    /// </summary>
    public string Shared(XmxUpscaler upscaler)
    {
        var kept = Path.Combine(paths.Xmx, "shared", XmxLayout.FileName);
        if (File.Exists(kept) && Files.Hash(kept) == upscaler.Sha256) return kept;

        var unpacking = Path.Combine(paths.Xmx, ".unpacking");
        Files.Discard(unpacking);

        try
        {
            Files.Place(UnpackInto(upscaler, unpacking), kept);
            return kept;
        }
        finally
        {
            Files.Discard(unpacking);
        }
    }

    /// <summary>Unpacks the stored upscaler into <paramref name="folder"/> and checks it is the one recorded.</summary>
    /// <returns>The upscaler's file.</returns>
    public string UnpackInto(XmxUpscaler upscaler, string folder)
    {
        var found = Unpack(Path.Combine(paths.Xmx, Archive), folder);

        if (Files.Hash(found) != upscaler.Sha256) throw new UpscalingException(Trouble.NotXmx, "the stored archive has changed");
        return found;
    }

    private static string Unpack(string archive, string folder)
    {
        Directory.CreateDirectory(folder);
        BuildStore.UnpackAsync(archive, folder, CancellationToken.None).GetAwaiter().GetResult();

        var found = Directory.EnumerateFiles(folder, XmxLayout.FileName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new UpscalingException(Trouble.NotXmx, Path.GetFileName(archive));

        // Reject links: one could point anywhere on the disk.
        if (File.GetAttributes(found).HasFlag(FileAttributes.ReparsePoint)) throw new UpscalingException(Trouble.NotXmx, "a link");

        return found;
    }
}
