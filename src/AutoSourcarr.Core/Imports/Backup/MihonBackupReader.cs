using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using ProtoBackup = AutoSourcarr.Core.Imports.Backup.Generated.Backup;
using ProtoManga = AutoSourcarr.Core.Imports.Backup.Generated.BackupManga;

namespace AutoSourcarr.Core.Imports.Backup;

/// <summary>A backup that is not a readable Mihon/Tachiyomi library backup (the message is shown to the person).</summary>
public sealed class BackupDecodeException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>One library manga of a backup, in the shape 0.4.x kept in import_items.raw_json.</summary>
public sealed record BackupMangaItem
{
    public long Source { get; init; }
    public string SourceName { get; init; } = "";
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Genre { get; init; } = [];
    public int Status { get; init; }
    public string? ThumbnailUrl { get; init; }
    public long DateAdded { get; init; }
    /// <summary>In the library (Mihon's default when the field is absent).</summary>
    public bool Favorite { get; init; } = true;
    public long Version { get; init; }
    public string Notes { get; init; } = "";
    public bool Initialized { get; init; }
    public int Chapters { get; init; }
    public int ReadChapters { get; init; }
    public int Tracking { get; init; }
    public IReadOnlyList<string> ExcludedScanlators { get; init; } = [];

    /// <summary>The 0.4.x decoded-backup row (decode_backup_bytes): the keys the import pipeline and old rows use.</summary>
    public JsonObject ToJson() => new()
    {
        ["source"] = Source, ["url"] = Url, ["title"] = Title, ["artist"] = Artist, ["author"] = Author, ["description"] = Description,
        ["genre"] = new JsonArray(Genre.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["status"] = Status, ["thumbnailUrl"] = ThumbnailUrl, ["dateAdded"] = DateAdded, ["favorite"] = Favorite,
        ["version"] = Version, ["notes"] = Notes, ["initialized"] = Initialized, ["sourceName"] = SourceName,
    };
}

/// <summary>A backup source name (BackupSource, or Tachiyomi's legacy BrokenBackupSource).</summary>
public sealed record BackupSourceName(long SourceId, string Name);

/// <summary>A decoded backup: the library manga, the source names and the detected container and version.</summary>
/// <param name="Format">Always "mihon-tachiyomi-protobuf" (the only backup family Mihon restores).</param>
/// <param name="Compression">"gzip" (.tachibk / .proto.gz) or "none" (a raw protobuf body).</param>
/// <param name="Version">The oldest release family whose fields the backup uses (see <see cref="MihonBackupReader.Versions"/>).</param>
public sealed record BackupLibrary(string Format, string Compression, string Version, IReadOnlyList<BackupMangaItem> Manga,
    IReadOnlyList<BackupSourceName> Sources, int Categories, int ExtensionRepos)
{
    public int FavoriteCount => Manga.Count(item => item.Favorite);

    /// <summary>The 0.4.x readable-backup document (decode_backup_bytes) plus compression and version.</summary>
    public JsonObject ToJson() => new()
    {
        ["format"] = Format, ["compression"] = Compression, ["version"] = Version,
        ["manga_count"] = Manga.Count, ["favorite_count"] = FavoriteCount,
        ["backup_sources"] = new JsonArray(Sources.Select(source => (JsonNode)new JsonObject { ["name"] = source.Name, ["sourceId"] = source.SourceId }).ToArray()),
        ["manga"] = new JsonArray(Manga.Select(item => (JsonNode)item.ToJson()).ToArray()),
    };
}

/// <summary>
/// Reads Mihon / Tachiyomi library backups through the generated parser of the hand-maintained MihonBackup.proto
/// (PIPE-9): gzip or raw protobuf, bounded like CheckedZipArchive (128 MiB upload, 512 MiB decoded, 100 000 manga),
/// unknown and fork fields ignored, Tachiyomi's field-0 source list read by hand. Replaces backup_import.py.
/// </summary>
public static class MihonBackupReader
{
    public const string Format = "mihon-tachiyomi-protobuf";
    /// <summary>Largest accepted upload (compressed or not); a larger file is refused before it is decoded.</summary>
    public const long MaxBackupBytes = 64L * 1024 * 1024;
    /// <summary>Largest decoded protobuf: gzip output is streamed into one buffer and stopped at this size.</summary>
    public const long MaxDecodedBytes = 64L * 1024 * 1024;
    public const int MaxRecords = 100_000;
    /// <summary>Most chapters one manga may carry (counted before the protobuf is materialized).</summary>
    public const int MaxChaptersPerManga = 20_000;

    /// <summary>Every version family the reader detects, oldest first (the sample backups cover each one).</summary>
    public static readonly IReadOnlyList<string> Versions =
        ["tachiyomi-legacy", "tachiyomi", "mihon-0.16", "mihon-0.17", "mihon-0.19", "mihon-0.20", "mihon-0.21"];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>One decode at a time: a decoded backup and its materialized model are the largest allocation of the app.</summary>
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);

    /// <summary>Reads at most <see cref="MaxBackupBytes"/> + 1 bytes of a stream and decodes them.</summary>
    /// <exception cref="BackupDecodeException">Too large, empty or not a readable backup.</exception>
    public static BackupLibrary Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var buffer = ReadBounded(input, MaxBackupBytes, "Backup file is larger than 64 MiB");
        return Read(Segment(buffer));
    }

    /// <exception cref="BackupDecodeException">Too large, empty or not a readable backup.</exception>
    public static BackupLibrary Read(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Read(new ArraySegment<byte>(payload));
    }

    private static BackupLibrary Read(ArraySegment<byte> payload)
    {
        if (payload.Count == 0) throw new BackupDecodeException("Backup file is empty");
        if (payload.Count > MaxBackupBytes) throw new BackupDecodeException("Backup file is larger than 64 MiB");
        DecodeGate.Wait();
        try { return Decode(payload); }
        finally { DecodeGate.Release(); }
    }

    private static BackupLibrary Decode(ArraySegment<byte> payload)
    {
        var compression = "none";
        MemoryStream? decoded = null;
        try
        {
            var raw = payload;
            if (payload.Count >= 2 && payload[0] == 0x1f && payload[1] == 0x8b)
            {
                compression = "gzip";
                try
                {
                    using var gzip = new GZipStream(new MemoryStream(payload.Array!, payload.Offset, payload.Count, writable: false), CompressionMode.Decompress);
                    decoded = ReadBounded(gzip, MaxDecodedBytes, "Decoded backup exceeds the 64 MiB safety limit");
                }
                catch (Exception error) when (error is InvalidDataException or IOException && error is not BackupDecodeException)
                {
                    throw new BackupDecodeException("Invalid gzip backup: " + error.Message, error);
                }
                // The decoded bytes are parsed in place (no ToArray copy of up to 64 MiB).
                raw = Segment(decoded);
            }
            var span = raw.AsSpan();
            var first = span.IndexOfAnyExcept(" \t\n\r"u8);
            if (first >= 0 && span[first] is (byte)'{' or (byte)'[')
                throw new BackupDecodeException("JSON backups are not supported by current Mihon backup format");

            CountManga(raw);
            ProtoBackup backup;
            try { backup = ProtoBackup.Parser.ParseFrom(raw.Array!, raw.Offset, raw.Count); }
            catch (InvalidProtocolBufferException error) { throw new BackupDecodeException("Backup could not be decoded as a Mihon/Tachiyomi protobuf backup: " + error.Message, error); }
            decoded?.Dispose(); decoded = null;
            if (backup.Manga.Count == 0) throw new BackupDecodeException("No manga entries were found in the backup");

            var sources = backup.Sources.Select(source => new BackupSourceName(source.SourceId, source.Name)).ToList();
            foreach (var legacy in backup.LegacyBrokenSources)
                if (ReadLegacySource(legacy.ToByteArray()) is { } old && sources.All(source => source.SourceId != old.SourceId)) sources.Add(old);
            var names = new Dictionary<long, string>();
            foreach (var source in sources)
                if (source.Name.Trim().Length > 0) names[source.SourceId] = source.Name.Trim();

            var manga = backup.Manga.Select(item => Item(item, names)).ToList();
            return new BackupLibrary(Format, compression, DetectVersion(backup), manga, sources, backup.Categories.Count, backup.ExtensionRepos.Count);
        }
        finally { decoded?.Dispose(); }
    }

    private static ArraySegment<byte> Segment(MemoryStream buffer) =>
        buffer.TryGetBuffer(out var segment) ? segment : new ArraySegment<byte>(buffer.ToArray());

    /// <summary>
    /// The oldest release family whose fields appear (kotlinx omits default values, so a backup without any newer field
    /// reads as the older family): next_update or memo without the sync fields → 0.21; memo or extension-store fields →
    /// 0.20; notes/initialized → 0.19; version or extension repos → 0.17; lastModifiedAt/excludedScanlators → 0.16;
    /// field-0 sources/history → Tachiyomi legacy; otherwise Tachiyomi.
    /// </summary>
    private static string DetectVersion(ProtoBackup backup)
    {
        var manga = backup.Manga;
        var nextUpdate = manga.Any(item => item.HasNextUpdate);
        var memo = manga.Any(item => item.HasMemo || item.Chapters.Any(chapter => chapter.HasMemo));
        var sync = manga.Any(item => item.HasVersion || item.HasLastModifiedAt || item.Chapters.Any(chapter => chapter.HasVersion || chapter.HasLastModifiedAt));
        var stores = backup.ExtensionRepos.Any(repo => repo.HasContactDiscord || repo.HasIsLegacy || repo.HasExtensionListUrl);
        if (nextUpdate || ((memo || stores) && !sync)) return "mihon-0.21";
        if (memo || stores) return "mihon-0.20";
        if (manga.Any(item => item.HasNotes || item.HasInitialized)) return "mihon-0.19";
        if (manga.Any(item => item.HasVersion) || backup.ExtensionRepos.Count > 0) return "mihon-0.17";
        if (manga.Any(item => item.HasLastModifiedAt || item.HasFavoriteModifiedAt || item.ExcludedScanlators.Count > 0)) return "mihon-0.16";
        if (backup.LegacyBrokenSources.Count > 0 || manga.Any(item => item.LegacyBrokenHistory.Count > 0)) return "tachiyomi-legacy";
        return "tachiyomi";
    }

    private static BackupMangaItem Item(ProtoManga item, IReadOnlyDictionary<long, string> names)
    {
        string url;
        try { url = StrictUtf8.GetString(item.Url.ToByteArray()); }
        catch (DecoderFallbackException error) { throw new BackupDecodeException("Backup URL contains invalid UTF-8", error); }
        return new BackupMangaItem
        {
            Source = item.Source,
            SourceName = names.TryGetValue(item.Source, out var name) ? name : "",
            Url = url,
            Title = item.Title,
            Artist = item.HasArtist ? item.Artist : null,
            Author = item.HasAuthor ? item.Author : null,
            Description = item.HasDescription ? item.Description : null,
            Genre = item.Genre.ToArray(),
            Status = item.Status,
            ThumbnailUrl = item.HasThumbnailUrl ? item.ThumbnailUrl : null,
            DateAdded = item.DateAdded,
            Favorite = !item.HasFavorite || item.Favorite,
            Version = item.HasVersion ? item.Version : 0,
            Notes = item.HasNotes ? item.Notes : "",
            Initialized = item.HasInitialized && item.Initialized,
            Chapters = item.Chapters.Count,
            ReadChapters = item.Chapters.Count(chapter => chapter.Read),
            Tracking = item.Tracking.Count,
            ExcludedScanlators = item.ExcludedScanlators.ToArray(),
        };
    }

    /// <summary>
    /// Counts the top-level manga (<see cref="MaxRecords"/>) and each manga's chapters (<see cref="MaxChaptersPerManga"/>)
    /// before anything is materialized, skipping every other field. The scan stops quietly at anything it does not
    /// understand (a group, a truncated field): the protobuf parser then gives the real error or reads the fork's data.
    /// </summary>
    private static void CountManga(ArraySegment<byte> raw)
    {
        var data = raw.AsSpan();
        var position = 0; var count = 0;
        while (position < data.Length)
        {
            if (!TryVarint(data, ref position, out var key)) return;
            var field = key >> 3; var wire = (int)(key & 7);
            if (wire != 2) { if (!SkipScalar(data, ref position, wire)) return; continue; }
            if (!TryVarint(data, ref position, out var size) || size > (ulong)(data.Length - position)) return;
            if (field == 1)
            {
                if (++count > MaxRecords) throw new BackupDecodeException("Backup has too many manga records");
                if (CountChapters(data.Slice(position, (int)size)) > MaxChaptersPerManga)
                    throw new BackupDecodeException("A backup manga has more than " + MaxChaptersPerManga.ToString(System.Globalization.CultureInfo.InvariantCulture) + " chapters");
            }
            position += (int)size;
        }
    }

    /// <summary>The BackupManga.chapters (field 16) entries of one encoded manga.</summary>
    private static int CountChapters(ReadOnlySpan<byte> manga)
    {
        var position = 0; var chapters = 0;
        while (position < manga.Length)
        {
            if (!TryVarint(manga, ref position, out var key)) return chapters;
            var wire = (int)(key & 7);
            if (wire != 2) { if (!SkipScalar(manga, ref position, wire)) return chapters; continue; }
            if (!TryVarint(manga, ref position, out var size) || size > (ulong)(manga.Length - position)) return chapters;
            if (key >> 3 == 16 && ++chapters > MaxChaptersPerManga) return chapters;
            position += (int)size;
        }
        return chapters;
    }

    private static bool SkipScalar(ReadOnlySpan<byte> data, ref int position, int wire)
    {
        switch (wire)
        {
            case 0: return TryVarint(data, ref position, out _);
            case 1: position += 8; return position <= data.Length;
            case 5: position += 4; return position <= data.Length;
            default: return false;
        }
    }

    /// <summary>Tachiyomi BrokenBackupSource { 0: name (bytes), 1: sourceId (varint) }; null when unreadable.</summary>
    private static BackupSourceName? ReadLegacySource(byte[] data)
    {
        var position = 0; var name = ""; long id = 0;
        while (position < data.Length)
        {
            if (!TryVarint(data, ref position, out var key)) return null;
            var field = key >> 3; var wire = (int)(key & 7);
            if (wire == 0)
            {
                if (!TryVarint(data, ref position, out var value)) return null;
                if (field == 1) id = unchecked((long)value);
            }
            else if (wire == 2)
            {
                if (!TryVarint(data, ref position, out var size) || size > (ulong)(data.Length - position)) return null;
                if (field == 0) name = Encoding.UTF8.GetString(data, position, (int)size);
                position += (int)size;
            }
            else if (wire == 1) position += 8;
            else if (wire == 5) position += 4;
            else return null;
            if (position > data.Length) return null;
        }
        return name.Length > 0 || id != 0 ? new BackupSourceName(id, name) : null;
    }

    private static bool TryVarint(ReadOnlySpan<byte> data, ref int position, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && position < data.Length; shift += 7)
        {
            var current = data[position++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return true;
        }
        return false;
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes into one growing buffer (the caller parses it in place).</summary>
    private static MemoryStream ReadBounded(Stream input, long limit, string tooLarge)
    {
        var buffer = new MemoryStream();
        try
        {
            var chunk = new byte[81920];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > limit) throw new BackupDecodeException(tooLarge);
                buffer.Write(chunk, 0, read);
            }
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Human label of a version family ("Mihon 0.19", "Tachiyomi (legacy)").</summary>
    public static string Label(string version) => version switch
    {
        "tachiyomi-legacy" => "Tachiyomi (legacy)",
        "tachiyomi" => "Tachiyomi",
        _ when version.StartsWith("mihon-", StringComparison.Ordinal) => "Mihon " + version["mihon-".Length..] + (version == "mihon-0.21" ? "+" : ""),
        _ => version,
    };
}
