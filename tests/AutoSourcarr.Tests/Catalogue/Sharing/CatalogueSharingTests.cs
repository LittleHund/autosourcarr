using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using AutoSourcarr.Api.Endpoints;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Tests;

public static class CatalogueSharingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("catalogue sharing: 150 released portable identity and strict wire oracle cases", Oracle);
        suite.Add("catalogue sharing: A/B/C round trips retain colliding local numbers and native proof", () =>
        {
            using var a = new World(); using var b = new World(); using var c = new World();
            Check.Equal(1L, a.Native("Alpha Chronicle", "/alpha", 112)); var first = a.Dump();
            Check.Equal(1L, b.Native("Beta Chronicle", "/beta", 900)); Check.Equal(1, Add(b, first)["added"]!.GetValue<int>()); var second = b.Dump();
            Check.Equal(1L, c.Native("Gamma Chronicle", "/gamma", 700)); Check.Equal(2, Add(c, second)["added"]!.GetValue<int>()); var third = c.Dump();
            Check.Equal(2, Add(a, third)["added"]!.GetValue<int>()); Check.Equal(3L, a.Count()); var fourth = a.Dump();
            Check.Equal(1, Add(b, fourth)["added"]!.GetValue<int>()); Check.Equal(0, Add(b, fourth)["added"]!.GetValue<int>()); Check.Equal(3L, b.Count());
            Check.Equal("Beta Chronicle", b.Scalar("SELECT preferred_title FROM entries WHERE id=1") as string); Check.Equal(900L, b.Scalar("SELECT source_manga_id FROM source_matches"));
            Check.Equal(1L, b.Scalar("SELECT COUNT(*) FROM source_matches")); Check.Equal(2L, b.Scalar("SELECT COUNT(*) FROM catalogue_shared_entries"));
            var uid = CatalogueFormat.Decode(first)["entries"]![0]!["uid"]!.GetValue<string>(); var found = new PortableIdentityResolver(b.Database).Resolve(uid); Check.Equal(1, found["total"]!.GetValue<int>());
            Check.True(!Encoding.UTF8.GetString(fourth).Contains("source_manga_id", StringComparison.Ordinal));
        });
        suite.Add("catalogue sharing: exact anchors reuse native IDs and retain local manual title", () =>
        {
            using var a = new World(); using var b = new World(); a.Native("Original title", "/shared", 112); a.ManualTitle(1, "Shared manual title"); b.Native("Original title", "/shared", 900); b.ManualTitle(1, "My local title");
            var raw = a.Dump(); var added = Add(b, raw); Check.Equal(0, added["added"]!.GetValue<int>()); Check.Equal(1L, b.Count()); Check.Equal("My local title", b.Scalar("SELECT preferred_title FROM entries WHERE id=1") as string);
            Check.True(b.Exchange.Decisions()["items"]!.AsArray().Any(choice => choice!["state"]!.GetValue<string>() == "conflict")); Check.Equal(0, Add(b, raw)["added"]!.GetValue<int>());
            Check.Equal(900L, b.Scalar("SELECT source_manga_id FROM source_matches"));
        });
        suite.Add("catalogue sharing: work-only identities retain separate editions and no native binding", () =>
        {
            using var a = new World(); using var b = new World(); a.Native("Edition Alpha", "/edition-a", 112); b.Native("Edition Beta", "/edition-b", 900);
            foreach (var world in new[] { a, b }) world.Write(c => c.Execute("INSERT INTO identity_links(entry_id,provider,external_id,identity_scope,last_checked) VALUES(1,'anilist','123','work','now')"));
            Check.Equal(1, Add(b, a.Dump())["added"]!.GetValue<int>()); var result = new PortableIdentityResolver(b.Database).Resolve("asc1:work:anilist:123"); Check.Equal(2, result["total"]!.GetValue<int>()); Check.True(result["ambiguous"]!.GetValue<bool>());
            Check.Equal(1L, b.Scalar("SELECT COUNT(*) FROM source_matches")); Check.Equal(1L, b.Scalar("SELECT COUNT(*) FROM identity_links"));
        });
        suite.Add("catalogue sharing: real SQLite failure rolls back rows, aliases and receipt; stale CAS refuses", () =>
        {
            using var a = new World(); using var b = new World(); a.Native("First catalogue work", "/one", 112); a.Native("Rejected second work", "/two", 113); var raw = a.Dump(); var stage = b.Exchange.Preview(raw);
            b.Write(c => c.Execute("CREATE TRIGGER fixture_import_abort BEFORE INSERT ON entries WHEN NEW.preferred_title='Rejected second work' BEGIN SELECT RAISE(ABORT,'fixture failure'); END"));
            Check.Throws<SqliteException>(() => b.Exchange.Apply(S(stage, "id"), S(stage, "revision"))); Check.Equal(0L, b.Count()); Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM catalogue_entry_ids")); Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM catalogue_exchange_receipts"));
            b.Write(c => c.Execute("DROP TRIGGER fixture_import_abort")); Check.Equal(2, b.Exchange.Apply(S(stage, "id"), S(stage, "revision"))["added"]!.GetValue<int>());
            stage = b.Exchange.Preview(raw); b.ManualTitle(1, "A newer local choice"); Check.Throws<ArgumentException>(() => b.Exchange.Apply(S(stage, "id"), S(stage, "revision")));
        });
        suite.Add("catalogue sharing: conflicting reused UUIDs retain versions and reviewed separate correction", () =>
        {
            using var a = new World(); using var b = new World(); a.Native("Original anchor", "/original", 112); var raw = a.Dump(); Add(b, raw);
            var bundle = CatalogueFormat.Decode(raw); var foreign = bundle["entries"]![0]!.AsObject(); var fact = foreign["sources"]![0]!.AsObject(); fact["reference"] = PortableIdentity.RecordId("17", "/different"); foreign["references"] = new JsonArray(S(fact, "reference")); foreign["lineage"] = new JsonArray(); foreign["title"] = "Different anchor";
            var stage = b.Exchange.Preview(Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle))); Check.Equal(1, stage["retained"]!.GetValue<int>()); b.Exchange.Apply(S(stage, "id"), S(stage, "revision")); Check.Equal(1L, b.Count());
            var retained = b.Exchange.Records()["items"]!.AsArray().Single(item => S(item!, "state") == "review")!.AsObject();
            Check.Throws<ArgumentException>(() => b.Exchange.ReviewRecord(S(retained, "uid"), S(retained, "digest"), "stale"));
            var reviewed = b.Exchange.ReviewRecord(S(retained, "uid"), S(retained, "digest"), S(retained, "revision")); Check.Equal(1, reviewed["added"]!.GetValue<int>()); Check.Equal(2L, b.Count());
            var output = CatalogueFormat.Decode(b.Dump()); Check.True(output["entries"]!.AsArray().Any(item => S(item!, "uid") != S(foreign, "uid") && S(item!, "title") == "Different anchor"));
            Check.Equal(0, Add(b, Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle)))["added"]!.GetValue<int>());
        });
        suite.Add("catalogue sharing: gallery off partitions exact lineage and policy revisions invalidate preview", () =>
        {
            using var a = new World(); using var b = new World(); a.Native("Shared Garden", "/gallery/11", 11); a.Variant(1, "Shared Garden Part Two", "/gallery/12", 12); a.Write(c => c.Execute("UPDATE source_config SET name='E-Hentai'"));
            var raw = a.Dump(); var groupUid = S(CatalogueFormat.Decode(raw)["entries"]![0]!, "uid");
            b.Policy(false); var stage = b.Exchange.Preview(raw); Check.Equal(2, stage["added"]!.GetValue<int>()); Check.Equal(2, b.Exchange.Apply(S(stage, "id"), S(stage, "revision"))["added"]!.GetValue<int>()); Check.Equal(2L, b.Count());
            Check.Equal(2, new PortableIdentityResolver(b.Database).Resolve(groupUid)["total"]!.GetValue<int>()); Check.Equal(0, Add(b, raw)["added"]!.GetValue<int>());
            var shared = b.Dump(); Check.Equal(0, Add(a, shared)["added"]!.GetValue<int>()); Check.Equal(1L, a.Count());
            stage = b.Exchange.Preview(raw); b.Policy(false); Check.Throws<ArgumentException>(() => b.Exchange.Apply(S(stage, "id"), S(stage, "revision")));
        });
        suite.Add("catalogue sharing: enabled gallery groups require actual evidence provider", () =>
        {
            using var a = new World(); using var b = new World(); a.Native("Shared Garden", "/gallery/11", 11); a.Variant(1, "Shared Garden Part Two", "/gallery/12", 12); a.Write(c => c.Execute("UPDATE source_config SET name='E-Hentai'"));
            Check.Throws<InvalidOperationException>(() => b.Exchange.Preview(a.Dump())); Check.Equal(0L, b.Count()); Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages"));
        });
        suite.Add("catalogue sharing: current gallery profile overrides false foreign hints and enforces criteria", () =>
        {
            using var a = new World(); a.Native("[Mira Vale] The Glass Lantern Part 1 [English]", "/gallery/11", 11); a.Variant(1, "[Mira Vale] The Glass Lantern Part 2 [English]", "/gallery/12", 12);
            var bundle = CatalogueFormat.Decode(a.Dump()); var facts = bundle["entries"]![0]!["sources"]!.AsArray();
            foreach (var fact in facts) { fact!["gallery"] = false; fact["metadata"]!["genre"] = "fantasy, adventure"; }
            var raw = Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle));
            foreach (var disabled in new[] { true, false })
            {
                using var b = new World(); b.Write(c => c.Execute("INSERT INTO source_config(source_id,name,last_seen) VALUES('17','E-Hentai','now')")); var gate = new object(); var evidence = new GalleryEvidenceService(b.Database, gate); var policies = new GalleryPolicyRepository(b.Database, gate);
                Check.Equal(true, evidence.CatalogueAdapter(facts[1]!.AsObject(), [facts[0]!.AsObject()]));
                var policy = GalleryPolicy.Default; policy["enabled"] = !disabled;
                if (!disabled) { policy["criteria"] = new JsonArray("tags"); policy["tag_overlap"] = 50; }
                policies.Save("17", policy, 0); Check.Equal(false, evidence.CatalogueAdapter(facts[1]!.AsObject(), [facts[0]!.AsObject()]));
                var exchange = new CatalogueExchange(b.Database, gate, new SupportPrivacy(), galleryEvidence: evidence.CatalogueAdapter); var preview = exchange.Preview(raw); Check.Equal(2, preview["added"]!.GetValue<int>());
                Check.Equal(2, exchange.Apply(S(preview, "id"), S(preview, "revision"))["added"]!.GetValue<int>()); Check.Equal(2L, b.Count()); Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM source_matches")); Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM source_variants"));
            }
        });
        suite.Add("catalogue sharing: all eight saved manual decision kinds survive typed export", () =>
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
            var bundle = CatalogueFormat.Decode(a.Dump()); Check.Equal(8, bundle["decisions"]!.AsArray().Select(item => S(item!, "kind")).Distinct(StringComparer.Ordinal).Count());
            Check.True(!CatalogueFormat.Canonical(bundle).Contains("local-group", StringComparison.Ordinal));
            using var b = new World(); Add(b, Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle))); Check.Equal("needs-review", b.Scalar("SELECT decision FROM portable_identity_reviews") as string);
            var before = b.Scalar("SELECT COUNT(*) FROM catalogue_decisions"); Add(b, b.Dump()); Check.Equal(before, b.Scalar("SELECT COUNT(*) FROM catalogue_decisions"));
        });
        suite.Add("catalogue sharing: structural review cannot claim success without real journalled task owner", () =>
        {
            using var a = new World(); using var b = new World(); a.Native("Imported target", "/one", 11); var bundle = CatalogueFormat.Decode(a.Dump()); var uid = S(bundle["entries"]![0]!, "uid");
            b.Native("Local counterpart", "/one", 900); b.Native("Other source record", "/other", 901);
            var other = (JsonObject)bundle["entries"]![0]!.DeepClone(); other["uid"] = CatalogueFormat.Uid(); other["aliases"] = new JsonArray(S(other, "uid")); other["lineage"] = new JsonArray(); other["sources"]![0]!["reference"] = PortableIdentity.RecordId("17", "/other"); other["references"] = new JsonArray(PortableIdentity.RecordId("17", "/other")); bundle["entries"]!.AsArray().Add(other);
            bundle["decisions"]!.AsArray().Add(new JsonObject { ["uid"] = CatalogueFormat.Uid("decision"), ["kind"] = "group", ["entries"] = new JsonArray(uid), ["records"] = new JsonArray(bundle["entries"]![0]!["sources"]![0]!.DeepClone(), other["sources"]![0]!.DeepClone()), ["value"] = new JsonObject() });
            Add(b, Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle)));
            var choice = b.Exchange.Decisions()["items"]!.AsArray().Single(item => S(item!, "kind") == "group")!; Check.Equal("pending", S(choice, "state"));
            Check.Throws<InvalidOperationException>(() => b.Exchange.ApplyDecision(S(choice, "uid"), S(choice, "digest"), S(choice, "revision"), taskId: "visible-task")); Check.Equal(2L, b.Count()); Check.Equal(0L, b.Scalar("SELECT COUNT(*) FROM manual_match_groups"));
            var ignored = b.Exchange.ApplyDecision(S(choice, "uid"), S(choice, "digest"), S(choice, "revision"), "ignore"); Check.Equal("ignored", S(ignored, "state"));
        });
        suite.Add("catalogue sharing: stage expiry and four-stage bound survive persistent reopen", () =>
        {
            using var a = new World(); a.Native("Fixture first work", "/one", 11); using var b = new World(); var raw = a.Dump();
            for (var i = 0; i < 6; i++) b.Exchange.Preview(raw); Check.Equal(4L, b.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages"));
            var stage = b.Exchange.Preview(raw); Check.Equal(200, b.Exchange.Stage(S(stage, "id"), limit: 200)["limit"]!.GetValue<int>()); Check.Throws<ArgumentException>(() => b.Exchange.Stage(S(stage, "id"), limit: 201));
            b.Write(c => c.Execute("UPDATE catalogue_exchange_stages SET created_at=0 WHERE id=$p0", ("$p0", S(stage, "id")))); Check.Throws<ArgumentException>(() => b.Exchange.Stage(S(stage, "id")));
            var added = Add(b, raw); Check.Equal(1, added["added"]!.GetValue<int>()); var before = b.Dump();
            using var reopened = new SqliteDatabase(new AutoSourcarrSettings { DataDir = b.Root }); var exchange = new CatalogueExchange(reopened, new object(), new SupportPrivacy()); exchange.Initialize();
            var repeat = exchange.Preview(raw); Check.True(repeat["already_applied"]!.GetValue<bool>()); Check.Equal(0, exchange.Apply(S(repeat, "id"), S(repeat, "revision"))["added"]!.GetValue<int>()); Check.True(before.Length > 0);
        });
        suite.Add("catalogue sharing: source-scoped split lineage retains group history and original IDs", () =>
        {
            using var a = new World(); a.Native("Fixture grouped work", "/one", 11); a.Variant(1, "Fixture part two", "/two", 12); var bundle = CatalogueFormat.Decode(a.Dump()); var root = S(bundle["entries"]![0]!, "uid");
            a.Write(c =>
            {
                c.Execute("INSERT INTO entries(preferred_title,created_at,updated_at) VALUES('Fixture split work','now','now')"); c.Execute("UPDATE source_variants SET entry_id=2 WHERE source_manga_id=12");
                CatalogueIdentity.Split(c, 1, 2, new HashSet<(string, long)> { ("17", 12) }); return 0;
            });
            Check.Equal(2, new PortableIdentityResolver(a.Database).Resolve(root)["total"]!.GetValue<int>()); var output = CatalogueFormat.Decode(a.Dump());
            foreach (var item in output["entries"]!.AsArray()) Check.Equal(1, new PortableIdentityResolver(a.Database).Resolve(S(item!, "uid"))["total"]!.GetValue<int>());
            Check.Equal(11L, a.Scalar("SELECT source_manga_id FROM source_matches")); Check.Equal(12L, a.Scalar("SELECT source_manga_id FROM source_variants"));
        });
        suite.Add("catalogue HTTP: admin authorization and exact upload bounds precede body work", () =>
        {
            using var setup = new HttpSetup();
            Check.Equal(HttpStatusCode.Unauthorized, setup.Client.GetAsync(HttpRoot + "/status").GetAwaiter().GetResult().StatusCode);
            using var reader = new HttpRequestMessage(HttpMethod.Get, HttpRoot + "/status"); reader.Headers.Add("X-AutoSourcarr-Api-Key", "reader"); Check.Equal(HttpStatusCode.Unauthorized, setup.Client.SendAsync(reader).GetAwaiter().GetResult().StatusCode);
            var oversized = setup.Server.SendAsync(context => { context.Request.Method = "POST"; context.Request.Path = HttpRoot + "/preview"; context.Request.Headers["X-Admin-Token"] = "admin"; context.Request.ContentLength = CatalogueFormat.MaximumBytes + 1L; context.Request.Body = new NoRead(); }).GetAwaiter().GetResult();
            Check.Equal(413, oversized.Response.StatusCode);
            var chunked = setup.Server.SendAsync(context => { context.Request.Method = "POST"; context.Request.Path = HttpRoot + "/preview"; context.Request.Headers["X-Admin-Token"] = "admin"; context.Request.Body = new SizeStream(CatalogueFormat.MaximumBytes + 1L); }).GetAwaiter().GetResult(); Check.Equal(413, chunked.Response.StatusCode);
            Check.Equal(0L, setup.World.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages")); Check.Equal(0, setup.Unexpected);
        });
        suite.Add("catalogue HTTP: only preview relaxes server metadata and denied/oversized bodies stay unread", () =>
        {
            using var setup = new HttpSetup();
            foreach (var mode in new[] { "unauthorized", "reader", "oversized", "unrelated" })
            {
                var feature = new UploadLimit(); using var body = new NoRead();
                var context = setup.Server.SendAsync(context =>
                {
                    context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature); context.Request.Method = "POST";
                    context.Request.Path = HttpRoot + (mode == "unrelated" ? "/apply" : "/preview"); context.Request.Body = body;
                    if (mode == "reader") context.Request.Headers["X-AutoSourcarr-Api-Key"] = "reader";
                    if (mode == "oversized") { context.Request.Headers["X-Admin-Token"] = "admin"; context.Request.ContentLength = CatalogueFormat.MaximumBytes + 1L; }
                }).GetAwaiter().GetResult();
                Check.Equal(mode == "oversized" ? 413 : 401, context.Response.StatusCode);
                var metadata = context.GetEndpoint()!.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
                if (mode == "unrelated") Check.True(metadata is null); else Check.True(metadata is not null && metadata.MaxRequestBodySize is null);
                // The framework metadata assignment occurs before the auth filter;
                // denied requests must still perform no body or domain work.
                Check.Equal(mode == "unrelated" ? 0 : 1, feature.Writes);
                if (mode == "unrelated") Check.Equal(30_000_000L, feature.MaxRequestBodySize); else Check.True(feature.MaxRequestBodySize is null);
                Check.Equal(0, body.Reads);
            }
            Check.Equal(0L, setup.World.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages")); Check.Equal(0, setup.Unexpected);
        });
        suite.Add("catalogue HTTP: exact64MiB unknown-length payload reaches format validation and max-plus-one stops", () =>
        {
            using var setup = new HttpSetup();
            foreach (var extra in new[] { 0, 1 })
            {
                var feature = new UploadLimit(); using var body = new SizeStream(CatalogueFormat.MaximumBytes + (long)extra, () => Check.True(feature.MaxRequestBodySize is null));
                var context = setup.Server.SendAsync(context =>
                {
                    context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature); context.Request.Method = "POST"; context.Request.Path = HttpRoot + "/preview";
                    context.Request.Headers["X-Admin-Token"] = "admin"; context.Request.Body = body;
                }).GetAwaiter().GetResult();
                Check.Equal(extra == 0 ? 409 : 413, context.Response.StatusCode); Check.Equal(CatalogueFormat.MaximumBytes + (long)extra, body.BytesRead);
                Check.Equal(2, feature.Writes); Check.True(feature.MaxRequestBodySize is null); // framework metadata, then authorized bounded reader
            }
            Check.Equal(0L, setup.World.Scalar("SELECT COUNT(*) FROM catalogue_exchange_stages")); Check.Equal(0, setup.Unexpected);
        });
        suite.Add("catalogue HTTP: strict uploads, stages, apply and portable large-ID resolution use real SQLite", () =>
        {
            using var original = new World(); original.Native("Original published fixture", "/作品", 112); var raw = original.Dump(); var uid = S(CatalogueFormat.Decode(raw)["entries"]![0]!, "uid");
            using var setup = new HttpSetup(); setup.World.Write(c => c.Execute("INSERT INTO entries(id,preferred_title,created_at,updated_at) VALUES(9007199254740993,'Exact local work','now','now')"));
            foreach (var damaged in new[] { new byte[] { 255 }, Encoding.UTF8.GetBytes("{\"format\":1,\"format\":2}"), Encoding.UTF8.GetBytes("{\"format\":NaN}"), new byte[] { 80, 75, 3, 4 } }) Check.Equal(HttpStatusCode.Conflict, setup.SendBytes(HttpRoot + "/preview", damaged).StatusCode);
            var preview = Body(setup.SendBytes(HttpRoot + "/preview", raw)); var stageId = S(preview, "id"); var revision = S(preview, "revision");
            var stage = Body(setup.Send(HttpMethod.Get, HttpRoot + "/stages/" + stageId + "?limit=200")); Check.Equal("preview", S(stage, "state")); Check.Equal(200, stage["limit"]!.GetValue<int>());
            Check.Equal(HttpStatusCode.UnprocessableEntity, setup.Send(HttpMethod.Get, HttpRoot + "/stages/" + stageId + "?limit=201").StatusCode);
            var added = Body(setup.Send(HttpMethod.Post, HttpRoot + "/apply", new() { ["id"] = stageId, ["revision"] = revision })); Check.Equal(1, added["added"]!.GetValue<int>()); Check.Equal(9007199254740994L, added["entry_ids"]![0]!.GetValue<long>());
            var resolved = Body(setup.Send(HttpMethod.Get, "/api/admin/portable-identities/resolve?reference=" + Uri.EscapeDataString(uid))); Check.Equal(9007199254740994L, resolved["items"]![0]!["id"]!.GetValue<long>());
            Check.Equal(HttpStatusCode.OK, setup.Send(HttpMethod.Get, HttpRoot + "/records").StatusCode); Check.Equal(HttpStatusCode.OK, setup.Send(HttpMethod.Get, HttpRoot + "/decisions").StatusCode);
            Check.Equal(HttpStatusCode.UnprocessableEntity, setup.SendBytes(HttpRoot + "/export", Encoding.UTF8.GetBytes("{\"after\":9007199254740993.0}")).StatusCode);
            var exported = setup.Send(HttpMethod.Post, HttpRoot + "/export", new() { ["after"] = 9007199254740993L, ["limit"] = 1 }); Check.Equal(HttpStatusCode.OK, exported.StatusCode); Check.Equal("application/json", exported.Content.Headers.ContentType!.MediaType);
            var exportedBody = Body(exported); var versions = exportedBody["catalogue"]!["entries"]!.AsArray();
            // limit selects one local entry; the released format also forwards its retained original version.
            Check.Equal(2, exportedBody["entries"]!.GetValue<int>()); Check.Equal(2, versions.Count); Check.Equal(1, versions.Select(item => S(item!, "uid")).Distinct(StringComparer.Ordinal).Count());
            var retained = versions.Single(item => item!["titles"]!.AsArray().Count == 0)!; var projected = versions.Single(item => item!["titles"]!.AsArray().Count == 1)!;
            Check.Equal(uid, S(projected, "uid")); Check.Equal("Original published fixture", projected["titles"]![0]!.GetValue<string>());
            Check.Equal(CatalogueFormat.Digest(CatalogueFormat.Decode(raw)["entries"]![0]), CatalogueFormat.Digest(retained));
            Check.Equal(CatalogueFormat.Canonical(retained["sources"]), CatalogueFormat.Canonical(projected["sources"]));
            Check.Equal(2L, setup.World.Count()); Check.Equal("Exact local work", setup.World.Scalar("SELECT preferred_title FROM entries WHERE id=9007199254740993") as string);
            Check.Equal(1L, setup.World.Scalar("SELECT COUNT(*) FROM catalogue_import_records")); Check.Equal(1L, setup.World.Scalar("SELECT COUNT(*) FROM titles WHERE entry_id=9007199254740994")); Check.Equal(0L, setup.World.Scalar("SELECT COUNT(*) FROM source_matches"));
            preview = Body(setup.SendBytes(HttpRoot + "/preview", raw)); setup.World.ManualTitle(9007199254740994L, "A newer local choice"); Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, HttpRoot + "/apply", new() { ["id"] = S(preview, "id"), ["revision"] = S(preview, "revision") }).StatusCode);
            Check.Equal(0, setup.Unexpected);
        });
        suite.Add("catalogue HTTP: reviewed choices own actual tasks and unsupported structure cannot succeed", () =>
        {
            var structure = new IncompleteStructure(); using var setup = new HttpSetup(structure); setup.World.Native("First native owner", "/one", 11); setup.World.Native("Second native owner", "/two", 12); var choice = PendingGroup(setup.World);
            var body = new JsonObject { ["uid"] = S(choice, "uid"), ["digest"] = S(choice, "digest"), ["revision"] = S(choice, "revision"), ["action"] = "apply" };
            Check.Equal(HttpStatusCode.Conflict, setup.Send(HttpMethod.Post, HttpRoot + "/decisions/review", new() { ["uid"] = S(choice, "uid"), ["digest"] = S(choice, "digest"), ["revision"] = new string('0', 64), ["action"] = "apply" }).StatusCode);
            var scheduled = Body(setup.Send(HttpMethod.Post, HttpRoot + "/decisions/review", body)); Check.True(scheduled["queued"]!.GetValue<bool>()); var taskId = S(scheduled, "task_id");
            Check.True(SpinWait.SpinUntil(() => !setup.Tasks.IsRunning(taskId), TimeSpan.FromSeconds(5))); Check.Equal(1, structure.Calls); Check.True(structure.LastTaskId == taskId); Check.Equal(2L, setup.World.Count());
            var finished = setup.Tasks.History()["items"]!.AsArray().Single(item => S(item!, "id") == taskId)!; Check.Equal("failed", S(finished, "state")); Check.Equal("pending", S(setup.World.Exchange.Decision(S(choice, "uid"), S(choice, "digest")), "state"));
            choice = setup.World.Exchange.Decision(S(choice, "uid"), S(choice, "digest")); var ignore = Body(setup.Send(HttpMethod.Post, HttpRoot + "/decisions/review", new() { ["uid"] = S(choice, "uid"), ["digest"] = S(choice, "digest"), ["revision"] = S(choice, "revision"), ["action"] = "ignore" })); var ignoredTask = S(ignore, "task_id");
            Check.True(SpinWait.SpinUntil(() => !setup.Tasks.IsRunning(ignoredTask), TimeSpan.FromSeconds(5))); Check.Equal("ignored", S(setup.World.Exchange.Decision(S(choice, "uid"), S(choice, "digest")), "state")); Check.Equal(1, structure.Calls); Check.Equal(0, setup.Unexpected);
        });
        suite.Add("catalogue tasks: cancellation and stop retain physical reviewed-domain ownership", () =>
        {
            using var structure = new BlockingStructure(); using var world = new World(structure); world.Native("First native owner", "/one", 11); world.Native("Second native owner", "/two", 12); var choice = PendingGroup(world);
            var tasks = new TaskRegistry(); var lanes = new WholeTaskCoordinator(new AutoSourcarrSettings(), tasks); var io = new BoundedWorkScheduler(1, 1); var reviews = new CatalogueReviewTasks(world.Exchange, tasks, lanes, io);
            Task? stopped = null;
            try
            {
                var scheduled = reviews.Schedule(S(choice, "uid"), S(choice, "digest"), S(choice, "revision")); var taskId = S(scheduled, "task_id"); Check.True(structure.Entered.Wait(TimeSpan.FromSeconds(5)), "Reviewed callback did not enter");
                Check.True(reviews.Schedule(S(choice, "uid"), S(choice, "digest"), S(choice, "revision"))["already_running"]!.GetValue<bool>());
                tasks.RequestCancelAsync(taskId).GetAwaiter().GetResult(); stopped = reviews.StopAsync(); Check.True(!stopped.IsCompleted); Check.Equal(1, io.Snapshot.Running); Check.True(!reviews.Schedule(S(choice, "uid"), S(choice, "digest"), S(choice, "revision"))["queued"]!.GetValue<bool>());
            }
            finally { structure.Release.Set(); (stopped ?? reviews.StopAsync()).GetAwaiter().GetResult(); reviews.DisposeAsync().AsTask().GetAwaiter().GetResult(); lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); io.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            Check.Equal(2L, world.Count()); Check.Equal(0L, world.Scalar("SELECT COUNT(*) FROM manual_match_groups"));
        });
    }
    private static string S(JsonNode node, string key) => node[key]!.GetValue<string>();
    private static JsonObject Add(World world, byte[] raw) { var preview = world.Exchange.Preview(raw); return world.Exchange.Apply(S(preview, "id"), S(preview, "revision")); }
    private static void Oracle()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("catalogue-sharing.json"))); var fixture = document.RootElement; var count = 0;
        // Compare parsed JSON values, because the fixture writer intentionally uses ASCII escapes.
        foreach (var row in fixture.GetProperty("source_paths").EnumerateArray())
        { count++; if (row.GetProperty("valid").GetBoolean()) Check.Equal(row.GetProperty("value").GetString(), PortableIdentity.SourcePath(row.GetProperty("input").GetString()!)); else Check.Throws<ArgumentException>(() => PortableIdentity.SourcePath(row.GetProperty("input").GetString()!)); }
        foreach (var row in fixture.GetProperty("record_ids").EnumerateArray())
        { count++; if (row.GetProperty("valid").GetBoolean()) Check.Equal(row.GetProperty("value").GetString(), PortableIdentity.RecordId(row.GetProperty("source").GetString()!, row.GetProperty("url").GetString()!)); else Check.Throws<ArgumentException>(() => PortableIdentity.RecordId(row.GetProperty("source").GetString()!, row.GetProperty("url").GetString()!)); }
        foreach (var row in fixture.GetProperty("parse").EnumerateArray())
        {
            count++; var input = row.GetProperty("input").GetString()!; if (!row.GetProperty("valid").GetBoolean()) { Check.Throws<ArgumentException>(() => PortableIdentity.Parse(input)); continue; }
            var result = PortableIdentity.Parse(input); var values = row.GetProperty("value"); Check.Equal(values[0].GetString(), result.Scope); Check.Equal(values[1].GetString(), result.Provider); Check.Equal(values[2].GetString(), result.ExternalId); Check.Equal(values[3].ValueKind == JsonValueKind.Null ? null : values[3].GetString(), result.Path);
        }
        foreach (var row in fixture.GetProperty("gallery").EnumerateArray())
        {
            count++; var value = row.GetProperty("input"); string? Field(string key) => value.TryGetProperty(key, out var field) ? field.GetString() : null;
            Check.Equal(row.GetProperty("expected").ValueKind == JsonValueKind.Null ? null : row.GetProperty("expected").GetString(), PortableIdentity.GalleryReference(Field("source_manga_url"), Field("real_url"), Field("base_url")));
        }
        foreach (var row in fixture.GetProperty("descriptions").EnumerateArray())
        { count++; Check.Equal(CatalogueFormat.Canonical(JsonNode.Parse(row.GetProperty("expected").GetRawText())), CatalogueFormat.Canonical(PortableIdentity.Describe(JsonNode.Parse(row.GetProperty("input").GetRawText())!.AsObject()))); }
        foreach (var row in fixture.GetProperty("wire").EnumerateArray())
        {
            count++; var raw = Convert.FromBase64String(row.GetProperty("base64").GetString()!); if (!row.GetProperty("valid").GetBoolean()) { Check.Throws<ArgumentException>(() => CatalogueFormat.Decode(raw)); continue; }
            var result = CatalogueFormat.Decode(raw); Check.Equal(row.GetProperty("canonical").GetString(), CatalogueFormat.Canonical(result)); Check.Equal(row.GetProperty("digest").GetString(), CatalogueFormat.Digest(result));
        }
        foreach (var row in fixture.GetProperty("uuid").EnumerateArray()) { count++; Check.Equal(row.GetProperty("expected").GetString(), PortableUuid.V5(Guid.Parse(row.GetProperty("namespace").GetString()!), row.GetProperty("name").GetString()!).ToString("D")); }
        Check.Equal(150, count); Console.WriteLine("Catalogue sharing released oracle cases " + count);
    }
    private sealed class World : IDisposable
    {
        private readonly Sandbox _sandbox = new(); public SqliteDatabase Database { get; } public CatalogueExchange Exchange { get; } public string Root => _sandbox.Path;
        public World(ICatalogueReviewedStructure? structure = null) { Database = new(new AutoSourcarrSettings { DataDir = Root }); new SchemaMigrator(Database).Initialize(); Exchange = new(Database, new object(), new SupportPrivacy(), structure: structure); Exchange.Initialize(); }
        public void Write(Func<DatabaseSession, int> action) => Database.Write(DatabaseFile.Catalog, action);
        public object? Scalar(string sql) => Database.Read(DatabaseFile.Catalog, c => c.Scalar(sql));
        public long Count() => (long)Scalar("SELECT COUNT(*) FROM entries")!;
        public byte[] Dump() => Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(Exchange.Export()["catalogue"]));
        public long Native(string title, string path, long mangaId) => Database.Write(DatabaseFile.Catalog, c =>
        {
            c.Execute("INSERT OR IGNORE INTO source_config(source_id,name,last_seen) VALUES('17','Native source','now')"); c.Execute("INSERT INTO entries(preferred_title,created_at,updated_at) VALUES($p0,'now','now')", ("$p0", title)); var id = (long)c.Scalar("SELECT last_insert_rowid()")!;
            c.Execute("INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_manga_url,source_title,last_checked) VALUES($p0,'17',$p1,$p2,$p3,'now')", ("$p0", id), ("$p1", mangaId), ("$p2", path), ("$p3", title)); return id;
        });
        public void Variant(long id, string title, string path, long mangaId) => Write(c => c.Execute("INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_manga_url,source_title,last_checked) VALUES($p0,'17',$p1,$p2,$p3,'now')", ("$p0", id), ("$p1", mangaId), ("$p2", path), ("$p3", title)));
        public void ManualTitle(long id, string title) => Write(c =>
        { c.Execute("UPDATE entries SET preferred_title=$p0,manual_preferred_title=1 WHERE id=$p1", ("$p0", title), ("$p1", id)); return c.Execute("INSERT INTO preferred_title_overrides VALUES($p0,$p1,'now') ON CONFLICT(entry_id) DO UPDATE SET title=excluded.title", ("$p0", id), ("$p1", title)); });
        public void Policy(bool enabled) => Write(c => c.Execute("INSERT INTO gallery_policies(source_id,revision,policy_json,updated_at) VALUES('17',1,$p0,'now') ON CONFLICT(source_id) DO UPDATE SET revision=revision+1,policy_json=excluded.policy_json", ("$p0", CatalogueFormat.Canonical(new JsonObject { ["profile"] = "auto", ["enabled"] = enabled, ["criteria"] = new JsonArray(), ["title_minimum"] = .94, ["tag_similarity"] = .5, ["tag_overlap"] = 2 }))));
        public void Dispose() { Database.Dispose(); _sandbox.Dispose(); }
    }
    private const string HttpRoot = "/api/admin/catalog-sharing";
    private static JsonObject Body(HttpResponseMessage response) => JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
    private static JsonObject PendingGroup(World world)
    {
        var bundle = CatalogueFormat.Decode(world.Dump()); var entries = bundle["entries"]!.AsArray(); var uid = CatalogueFormat.Uid("decision");
        bundle["decisions"]!.AsArray().Add(new JsonObject { ["uid"] = uid, ["kind"] = "group", ["entries"] = new JsonArray(S(entries[0]!, "uid")), ["records"] = new JsonArray(entries[0]!["sources"]![0]!.DeepClone(), entries[1]!["sources"]![0]!.DeepClone()), ["value"] = new JsonObject() });
        Add(world, Encoding.UTF8.GetBytes(CatalogueFormat.Canonical(bundle))); return world.Exchange.Decisions()["items"]!.AsArray().Single(item => S(item!, "uid") == uid)!.AsObject();
    }
    private sealed class HttpSetup : IDisposable
    {
        private readonly SemaphoreSlim _admin = new(1, 1); private readonly BoundedWorkScheduler _io = new(2, 8); private readonly WholeTaskCoordinator _lanes; private readonly CatalogueReviewTasks _reviews; private readonly IHost _host;
        public World World { get; } public TaskRegistry Tasks { get; } = new(); public TestServer Server { get; } public HttpClient Client { get; } public int Unexpected;
        public HttpSetup(ICatalogueReviewedStructure? structure = null)
        {
            World = new(structure); var settings = new AutoSourcarrSettings { AdminToken = "admin", AutosourcarrReaderApiToken = "reader" }; var privacy = new SupportPrivacy();
            _lanes = new(settings, Tasks); _reviews = new(World.Exchange, Tasks, _lanes, _io);
            _host = new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            { app.UseAutoSourcarrHttp("0.4.3-migration", () => settings, (_, _) => { Unexpected++; return "unexpected"; }, new RequestAdmissionGate(), _lanes, Tasks, privacy, _ => { }); app.UseEndpoints(endpoints => endpoints.MapCatalogueSharing(World.Exchange, new PortableIdentityResolver(World.Database), _reviews, () => settings, _admin, _io)); })).Build();
            _host.StartAsync().GetAwaiter().GetResult(); Server = _host.GetTestServer(); Client = Server.CreateClient();
        }
        public HttpResponseMessage Send(HttpMethod method, string path, JsonObject? body = null)
        { using var request = new HttpRequestMessage(method, path); request.Headers.Add("X-Admin-Token", "admin"); if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"); return Client.SendAsync(request).GetAwaiter().GetResult(); }
        public HttpResponseMessage SendBytes(string path, byte[] raw)
        { using var request = new HttpRequestMessage(HttpMethod.Post, path); request.Headers.Add("X-Admin-Token", "admin"); request.Content = new ByteArrayContent(raw); return Client.SendAsync(request).GetAwaiter().GetResult(); }
        public void Dispose() { Client.Dispose(); _host.Dispose(); _reviews.DisposeAsync().AsTask().GetAwaiter().GetResult(); _lanes.DisposeAsync().AsTask().GetAwaiter().GetResult(); _io.DisposeAsync().AsTask().GetAwaiter().GetResult(); _admin.Dispose(); World.Dispose(); }
    }
    private sealed class IncompleteStructure : ICatalogueReviewedStructure
    {
        public int Calls; public string LastTaskId = "";
        // Controlled callback fixture: its scenarios have no pending structural journal.
        public void ReconcilePending(CancellationToken cancellationToken) => cancellationToken.ThrowIfCancellationRequested();
        public void ApplyReviewed(string decisionUid, string decisionDigest, string revision, JsonObject decision, IReadOnlyList<CatalogueNativeRecord> records, IReadOnlyList<long> targetIds, string taskId, CancellationToken cancellationToken) { Calls++; LastTaskId = taskId; }
    }
    private sealed class BlockingStructure : ICatalogueReviewedStructure, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        // Controlled physical-ownership fixture: its scenario has no prior pending journal.
        public void ReconcilePending(CancellationToken cancellationToken) => cancellationToken.ThrowIfCancellationRequested();
        public void ApplyReviewed(string decisionUid, string decisionDigest, string revision, JsonObject decision, IReadOnlyList<CatalogueNativeRecord> records, IReadOnlyList<long> targetIds, string taskId, CancellationToken cancellationToken) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture reviewed callback was not released"); cancellationToken.ThrowIfCancellationRequested(); }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
    private sealed class NoRead : Stream
    {
        public int Reads; public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private Exception RejectedRead() { Reads++; return new InvalidOperationException("Rejected sharing body must not be read"); }
        public override int Read(byte[] buffer, int offset, int count) => throw RejectedRead();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(RejectedRead());
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(RejectedRead());
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class SizeStream(long remaining, Action? beforeRead = null) : Stream
    {
        public long BytesRead;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { beforeRead?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); var count = (int)Math.Min(buffer.Length, remaining); buffer.Span[..count].Fill(32); remaining -= count; BytesRead += count; return ValueTask.FromResult(count); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class UploadLimit : IHttpMaxRequestBodySizeFeature
    {
        private long? _maximum = 30_000_000; public int Writes; public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get => _maximum; set { Writes++; _maximum = value; } }
    }
}
