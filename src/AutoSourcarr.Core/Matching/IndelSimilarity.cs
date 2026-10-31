using System.Buffers;
using System.Numerics;

namespace AutoSourcarr.Core.Matching;

/// <summary>Normalized insertion/deletion similarity used by the released matcher.</summary>
internal static class IndelSimilarity
{
    public static double Ratio(string left, string right)
    {
        if (left == right) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left.Length > right.Length) (left, right) = (right, left);
        var words = (left.Length + 63) / 64;
        var masks = ArrayPool<ulong>.Shared.Rent(128 * words);
        var state = ArrayPool<ulong>.Shared.Rent(words);
        Array.Clear(masks, 0, 128 * words);
        Array.Clear(state, 0, words);
        try
        {
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] > 127)
                    throw new ArgumentException("Indel input must be normalized ASCII.");
                masks[left[i] * words + i / 64] |= 1UL << (i % 64);
            }
            foreach (var character in right)
            {
                if (character > 127)
                    throw new ArgumentException("Indel input must be normalized ASCII.");
                ulong shifted = 1;
                ulong borrow = 0;
                for (var word = 0; word < words; word++)
                {
                    var old = state[word];
                    var x = old | masks[character * words + word];
                    var y = (old << 1) | shifted;
                    shifted = old >> 63;
                    var difference = unchecked(x - y);
                    var nextBorrow = x < y;
                    var subtracted = unchecked(difference - borrow);
                    nextBorrow |= difference < borrow;
                    state[word] = x & ~subtracted;
                    borrow = nextBorrow ? 1UL : 0UL;
                }
            }
            var common = 0;
            for (var word = 0; word < words; word++)
                common += BitOperations.PopCount(state[word]);
            var sum = left.Length + right.Length;
            // Preserve the percent -> fraction round trip of rapidfuzz.fuzz.ratio.
            return (100.0 * (1.0 - (sum - 2 * common) / (double)sum)) / 100.0;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(masks, clearArray: true);
            ArrayPool<ulong>.Shared.Return(state, clearArray: true);
        }
    }

    public static double TokenSet(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (a.Count == 0 || b.Count == 0) return 0;
        var common = a.Intersect(b, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extraA = a.Except(b, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extraB = b.Except(a, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var shared = string.Join(' ', common);
        var combinedA = string.Join(' ', common.Concat(extraA));
        var combinedB = string.Join(' ', common.Concat(extraB));
        if (common.Length == 0) return Ratio(combinedA, combinedB);
        return Math.Max(Ratio(combinedA, combinedB), Math.Max(Ratio(shared, combinedA), Ratio(shared, combinedB)));
    }
}
