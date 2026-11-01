using AutoSourcarr.Core.Matching;

namespace AutoSourcarr.Core.Catalogue.Titles;

/// <summary>
/// MAT-4: alternative titles stay whole. The released alias splitter cuts lists at ",", ";", "/" and "|", which also
/// cut real titles such as "Steins;Gate", "Love, Chunibyo &amp; Other Delusions" or "Yotsuba&amp;!". This keeps the released
/// result and repairs it: neighbouring pieces are joined again when they form a known whole title of the record (its own
/// title, the entry's titles) or when the separator sat inside a word ("Steins;Gate", "Fate/Zero"); a known whole title
/// written in the alias section is kept even when the released filters dropped it.
/// </summary>
public static class WholeTitleAliases
{
    private static readonly char[] InWordSeparators = [';', '/', '|', ','];

    public static IReadOnlyList<string> Extract(string? description, IEnumerable<string>? knownTitles = null)
    {
        if (string.IsNullOrEmpty(description)) return [];
        var pieces = TextMatcher.ExtractAltTitles(description).ToList();
        var known = (knownTitles ?? []).Select(title => title.Trim()).Where(TextMatcher.IsMeaningfulTitle)
            .GroupBy(TextMatcher.NormalizeTitle, StringComparer.Ordinal).Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // Join neighbours whose concatenation is a known whole title (longest window first).
        for (var start = 0; start < pieces.Count; start++)
            for (var end = pieces.Count; end > start + 1; end--)
            {
                var joined = TextMatcher.NormalizeTitle(string.Join(" ", pieces.GetRange(start, end - start)));
                if (!known.TryGetValue(joined, out var whole)) continue;
                pieces.RemoveRange(start, end - start); pieces.Insert(start, whole);
                break;
            }

        // Join neighbours that were cut at a separator written inside a word ("Steins;Gate").
        for (var index = 0; index + 1 < pieces.Count; index++)
        {
            var left = pieces[index]; var right = pieces[index + 1];
            var glued = InWordSeparators.Select(separator => left + separator + right).FirstOrDefault(candidate => description.Contains(candidate, StringComparison.Ordinal));
            if (glued is null || left.Length == 0 || right.Length == 0 || char.IsWhiteSpace(left[^1]) || char.IsWhiteSpace(right[0])) continue;
            pieces[index] = glued; pieces.RemoveAt(index + 1); index--;
        }

        // Known whole titles written in the alias section survive filters that dropped them (e.g. "Yotsuba&!").
        var header = TextPatterns.Get("ALT_HEADER_RE").Match(description);
        var section = header.Success ? description[header.Index..] : "";
        foreach (var (normalized, whole) in known)
        {
            if (!section.Contains(whole, StringComparison.OrdinalIgnoreCase)) continue;
            if (pieces.Any(piece => TextMatcher.NormalizeTitle(piece) == normalized)) continue;
            pieces.RemoveAll(piece => IsPieceOf(piece, normalized));
            pieces.Add(whole);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return pieces.Where(piece => TextMatcher.IsMeaningfulTitle(piece) && seen.Add(TextMatcher.NormalizeTitle(piece))).ToArray();
    }

    /// <summary>A fragment strictly inside a whole title (word-aligned).</summary>
    private static bool IsPieceOf(string piece, string wholeNormalized)
    {
        var normalized = TextMatcher.NormalizeTitle(piece);
        return normalized.Length > 0 && normalized != wholeNormalized && (" " + wholeNormalized + " ").Contains(" " + normalized + " ", StringComparison.Ordinal);
    }
}
