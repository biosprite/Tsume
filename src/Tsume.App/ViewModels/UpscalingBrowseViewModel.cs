using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Theme;

namespace Tsume.App.ViewModels;

/// <summary>
/// In-panel folder browser for adding a game by hand. Not a Windows file dialog:
/// that would take focus, and the panel closes when it loses focus.
/// </summary>
public sealed class UpscalingBrowseViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;

    /// <summary>The folder being shown; empty for the list of drives.</summary>
    private string _folder = "";

    public UpscalingBrowseViewModel(UpscalingViewModel hub)
    {
        _hub = hub;
        Up = new Command(() => Go(Path.GetDirectoryName(_folder) ?? ""));
    }

    /// <summary>Raised when the list has been refilled, so the screen can put focus on its first row.</summary>
    public event Action? Moved;

    public ICommand Up { get; }

    public ObservableCollection<BrowseEntry> Entries { get; } = [];

    public string Location => _folder.Length == 0 ? Strings.ThisPC : _folder;

    public Visibility UpShown => _folder.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string Status { get; private set => Set(ref field, value); } = "";

    /// <summary>Reopens in the folder it was last left in.</summary>
    protected override void OnOpened() => Go(Directory.Exists(_folder) ? _folder : "");

    private void Go(string folder)
    {
        List<BrowseEntry> entries;
        try
        {
            entries = folder.Length == 0 ? Drives() : Contents(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = Strings.CannotOpenFolder;
            return;
        }

        _folder = folder;
        Status = Strings.BrowseHint;

        Entries.Clear();
        foreach (var entry in entries) Entries.Add(entry);

        Notify(nameof(Location));
        Notify(nameof(UpShown));
        Moved?.Invoke();
    }

    private List<BrowseEntry> Drives() => [.. DriveInfo.GetDrives()
        .Where(drive => drive.IsReady)
        .Select(drive => new BrowseEntry(
            string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
            Glyphs.Drive,
            new Command(() => Go(drive.RootDirectory.FullName))))];

    /// <summary>Folders first, then programs. Hidden and system folders are left out.</summary>
    private List<BrowseEntry> Contents(string folder)
    {
        var folders = new DirectoryInfo(folder).EnumerateDirectories()
            .Where(inner => (inner.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderBy(inner => inner.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(inner => new BrowseEntry(inner.Name, Glyphs.Folder, new Command(() => Go(inner.FullName))));

        var programs = Directory.EnumerateFiles(folder, "*.exe")
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .Select(program => new BrowseEntry(Path.GetFileName(program), Glyphs.Game, new Command(() => Pick(program)), IsProgram: true));

        return [.. folders, .. programs];
    }

    private void Pick(string program)
    {
        IsOpen = false;
        _hub.AddByProgram(program);
    }
}

/// <summary>A row in the browser: a drive, a folder, or a program.</summary>
public sealed record BrowseEntry(string Name, string Glyph, ICommand Open, bool IsProgram = false);
