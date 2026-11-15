using System.IO;

namespace Tsume.App.Platform;

/// <summary>A per-user "Tsume" entry in the Start menu.</summary>
public static class StartMenuShortcut
{
    private static readonly string Link = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Tsume.lnk");

    /// <summary>
    /// True when Tsume runs from Program Files: the installer already added an all-users
    /// Start-menu entry, so the per-user one is neither offered nor kept.
    /// </summary>
    public static bool IsProvidedByInstaller { get; } = AppContext.BaseDirectory.StartsWith(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

    public static bool Exists
    {
        get => File.Exists(Link);
        set
        {
            if (value) Create();
            else File.Delete(Link);
        }
    }

    /// <summary>If Tsume has moved since the shortcut was made, remake it — or drop it, if the installer now provides one.</summary>
    public static void RepairIfMoved()
    {
        // A shortcut from when Tsume was called Talon carries over under the new name.
        var old = Path.Combine(Path.GetDirectoryName(Link)!, "Talon.lnk");
        if (File.Exists(old))
        {
            File.Delete(old);
            if (!IsProvidedByInstaller) Create();
        }

        if (!Exists) return;

        if (IsProvidedByInstaller) File.Delete(Link);
        else Create();
    }

    private static void Create()
    {
        // Windows has no plain API for .lnk files; the scripting shell object is the simplest route.
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(Link);

        link.TargetPath = Environment.ProcessPath;
        link.WorkingDirectory = AppContext.BaseDirectory;
        link.Description = Language.Strings.ShortcutDescription;
        link.Save();
    }
}
