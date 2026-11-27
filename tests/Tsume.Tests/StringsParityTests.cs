using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tsume.App.Language;
using Tsume.Upscaling;

namespace Tsume.Tests;

public sealed partial class StringsParityTests
{
    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex FormatPlaceholderRegex();

    [GeneratedRegex(@"Find\(\""([A-Za-z0-9_]+)\""\)")]
    private static partial Regex ExplicitFindKeyRegex();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tsume.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Tsume.slnx.");
    }

    private static Dictionary<string, string> LoadResxEntries(string resxPath, out List<string> duplicateKeys)
    {
        var doc = XDocument.Load(resxPath);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        duplicateKeys = [];

        foreach (var data in doc.Root!.Elements("data"))
        {
            var name = data.Attribute("name")?.Value;
            var value = data.Element("value")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(name)) continue;

            if (!entries.TryAdd(name, value))
                duplicateKeys.Add(name);
        }

        return entries;
    }

    [Fact]
    public void StringsCsAndStringsResx_AreInFullParityWithMatchingPlaceholders()
    {
        var root = FindRepoRoot();
        var resxPath = Path.Combine(root, "src", "Tsume.App", "Language", "Strings.resx");
        var csPath = Path.Combine(root, "src", "Tsume.App", "Language", "Strings.cs");

        var resx = LoadResxEntries(resxPath, out var duplicateKeys);
        Assert.Empty(duplicateKeys);

        var csSource = File.ReadAllText(csPath);
        var stringsType = typeof(Strings);

        var referencedKeys = new HashSet<string>(StringComparer.Ordinal);
        var missingInResx = new List<string>();
        var placeholderMismatches = new List<string>();

        // 1. Every public static property on Strings must exist in Strings.resx and have no format placeholders.
        foreach (var prop in stringsType.GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            referencedKeys.Add(prop.Name);
            if (!resx.TryGetValue(prop.Name, out var value))
            {
                missingInResx.Add($"Property Strings.{prop.Name}");
                continue;
            }

            var placeholders = DistinctPlaceholderIndices(value);
            if (placeholders.Count > 0)
            {
                placeholderMismatches.Add(
                    $"Property Strings.{prop.Name} has format placeholder(s) in Strings.resx: '{value}'");
            }
        }

        // 2. Every public static method (excluding helpers Explain/FormatSize) maps to a key of the same name
        //    and its placeholder count must match the method's parameter count.
        var nonKeyMethods = new HashSet<string>(StringComparer.Ordinal) { nameof(Strings.Explain), nameof(Strings.FormatSize) };
        foreach (var method in stringsType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.IsSpecialName || nonKeyMethods.Contains(method.Name))
                continue;

            referencedKeys.Add(method.Name);
            if (!resx.TryGetValue(method.Name, out var value))
            {
                missingInResx.Add($"Method Strings.{method.Name}");
                continue;
            }

            var expectedArgs = method.GetParameters().Length;
            var placeholders = DistinctPlaceholderIndices(value);
            var maxIndex = placeholders.Count == 0 ? -1 : placeholders.Max();

            if (placeholders.Count != expectedArgs || maxIndex != expectedArgs - 1)
            {
                placeholderMismatches.Add(
                    $"Method Strings.{method.Name}({expectedArgs} args) expects placeholders {{0}}..{{{expectedArgs - 1}}}, " +
                    $"but Strings.resx has '{value}'");
            }
        }

        // 3. Every explicit Find("Key") call in Strings.cs must exist in Strings.resx.
        foreach (Match match in ExplicitFindKeyRegex().Matches(csSource))
        {
            var key = match.Groups[1].Value;
            referencedKeys.Add(key);
            if (!resx.ContainsKey(key))
                missingInResx.Add($"Explicit Find(\"{key}\")");
        }

        // 4. Every Trouble enum value must have a matching Trouble<Name> key in Strings.resx.
        foreach (var troubleName in Enum.GetNames<Trouble>())
        {
            var key = "Trouble" + troubleName;
            referencedKeys.Add(key);
            if (!resx.ContainsKey(key))
                missingInResx.Add($"Trouble.{troubleName} -> {key}");
        }

        // 5. Every key in Strings.resx must be referenced by Strings.cs or Trouble enum.
        var orphanedInResx = resx.Keys
            .Where(k => !referencedKeys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missingInResx.Count == 0, "Missing in Strings.resx:\n" + string.Join('\n', missingInResx));
        Assert.True(placeholderMismatches.Count == 0, "Placeholder mismatches:\n" + string.Join('\n', placeholderMismatches));
        Assert.True(orphanedInResx.Count == 0, "Orphaned keys in Strings.resx:\n" + string.Join('\n', orphanedInResx));
    }

    private static HashSet<int> DistinctPlaceholderIndices(string text)
    {
        // Strip escaped braces {{ and }} before scanning for placeholders.
        var unescaped = text.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal);
        var indices = new HashSet<int>();
        foreach (Match match in FormatPlaceholderRegex().Matches(unescaped))
        {
            if (int.TryParse(match.Groups[1].Value, out var index))
                indices.Add(index);
        }
        return indices;
    }
}
