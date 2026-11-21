namespace Tsume.Core.Diagnostics;

/// <summary>A log file under %LOCALAPPDATA%\Tsume, deleted and restarted when it passes 256 KB.</summary>
public sealed class Journal(string name)
{
    private const long MaxBytes = 256 * 1024;

    private readonly Lock _gate = new();

    public string Path { get; } = System.IO.Path.Combine(
        Core.DataFolder.Path, $"{name}.log");

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes) File.Delete(Path);

                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the program down.
            }
        }
    }

    /// <summary>Reads the whole log, or null if it has not been written yet or cannot be read.</summary>
    public string? Read()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path)) return null;

                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
