using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching;

namespace AutoSourcarr.Core.Catalogue.Chapters;

public sealed record NativeChapter(long Id, double Number, string Name, string Url = "", long SourceOrder = 0,
    double Volume = -1, long UploadDate = 0, string Scanlator = "", long PageCount = 0)
{
    public static NativeChapter FromJson(JsonObject value) => new(Integer(value["id"]), ChapterNumbers.Float(value["chapterNumber"]),
        Text(value["name"]), Text(value["url"]), Integer(value["sourceOrder"], 0), ChapterNumbers.Float(value["volumeNumber"]),
        Integer(value["uploadDate"], 0), Text(value["scanlator"]), Integer(value["pageCount"], 0));
    private static string Text(JsonNode? value) => value is null ? "" : value.GetValue<string>();
    private static long Integer(JsonNode? value, long? fallback = null) => value is null && fallback.HasValue ? fallback.Value
        : value is JsonValue scalar && scalar.TryGetValue<long>(out var number) ? number : throw new InvalidDataException("Native chapter identity/count must be an exact integer");
}

/// <summary>A complete native source-record refresh, never an independently deactivating page of one record.</summary>
public sealed class ChapterBatch(long entryId, string sourceId, long mangaId, IReadOnlyList<NativeChapter> chapters)
{
    public long EntryId { get; } = entryId;
    public string SourceId { get; } = sourceId;
    public long MangaId { get; } = mangaId;
    public IReadOnlyList<NativeChapter> Chapters { get; } = chapters.ToArray();
}

public sealed record ChapterWriteResult(IReadOnlyList<long> TouchedChapters, IReadOnlyList<long> InvalidatedCandidates);

public static class ChapterNumbers
{
    private static readonly Regex Number = new(@"(?:chapter|ch\.?|episode|ep\.?)?\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static double Float(JsonNode? value, double fallback = -1)
    {
        if (value is null) return fallback;
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
        var text = Digits(value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>().Trim() : value.ToJsonString());
        for (var i = 0; i < text.Length; i++) if (text[i] == '_' && (i == 0 || i + 1 == text.Length || text[i - 1] is < '0' or > '9' || text[i + 1] is < '0' or > '9')) return fallback;
        text = text.Replace("_", "", StringComparison.Ordinal);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    }
    public static string Format(double number) => number < 0 ? "" : number.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    public static (string Key, double Number) Key(double number, string name)
    {
        if (!double.IsFinite(number)) throw new InvalidDataException("Native chapter number is not finite");
        // CHAP-1: an explicit part of the same number files apart (10.1, 10.2); a label without a source number is
        // read from its numbering word (Chapter, Episode, Act, Stage, No., Part, Vol., 第…話 …) before any bare number.
        var resolved = ChapterNumbering.Resolve(number, name);
        if (resolved >= 0) return ("n:" + Format(resolved), resolved);
        var match = Number.Match(RegexText(name));
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 && double.IsFinite(parsed)) return ("n:" + Format(parsed), parsed);
        return ("t:" + TextMatcher.NormalizeTitle(name), -1);
    }
    public static (string Key, double Number) Key(JsonObject chapter) => Key(Float(chapter["chapterNumber"]), chapter["name"]?.GetValue<string>() ?? "");
    internal static string RegexText(string value) => Digits(value).Replace('\x1c', ' ').Replace('\x1d', ' ').Replace('\x1e', ' ').Replace('\x1f', ' ');
    private static string Digits(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber) result.Append((char)('0' + (int)Rune.GetNumericValue(rune)));
            else result.Append(rune.ToString());
        return result.ToString();
    }
}

internal sealed record OldChapter(long Id, long MangaId, long Order, long VirtualId);
internal sealed record FrozenChapter(NativeChapter Chapter, OldChapter? Old);

/// <summary>Frozen record input, read repeatedly from the same owned handle; never from a path reopened after validation.</summary>
internal sealed class FrozenChapterBatch(FileStream file)
{
    public long Count { get; private set; }
    public long Start { get; } = file.Position;
    public long End { get; private set; } = file.Position;
    public void Append(FrozenChapter value)
    {
        file.Position = End;
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        var ch = value.Chapter; writer.Write(ch.Id); writer.Write(ch.Number); writer.Write(ch.Name); writer.Write(ch.Url);
        writer.Write(ch.SourceOrder); writer.Write(ch.Volume); writer.Write(ch.UploadDate); writer.Write(ch.Scanlator); writer.Write(ch.PageCount);
        writer.Write(value.Old is not null);
        if (value.Old is { } old) { writer.Write(old.Id); writer.Write(old.MangaId); writer.Write(old.Order); writer.Write(old.VirtualId); }
        Count = checked(Count + 1); End = file.Position;
    }
    public IEnumerable<FrozenChapter> Read()
    {
        file.Position = Start;
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        for (long i = 0; i < Count; i++)
        {
            var value = new NativeChapter(reader.ReadInt64(), reader.ReadDouble(), reader.ReadString(), reader.ReadString(), reader.ReadInt64(), reader.ReadDouble(), reader.ReadInt64(), reader.ReadString(), reader.ReadInt64());
            var old = reader.ReadBoolean() ? new OldChapter(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64()) : null;
            yield return new(value, old);
        }
        if (file.Position != End) throw new InvalidDataException("Owned chapter input changed during replay");
    }
}
