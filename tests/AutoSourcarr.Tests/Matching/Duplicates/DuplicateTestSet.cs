using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests.Matching;

/// <summary>In-memory decision log for tests.</summary>
internal sealed class MemoryDecisionLog : IDecisionLog
{
    public List<DecisionEvent> Events { get; } = [];
    public long Record(DecisionEvent decision) { lock (Events) { Events.Add(decision); return Events.Count; } }
}

/// <summary>A temporary native catalogue with the real CatalogueStructure primitives and duplicate matching.</summary>
internal sealed class DuplicateFixture : IDisposable
{
    private const string Stamp = "2026-10-21T00:00:00+00:00";
    private readonly Sandbox _sandbox = new();
    private long _manga = 1000;
    public AutoSourcarrSettings Settings { get; }
    public SqliteDatabase Database { get; }
    public object Gate { get; } = new();
    public TaskRegistry Tasks { get; } = new();
    public CatalogueMetadata Metadata { get; } = new(() => .9);
    public CatalogueStructure Structure { get; }
    public MemoryDecisionLog Log { get; } = new();
    public DuplicateMatching Duplicates { get; }

    public DuplicateFixture(IAutomaticMatchGate? gate = null)
    {
        Settings = new AutoSourcarrSettings { DataDir = _sandbox.Path };
        Database = new SqliteDatabase(Settings);
        new SchemaMigrator(Database).Initialize();
        CatalogueExchange.InitializeSchema(Database);
        var gallery = new GalleryEvidenceService(Database, Gate);
        var evidence = new NativeCatalogueEvidence(Metadata, gallery, () => Settings.DuplicateAutoMergeMinSummaryChars, () => Settings.ExactThumbnailMatching);
        var reader = new ReaderRepository(Database, Gate);
        Structure = new CatalogueStructure(Database, Gate, Tasks, gallery, Metadata.Recompute, reader.ReconcileVisibility, DisplayMetadata.Reparent, evidence.Snapshot, _ => { });
        Duplicates = new DuplicateMatching(Database, Gate, Structure, evidence.Snapshot, Log, () => Settings, gate, chooseMetadata: Metadata.ChooseMetadata);
    }

    /// <summary>Ingests one entry with one source record; every title is a native source title of that record.</summary>
    public long Entry(IReadOnlyList<string> titles, IReadOnlyList<string>? authors = null, long? year = null, string? source = null, string matchType = "auto",
        bool locked = false, string description = "")
    {
        if (titles.Count == 0) throw new ArgumentException("An entry needs a title", nameof(titles));
        var author = string.Join(", ", authors ?? []);
        var manga = Interlocked.Increment(ref _manga);
        var sid = source ?? "fixture-" + manga.ToString(CultureInfo.InvariantCulture);
        var metadata = year is { } y ? new JsonObject { ["year"] = y }.ToJsonString() : "{}";
        return Database.Write(DatabaseFile.Catalog, c =>
        {
            c.Execute("INSERT INTO entries(preferred_title,author,description,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p3)", ("$p0", titles[0]), ("$p1", author), ("$p2", description), ("$p3", Stamp));
            var id = Convert.ToInt64(c.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
            c.Execute("INSERT OR IGNORE INTO source_config(source_id,name,last_seen) VALUES($p0,$p1,$p2)", ("$p0", sid), ("$p1", "Fixture " + sid), ("$p2", Stamp));
            c.Execute("""
                INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,match_type,locked,author,description,metadata_json,last_checked)
                VALUES($p0,$p1,$p2,$p3,$p4,1,$p5,$p6,$p7,$p8,$p9,$p10)
                """, ("$p0", id), ("$p1", sid), ("$p2", manga), ("$p3", "/manga/" + manga.ToString(CultureInfo.InvariantCulture)), ("$p4", titles[0]), ("$p5", matchType),
                ("$p6", locked ? 1 : 0), ("$p7", author), ("$p8", description), ("$p9", metadata), ("$p10", Stamp));
            foreach (var title in titles)
            {
                var normalized = TextMatcher.NormalizeTitle(title);
                if (normalized.Length == 0) continue;
                c.Execute("INSERT OR IGNORE INTO titles(entry_id,title,normalized,title_type,source_id,source_manga_id) VALUES($p0,$p1,$p2,'source',$p3,$p4)",
                    ("$p0", id), ("$p1", title), ("$p2", normalized), ("$p3", sid), ("$p4", manga));
            }
            return id;
        });
    }

    /// <summary>Attaches one more automatic record (a variant) to an entry.</summary>
    public NativeSourceRecord Attach(long entryId, string title, string matchType = "auto-additional", bool locked = false)
    {
        var manga = Interlocked.Increment(ref _manga); var sid = "fixture-" + manga.ToString(CultureInfo.InvariantCulture);
        Database.Write(DatabaseFile.Catalog, c =>
        {
            c.Execute("INSERT OR IGNORE INTO source_config(source_id,name,last_seen) VALUES($p0,$p1,$p2)", ("$p0", sid), ("$p1", "Fixture " + sid), ("$p2", Stamp));
            return c.Execute("INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,match_type,locked,last_checked) VALUES($p0,$p1,$p2,$p3,$p4,1,$p5,$p6,$p7)",
                ("$p0", entryId), ("$p1", sid), ("$p2", manga), ("$p3", "/manga/" + manga.ToString(CultureInfo.InvariantCulture)), ("$p4", title), ("$p5", matchType), ("$p6", locked ? 1 : 0), ("$p7", Stamp));
        });
        return new NativeSourceRecord(sid, manga);
    }

    public long Canonical(long id) => Database.Read(DatabaseFile.Catalog, c => DuplicateEvidence.Canonical(c, id));
    public long Scalar(string sql) => Convert.ToInt64(Database.Read(DatabaseFile.Catalog, c => c.Scalar(sql)) ?? 0L, CultureInfo.InvariantCulture);

    public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
}

/// <summary>
/// DONE-WHEN harness for automatic merging: ingests both sides of every pair of a pair file as catalogue entries,
/// runs the automatic duplicate sweep end to end, and checks that no "same": false pair was merged; recall on the
/// "same": true pairs is reported. Pair file format (shared with the phase 8A PairScoringTool):
/// {"pairs":[{"a":{"titles":[...],"authors":[...],"year":2001},"b":{...},"same":true}]}.
/// </summary>
public static class DuplicateTestSet
{
    public const string EnvironmentVariable = "AUTOSOURCARR_DUPLICATE_TEST_SET";
    public static string SyntheticPath => ConfigurationTests.Fixture(Path.Combine("matching", "duplicate-pairs.synthetic.json"));

    public sealed record Result(int Pairs, int SamePairs, int Merged, int MergedSame, IReadOnlyList<string> WrongMerges, IReadOnlyList<string> Missed)
    {
        public double Recall => SamePairs == 0 ? 1 : (double)MergedSame / SamePairs;
    }

    /// <summary>Program.cs hook: <c>--duplicate-test-set &lt;file&gt;</c>. Returns 0 when no different-work pair was merged.</summary>
    public static int Run(string path, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Result result;
        try { result = Evaluate(path); }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            output.WriteLine("Duplicate test set could not be read: " + error.Message);
            return 2;
        }
        output.WriteLine(Report(result));
        return result.WrongMerges.Count == 0 ? 0 : 1;
    }

    public static string Report(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"pairs {result.Pairs} · same-work pairs {result.SamePairs} · merged {result.Merged} · wrong merges {result.WrongMerges.Count} · recall {result.Recall:P1} ({result.MergedSame}/{result.SamePairs})"),
        };
        lines.AddRange(result.WrongMerges.Select(item => "WRONG MERGE " + item));
        lines.AddRange(result.Missed.Select(item => "not merged  " + item));
        return string.Join(Environment.NewLine, lines);
    }

    public static Result Evaluate(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("pairs", out var pairs) || pairs.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The pair file needs a \"pairs\" array");
        using var fixture = new DuplicateFixture();
        var ingested = new List<(long A, long B, bool Same, string Label)>();
        var index = 0;
        foreach (var pair in pairs.EnumerateArray())
        {
            index++;
            var (a, aLabel) = Side(fixture, pair, "a", index); var (b, bLabel) = Side(fixture, pair, "b", index);
            var same = pair.TryGetProperty("same", out var flag) && flag.ValueKind == JsonValueKind.True;
            ingested.Add((a, b, same, string.Create(CultureInfo.InvariantCulture, $"#{index} {aLabel} | {bLabel}")));
        }
        fixture.Duplicates.RunAutomaticAsync().GetAwaiter().GetResult();
        var wrong = new List<string>(); var missed = new List<string>(); var merged = 0; var mergedSame = 0;
        foreach (var (a, b, same, label) in ingested)
        {
            var together = fixture.Canonical(a) == fixture.Canonical(b);
            if (together) merged++;
            if (together && !same) wrong.Add(label);
            if (same && together) mergedSame++;
            if (same && !together) missed.Add(label);
        }
        return new Result(ingested.Count, ingested.Count(item => item.Same), merged, mergedSame, wrong, missed);
    }

    private static (long Id, string Label) Side(DuplicateFixture fixture, JsonElement pair, string name, int index)
    {
        if (!pair.TryGetProperty(name, out var side) || side.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Pair " + index.ToString(CultureInfo.InvariantCulture) + " needs \"" + name + "\"");
        static string[] Strings(JsonElement value, string key) => value.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim()).Where(item => item.Length != 0).ToArray() : [];
        var titles = Strings(side, "titles");
        if (titles.Length == 0) throw new InvalidDataException("Pair " + index.ToString(CultureInfo.InvariantCulture) + " side " + name + " has no titles");
        long? year = side.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt64(out var number) ? number : null;
        var description = side.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        return (fixture.Entry(titles, Strings(side, "authors"), year, description: description), titles[0]);
    }
}
