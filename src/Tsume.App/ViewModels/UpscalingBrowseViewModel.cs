using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Theme;

namespace Tsume.App.ViewModels;

/// <summary>
/// In-panel folder browser for adding a game's .exe or managing saved game library folders.
/// Not a Windows file dialog: that would take focus, and the panel closes when it loses focus.
/// </summary>
public sealed class UpscalingBrowseViewModel : LayerViewModel
{
    private readonly UpscalingViewModel _hub;

    /// <summary>The folder currently shown; empty for the list of drives.</summary>
    private string _folder = "";

    /// <summary>Last folder visited in Add a game mode, so it reopens there.</summary>
    private string _gameFolder = "";

    public UpscalingBrowseViewModel(UpscalingViewModel hub)
    {
        _hub = hub;
        OpenGame = new Command(() => OpenIn(folderMode: false));
        OpenFolder = new Command(() => OpenIn(folderMode: true));
        Up = new Command(GoUp);
        ToggleCurrentFolder = new Command(ToggleCurrent);
    }

    /// <summary>Raised when the list has been refilled or a saved folder removed, so the screen can fix focus.</summary>
    public event Action? Moved;

    public ICommand OpenGame { get; }
    public ICommand OpenFolder { get; }
    public ICommand Up { get; }
    public ICommand ToggleCurrentFolder { get; }

    public ObservableCollection<BrowseEntry> Entries { get; } = [];
    public ObservableCollection<SavedFolderEntry> SavedFolders { get; } = [];

    /// <summary>Picking a games folder to save (Add folder), rather than a program (Add a game).</summary>
    public bool IsFolderMode { get; private set; }

    public string Title => IsFolderMode ? Strings.AddFolder : Strings.AddGame;

    public string Location => _folder.Length == 0 ? Strings.ThisPC : _folder;

    public Visibility UpShown => Shown(_folder.Length > 0);

    public Visibility SavedFoldersShown => Shown(IsFolderMode && _folder.Length == 0 && SavedFolders.Count > 0);

    public Visibility FolderActionShown => Shown(IsFolderMode && _folder.Length > 0);

    public bool CanAddCurrentFolder => IsFolderMode && _folder.Length > 0 && !_hub.List.HasFolder(_folder);

    public string FolderActionLabel => CanAddCurrentFolder ? Strings.UseThisFolder : Strings.RemoveThisFolder;

    public string FolderActionGlyph => CanAddCurrentFolder ? Glyphs.Add : Glyphs.Remove;

    public Visibility EmptyFolderNoteShown => Shown(IsFolderMode && _folder.Length > 0 && Entries.Count == 0);

    public string Status { get; private set => Set(ref field, value); } = "";

    private void OpenIn(bool folderMode)
    {
        IsFolderMode = folderMode;
        IsOpen = true;
    }

    /// <summary>Add a game reopens in the folder it was last left in; Add folder starts at the drives and saved folders.</summary>
    protected override void OnOpened()
    {
        RefreshSavedFolders();
        Go(!IsFolderMode && Directory.Exists(_gameFolder) ? _gameFolder : "");
    }

    protected override void OnClosed()
    {
        if (!IsFolderMode) _gameFolder = _folder;
        IsFolderMode = false;
    }

    /// <summary>Controller B / Escape steps up one folder until This PC, then closes the browser.</summary>
    public override bool GoBack()
    {
        if (_folder.Length == 0) return false;
        GoUp();
        return true;
    }

    private void GoUp()
    {
        if (_folder.Length == 0) return;

        var parent = Path.GetDirectoryName(_folder) ?? "";
        if (!Go(parent) && parent.Length > 0) Go("");
    }

    private bool Go(string folder)
    {
        List<BrowseEntry> entries;
        try
        {
            entries = folder.Length == 0 ? Drives() : Contents(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status = Strings.CannotOpenFolder;
            return false;
        }

        _folder = folder;

        Entries.Clear();
        foreach (var entry in entries) Entries.Add(entry);

        NotifyFolderState();
        Moved?.Invoke();
        return true;
    }

    /// <summary>Add this folder (and back to the games list), or Remove this folder (and stay).</summary>
    private void ToggleCurrent()
    {
        if (!IsFolderMode || _folder.Length == 0) return;

        if (CanAddCurrentFolder)
        {
            _hub.AddFolder(_folder);
            IsOpen = false;
        }
        else
        {
            Forget(_folder);
        }
    }

    private void Forget(string folder)
    {
        _hub.RemoveFolder(folder);
        RefreshSavedFolders();
        NotifyFolderState();
    }

    private void RefreshSavedFolders()
    {
        SavedFolders.Clear();
        if (!IsFolderMode) return;

        foreach (var folder in _hub.List.Folders)
        {
            SavedFolders.Add(new SavedFolderEntry(
                Path.GetFileName(folder) is { Length: > 0 } name ? name : folder,   // a drive's root has no name
                Directory.Exists(folder) ? folder : Strings.FolderMissingDetail(folder),
                new Command(() => Go(folder)),
                new Command(() =>
                {
                    Forget(folder);
                    Moved?.Invoke();   // its row is gone, and focus with it
                })));
        }
    }

    /// <summary>Everything that depends on the folder shown, the mode, or the saved folders.</summary>
    private void NotifyFolderState()
    {
        Status = !IsFolderMode ? Strings.BrowseHint
            : _folder.Length > 0 && _hub.List.HasFolder(_folder) ? Strings.BrowseFolderSavedHint
            : Strings.BrowseFolderHint;

        foreach (var name in new[] { nameof(Title), nameof(Location), nameof(UpShown), nameof(SavedFoldersShown),
                     nameof(FolderActionShown), nameof(CanAddCurrentFolder), nameof(FolderActionLabel),
                     nameof(FolderActionGlyph), nameof(EmptyFolderNoteShown) })
            Notify(name);
    }

    private static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private List<BrowseEntry> Drives() => [.. DriveInfo.GetDrives()
        .Where(drive => drive.IsReady)
        .Select(drive => new BrowseEntry(
            string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})",
            Glyphs.Drive,
            new Command(() => Go(drive.RootDirectory.FullName))))];

    /// <summary>Folders first, then programs (in Add a game). Hidden and system folders are left out.</summary>
    private List<BrowseEntry> Contents(string folder)
    {
        var folders = new DirectoryInfo(folder).EnumerateDirectories()
            .Where(inner => (inner.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderBy(inner => inner.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(inner => new BrowseEntry(
                inner.Name,
                Glyphs.Folder,
                new Command(() => Go(inner.FullName)),
                Highlight: IsFolderMode && _hub.List.HasFolder(inner.FullName)));

        if (IsFolderMode) return [.. folders];

        var programs = Directory.EnumerateFiles(folder, "*.exe")
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .Select(program => new BrowseEntry(Path.GetFileName(program), Glyphs.Game, new Command(() => Pick(program)), Highlight: true));

        return [.. folders, .. programs];
    }

    private void Pick(string program)
    {
        IsOpen = false;
        _hub.AddByProgram(program);
    }
}

/// <summary>A row in the browser: a drive, a folder, or a program. Saved folders and programs are highlighted.</summary>
public sealed record BrowseEntry(string Name, string Glyph, ICommand Open, bool Highlight = false);

/// <summary>A remembered games folder shown at the top of Add folder.</summary>
public sealed record SavedFolderEntry(string Name, string Detail, ICommand Open, ICommand Remove);
