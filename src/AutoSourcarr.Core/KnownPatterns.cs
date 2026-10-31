using System.Text.RegularExpressions;

namespace AutoSourcarr.Core;

/// <summary>
/// Source-generated regular expressions for the fixed patterns used across the core
/// (identifiers, hashes, whitespace). Generated at build time: no runtime parsing or compilation.
/// </summary>
public static partial class KnownPatterns
{
    /// <summary>Lowercase hexadecimal, exactly 12 characters (support log ids).</summary>
    [GeneratedRegex(@"\A[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Hex12();

    /// <summary>Lowercase hexadecimal, exactly 32 characters (job and task ids).</summary>
    [GeneratedRegex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Hex32();

    /// <summary>Lowercase hexadecimal, exactly 40 characters (git commit ids).</summary>
    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Hex40();

    /// <summary>Lowercase hexadecimal, exactly 64 characters (SHA-256).</summary>
    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Hex64();

    /// <summary>A Tachiyomi/Mihon source id: optional minus and up to 20 digits.</summary>
    [GeneratedRegex(@"\A-?[0-9]{1,20}\z", RegexOptions.CultureInvariant)]
    public static partial Regex SourceId();

    /// <summary>One or more whitespace characters.</summary>
    [GeneratedRegex(@"\s+")]
    public static partial Regex Whitespace();
}
