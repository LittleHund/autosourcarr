namespace AutoSourcarr.Core.Text;

/// <summary>Retained Unicode15 text boundaries, independent of the host culture/runtime tables.</summary>
public static class UnicodeText
{
    public static string Trim(string value)
    {
        var start = 0; var end = value.Length;
        while (start < end && Whitespace(value[start])) start++;
        while (end > start && Whitespace(value[end - 1])) end--;
        return start == 0 && end == value.Length ? value : value[start..end];
    }
    private static bool Whitespace(char value) => value is >= '\u0009' and <= '\u000d' or >= '\u001c' and <= '\u0020'
        or '\u0085' or '\u00a0' or '\u1680' or >= '\u2000' and <= '\u200a' or '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000';
}
