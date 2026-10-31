using System.Text;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Text;

namespace AutoSourcarr.Core.Catalogue;

public sealed record NativeTitleVote(string Title, string Language = "", long? Priority = 1000, bool IsVariant = false, bool IsIdentity = false);

/// <summary>One vote per concrete bound record. External catalogues and generated aliases never vote.</summary>
public static class CatalogueTitleConsensus
{
    public static string MostBoundTitle(IEnumerable<NativeTitleVote> records, string currentTitle = "")
    {
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal); var current = TextMatcher.NormalizeTitle(currentTitle);
        foreach (var row in records)
        {
            if (row.IsIdentity) continue;
            var value = UnicodeText.Trim(row.Title);
            if (value.Length == 0 || !TextMatcher.IsMeaningfulTitle(value)) continue;
            if (TextMatcher.TitleColorVariant(value) != "unknown") value = Nonempty(TextMatcher.ColorBaseTitle(value), value);
            if (TextMatcher.TitleMediaVariant(value) != "unknown") value = Nonempty(TextMatcher.MediaBaseTitle(value), value);
            value = UnicodeText.Trim(value); var norm = TextMatcher.NormalizeTitle(value); if (norm.Length == 0) continue;
            if (!groups.TryGetValue(norm, out var group)) groups[norm] = group = new(norm);
            group.Count++; if (!row.IsVariant) group.Primary++;
            var lang = row.Language.ToLowerInvariant(); var explicitEnglish = lang == "en";
            var english = explicitEnglish || lang is "" or "all" && TextMatcher.LooksPreferredEnglish(value);
            if (explicitEnglish) group.ExplicitEnglish++; if (english) group.English++;
            if (row.Priority is { } priority) group.BestPriority = Math.Min(group.BestPriority, priority == 0 ? 1000 : priority);
            if (!group.Forms.TryGetValue(value, out var form)) group.Forms[value] = form = new(value);
            form.Count++; if (explicitEnglish) form.ExplicitEnglish++; if (english) form.English++;
        }
        var winner = groups.Values.OrderByDescending(g => g.Count).ThenByDescending(g => g.Primary)
            .ThenByDescending(g => g.ExplicitEnglish).ThenByDescending(g => g.English).ThenByDescending(g => g.Normalized == current)
            .ThenBy(g => g.BestPriority).ThenBy(g => g.Normalized, UnicodeCodePointComparer.Instance).FirstOrDefault();
        return winner is null ? "" : UnicodeText.Trim(winner.Forms.Values.OrderByDescending(f => f.Count).ThenByDescending(f => f.ExplicitEnglish)
            .ThenByDescending(f => f.English).ThenBy(f => f.Value.EnumerateRunes().Count())
            .ThenBy(f => UnicodeCaseFold.Fold(f.Value), UnicodeCodePointComparer.Instance).First().Value);
    }
    private static string Nonempty(string value, string fallback) => value.Length == 0 ? fallback : value;
    private sealed class Group(string normalized)
    {
        public string Normalized { get; } = normalized;
        public int Count, Primary, ExplicitEnglish, English; public long BestPriority = 1000000;
        public Dictionary<string, Form> Forms { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Form(string value)
    { public string Value { get; } = value; public int Count, ExplicitEnglish, English; }
}
