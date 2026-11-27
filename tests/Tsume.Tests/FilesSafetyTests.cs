using System.Diagnostics;
using System.Reflection;
using Tsume.Upscaling.Config;

namespace Tsume.Tests;

public sealed class FilesSafetyTests
{
    private static readonly Type FilesType =
        typeof(IniFile).Assembly.GetType("Tsume.Upscaling.Files", throwOnError: true)!;

    private static readonly Func<string, string, string> Inside =
        FilesType.GetMethod("Inside", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<string, string, string>>();

    private static readonly Func<string, string, string> SafeChildFolder =
        FilesType.GetMethod("SafeChildFolder", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<string, string, string>>();

    private static readonly Func<string, string?> FindReparsePoint =
        FilesType.GetMethod("FindReparsePoint", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<string, string?>>();

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData(".\\")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("sub/../../escape")]
    [InlineData("sub\\..\\")]
    public void Inside_RejectsRootEscapeAndSelfPaths(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "tsume-inside-root");

        Assert.Throws<InvalidDataException>(() => Inside(root, relative));
    }

    [Fact]
    public void Inside_RejectsAbsolutePathOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "tsume-inside-root");
        var outside = Path.Combine(Path.GetPathRoot(root)!, "Windows", "win.ini");

        Assert.Throws<InvalidDataException>(() => Inside(root, outside));
    }

    [Theory]
    [InlineData("OptiScaler.ini")]
    [InlineData("sub/file.dll")]
    [InlineData("sub\\nested\\file.dll")]
    [InlineData("a/b/../c.dll")]
    public void Inside_AcceptsValidChildPaths(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "tsume-inside-root");
        var expectedPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        var resolved = Inside(root, relative);

        Assert.StartsWith(expectedPrefix, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(root), resolved.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/..")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("tag:colon")]
    [InlineData("tag*star")]
    [InlineData("tag?q")]
    [InlineData("tag<lt")]
    [InlineData("trailing.")]
    [InlineData("trailing..")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void SafeChildFolder_RejectsUnsafeFolderNames(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "tsume-safe-folder-root");

        Assert.Throws<InvalidDataException>(() => SafeChildFolder(root, name));
    }

    [Theory]
    [InlineData("v0.7.9-pre1")]
    [InlineData("nightly-2026-04-01")]
    [InlineData("OptiPatcher_v1.2.3")]
    public void SafeChildFolder_AcceptsValidTags(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), "tsume-safe-folder-root");
        var expected = Path.GetFullPath(Path.Combine(root, tag));

        var resolved = SafeChildFolder(root, tag);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void FindReparsePoint_ReturnsNullForNormalTreeAndDetectsDirectoryJunction()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tsume-reparse-test-{Guid.NewGuid():N}");
        var scanRoot = Path.Combine(tempRoot, "scan");
        var nestedDir = Path.Combine(scanRoot, "nested");
        var targetDir = Path.Combine(tempRoot, "outside-target");
        var junctionPath = Path.Combine(nestedDir, "escape-link");

        Directory.CreateDirectory(nestedDir);
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(nestedDir, "normal.txt"), "ok");
        File.WriteAllText(Path.Combine(targetDir, "secret.txt"), "outside");

        try
        {
            Assert.Null(FindReparsePoint(scanRoot));

            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)!;
            process.WaitForExit(10_000);
            Assert.Equal(0, process.ExitCode);

            var detected = FindReparsePoint(scanRoot);
            Assert.Equal(Path.Combine("nested", "escape-link"), detected);
        }
        finally
        {
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
