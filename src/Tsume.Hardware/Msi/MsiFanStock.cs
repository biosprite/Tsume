using System.Text.Json;

namespace Tsume.Hardware.Msi;

/// <summary>
/// What Tsume knows about the board's fan tables, kept on disk so it survives a helper restart: the firmware's own
/// speeds, and whether the tables may hold a custom curve right now. The firmware keeps following its tables with
/// the custom curve switched off, so a custom table must never be taken for the firmware's own; the marker is set
/// before the first custom speed is written and cleared only once the firmware's own speeds are back.
/// </summary>
internal sealed class MsiFanStock(string file)
{
    /// <summary>Saved stock fan tables for one board and firmware build.</summary>
    public sealed record Entry(
        string Board,
        string? Firmware,
        int[]? Fan1,
        int[]? Fan2,
        bool TablesMayBeCustom,
        int[]? Temperatures = null,
        int? OverflowTemp = null,
        int[]? Falling1 = null,
        int[]? Falling2 = null);

    /// <summary>
    /// The saved entry for this board in <paramref name="entry"/> (null if there is none, or it is another board's).
    /// False when the file could not be read for now; then nothing may be saved over it. A damaged file is read as
    /// "tables may be custom, firmware's own speeds unknown", the safe assumption.
    /// </summary>
    public bool TryLoad(string board, out Entry? entry)
    {
        entry = null;
        try
        {
            if (!File.Exists(file)) return true;
            var saved = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
            if (saved is null || string.IsNullOrEmpty(saved.Board)) throw new JsonException("empty");
            entry = saved.Board == board ? saved : null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            entry = new(board, Firmware: null, Fan1: null, Fan2: null, TablesMayBeCustom: true);
            return true;
        }
    }

    /// <summary>Saves the entry, replacing any other. False if it could not be written.</summary>
    public bool Save(Entry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tsume-tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
