using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Compatibility;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Privacy;
using static AutoSourcarr.Tests.CatalogueSharingTests;

namespace AutoSourcarr.Tests;

/// <summary>Phase 9B: versioned catalogue files, the compatibility list, the export/import round trip, refusals
/// without partial writes, identity review and manual decisions that an imported structural choice cannot override.</summary>
public static class CatalogueSharingVersionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue sharing: compatibility list matches the share format, migrations and extension API", () =>
        {
            var list = CompatibilityList.Current;
            Check.Equal(CatalogueFormat.FileVersion, list.ShareFormat); Check.Equal(CatalogueFormat.MinimumReaderVersion, list.ShareFormatMinReader);
            Check.Equal("0.4.2", list.ShareReaderAppVersion(CatalogueFormat.LegacyVersion)); Check.Equal(list.AppVersion, list.ShareReaderAppVersion(CatalogueFormat.FileVersion));
            Check.Equal(SchemaMigrator.LatestMigration(DatabaseFile.Catalog), list.CatalogSchema); Check.Equal(SchemaMigrator.LatestMigration(DatabaseFile.Origins), list.OriginsSchema);
            Check.Equal("1.4", list.ExtensionApi); Check.True(list.ProfileReaderAppVersion(list.ProfileFormat) is not null);
            var json = list.ToJson(); Check.Equal(list.CatalogSchema, json["database_schema"]!["catalog"]!.GetValue<long>());
            Check.Throws<InvalidOperationException>(() => CompatibilityList.Parse("""{"list":"autosourcarr-compatibility","list_version":1,"app_version":"1","share_format":{"version":2,"min_reader_version":3,"readers":[{"version":2,"app_version":"1"}]},"extension_api":{"version":"1.4"},"profile_format":{"version":1,"readers":[{"version":1,"app_version":"1"}]}}""", 1, 1));
        });

        suite.Add("catalogue sharing: export/import round trip keeps every portable decision and records the actor", () =>
        {
            using var a = new World(); a.Native("First local work", "/one", 11); a.Native("Second local work", "/two", 12); a.Variant(1, "First alternative", "/one-alt", 13); a.ManualTitle(1, "Manual first work");
            a.Write(c =>
            {
                c.Execute("INSERT INTO duplicate_reviews VALUES(1,2,'not_duplicate','now')"); c.Execute("INSERT INTO manual_match_groups VALUES('local-group','Group title','now','now')");
                c.Execute("INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_title,created_at,updated_at) VALUES('17',11,'local-group',1,'First local work','now','now'),('17',13,'local-group',1,'First alternative','now','now'),('17',12,NULL,2,'Second local work','now','now')");
                c.Execute("INSERT INTO manual_separate_records VALUES(1,'17',12,2,'Second local work','now','now')");
                c.Execute("INSERT INTO import_manual_autoresolve(backup_key,backup_title,target_source_id,target_source_manga_id,target_entry_id_hint,created_at,updated_at) VALUES('public-backup','Backup title','17',11,1,'now','now')");
                c.Execute("INSERT INTO import_match_rejections VALUES('public-backup','source-url:/unwanted','Backup title','Source','Rejected title','now')");
                c.Execute("INSERT INTO portable_identity_reviews VALUES(1,'asc1:work:anilist:12','confirmed','old-evidence','My provenance note','now')"); return 0;
            });
            var raw = a.Dump(); var file = JsonNode.Parse(Encoding.UTF8.GetString(raw))!.AsObject();
            Check.Equal(CatalogueFormat.Format, S(file, "format")); Check.Equal("2", file["format_version"]!.ToJsonString()); Check.Equal("2", file["min_reader_version"]!.ToJsonString());
            Check.Equal(CompatibilityList.RunningVersion, S(file, "app_version")); Check.Equal(AutoSourcarr.Api.ApplicationRuntime.Version, CompatibilityList.RunningVersion); Check.Equal(SchemaMigrator.LatestMigration(DatabaseFile.Catalog), file["schema_version"]!.GetValue<long>());
            Check.True(S(file, "created_at").EndsWith('Z')); Check.True(!file.ContainsKey("version"), "The file header replaces the legacy version field");
            var expected = Decisions(CatalogueFormat.Decode(raw)); Check.Equal(8, CatalogueFormat.Decode(raw)["decisions"]!.AsArray().Select(item => S(item!, "kind")).Distinct(StringComparer.Ordinal).Count());

            using var b = new World();
            var exchange = new CatalogueExchange(b.Database, b.Gate, new SupportPrivacy()) { Log = new DecisionLog(b.Database) }; exchange.Initialize();
            var preview = exchange.Preview(raw); Check.Equal("false", preview["file"]!["legacy"]!.ToJsonString()); Check.Equal("2", preview["file"]!["format_version"]!.ToJsonString());
            var added = exchange.Apply(S(preview, "id"), S(preview, "revision"), actor: DecisionActor.User("fixture admin"));
            Check.Equal(2, added["added"]!.GetValue<int>()); Check.Equal(2L, b.Count());
            var exported = CatalogueFormat.Decode(Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(exchange.Export()["catalogue"])));
            var actual = Decisions(exported);
            Check.True(actual.SetEquals(expected), "Every portable decision survives the round trip (" + expected.Count + " expected, " + actual.Count + " exported)");
            Check.Equal("Manual first work", b.Scalar("SELECT title FROM preferred_title_overrides") as string);
            Check.Equal("needs-review", b.Scalar("SELECT decision FROM portable_identity_reviews") as string);
            Check.Equal(1L, b.Scalar("SELECT COUNT(*) FROM duplicate_reviews WHERE decision='not_duplicate'"));
            Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM manual_match_groups")); // structural choices wait for their own reviewed task
            Check.Equal(1L, b.Scalar("SELECT COUNT(*) FROM decision_log WHERE kind='catalogue.import' AND actor_kind='user' AND actor_name='fixture admin'"));
            Check.True((long)b.Scalar("SELECT COUNT(*) FROM decision_log WHERE kind LIKE 'catalogue.decision.%' AND actor_name='fixture admin'")! >= 3);
            // Importing the same decisions again adds nothing.
            var again = exchange.Preview(raw); Check.Equal(0, exchange.Apply(S(again, "id"), S(again, "revision"))["added"]!.GetValue<int>()); Check.Equal(2L, b.Count());
        });

        suite.Add("catalogue sharing: a newer file is refused with the exact version mismatch and nothing staged", () =>
        {
            using var a = new World(); a.Native("Future work", "/future", 11); var raw = a.Dump();
            using var b = new World();
            var newer = Parse(raw); newer["format_version"] = 3; newer["min_reader_version"] = 3; newer["app_version"] = "0.9.1"; newer["entries"] = "a later entry shape";
            var error = Check.Throws<CatalogueVersionMismatchException>(() => b.Exchange.Preview(Bytes(newer)));
            Check.Equal("Version mismatch: version 0.9.1 required", error.Message); Check.Equal("0.9.1", error.RequiredVersion);
            Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages")); Check.Equal(0L, b.Count());
            // A newer format that this reader may read (min_reader_version 2) may add header fields.
            var compatible = Parse(raw); compatible["format_version"] = 3; compatible["future_header"] = "ignored";
            var stage = b.Exchange.Preview(Bytes(compatible)); Check.Equal(1, b.Exchange.Apply(S(stage, "id"), S(stage, "revision"))["added"]!.GetValue<int>());
            // The current format is strict; an older header version is refused clearly.
            var unknown = Parse(raw); unknown["future_header"] = "x"; Check.Throws<ArgumentException>(() => b.Exchange.Preview(Bytes(unknown)));
            var older = Parse(raw); older["format_version"] = 1; older["min_reader_version"] = 1;
            Check.Equal("Unsupported catalogue file version", Check.Throws<ArgumentException>(() => b.Exchange.Preview(Bytes(older))).Message);
            // A 0.4.x header-less file (share format 1) is still read: its body is the current body shape.
            using var c = new World(); var legacy = c.Exchange.Preview(Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(CatalogueFormat.Decode(raw))));
            Check.Equal("true", legacy["file"]!["legacy"]!.ToJsonString()); Check.Equal(1, c.Exchange.Apply(S(legacy, "id"), S(legacy, "revision"))["added"]!.GetValue<int>());
        });

        suite.Add("catalogue sharing: the header names the first reader app version; a malformed app_version falls back to the format (L1)", () =>
        {
            using var a = new World(); a.Native("Header work", "/header", 21); var raw = a.Dump();
            var written = Parse(raw);
            Check.True(written["min_reader_app_version"] is JsonValue header && header.GetValue<string>().Length > 0, written.ToJsonString());
            using var b = new World();
            var newer = Parse(raw); newer["format_version"] = 5; newer["min_reader_version"] = 5; newer["app_version"] = "1.4.0"; newer["min_reader_app_version"] = "1.3.0";
            var error = Check.Throws<CatalogueVersionMismatchException>(() => b.Exchange.Preview(Bytes(newer)));
            Check.Equal("Version mismatch: version 1.3.0 required", error.Message);
            newer.Remove("min_reader_app_version"); newer["app_version"] = "not a version!";
            Check.Equal("Version mismatch: version 5 required", Check.Throws<CatalogueVersionMismatchException>(() => b.Exchange.Preview(Bytes(newer))).Message);
            Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages"));
            // A current-format file from before the field existed is still read; a malformed one is refused.
            var without = Parse(raw); without.Remove("min_reader_app_version");
            Check.Equal("false", b.Exchange.Preview(Bytes(without))["file"]!["legacy"]!.ToJsonString());
            var malformed = Parse(raw); malformed["min_reader_app_version"] = "not a version!";
            Check.Equal("Invalid catalogue file header", Check.Throws<ArgumentException>(() => b.Exchange.Preview(Bytes(malformed))).Message);
        });

        suite.Add("catalogue sharing: truncated or corrupted files and a failing apply leave no partial writes", () =>
        {
            using var a = new World(); a.Native("First catalogue work", "/one", 112); a.Native("Rejected second work", "/two", 113); var raw = a.Dump();
            using var b = new World(); var exchange = new CatalogueExchange(b.Database, b.Gate, new SupportPrivacy()) { Log = new DecisionLog(b.Database) }; exchange.Initialize();
            var corrupted = (byte[])raw.Clone(); corrupted[raw.Length / 2] = 0xFF;
            foreach (var damaged in new[] { raw[..(raw.Length / 2)], raw[..^1], corrupted })
                Check.Throws<ArgumentException>(() => exchange.Preview(damaged));
            Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages"));
            var stage = exchange.Preview(raw);
            b.Write(c => c.Execute("CREATE TRIGGER fixture_import_abort BEFORE INSERT ON entries WHEN NEW.preferred_title='Rejected second work' BEGIN SELECT RAISE(ABORT,'fixture failure'); END"));
            Check.Throws<Microsoft.Data.Sqlite.SqliteException>(() => exchange.Apply(S(stage, "id"), S(stage, "revision"), actor: DecisionActor.User("fixture admin")));
            foreach (var table in new[] { "entries", "catalogue_entry_ids", "catalogue_exchange_receipts", "catalogue_import_records", "catalogue_decisions", "decision_log" })
                Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM " + table));
            b.Write(c => c.Execute("DROP TRIGGER fixture_import_abort"));
            Check.Equal(2, exchange.Apply(S(stage, "id"), S(stage, "revision"))["added"]!.GetValue<int>()); Check.Equal(1L, b.Scalar("SELECT COUNT(*) FROM decision_log WHERE kind='catalogue.import'"));
        });

        suite.Add("catalogue sharing: an imported group never overrides a local Separate decision or manual match", () =>
        {
            var structure = new CountingStructure();
            using var world = new World(structure); world.Native("First native owner", "/one", 11); world.Native("Second native owner", "/two", 12);
            world.Write(c => c.Execute("INSERT INTO duplicate_reviews VALUES(1,2,'not_duplicate','now')"));
            var choice = PendingGroup(world); Check.Equal("pending", S(choice, "state"));
            var error = Check.Throws<ArgumentException>(() => world.Exchange.ApplyDecision(S(choice, "uid"), S(choice, "digest"), S(choice, "revision"), taskId: "visible-task"));
            Check.True(error.Message.StartsWith("A local Separate decision is retained", StringComparison.Ordinal)); Check.Equal(0, structure.Calls);
            Check.Equal(2L, world.Count()); Check.Equal("pending", S(world.Exchange.Decision(S(choice, "uid"), S(choice, "digest")), "state"));

            using var other = new World(structure); other.Native("Manual owner", "/one", 11); other.Native("Imported counterpart", "/two", 12);
            var bundle = CatalogueFormat.Decode(other.Dump()); var entries = bundle["entries"]!.AsArray();
            bundle["decisions"]!.AsArray().Add(new JsonObject { ["uid"] = CatalogueFormat.Uid("decision"), ["kind"] = "source-match", ["entries"] = new JsonArray(S(entries[1]!, "uid")), ["records"] = new JsonArray(entries[0]!["sources"]![0]!.DeepClone()), ["value"] = new JsonObject() });
            other.Write(c => c.Execute("INSERT INTO manual_match_bindings(source_id,source_manga_id,group_id,entry_id_hint,source_title,created_at,updated_at) VALUES('17',11,NULL,1,'Manual owner','now','now')"));
            Add(other, Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle)));
            var move = other.Exchange.Decisions()["items"]!.AsArray().Single(item => S(item!, "kind") == "source-match" && item!["entry_ids"]!.AsArray().Count == 1 && item["entry_ids"]![0]!.GetValue<long>() == 2)!;
            if (S(move, "state") != "conflict")
                Check.Throws<ArgumentException>(() => other.Exchange.ApplyDecision(S(move, "uid"), S(move, "digest"), S(move, "revision"), taskId: "visible-task"));
            Check.Equal(0, structure.Calls); Check.Equal(1L, other.Scalar("SELECT entry_id FROM source_matches WHERE source_manga_id=11"));
        });

        suite.Add("catalogue HTTP v1: version mismatch detail, compatibility list and identity review", () =>
        {
            using var original = new World(); original.Native("Published fixture", "/published", 112); var newer = Parse(original.Dump());
            newer["format_version"] = 4; newer["min_reader_version"] = 4; newer["app_version"] = "1.2.0";
            using var setup = new HttpSetup();
            var refused = setup.SendBytes("/api/v1/catalog-sharing/preview", Bytes(newer)); Check.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Check.Equal("Version mismatch: version 1.2.0 required", S(Body(refused), "detail"));
            Check.Equal(0L, setup.World.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages"));
            Check.Equal(HttpStatusCode.Unauthorized, setup.Client.GetAsync("/api/v1/compatibility").GetAwaiter().GetResult().StatusCode);
            var compatibility = Body(setup.Send(HttpMethod.Get, "/api/v1/compatibility"));
            Check.Equal(CatalogueFormat.FileVersion, compatibility["share_format"]!["version"]!.GetValue<int>()); Check.Equal("1.4", S(compatibility["extension_api"]!, "version"));

            setup.World.Native("Identity work", "/identity", 11);
            setup.World.Write(c => c.Execute("INSERT INTO identity_links(entry_id,provider,external_id,identity_scope,last_checked) VALUES(1,'anilist','123','work','now')"));
            var listing = Body(setup.Send(HttpMethod.Get, "/api/v1/identity-review?search=Identity")); Check.Equal(1L, listing["total"]!.GetValue<long>());
            var detail = Body(setup.Send(HttpMethod.Get, "/api/v1/identity-review/1"));
            var reference = detail["references"]!.AsArray().Single(item => S(item!, "id") == "asc1:work:anilist:123")!; Check.Equal("inherited", S(reference, "decision"));
            Check.Equal(1L, Body(setup.Send(HttpMethod.Post, "/api/v1/portable-identities/resolve", new() { ["reference"] = "asc1:work:anilist:123" }))["total"]!.GetValue<long>());
            var saved = Body(setup.Send(HttpMethod.Post, "/api/v1/identity-review/1", new() { ["reference"] = "asc1:work:anilist:123", ["decision"] = "excluded", ["revision"] = S(detail, "revision"), ["note"] = "Different edition" }));
            Check.Equal("excluded", S(saved["references"]!.AsArray().Single(item => S(item!, "id") == "asc1:work:anilist:123")!, "decision"));
            Check.Equal(0L, Body(setup.Send(HttpMethod.Post, "/api/v1/portable-identities/resolve", new() { ["reference"] = "asc1:work:anilist:123" }))["total"]!.GetValue<long>());
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, "/api/v1/identity-review/1", new() { ["reference"] = "asc1:work:anilist:123", ["decision"] = "reset", ["revision"] = S(detail, "revision") }).StatusCode);
            Check.Equal(HttpStatusCode.NotFound, setup.Send(HttpMethod.Get, "/api/v1/identity-review/99").StatusCode);
            Check.Equal(0, setup.Unexpected);
        });
    }

    private static HashSet<(string, string)> Decisions(JsonObject body) =>
        body["decisions"]!.AsArray().Select(item => (S(item!, "uid"), CatalogueFormat.Digest(item))).ToHashSet();

    private static JsonObject Parse(byte[] raw) => JsonNode.Parse(Encoding.UTF8.GetString(raw))!.AsObject();

    private static byte[] Bytes(JsonObject file) => Encoding.UTF8.GetBytes(file.ToJsonString());

    private sealed class CountingStructure : ICatalogueReviewedStructure
    {
        public int Calls;
        public void ReconcilePending(CancellationToken cancellationToken) => cancellationToken.ThrowIfCancellationRequested();
        public void ApplyReviewed(string decisionUid, string decisionDigest, string revision, JsonObject decision, IReadOnlyList<CatalogueNativeRecord> records, IReadOnlyList<long> targetIds, string taskId, CancellationToken cancellationToken) => Calls++;
    }
}
