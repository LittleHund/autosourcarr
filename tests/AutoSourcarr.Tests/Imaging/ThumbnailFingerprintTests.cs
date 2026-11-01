using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Imaging;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Tests.Matching;

namespace AutoSourcarr.Tests.Imaging;

/// <summary>Phase 10 imaging: exact thumbnail fingerprints, the import tie-break, cover hints and cover recovery.</summary>
public static class ThumbnailFingerprintTests
{
    /// <summary>A solid RGB PNG written by hand (no image library), at the given zlib level.</summary>
    internal static byte[] Png(int width, int height, byte red, byte green, byte blue, CompressionLevel level = CompressionLevel.Optimal)
    {
        var raw = new byte[height * (width * 3 + 1)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = y * (width * 3 + 1) + 1 + x * 3;
                raw[at] = red; raw[at + 1] = green; raw[at + 2] = blue;
            }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, level, leaveOpen: true)) zlib.Write(raw);
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length); output.Write(length);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); output.Write(body);
        var crc = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(body)); output.Write(crc);
    }

    private static uint Crc(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    internal sealed class FakeThumbnails : IThumbnailSource
    {
        public Dictionary<string, byte[]> Urls { get; } = new(StringComparer.Ordinal);
        public Dictionary<long, byte[]> Manga { get; } = [];
        public int Calls;
        public Task<(byte[] Bytes, string ContentType)> FetchUrlAsync(string url, string referer, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Urls.TryGetValue(url, out var bytes) ? Task.FromResult((bytes, "image/png")) : throw new HttpRequestException("404");
        }
        public Task<(byte[] Bytes, string ContentType)> FetchMangaThumbnailAsync(long mangaId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Manga.TryGetValue(mangaId, out var bytes) ? Task.FromResult((bytes, "image/jpeg")) : throw new HttpRequestException("404");
        }
    }

    /// <summary>Fake decoder: the "image" bytes are the fingerprint text.</summary>
    private static ThumbnailFingerprint Fake(byte[] bytes) => new(Encoding.UTF8.GetString(bytes), 200, 300);
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static void Thumbnail(SqliteDatabase database, long entry, string source, long manga, string url) =>
        database.Write(DatabaseFile.Catalog, s => s.Execute("UPDATE source_matches SET thumbnail_url=$url WHERE entry_id=$e AND source_id=$s AND source_manga_id=$m",
            ("$url", url), ("$e", entry), ("$s", source), ("$m", manga)));

    public static void Register(TestSuite suite)
    {
        suite.Add("imaging: exact thumbnail fingerprints ignore encoding, see pixels and refuse icons", () =>
        {
            var first = ExactThumbnail.Compute(Png(120, 100, 200, 10, 10, CompressionLevel.Fastest));
            var second = ExactThumbnail.Compute(Png(120, 100, 200, 10, 10, CompressionLevel.SmallestSize));
            var other = ExactThumbnail.Compute(Png(120, 100, 200, 10, 11));
            Check.Equal(first.Fingerprint, second.Fingerprint);
            Check.True(first.Fingerprint != other.Fingerprint);
            Check.Equal(120, first.Width); Check.Equal(100, first.Height); Check.Equal(64, first.Fingerprint.Length);
            Check.Throws<InvalidDataException>(() => ExactThumbnail.Compute(Png(95, 200, 1, 2, 3)));
            Check.Throws<InvalidDataException>(() => ExactThumbnail.Compute([]));
        });

        suite.Add("imaging: fingerprints are cached per URL and record, expire, and stay off when disabled", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            MatchingFixtures.Source(database, "1", "Alpha", "en");
            var entry = MatchingFixtures.Entry(database, "Witch Hat Atelier");
            MatchingFixtures.Record(database, entry, "1", 11, "Witch Hat Atelier");
            Thumbnail(database, entry, "1", 11, "https://a.test/11.jpg");
            var clock = new ManualClock(DateTimeOffset.Parse("2026-11-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var settings = new AutoSourcarrSettings { DataDir = temp.Path, ThumbnailFingerprintTtlSeconds = 3600 };
            var source = new FakeThumbnails(); source.Urls["https://a.test/11.jpg"] = Bytes("cover-a");
            var prints = new ThumbnailFingerprints(database, () => settings, source, clock, Fake);
            var found = prints.EntryFingerprintsAsync(entry).GetAwaiter().GetResult();
            Check.True(found.SetEquals(["cover-a"]));
            Check.Equal(1, source.Calls);
            _ = prints.EntryFingerprintsAsync(entry).GetAwaiter().GetResult();
            Check.Equal(1, source.Calls); // record and URL rows are cached
            Check.True(prints.EntryCachedFingerprints(entry).SetEquals(["cover-a"]));
            Check.True(prints.CacheGet(ThumbnailFingerprints.SourceKey("1", 11)) is not null);
            clock.Advance(TimeSpan.FromHours(2));
            Check.True(prints.CacheGet(ThumbnailFingerprints.SourceKey("1", 11)) is null);
            // Without a usable cover URL the source host's thumbnail of the manga is used.
            source.Manga[12] = Bytes("cover-b");
            Check.Equal("cover-b", prints.ForSourceAsync("1", 12, "https://missing.test/x.jpg").GetAwaiter().GetResult()!.Fingerprint);
            var off = new ThumbnailFingerprints(database, () => settings with { ExactThumbnailMatching = false }, source, clock, Fake);
            Check.True(off.ForUrlAsync("https://a.test/11.jpg").GetAwaiter().GetResult() is null);
            Check.Equal(0, off.HydrateAsync(10).GetAwaiter().GetResult());
            Check.Equal(1, prints.HydrateAsync(10).GetAwaiter().GetResult());
        });

        suite.Add("imaging: the import tie-break picks only candidates with the exact cover", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            MatchingFixtures.Source(database, "1", "Alpha", "en");
            var left = MatchingFixtures.Entry(database, "Blue Box");
            var right = MatchingFixtures.Entry(database, "Blue Box");
            MatchingFixtures.Record(database, left, "1", 21, "Blue Box"); Thumbnail(database, left, "1", 21, "https://a.test/21.jpg");
            MatchingFixtures.Record(database, right, "1", 22, "Blue Box"); Thumbnail(database, right, "1", 22, "https://a.test/22.jpg");
            var source = new FakeThumbnails();
            source.Urls["https://a.test/21.jpg"] = Bytes("same"); source.Urls["https://a.test/22.jpg"] = Bytes("other");
            source.Urls["https://backup.test/cover.jpg"] = Bytes("same"); source.Urls["https://meta.test/c.jpg"] = Bytes("other-meta");
            var prints = new ThumbnailFingerprints(database, () => new AutoSourcarrSettings { DataDir = temp.Path }, source, compute: Fake);
            var item = new JsonObject { ["title"] = "Blue Box", ["thumbnailUrl"] = "https://backup.test/cover.jpg" };
            var bundle = new JsonObject { ["records"] = new JsonArray(new JsonObject { ["candidate"] = new JsonObject { ["thumbnail_url"] = "https://meta.test/c.jpg" } }) };
            Check.True(prints.ImportFingerprintsAsync(item, bundle).GetAwaiter().GetResult().SetEquals(["same", "other-meta"]));
            var chosen = prints.ImportExactEntryIdsAsync(item, bundle, [left, right]).GetAwaiter().GetResult();
            Check.Equal(1, chosen.Count); Check.Equal(left, chosen[0]);
            Check.True(prints.CandidateExactMatchAsync(left, "1", new JsonObject { ["id"] = 99, ["thumbnailUrl"] = "https://backup.test/cover.jpg" }).GetAwaiter().GetResult());
            Check.True(!prints.CandidateExactMatchAsync(right, "1", new JsonObject { ["id"] = 98, ["thumbnailUrl"] = "https://backup.test/cover.jpg" }).GetAwaiter().GetResult());
            Check.Equal(0, prints.ImportExactEntryIdsAsync(new JsonObject { ["title"] = "x" }, null, [left]).GetAwaiter().GetResult().Count);
        });

        suite.Add("imaging: cover hints fail over across records, refresh keys and recovery cooldown", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            MatchingFixtures.Source(database, "1", "Alpha", "en"); MatchingFixtures.Source(database, "2", "Beta", "en");
            database.Write(DatabaseFile.Catalog, s => s.Execute("UPDATE source_config SET priority=CASE source_id WHEN '1' THEN 1 ELSE 2 END,base_url='https://' || source_id || '.test'"));
            var entry = MatchingFixtures.Entry(database, "Dandadan");
            MatchingFixtures.Record(database, entry, "1", 31, "Dandadan");
            MatchingFixtures.Record(database, entry, "2", 32, "Dandadan"); Thumbnail(database, entry, "2", 32, "https://2.test/32.jpg");
            var scheduled = new List<(string, long)>();
            var now = 1000.0;
            var hints = new CoverHints(database, () => new AutoSourcarrSettings { DataDir = temp.Path }, (source, manga) => { scheduled.Add((source, manga)); return true; }, () => now);
            var list = hints.EntryHints(entry);
            Check.Equal(2, list.Count);
            Check.Equal(32L, list[0].SourceMangaId); // the record with a thumbnail first
            Check.Equal("https://2.test", list[0].BaseUrl);
            var plan = hints.RefreshPlan(entry);
            Check.True(plan.Keys.SequenceEqual(["entry-" + entry, "source-31", "source-32"]));
            Check.Throws<KeyNotFoundException>(() => hints.RefreshPlan(9999));
            Check.True(hints.ScheduleRecovery(entry));
            Check.Equal(("1", 31L), scheduled[0]); // the record still missing its thumbnail
            Check.True(!hints.ScheduleRecovery(entry));
            now += 61;
            Check.True(hints.ScheduleRecovery(entry));
            hints.Remember("2", new JsonObject { ["id"] = 77, ["thumbnailUrl"] = "https://2.test/77.jpg" });
            Check.Equal("https://2.test/77.jpg", hints.SourceHint(77)!.ThumbnailUrl);
            Check.Equal("1", hints.SourceHint(31)!.SourceId);
            Check.True(hints.SourceHint(404) is null);
        });
    }

    internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
