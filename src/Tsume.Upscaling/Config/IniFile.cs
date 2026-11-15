namespace Tsume.Upscaling.Config;

/// <summary>
/// OptiScaler.ini, edited in place: a change rewrites only its own line and leaves every other byte
/// (comments, order, line endings, BOM) alone.
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines;
    private readonly string _lineEnd;
    private readonly bool _hasMark;

    private IniFile(string text)
    {
        _hasMark = text.StartsWith('﻿');
        if (_hasMark) text = text[1..];

        _lineEnd = text.Contains("\r\n") ? "\r\n" : "\n";
        _lines = [.. text.Split(_lineEnd)];
    }

    public static IniFile Parse(string text) => new(text);

    /// <summary>An empty file, if there is none: OptiScaler takes its own default for anything not written.</summary>
    public static IniFile Load(string path) => new(File.Exists(path) ? File.ReadAllText(path) : "");

    public void Save(string path) => Files.WriteWhole(path, ToString(), _hasMark);

    public override string ToString() => string.Join(_lineEnd, _lines);

    /// <summary>The value as written, or null when the key is not in the file. "auto" means OptiScaler decides.</summary>
    public string? Get(string section, string key) =>
        Find(section, key) is { } line ? _lines[line][(_lines[line].IndexOf('=') + 1)..].Trim() : null;

    public void Set(string section, string key, string value)
    {
        if (Find(section, key) is { } line)
        {
            // Keep the key as the file spells it.
            _lines[line] = $"{_lines[line][.._lines[line].IndexOf('=')].Trim()}={value}";
            return;
        }

        var (start, end) = Section(section);

        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        // After the section's last setting — not after the blank lines and the
        // banner that lead into the next section.
        var after = end;
        while (after > start + 1 && !IsSetting(_lines[after - 1])) after--;
        if (after == start + 1) after = end;   // a section with no settings yet

        _lines.Insert(after, $"{key}={value}");
    }

    private int? Find(string section, string key)
    {
        var (start, end) = Section(section);

        for (var line = start + 1; start >= 0 && line < end; line++)
        {
            if (!IsSetting(_lines[line])) continue;

            var name = _lines[line][.._lines[line].IndexOf('=')].Trim();
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) return line;
        }

        return null;
    }

    /// <summary>The line holding <c>[section]</c> and the line after its last; (-1, -1) when it is not there.</summary>
    private (int Start, int End) Section(string section)
    {
        var start = _lines.FindIndex(line => line.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return (-1, -1);

        var end = _lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
        return (start, end < 0 ? _lines.Count : end);
    }

    private static bool IsSetting(string line)
    {
        var text = line.TrimStart();
        return text.Length > 0 && text[0] is not (';' or '#' or '[') && text.Contains('=');
    }
}
