namespace Tsume.Core;

/// <summary>%LOCALAPPDATA%\Tsume: settings, logs and the OptiScaler store.</summary>
public static class DataFolder
{
    public static string Path { get; } = Adopt();

    /// <summary>Tsume was called Talon until 0.6.8; the folder it left is taken over once.</summary>
    private static string Adopt()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = System.IO.Path.Combine(local, "Tsume");
        var old = System.IO.Path.Combine(local, "Talon");

        try
        {
            if (!Directory.Exists(path) && Directory.Exists(old)) Directory.Move(old, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Still in use by a running Talon: start fresh rather than fail.
        }

        return path;
    }
}
