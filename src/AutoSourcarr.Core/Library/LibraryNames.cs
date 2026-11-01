using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// File and folder names of the CBZ library (CHAP-7): <c>&lt;Library&gt;/&lt;Series&gt;/&lt;Chapter&gt;.cbz</c>.
/// Names are safe on Windows and Linux (no reserved characters, device names, trailing dots or spaces, traversal),
/// NFC-normalised and capped in UTF-8 bytes; a capped name keeps a short hash so two long titles stay distinct.
/// Chapter files follow Kavita's manga markers (<c>Vol.01 Ch.012.5</c>), which Mihon's chapter recognition and
/// Komga's natural sort read the same way. The series title is only put in front of the markers when it holds no
/// digit, so a title like "Kaiju No. 8" can never be parsed as a volume or chapter number by a reader server.
/// </summary>
public static class LibraryNames
{
    /// <summary>Byte cap of a series folder (ext4 and NTFS allow 255; leave room for sync tools and suffixes).</summary>
    public const int SeriesFolderBytes = 150;
    /// <summary>Byte cap of a chapter file name including ".cbz".</summary>
    public const int ChapterFileBytes = 200;

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>One safe path component: never empty, never "." or "..", no separators, at most <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    public static string Component(string? value, int maxBytes = SeriesFolderBytes, string fallback = "Untitled")
    {
        if (maxBytes < 16) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var text = (value ?? "").Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var replaced = Rune.IsControl(rune) || rune.Value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                ? new Rune('_') : rune;
            if (Rune.IsWhiteSpace(replaced))
            {
                if (!space && builder.Length > 0) builder.Append(' ');
                space = true;
                continue;
            }
            space = false;
            builder.Append(replaced.ToString());
        }
        var clean = Trim(builder.ToString());
        if (clean.Length == 0 || clean.All(character => character is '.' or '_')) clean = Trim(fallback);
        if (clean.Length == 0) clean = "_";
        var stem = clean.Split('.')[0].TrimEnd();
        if (DeviceNames.Contains(stem)) clean = "_" + clean;
        return Cap(clean, maxBytes);
    }

    /// <summary>The series folder of a work.</summary>
    public static string SeriesFolder(string seriesName) => Component(seriesName, SeriesFolderBytes);

    /// <summary>Invariant chapter number with at least three integer digits ("001", "012.5", "1000").</summary>
    public static string ChapterNumber(double number) => number.ToString("000.#####", CultureInfo.InvariantCulture);

    /// <summary>Invariant volume number with at least two integer digits ("01", "07.5").</summary>
    public static string VolumeNumber(double volume) => volume.ToString("00.#####", CultureInfo.InvariantCulture);

    /// <summary>A ComicInfo / reader number without padding ("1", "12.5").</summary>
    public static string PlainNumber(double number) => number.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>Whether a chapter or volume number is known (the catalogue stores -1 for unknown).</summary>
    public static bool Known(double number) => number >= 0 && double.IsFinite(number);

    /// <summary>
    /// The chapter file name: "[Series ]Vol.VV Ch.NNN.cbz" (volume only when known), or for a chapter without number
    /// "[Series ]&lt;title&gt;.cbz" (ComicInfo marks it a special). <paramref name="collision"/> adds " [n]" for a second
    /// chapter with the same name.
    /// </summary>
    public static string ChapterFile(string seriesFolder, double number, double volume, string title, long collision = 0)
    {
        ArgumentNullException.ThrowIfNull(seriesFolder);
        var prefix = SafePrefix(seriesFolder) ? seriesFolder + " " : "";
        string marker;
        if (Known(number))
            marker = (Known(volume) ? "Vol." + VolumeNumber(volume) + " " : "") + "Ch." + ChapterNumber(number);
        else
        {
            // No number: no digits may reach the name, or Kavita reads one as a chapter.
            var words = new string(Component(title, 120, "Special").Select(character => char.IsAsciiDigit(character) ? '_' : character).ToArray());
            marker = Component(words, 120, "Special");
        }
        var suffix = collision > 0 ? " [" + collision.ToString(CultureInfo.InvariantCulture) + "]" : "";
        const string extension = ".cbz";
        var budget = ChapterFileBytes - Encoding.UTF8.GetByteCount(marker + suffix + extension);
        if (prefix.Length > 0 && Encoding.UTF8.GetByteCount(prefix) > budget) prefix = "";
        return Component(prefix + marker + suffix, ChapterFileBytes - extension.Length) + extension;
    }

    /// <summary>A title can lead the chapter name only without digits (Kavita reads any digit run as a number marker).</summary>
    public static bool SafePrefix(string seriesFolder) => seriesFolder.Length > 0 && !seriesFolder.Any(char.IsDigit);

    /// <summary>Joins the library root and relative parts, refusing anything that leaves the root.</summary>
    public static string Inside(string root, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var full = Path.GetFullPath(root);
        var target = full;
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(['/', '\\', '\0']) >= 0 || Path.IsPathRooted(part))
                throw new ArgumentException("Unsafe library path component", nameof(parts));
            target = Path.Combine(target, part);
        }
        target = Path.GetFullPath(target);
        var boundary = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        if (!target.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Library path leaves the library folder", nameof(parts));
        return target;
    }

    private static string Trim(string value) => value.Trim(' ').TrimEnd('.', ' ');

    private static string Cap(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        var tail = " ~" + hash;
        var budget = maxBytes - tail.Length;
        var kept = new StringBuilder();
        var used = 0;
        // Cut at text elements so a combining sequence or emoji is never split.
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (used + size > budget) break;
            kept.Append(element); used += size;
        }
        var head = Trim(kept.ToString());
        return (head.Length == 0 ? "_" : head) + tail;
    }
}
