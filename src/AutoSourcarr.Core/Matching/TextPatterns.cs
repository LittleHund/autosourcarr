using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Matching;

internal static class TextPatterns
{
    private static readonly IReadOnlyDictionary<string, Regex> Patterns = Load();
    public static Regex Get(string name) => Patterns[name];

    private static IReadOnlyDictionary<string, Regex> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutoSourcarr.Core.Matching.patterns.json")
            ?? throw new InvalidDataException("Missing matching expressions.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p =>
        {
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (p.Value.GetProperty("ignoreCase").GetBoolean()) options |= RegexOptions.IgnoreCase;
            if (p.Value.GetProperty("multiline").GetBoolean()) options |= RegexOptions.Multiline;
            return new Regex(p.Value.GetProperty("pattern").GetString()!, options, TimeSpan.FromSeconds(2));
        }, StringComparer.Ordinal);
    }
}

internal static class UnicodeProperties
{
    private static readonly Dictionary<string, int[][]> Ranges = Load();
    public static bool IsLetter(Rune rune) => Contains("letters", rune.Value);
    public static bool IsAlphanumeric(Rune rune) => Contains("alphanumeric", rune.Value);
    public static bool IsLatin(Rune rune) => Contains("latinLetters", rune.Value);
    public static bool IsLowerLatin(Rune rune) => Contains("lowerLatin", rune.Value);
    public static int Length(string value) => value.EnumerateRunes().Count();

    public static string[] Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '_' || IsAlphanumeric(rune))
                word.Append(rune.ToString());
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words.ToArray();
    }

    private static bool Contains(string key, int value)
    {
        var ranges = Ranges[key];
        var left = 0;
        var right = ranges.Length - 1;
        while (left <= right)
        {
            var middle = (left + right) / 2;
            if (value < ranges[middle][0]) right = middle - 1;
            else if (value > ranges[middle][1]) left = middle + 1;
            else return true;
        }
        return false;
    }

    private static Dictionary<string, int[][]> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutoSourcarr.Core.Matching.unicode-properties.json")
            ?? throw new InvalidDataException("Missing matching Unicode properties.");
        using var json = JsonDocument.Parse(stream);
        return new[] { "letters", "alphanumeric", "latinLetters", "lowerLatin" }.ToDictionary(
            name => name, name => json.RootElement.GetProperty(name).Deserialize<int[][]>()!, StringComparer.Ordinal);
    }
}
