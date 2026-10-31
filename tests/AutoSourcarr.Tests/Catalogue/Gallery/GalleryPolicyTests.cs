using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class GalleryPolicyTests
{
    private const string Source = "-9223372036854775808", Other = "6084907896154116083";
    public static void Register(TestSuite suite)
    {
        suite.Add("gallery: 862 released metadata/policy/family/tag-review differential cases", Oracle);
        suite.Add("gallery: policy CAS, reopen and pending layout receipts preserve persistent state", PolicyPersistence);
        suite.Add("gallery: source policy edits preserve entries, source bindings and chapter provenance", NoStructuralWrites);
        suite.Add("gallery: unknown shared source policies remain separate from native installation", SharedSource);
        suite.Add("gallery: all selected criteria add requirements to current native evidence", SelectedCriteria);
        suite.Add("gallery: native anchor contexts retain imports without trusting later automatic attachments", AnchorContexts);
        suite.Add("gallery: every trusted native anchor must pass and manual locks remain authoritative", AllAnchors);
        suite.Add("gallery: portable facts cannot become native anchors and adapter writes no bindings", MetadataAuthority);
        suite.Add("gallery: current tag Separate decisions invalidate previously eligible evidence", CurrentTagReviews);
        suite.Add("gallery: authoritative source ID controls cleaning and disabled grouping", SourceOwnership);
        suite.Add("gallery: title floor, edition and animation constraints cannot be lowered by criteria", HardGates);
        suite.Add("gallery: collection names trim complete Unicode characters and preserve raw provenance", UnicodeCollection);
    }

    private static void Oracle()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("gallery-policy.json")))!.AsObject(); var count = 0;
        foreach (var row in fixture["cases"]!.AsArray().OfType<JsonObject>())
        {
            JsonNode? actual; IReadOnlySet<string> Canonical(IEnumerable<JsonNode?> tags) => GalleryTagEquivalence.CanonicalKeys(tags);
            switch (Text(row["kind"]))
            {
                case "detect": actual = JsonValue.Create(GalleryMetadata.IsGallerySource(row["source"]!.AsObject(), row["payload"]!.AsObject())); break;
                case "enrich": actual = GalleryMetadata.Enrich(row["payload"]!.AsObject(), row["source"]!.AsObject()); break;
                case "tags": actual = GalleryMetadata.SourceTags(row["payload"]!.AsObject()); break;
                case "pair": actual = GalleryEvidence.Pair(row["left"]!.AsObject(), row["right"]!.AsObject(), Canonical, row["threshold"]!.GetValue<double>()); break;
                case "family": actual = GalleryEvidence.Family(row["left"]!.AsObject(), row["right"]!.AsObject(), Canonical, row["threshold"]!.GetValue<double>()); break;
                case "policy":
                    if (!row["valid"]!.GetValue<bool>()) { Check.Throws<ArgumentException>(() => GalleryPolicy.Validate(row["value"])); count++; continue; }
                    actual = GalleryPolicy.Validate(row["value"]); break;
                case "policy-pair":
                    actual = GalleryPolicy.PairGate(row["left"]!.AsObject(), row["right"]!.AsObject(), sid => row["policies"]![sid] as JsonObject ?? GalleryPolicy.Default, Canonical, row["threshold"]!.GetValue<double>(), row["manual"]!.GetValue<bool>()); break;
                case "engine":
                    actual = GalleryEvidence.Compose(row["candidate"]!.AsObject(), row["trusted"]!.AsArray().OfType<JsonObject>().ToArray(), sid => row["sources"]![sid] as JsonObject ?? new(), sid => row["policies"]![sid] as JsonObject ?? GalleryPolicy.Default, Canonical); break;
                case "mapping":
                    var rows = row["rows"]!.AsArray().OfType<JsonObject>().ToArray(); var reviews = row["reviews"]!.AsArray().OfType<JsonObject>().ToArray(); var strict = row["strict"]!.GetValue<bool>();
                    if (!row["valid"]!.GetValue<bool>()) { Check.Throws<ArgumentException>(() => GalleryTagEquivalence.Mapping(rows, reviews, strict)); count++; continue; }
                    actual = JsonSerializer.SerializeToNode(GalleryTagEquivalence.Mapping(rows, reviews, strict)); break;
                case "canonical":
                    var mapping = row["mapping"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);
                    actual = new JsonArray(GalleryTagEquivalence.CanonicalKeys(row["tags"]!.AsArray(), mapping).Order(AutoSourcarr.Core.Unicode.UnicodeCodePointComparer.Instance).Select(value => JsonValue.Create(value)).ToArray()); break;
                default: throw new InvalidDataException("Unknown gallery oracle case");
            }
            Equivalent(row["result"], actual, "gallery case " + count.ToString(CultureInfo.InvariantCulture) + "/" + Text(row["kind"])); count++;
        }
        Check.Equal(862, count);
    }

    private static void PolicyPersistence()
    {
        using var setup = new Setup(); var initial = setup.Policies.Read(Source);
        Check.Equal(0L, Integer(initial["revision"])); Check.True(initial["policy"]!["enabled"]!.GetValue<bool>()); Check.Equal("auto", Text(initial["policy"]!["profile"]));
        var policy = GalleryPolicy.Default; policy["criteria"] = new JsonArray("language", "artist"); var first = setup.Policies.Save(Source, policy, 0);
        Check.Equal(1L, Integer(first["revision"])); Check.True(first["layout_pending"]!.GetValue<bool>()); Check.True(setup.Policies.Pending().Contains(Source));
        Check.Equal("artist", Text(first["policy"]!["criteria"]![0])); Check.Throws<ArgumentException>(() => setup.Policies.Save(Source, policy, 0));
        policy["enabled"] = false; setup.Policies.Save(Source, policy, 1);
        Check.True(!setup.Policies.RecordLayout(Source, 1, new JsonObject { ["stale"] = true })); Check.True(setup.Policies.Read(Source)["layout_pending"]!.GetValue<bool>());
        Check.True(setup.Policies.RecordLayout(Source, 2, new JsonObject { ["checked"] = true }));
        using var reopened = new SqliteDatabase(new AutoSourcarrSettings { DataDir = setup.Path });
        var saved = new GalleryPolicyRepository(reopened, setup.Gate).Read(Source); Check.Equal(2L, Integer(saved["revision"])); Check.True(!saved["layout_pending"]!.GetValue<bool>()); Check.True(saved["layout_result"]!["checked"]!.GetValue<bool>());
        Check.Equal(2, setup.Invalidations); Check.Throws<KeyNotFoundException>(() => setup.Policies.Read("unknown"));
    }

    private static void NoStructuralWrites()
    {
        using var setup = new Setup(); setup.AddPrimary(1, 1, Source, "[Mira Vale] The Glass Lantern Part 1");
        setup.Database.Write(DatabaseFile.Origins, session => session.Execute("""
            INSERT INTO virtual_chapters(id,entry_id,chapter_key,display_name,created_at,updated_at) VALUES(91,1,'g:member','Gallery','now','now');
            INSERT INTO chapter_candidates(id,virtual_chapter_id,source_id,source_name,source_manga_id,source_chapter_id,name,first_seen,last_seen) VALUES(92,91,$source,'Gallery',9007199254740994,9007199254740995,'Member','now','now');
            INSERT INTO candidate_pages VALUES(92,0,'https://images.invalid/original','now');
            INSERT INTO served_origin VALUES(91,92,'now','initial');
            """, ("$source", Source)));
        var before = Snapshot(setup.Database); var policy = GalleryPolicy.Default; policy["enabled"] = false; setup.Policies.Save(Source, policy, 0);
        Check.Equal(before, Snapshot(setup.Database)); Check.True(setup.Policies.Read(Source)["layout_pending"]!.GetValue<bool>());
    }

    private static void SharedSource()
    {
        using var setup = new Setup(); new SchemaMigrator(setup.Database).EnsureFeatureSchema("catalogue-identity.sql");
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO catalogue_source_facts VALUES(1,'reference','not-installed','{}')"));
        var state = setup.Policies.Read("not-installed"); Check.Equal("not installed", Text(state["source"]!["lang"]));
        var value = GalleryPolicy.Default; value["profile"] = "gallery"; value["enabled"] = false; setup.Policies.Save("not-installed", value, 0);
        Check.Equal(0L, setup.Database.Read(DatabaseFile.Catalog, session => (long)session.Scalar("SELECT COUNT(*) FROM source_config WHERE source_id='not-installed'")!));
    }

    private static void SelectedCriteria()
    {
        using var setup = new Setup(); setup.AddPrimary(1, 1, Source, "[Mira Vale] The Glass Lantern Part 1 [English]");
        var policy = GalleryPolicy.Default; policy["criteria"] = new JsonArray("artist", "language"); setup.Policies.Save(Source, policy, 0);
        Check.True(Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern Part 2 [English]"))));
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern Part 2 [Japanese]"))));
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Other Artist] The Glass Lantern Part 2 [English]"))));
    }

    private static void AnchorContexts()
    {
        using var setup = new Setup(); setup.AddPrimary(10, 1, Source, "[Mira Vale] The Glass Lantern");
        setup.AddPrimary(11, 1, Other, "[Other Artist] The Glass Lantern");
        Check.Equal(1, setup.Evidence.TrustedNativePayloads(1).Count);
        Check.True(Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern"))));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_matches SET match_type='import' WHERE id=11"));
        Check.Equal(2, setup.Evidence.TrustedNativePayloads(1, GalleryAnchorContext.AutomaticEntry).Count);
        Check.Equal(1, setup.Evidence.TrustedNativePayloads(1, GalleryAnchorContext.LocalAttachment).Count);
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern"))));
        Check.True(Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern"), GalleryAnchorContext.LocalAttachment)));
    }

    private static void AllAnchors()
    {
        using var setup = new Setup(); setup.AddPrimary(10, 1, Source, "[Mira Vale] The Glass Lantern"); setup.AddPrimary(11, 1, Other, "[Other Artist] The Glass Lantern", locked: true);
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern"))));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("UPDATE source_matches SET active=0 WHERE id=11"));
        Check.True(Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern"))));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO entry_aliases VALUES(99,1,'merge','now')"));
        Check.Equal(1, setup.Evidence.TrustedNativePayloads(99).Count);
    }

    private static void MetadataAuthority()
    {
        using var setup = new Setup(); new SchemaMigrator(setup.Database).EnsureFeatureSchema("catalogue-identity.sql");
        var left = Fact(Source, "[Mira Vale] The Glass Lantern Part 1"); var right = Fact(Source, "[Mira Vale] The Glass Lantern Part 2");
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("INSERT INTO catalogue_source_facts VALUES(1,'reference',$source,$fact)", ("$source", Source), ("$fact", left.ToJsonString())));
        Check.Equal(0, setup.Evidence.TrustedNativePayloads(1).Count);
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern Part 2"))));
        Check.Equal(true, setup.Evidence.CatalogueAdapter(right, [left]));
        Check.Equal(0L, setup.Database.Read(DatabaseFile.Catalog, session => (long)session.Scalar("SELECT COUNT(*) FROM source_matches")!));
        var off = GalleryPolicy.Default; off["enabled"] = false; setup.Policies.Save(Source, off, 0); Check.Equal(false, setup.Evidence.CatalogueAdapter(right, [left]));
        left["gallery"] = false; right["gallery"] = false; Check.Equal(false, setup.Evidence.CatalogueAdapter(right, [left]));
    }

    private static void CurrentTagReviews()
    {
        using var setup = new Setup(); setup.AddPrimary(1, 1, Source, "[Mira Vale] The Glass Lantern", "fantasy, sci-fi");
        var policy = GalleryPolicy.Default; policy["criteria"] = new JsonArray("tags"); policy["tag_overlap"] = 3; setup.Policies.Save(Source, policy, 0);
        var candidate = Payload("[Mira Vale] The Glass Lantern", "fantasy, science fiction"); Check.True(Eligible(setup.Evidence.EvaluateNative(1, Other, candidate)));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("""
            INSERT INTO tags(id,namespace,name,normalized) VALUES(1,'tag','sci-fi','sci-fi'),(2,'tag','science fiction','science fiction');
            INSERT INTO tag_reviews VALUES(1,2,'different','now');
            """));
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Other, candidate)));
        setup.Database.Write(DatabaseFile.Catalog, session => session.Execute("DELETE FROM tag_reviews")); Check.True(Eligible(setup.Evidence.EvaluateNative(1, Other, candidate)));
    }

    private static void SourceOwnership()
    {
        using var setup = new Setup(); setup.AddPrimary(1, 1, Source, "[Mira Vale] The Glass Lantern Part 1"); var off = GalleryPolicy.Default; off["enabled"] = false; setup.Policies.Save(Source, off, 0);
        var input = Payload("[Mira Vale] The Glass Lantern Part 2"); input["sourceId"] = Other;
        var clean = setup.Evidence.CleanNative(Source, input); Check.Equal(Source, Text(clean["sourceId"])); Check.Equal(Source, Text(clean["_gallery_metadata"]!["source_id"]));
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, input)));
        var second = (JsonObject)clean.DeepClone(); second["id"] = 9007199254740994L;
        Check.Throws<ArgumentException>(() => GalleryPolicy.GroupingGate([clean, second], setup.Policies.Effective));
    }

    private static void HardGates()
    {
        using var setup = new Setup(); setup.AddPrimary(1, 1, Source, "[Mira Vale] The Glass Lantern (Colored)");
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern (Monochrome)"), threshold: .1)));
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] The Glass Lantern (animated)"), threshold: .1)));
        Check.True(!Eligible(setup.Evidence.EvaluateNative(1, Source, Payload("[Mira Vale] Unrelated Plain Work"), threshold: .1)));
        var invalid = GalleryPolicy.Default; invalid["title_minimum"] = .93; Check.Throws<ArgumentException>(() => setup.Policies.Save(Source, invalid, 0));
    }

    private static void UnicodeCollection()
    {
        var payload = Payload("Artist - 👽Example Creator"); var parsed = GalleryMetadata.Enrich(payload, new JsonObject { ["name"] = "E-Hentai" });
        Check.Equal("👽Example Creator", Text(parsed["_gallery_metadata"]!["artist"])); Check.Equal(Text(payload["title"]), Text(parsed["_gallery_metadata"]!["raw_title"]));
        Check.True(!parsed.ContainsKey("description"));
    }

    private sealed class Setup : IDisposable
    {
        private readonly Sandbox _temp = new(); public string Path => _temp.Path; public SqliteDatabase Database { get; } public object Gate { get; } = new();
        public GalleryPolicyRepository Policies { get; } public GalleryEvidenceService Evidence { get; } public int Invalidations;
        public Setup()
        {
            Database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(Database).Initialize();
            Policies = new(Database, Gate, () => Invalidations++); Policies.Initialize(); Evidence = new(Database, Gate);
            Database.Write(DatabaseFile.Catalog, session => session.Execute("""
                INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(1,'Native work','now','now');
                INSERT INTO source_config(source_id,name,lang,last_seen) VALUES($source,'E-Hentai','en','now'),($other,'Luscious','ja','now');
                """, ("$source", Source), ("$other", Other)));
        }
        public void AddPrimary(long id, long entry, string sid, string title, string genre = "fantasy, sci-fi", bool locked = false) => Database.Write(DatabaseFile.Catalog, session => session.Execute("""
            INSERT INTO source_matches(id,entry_id,source_id,source_manga_id,source_title,genre,locked,last_checked)
            VALUES($id,$entry,$source,$manga,$title,$genre,$locked,'now')
            """, ("$id", id), ("$entry", entry), ("$source", sid), ("$manga", 9007199254740993L + id), ("$title", title), ("$genre", genre), ("$locked", locked ? 1 : 0)));
        public void Dispose() { Database.Dispose(); _temp.Dispose(); }
    }
    private static JsonObject Payload(string title, string genre = "fantasy, sci-fi") => new() { ["id"] = 9007199254740993L, ["title"] = title, ["genre"] = genre, ["initialized"] = true };
    private static JsonObject Fact(string source, string title) => new() { ["source_id"] = source, ["title"] = title, ["raw_title"] = title, ["gallery"] = true, ["metadata"] = new JsonObject { ["genre"] = "fantasy, sci-fi", ["artist"] = "Mira Vale" } };
    private static bool Eligible(JsonObject? evidence) => evidence?["eligible"] is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static string Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    private static long Integer(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<long>(out var number) ? number : 0;
    private static string Snapshot(SqliteDatabase database) => string.Join("|", new[] { DatabaseFile.Catalog, DatabaseFile.Origins }.Select(file => database.Read(file, session => string.Join(";", (file == DatabaseFile.Catalog ? new[] { "entries", "source_matches", "source_variants" } : new[] { "virtual_chapters", "chapter_candidates", "candidate_pages", "served_origin" }).Select(table => JsonSerializer.Serialize(session.Query("SELECT * FROM " + table + " ORDER BY rowid")))))));
    private static void Equivalent(JsonNode? expected, JsonNode? actual, string path)
    {
        using var left = JsonDocument.Parse(JsonSerializer.Serialize(expected)); using var right = JsonDocument.Parse(JsonSerializer.Serialize(actual)); Compare(left.RootElement, right.RootElement, path);
    }
    private static void Compare(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind) throw new InvalidOperationException(path + ": JSON type mismatch");
        if (expected.ValueKind == JsonValueKind.Number)
        {
            if (expected.TryGetInt64(out var left) && actual.TryGetInt64(out var right)) Check.Equal(left, right);
            else Check.True(Math.Abs(expected.GetDouble() - actual.GetDouble()) < 1e-12, path + ": numeric mismatch");
        }
        else if (expected.ValueKind == JsonValueKind.Object)
        {
            var a = expected.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value); var b = actual.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value);
            Check.Equal(a.Count, b.Count); foreach (var pair in a) { Check.True(b.ContainsKey(pair.Key), path + ": missing " + pair.Key); Compare(pair.Value, b[pair.Key], path + "." + pair.Key); }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Check.Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var index = 0; index < expected.GetArrayLength(); index++) Compare(expected[index], actual[index], path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]"); }
        else if (expected.ValueKind == JsonValueKind.String) Check.Equal(expected.GetString(), actual.GetString());
        else Check.Equal(expected.GetRawText(), actual.GetRawText());
    }
}
