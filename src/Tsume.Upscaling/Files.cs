using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Tsume.Upscaling;

internal static class Files
{
    /// <summary>The file's SHA-256, as lower-case hex.</summary>
    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 17,
            FileOptions.SequentialScan);

        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Resolves <paramref name="relative"/> strictly under <paramref name="root"/> and throws if it would land at or outside it.
    /// Every path read from a manifest or an archive must pass through here.
    /// </summary>
    public static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException($"'{relative}' leads outside '{root}'.");

        var baseFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fence = baseFull + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fence, relative));
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!path.StartsWith(fence, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, baseFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{relative}' leads outside '{root}'.");
        }

        return path;
    }

    /// <summary>
    /// Validates <paramref name="name"/> as a single safe child folder name under <paramref name="root"/>
    /// (rejecting empty/whitespace, ".", "..", path separators, invalid filename chars, or trailing dots)
    /// and returns the resolved path verified with <see cref="Inside"/>.
    /// </summary>
    public static string SafeChildFolder(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Trim() != name
            || name.EndsWith('.'))
        {
            throw new InvalidDataException($"Invalid folder name '{name}'.");
        }

        return Inside(root, name);
    }

    /// <summary>
    /// Walks <paramref name="root"/> without following directory links and returns the relative path of the first
    /// file or directory that is a reparse point (symlink or junction), or null if none exist.
    /// </summary>
    public static string? FindReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var attrs = File.GetAttributes(entry);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                    return Path.GetRelativePath(root, entry);

                if (attrs.HasFlag(FileAttributes.Directory))
                    pending.Push(entry);
            }
        }

        return null;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> via a temporary name and a rename,
    /// so a crash or a full disk never leaves half a file under the real name.
    /// </summary>
    public static void Place(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var temporary = destination + ".tsume-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Copy(source, temporary);
            File.SetAttributes(temporary, FileAttributes.Normal);

            Unprotect(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Hard-links <paramref name="destination"/> to <paramref name="existing"/> (same volume; exFAT cannot).
    /// </summary>
    /// <returns>False when the disk cannot; nothing has been written then.</returns>
    public static bool TryLink(string existing, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var temporary = destination + ".tsume-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (!CreateHardLink(temporary, existing, IntPtr.Zero)) return false;

            Unprotect(destination);
            File.Move(temporary, destination, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Whether <paramref name="existing"/> can be hard-linked into <paramref name="folder"/>: the same drive,
    /// and a file system that has links (NTFS; not exFAT or FAT32, as on most SD cards).
    /// </summary>
    public static bool CanLink(string existing, string folder)
    {
        var probe = Path.Combine(folder, ".tsume-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            return CreateHardLink(probe, existing, IntPtr.Zero);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Lets every account and packaged app (Xbox / Game Pass AppContainer) on this PC read the file.
    /// A hard-linked file keeps the permissions of Tsume's own data folder rather than inheriting the game folder's ACL.
    /// </summary>
    /// <returns>False if the permission could not be given.</returns>
    public static bool LetEveryoneRead(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl(AccessControlSections.Access);
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            SecurityIdentifier[] wanted =
            [
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                new SecurityIdentifier("S-1-15-2-1"),   // ALL APPLICATION PACKAGES (Xbox / MSIX AppContainer games)
            ];

            var changed = false;
            foreach (var sid in wanted)
            {
                var already = rules.Any(rule => rule.AccessControlType == AccessControlType.Allow
                    && rule.IdentityReference.Equals(sid)
                    && rule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));
                if (already) continue;

                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                changed = true;
            }

            if (changed) file.SetAccessControl(security);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    /// <summary>The file's hard-link count: 1 unless it has been linked.</summary>
    public static int Names(string path)
    {
        using var file = File.OpenHandle(path);
        return GetFileInformationByHandle(file, out var facts) ? (int)facts.Links : 1;
    }

    // BY_HANDLE_FILE_INFORMATION. Pack = 4 because its FILETIMEs are pairs of 32-bit numbers.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct FileFacts
    {
        public uint Attributes;
        public long Created, Read, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileFacts facts);

    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;

        Unprotect(path);
        File.Delete(path);
    }

    /// <summary>Writes text under a temporary name, then renames it into place.</summary>
    public static void WriteWhole(string path, string text, bool withMark = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(withMark));

        Unprotect(path);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes a folder recursively, ignoring failures. Only for Tsume's own data folders.</summary>
    public static void Discard(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left-overs only cost disk space.
        }
    }

    public static bool IsEmpty(string folder) => !Directory.EnumerateFileSystemEntries(folder).Any();

    /// <summary>Clears the read-only attribute, which would block replacing or deleting the file.</summary>
    private static void Unprotect(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, FileAttributes.Normal);
    }
}
