using System.Text;

namespace AutoSourcarr.Core.Matching;

public static partial class TextMatcher
{
    private static readonly Dictionary<char, char> PairDelimiters = new()
    {
        ['('] = ')', ['['] = ']', ['{'] = '}', ['（'] = '）', ['［'] = '］', ['｛'] = '｝',
        ['「'] = '」', ['『'] = '』', ['【'] = '】', ['〈'] = '〉', ['《'] = '》', ['〔'] = '〕',
        ['“'] = '”', ['‘'] = '’'
    };
    private static readonly HashSet<string> CommaContinuations = [
        "a", "an", "and", "as", "at", "because", "before", "but", "by", "for", "from", "how", "if", "in", "is", "of", "on", "or", "that", "the", "then", "to", "was", "when", "where", "which", "while", "who", "why", "with", "without",
        "i", "you", "he", "she", "we", "they", "it", "me", "him", "her", "us", "them", "my", "your", "his", "our", "their", "this", "these", "those", "there", "here"
    ];

    public static IReadOnlyList<string> ExtractAltTitles(string? description)
    {
        if (string.IsNullOrEmpty(description)) return [];
        var output = new List<string>();
        var active = false;
        var pending = "";
        void Append(string value)
        {
            var parts = SplitAliases(value).ToList();
            if (parts.Count == 0) return;
            if (pending.Length > 0) { parts[0] = (pending + " " + parts[0]).Trim(); pending = ""; }
            foreach (var piece in parts)
            {
                var part = piece.Trim();
                if (part.Length == 0) continue;
                var tokens = NormalizeTitle(part).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length is >= 2 and <= 12 && Continuations.Contains(tokens[^1])) { pending = part; continue; }
                output.Add(part);
            }
        }
        foreach (var line in description.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var text = line.Trim();
            var header = Pattern("ALT_HEADER_RE").Match(text);
            if (header.Success)
            {
                active = true;
                if (header.Groups[1].Value.Trim() is { Length: > 0 } inline) Append(inline);
                continue;
            }
            if (!active) continue;
            if (text.Length == 0)
            {
                if (output.Count > 0 && pending.Length == 0) break;
                continue;
            }
            if (Pattern("STOP_HEADER_RE").IsMatch(text)) { pending = ""; break; }
            Append(text);
            if (output.Count >= 30) break;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return output.Where(title => IsMeaningfulTitle(title) && !IsAliasFragment(title)
            && UnicodeProperties.Length(title) <= 250 && seen.Add(NormalizeTitle(title))).Select(title => title.Trim()).ToArray();
    }

    private static IEnumerable<string> SplitAliases(string value)
    {
        foreach (var part in SplitTopLevel(value, [" / ", "|", ";", "•"]))
        {
            var clean = part.Trim(" -•\t".ToCharArray());
            if (clean.Length == 0) continue;
            var comma = SplitCommaAliases(clean);
            if (comma.Count == 0) yield return clean;
            else foreach (var item in comma) yield return item;
        }
    }

    private static IReadOnlyList<string> SplitCommaAliases(string value)
    {
        if (!value.Contains(',')) return [];
        var parts = SplitTopLevel(value, [","]).Select(part => part.Trim(" \t-•".ToCharArray())).ToArray();
        if (parts.Length < 2 || parts.Any(part => part.Length == 0 || !LooksLikeCommaItem(part))) return [];
        if (parts.Length == 2 && !parts.All(StrongItem)) return [];
        return parts;
    }

    private static bool LooksLikeCommaItem(string value)
    {
        value = value.Trim(" \t-•".ToCharArray());
        var normalized = NormalizeTitle(value);
        if (!IsMeaningfulTitle(value) || UnicodeProperties.Length(value) > 250 || normalized.Length <= 1) return false;
        if (CommaContinuations.Contains(normalized.Split(' ')[0])) return false;
        var letter = value.EnumerateRunes().FirstOrDefault(UnicodeProperties.IsLetter);
        return letter.Value == 0 || !UnicodeProperties.IsLatin(letter) || !UnicodeProperties.IsLowerLatin(letter);
    }

    private static bool StrongItem(string part)
    {
        if (NormalizeTitle(part).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2) return true;
        var letters = part.EnumerateRunes().Where(UnicodeProperties.IsLetter).ToArray();
        return letters.Length > 0 && !letters.All(UnicodeProperties.IsLatin);
    }

    private static IReadOnlyList<string> SplitTopLevel(string value, string[] separators)
    {
        if (value.Length == 0) return [];
        var same = "~～〜".Where(delimiter => value.Count(c => c == delimiter) >= 2).ToHashSet();
        var stack = new Stack<char>();
        char? sameOpen = null;
        var quote = false;
        var output = new List<string>();
        var buffer = new StringBuilder();
        for (var i = 0; i < value.Length;)
        {
            var c = value[i];
            if (c == '"') { quote = !quote; buffer.Append(c); i++; continue; }
            if (!quote)
            {
                if (same.Contains(c))
                {
                    if (sameOpen == c) sameOpen = null;
                    else if (sameOpen is null) sameOpen = c;
                    buffer.Append(c); i++; continue;
                }
                if (sameOpen is null)
                {
                    if (PairDelimiters.TryGetValue(c, out var end)) { stack.Push(end); buffer.Append(c); i++; continue; }
                    if (stack.Count > 0 && c == stack.Peek()) { stack.Pop(); buffer.Append(c); i++; continue; }
                    if (stack.Count == 0)
                    {
                        var separator = separators.FirstOrDefault(separator => value.AsSpan(i).StartsWith(separator, StringComparison.Ordinal));
                        if (separator is not null)
                        {
                            var piece = buffer.ToString().Trim(" -•\t".ToCharArray());
                            if (piece.Length > 0) output.Add(piece);
                            buffer.Clear(); i += separator.Length; continue;
                        }
                    }
                }
            }
            buffer.Append(c); i++;
        }
        var final = buffer.ToString().Trim(" -•\t".ToCharArray());
        if (final.Length > 0) output.Add(final);
        return output;
    }
}
