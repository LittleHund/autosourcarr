using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AutoSourcarr.Core.Unicode;

/// <summary>Retained Unicode15 full case folding for persistent non-title identity keys.</summary>
public static class UnicodeCaseFold
{
    private static readonly Lazy<IReadOnlyDictionary<int, string>> Mapping = new(Load);
    public static string Fold(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes()) output.Append(Mapping.Value.TryGetValue(rune.Value, out var folded) ? folded : rune.ToString());
        return output.ToString();
    }
    public static string Truncate(string text, int runes) => string.Concat(text.EnumerateRunes().Take(runes).Select(rune => rune.ToString()));
    private static IReadOnlyDictionary<int, string> Load()
    {
        using var input = typeof(UnicodeCaseFold).Assembly.GetManifestResourceStream("AutoSourcarr.Core.Unicode.unicode-casefold.json")
            ?? throw new InvalidOperationException("Retained case-fold table is missing");
        using var document = JsonDocument.Parse(input);
        return document.RootElement.GetProperty("mapping").EnumerateObject().ToDictionary(item => int.Parse(item.Name, CultureInfo.InvariantCulture), item => item.Value.GetString()!);
    }
}
