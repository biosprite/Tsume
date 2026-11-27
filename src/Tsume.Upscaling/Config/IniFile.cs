using System.Text;

namespace Tsume.Upscaling.Config;

/// <summary>
/// OptiScaler.ini, edited in place: a change rewrites only its own line and leaves every other byte
/// (comments, order, line endings, BOM) alone.
/// </summary>
public sealed class IniFile
{
    private readonly List<(string Text, string End)> _lines;
    private readonly string _lineEnd;
    private readonly bool _hasMark;

    private IniFile(string text, bool? hasMark = null)
    {
        if (text.StartsWith('\uFEFF'))
        {
            _hasMark = true;
            text = text[1..];
        }
        else
        {
            _hasMark = hasMark ?? false;
        }

        _lineEnd = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lines = SplitLines(text);
    }

    public static IniFile Parse(string text) => new(text);

    /// <summary>An empty file, if there is none: OptiScaler takes its own default for anything not written.</summary>
    public static IniFile Load(string path)
    {
        if (!File.Exists(path)) return new("", hasMark: false);

        var bytes = File.ReadAllBytes(path);
        var hasMark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = hasMark
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

        return new(text, hasMark);
    }

    public void Save(string path) => Files.WriteWhole(path, ToString(), _hasMark);

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (text, end) in _lines)
        {
            sb.Append(text);
            sb.Append(end);
        }

        return sb.ToString();
    }

    /// <summary>The value as written, or null when the key is not in the file. "auto" means OptiScaler decides.</summary>
    public string? Get(string section, string key) =>
        Find(section, key) is { } line ? _lines[line].Text[(_lines[line].Text.IndexOf('=') + 1)..].Trim() : null;

    public void Set(string section, string key, string value)
    {
        if (Find(section, key) is { } line)
        {
            // Keep the key as the file spells it, along with its original line ending.
            var existing = _lines[line];
            _lines[line] = ($"{existing.Text[..existing.Text.IndexOf('=')].Trim()}={value}", existing.End);
            return;
        }

        var (start, end) = Section(section);

        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Text.Length > 0) AppendLine("");
            AppendLine($"[{section}]");
            AppendLine($"{key}={value}");
            return;
        }

        // After the section's last setting — not after the blank lines and the
        // banner that lead into the next section.
        var after = end;
        while (after > start + 1 && !IsSetting(_lines[after - 1].Text)) after--;
        if (after == start + 1) after = end;   // a section with no settings yet

        if (after >= _lines.Count)
            AppendLine($"{key}={value}");
        else
            _lines.Insert(after, ($"{key}={value}", _lineEnd));
    }

    private void AppendLine(string text)
    {
        if (_lines.Count > 0 && _lines[^1].End.Length == 0)
            _lines[^1] = (_lines[^1].Text, _lineEnd);

        _lines.Add((text, ""));
    }

    private int? Find(string section, string key)
    {
        var (start, end) = Section(section);

        for (var line = start + 1; start >= 0 && line < end; line++)
        {
            var text = _lines[line].Text;
            if (!IsSetting(text)) continue;

            var name = text[..text.IndexOf('=')].Trim();
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) return line;
        }

        return null;
    }

    /// <summary>The line holding <c>[section]</c> and the line after its last; (-1, -1) when it is not there.</summary>
    private (int Start, int End) Section(string section)
    {
        var start = _lines.FindIndex(line => line.Text.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return (-1, -1);

        var end = _lines.FindIndex(start + 1, line => line.Text.TrimStart().StartsWith('['));
        return (start, end < 0 ? _lines.Count : end);
    }

    private static List<(string Text, string End)> SplitLines(string text)
    {
        var lines = new List<(string Text, string End)>();
        var i = 0;

        while (i < text.Length)
        {
            var next = text.IndexOfAny(['\r', '\n'], i);
            if (next < 0)
            {
                lines.Add((text[i..], ""));
                break;
            }

            var end = text[next] == '\r' && next + 1 < text.Length && text[next + 1] == '\n'
                ? "\r\n"
                : text[next..(next + 1)];

            lines.Add((text[i..next], end));
            i = next + end.Length;

            if (i == text.Length)
                lines.Add(("", ""));
        }

        if (lines.Count == 0)
            lines.Add(("", ""));

        return lines;
    }

    private static bool IsSetting(string line)
    {
        var text = line.TrimStart();
        return text.Length > 0 && text[0] is not (';' or '#' or '[') && text.Contains('=');
    }
}
