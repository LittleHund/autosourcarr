namespace AutoSourcarr.Core.Unicode;

/// <summary>Python's ordinal Unicode code-point ordering, including retained lone surrogate values.</summary>
public sealed class UnicodeCodePointComparer : IComparer<string>
{
    public static UnicodeCodePointComparer Instance { get; } = new();
    private UnicodeCodePointComparer() { }
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1; if (right is null) return 1;
        var l = 0; var r = 0;
        while (l < left.Length && r < right.Length)
        {
            var a = Next(left, ref l); var b = Next(right, ref r); if (a != b) return a.CompareTo(b);
        }
        return l < left.Length ? 1 : r < right.Length ? -1 : 0;
    }
    private static int Next(string value, ref int index)
    {
        var first = value[index++];
        return char.IsHighSurrogate(first) && index < value.Length && char.IsLowSurrogate(value[index]) ? char.ConvertToUtf32(first, value[index++]) : first;
    }
}
