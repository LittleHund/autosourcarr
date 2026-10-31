using System.Text;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Settings;

/// <summary>Dotenv values without executable expansion; canonical files disable interpolation.</summary>
public static partial class DotEnv
{
    public static IReadOnlyDictionary<string, string?> Parse(string text, bool interpolate = false,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var position = 0;
        while (position < text.Length)
        {
            SkipSpace(text, ref position);
            if (position >= text.Length)
                break;
            if (text[position] == '#')
            {
                NextLine(text, ref position);
                continue;
            }
            if (text.AsSpan(position).StartsWith("export", StringComparison.Ordinal)
                && position + 6 < text.Length && char.IsWhiteSpace(text[position + 6]))
            {
                position += 6;
                SkipInlineSpace(text, ref position);
            }
            var key = ReadKey(text, ref position);
            if (key is null)
            {
                NextLine(text, ref position);
                continue;
            }
            SkipInlineSpace(text, ref position);
            string? value = null;
            var valid = true;
            if (position < text.Length && text[position] == '=')
            {
                position++;
                SkipInlineSpace(text, ref position);
                if (position < text.Length && text[position] is '\'' or '"')
                {
                    var beforeQuotedValue = position;
                    (value, valid) = Quoted(text, ref position);
                    if (!valid)
                        position = beforeQuotedValue;
                }
                else if (position < text.Length && text[position] == '#')
                    value = "";
                else
                {
                    var start = position;
                    while (position < text.Length && text[position] is not ('\r' or '\n'))
                        position++;
                    value = InlineComment().Replace(text[start..position], "").Trim();
                }
            }
            SkipInlineSpace(text, ref position);
            if (position < text.Length && text[position] is not ('\r' or '\n' or '#'))
                valid = false;
            if (valid)
            {
                if (interpolate && value is not null)
                    value = Variable().Replace(value, match =>
                    {
                        var name = match.Groups[1].Value;
                        if (values.TryGetValue(name, out var previous))
                            return previous ?? "";
                        if (environment?.TryGetValue(name, out var supplied) == true)
                            return supplied ?? "";
                        return match.Groups[2].Success ? match.Groups[2].Value : "";
                    });
                values[key] = value;
            }
            NextLine(text, ref position);
        }
        return values;
    }

    private static string? ReadKey(string text, ref int position)
    {
        if (text[position] == '\'')
        {
            var start = ++position;
            while (position < text.Length && text[position] is not ('\'' or '\r' or '\n'))
                position++;
            if (position >= text.Length || text[position] != '\'')
                return null;
            var key = text[start..position];
            position++;
            return key;
        }
        var begin = position;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('=' or '#'))
            position++;
        return position == begin ? null : text[begin..position];
    }

    private static (string Value, bool Valid) Quoted(string text, ref int position)
    {
        var quote = text[position++];
        var value = new StringBuilder();
        while (position < text.Length)
        {
            var c = text[position++];
            if (c == quote)
                return (value.ToString(), true);
            if (c != '\\' || position >= text.Length)
            {
                value.Append(c);
                continue;
            }
            var next = text[position++];
            if (next == quote || next == '\\')
                value.Append(next);
            else if (quote == '"' && "abfnrtv'".Contains(next, StringComparison.Ordinal))
                value.Append(next switch { 'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => next });
            else
                value.Append('\\').Append(next);
        }
        return (value.ToString(), false);
    }

    private static void SkipSpace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }

    private static void SkipInlineSpace(string text, ref int position)
    {
        while (position < text.Length && text[position] is not ('\r' or '\n') && char.IsWhiteSpace(text[position]))
            position++;
    }

    private static void NextLine(string text, ref int position)
    {
        while (position < text.Length && text[position] is not ('\r' or '\n'))
            position++;
        if (position < text.Length && text[position] == '\r')
            position++;
        if (position < text.Length && text[position] == '\n')
            position++;
    }

    [GeneratedRegex(@"\s+#.*$", RegexOptions.CultureInvariant)]
    private static partial Regex InlineComment();
    [GeneratedRegex(@"\$\{([^}:]*)(?::-([^}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();
}
