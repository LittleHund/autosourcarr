using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

public sealed partial class CatalogueExchange
{
    public JsonObject Status() => _database.Read(DatabaseFile.Catalog, c => new JsonObject
    {
        ["origin"] = c.Scalar("SELECT uid FROM catalogue_origin WHERE id=1") as string,
        ["entries"] = (long)c.Scalar("SELECT COUNT(*) FROM entries")!, ["files_added"] = (long)c.Scalar("SELECT COUNT(*) FROM catalogue_exchange_receipts")!,
        ["retained_records"] = (long)c.Scalar("SELECT COUNT(*) FROM catalogue_import_records WHERE state='review'")!, ["pending_decisions"] = (long)c.Scalar("SELECT COUNT(*) FROM catalogue_decisions WHERE state NOT IN ('applied','ignored')")!,
        ["note"] = "Catalogue sharing adds records and portable decisions. Existing local reader IDs, accounts, progress, downloaded files and source settings stay local."
    });
    public JsonObject Decisions(int offset = 0, int limit = 30)
    {
        Page(offset, limit); lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, c =>
        { var native = _native.Read(c); return new JsonObject { ["items"] = Objects(c.Query("SELECT * FROM catalogue_decisions ORDER BY rowid DESC LIMIT $p0 OFFSET $p1", P(limit, offset)).Select(row => DecisionView(c, row, native))), ["total"] = (long)c.Scalar("SELECT COUNT(*) FROM catalogue_decisions")!, ["offset"] = offset, ["limit"] = limit }; });
    }
    public JsonObject Decision(string uid, string digest)
    {
        lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, c => DecisionView(c, FindDecision(c, uid, digest), _native.Read(c)));
    }
    private static DatabaseRow FindDecision(DatabaseSession c, string uid, string digest) => c.Query("SELECT * FROM catalogue_decisions WHERE uid=$p0 AND digest=$p1", P(uid, digest)).FirstOrDefault() ?? throw new KeyNotFoundException(uid);
    private static JsonObject DecisionView(DatabaseSession c, DatabaseRow row, IReadOnlyList<CatalogueNativeRecord> native)
    {
        var choice = JsonNode.Parse(row.Text("decision_json"))!.AsObject(); var status = row.Text("state") is "applied" or "ignored" ? CatalogueDecisions.Status(row.Text("state"), row.Text("message")) : CatalogueDecisions.Inspect(c, choice, native);
        var mapped = choice.A("entries").Select(uid => CatalogueIdentity.Targets(c, [uid!.GetValue<string>()])).ToList(); var resolved = choice.A("records").Select(fact => CatalogueNativeRecords.Resolve(native, fact!.AsObject())).ToList();
        var proof = new JsonArray();
        foreach (var record in resolved.SelectMany(records => records))
        { var binding = c.Query("SELECT * FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(record.SourceId, record.MangaId)).FirstOrDefault(); proof.Add(new JsonArray(record.EntryId, record.SourceId, record.MangaId, record.Fact.S("reference"), binding is null ? null : Row(binding))); }
        var canApply = status.S("state") == "ready" || status.S("state") == "pending" && choice.S("kind") is "group" or "source-match" or "separate" && resolved.Count != 0 && resolved.All(records => records.Count == 1);
        if (choice.S("kind") == "group" && resolved.SelectMany(records => records).Any(record => record.Fact["gallery"]!.GetValue<bool>() && !CatalogueNativeRecords.GalleryEnabled(c, record.SourceId))) canApply = false;
        var revision = CatalogueFormat.Digest(new JsonArray(Row(row), status.DeepClone(), new JsonArray(mapped.Select(values => (JsonNode?)Numbers(values)).ToArray()), proof, CatalogueNativeRecords.Policies(c)));
        var view = new JsonObject { ["uid"] = row.Text("uid"), ["digest"] = row.Text("digest"), ["kind"] = choice.S("kind"), ["decision"] = choice, ["can_apply"] = canApply,
            ["entry_ids"] = Numbers(mapped.SelectMany(values => values).Distinct().Order()), ["local_records"] = Objects(resolved.SelectMany(values => values).Select(record => new JsonObject { ["entry_id"] = record.EntryId, ["title"] = record.Row.Text("source_title"), ["source"] = Nonempty(record.Row.Text("source_name"), record.SourceId) })), ["revision"] = revision };
        foreach (var pair in status) view[pair.Key] = pair.Value?.DeepClone(); return view;
    }
    /// <summary>Applies or ignores one retained shared choice. Structural choices go through the journalled
    /// <see cref="ICatalogueReviewedStructure"/> merge/split owner; a local manual binding, manual separate record or
    /// Separate review is never overridden by an imported choice.</summary>
    public JsonObject ApplyDecision(string uid, string digest, string revision, string action = "apply", string? taskId = null, DecisionActor? actor = null, CancellationToken cancellationToken = default)
    {
        CatalogueFormat.CheckUid(uid, "decision"); if (action is not ("apply" or "ignore")) throw new ArgumentException("Choose Apply or Keep local");
        lock (_sourceWriteGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_structure is not null) _structure.ReconcilePending(cancellationToken);
            else if (_database.Read(DatabaseFile.Catalog, c => Convert.ToInt64(c.Scalar("SELECT (SELECT COUNT(*) FROM pending_entry_merges)+(SELECT COUNT(*) FROM pending_entry_splits)"), System.Globalization.CultureInfo.InvariantCulture)) != 0)
                throw new InvalidOperationException("Catalogue origin recovery owner is unavailable; pending structural work must finish before reviewing another choice");
            JsonObject? structural = null; List<CatalogueNativeRecord>? records = null; List<long>? targets = null;
            var ready = _database.Write(DatabaseFile.Catalog, c =>
            {
                var row = FindDecision(c, uid, digest); var native = _native.Read(c); var view = DecisionView(c, row, native);
                if (view.S("revision") != revision) throw new ArgumentException("Local records or this retained choice changed; refresh before applying");
                if (row.Text("state") is "applied" or "ignored") return view;
                var choice = view.O("decision"); JsonObject? status = null;
                if (action == "ignore") status = CatalogueDecisions.Status("ignored", "Local choices retained; incoming evidence remains in history.");
                else if (view.S("state") == "conflict") throw new ArgumentException(view.S("message"));
                else if (view.S("state") == "ready") status = CatalogueDecisions.ApplyReady(c, choice, native);
                if (status is not null)
                {
                    c.Execute("UPDATE catalogue_decisions SET state=$p0,message=$p1,applied_revision=$p2 WHERE uid=$p3 AND digest=$p4", P(status.S("state"), status.S("message"), revision, uid, digest)); foreach (var pair in status) view[pair.Key] = pair.Value?.DeepClone();
                    if (status.S("state") is "applied" or "ignored")
                        Record(action == "ignore" ? "catalogue.decision.ignore" : "catalogue.decision." + choice.S("kind"), actor, view.A("entry_ids").Select(value => value!.GetValue<long>()), [],
                            status.S("message"), new JsonObject { ["uid"] = uid, ["digest"] = digest, ["state"] = status.S("state") });
                    return view;
                }
                var resolved = choice.A("records").Select(fact => CatalogueNativeRecords.Resolve(native, fact!.AsObject())).ToList();
                if (resolved.Count == 0 || resolved.Any(values => values.Count != 1)) throw new ArgumentException("Resolve each public source reference to exactly one local record before applying this choice");
                records = resolved.Select(values => values[0]).ToList(); targets = CatalogueIdentity.Targets(c, choice.A("entries").Select(item => item!.GetValue<string>()));
                var owners = records.Select(record => record.EntryId).Distinct().ToList(); var kind = choice.S("kind");
                if (owners.Count > 32 || kind == "group" && records.Count > 64) throw new ArgumentException("Review this large group in smaller local groups before applying it");
                if (kind is not ("group" or "source-match" or "separate")) throw new ArgumentException(view.S("message"));
                if (kind == "group" && records.Any(record => c.Scalar("SELECT 1 FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(record.SourceId, record.MangaId)) is not null)) throw new ArgumentException("A local manual source binding is retained; review it before combining imported members");
                Protect(c, kind, records, targets);
                if (_structure is null || string.IsNullOrEmpty(taskId)) throw new InvalidOperationException("A visible reviewed task and the journalled merge/split domain owner are required for this structural choice");
                structural = choice.Copy(); c.Execute("UPDATE catalogue_decisions SET state='pending',message='Applying the reviewed local record mapping',applied_revision=$p0 WHERE uid=$p1 AND digest=$p2", P(revision, uid, digest)); return null;
            });
            if (ready is not null) { _invalidate?.Invoke(); return ready; }
            cancellationToken.ThrowIfCancellationRequested();
            _structure!.ApplyReviewed(uid, digest, revision, structural!, records!, targets!, taskId!, cancellationToken);
            // The real domain operation must have committed its own recoverable journals. Re-read
            // actual local ownership; a callback returning without doing the work cannot claim success.
            var result = _database.Write(DatabaseFile.Catalog, c =>
            {
                var native = _native.Read(c); var status = CatalogueDecisions.ApplyReady(c, structural!, native);
                if (structural!.S("kind") == "source-match" && status.S("state") == "conflict")
                {
                    var mapped = CatalogueIdentity.Targets(c, structural.A("entries").Select(item => item!.GetValue<string>())); var resolved = structural.A("records").Select(fact => CatalogueNativeRecords.Resolve(native, fact!.AsObject())).ToList();
                    if (mapped.Count == 1 && resolved.Count != 0 && resolved.All(values => values.Count == 1 && values[0].EntryId == mapped[0])) status = CatalogueDecisions.Status("applied", "Verified local record linked; its existing manual binding was retained.");
                }
                if (status.S("state") == "applied")
                {
                    var mapped = CatalogueIdentity.Targets(c, structural.A("entries").Select(item => item!.GetValue<string>()));
                    if (mapped.Count == 1)
                    {
                        var actual = native.Where(record => record.EntryId == mapped[0]).Select(record => (record.SourceId, record.Fact.S("reference"))).ToHashSet();
                        foreach (var row in c.Query("SELECT uid,digest,record_json FROM catalogue_import_records WHERE state='review'"))
                        {
                            var record = JsonNode.Parse(row.Text("record_json"))!.AsObject();
                            if (!structural.A("entries").Any(item => item!.GetValue<string>() == record.S("uid")) || !Anchors(record).IsSubsetOf(actual)) continue;
                            Associate(c, mapped[0], record); c.Execute("UPDATE catalogue_import_records SET state='imported',message='Mapped through a reviewed local group' WHERE uid=$p0 AND digest=$p1", P(row.Text("uid"), row.Text("digest")));
                        }
                    }
                }
                c.Execute("UPDATE catalogue_decisions SET state=$p0,message=$p1,applied_revision=$p2 WHERE uid=$p3 AND digest=$p4", P(status.S("state"), status.S("message"), revision, uid, digest));
                if (status.S("state") == "applied")
                    Record("catalogue.decision." + structural!.S("kind"), actor, CatalogueIdentity.Targets(c, structural.A("entries").Select(item => item!.GetValue<string>())),
                        records!.Select(record => record.SourceId + ":" + record.MangaId), status.S("message"), new JsonObject { ["uid"] = uid, ["digest"] = digest, ["state"] = "applied" });
                return status;
            });
            _invalidate?.Invoke(); return result;
        }
    }
    /// <summary>
    /// Phase-8 manual decisions win over an imported structural choice: a Separate review between any two involved
    /// entries, a manual separate record for an involved target, or a manual binding that keeps a record on another entry
    /// (or, for a split, on this one) refuses the choice before the structure owner is called.
    /// </summary>
    private static void Protect(DatabaseSession c, string kind, IReadOnlyList<CatalogueNativeRecord> records, IReadOnlyList<long> targets)
    {
        var involved = targets.Concat(records.Select(record => record.EntryId)).Distinct().Order().ToArray();
        if (kind is "group" or "source-match")
            for (var i = 0; i < involved.Length; i++) for (var j = i + 1; j < involved.Length; j++)
                if (c.Scalar("SELECT 1 FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1 AND decision='not_duplicate'", P(involved[i], involved[j])) is not null)
                    throw new ArgumentException("A local Separate decision is retained; an imported choice cannot combine these entries");
        foreach (var record in records)
        {
            if (kind is "group" or "source-match" && targets.Any(target => c.Scalar("SELECT 1 FROM manual_separate_records WHERE target_entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(target, record.SourceId, record.MangaId)) is not null))
                throw new ArgumentException("A local manual separate record is retained; an imported choice cannot attach it");
            var hint = c.Scalar("SELECT entry_id_hint FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(record.SourceId, record.MangaId));
            if (hint is null) continue;
            if (kind == "source-match" && targets.Count == 1 && targets[0] != record.EntryId) throw new ArgumentException("A local manual match keeps this source record on its entry; an imported choice cannot move it");
            if (kind == "separate") throw new ArgumentException("A local manual match keeps this source record on its entry; an imported choice cannot split it");
        }
    }
    private static void Page(int offset, int limit) { if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid catalogue review page"); }
    public JsonObject Records(int offset = 0, int limit = 30)
    {
        Page(offset, limit); lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, c =>
        { var native = _native.Read(c); return new JsonObject { ["items"] = Objects(c.Query("SELECT * FROM catalogue_import_records ORDER BY rowid DESC LIMIT $p0 OFFSET $p1", P(limit, offset)).Select(row => RecordView(c, row, native))), ["total"] = (long)c.Scalar("SELECT COUNT(*) FROM catalogue_import_records")!, ["offset"] = offset, ["limit"] = limit }; });
    }
    private static JsonObject RecordView(DatabaseSession c, DatabaseRow row, IReadOnlyList<CatalogueNativeRecord> native)
    {
        var record = JsonNode.Parse(row.Text("record_json"))!.AsObject(); var resolution = Resolution(c, record); var current = ResolvedRecord(record, resolution); var targets = CatalogueIdentity.Targets(c, [current.S("uid")]);
        var candidates = record.A("sources").SelectMany(fact => CatalogueNativeRecords.Resolve(native, fact!.AsObject())).Select(record => record.EntryId).Distinct().Order().ToList();
        var view = Row(row); view["record"] = record; view["resolved_uid"] = current.S("uid"); view["entry_ids"] = Numbers(targets);
        view["local_candidates"] = Objects(candidates.Select(id => new JsonObject { ["id"] = id, ["title"] = c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(id)) as string }));
        view["can_link"] = targets.Count == 1 && CatalogueIdentity.Unverified(c, targets[0]); view["revision"] = CatalogueFormat.Digest(new JsonArray(Row(row), Numbers(targets), resolution?.DeepClone(), CatalogueNativeRecords.Policies(c), Numbers(candidates))); return view;
    }
    public JsonObject ReviewRecord(string uid, string digest, string revision, string action = "separate", DecisionActor? actor = null)
    {
        if (action is not ("separate" or "ignore")) throw new ArgumentException("Choose Add separately or Keep local"); JsonObject result;
        lock (_sourceWriteGate) result = _database.Write(DatabaseFile.Catalog, c =>
        {
            var row = c.Query("SELECT * FROM catalogue_import_records WHERE uid=$p0 AND digest=$p1", P(uid, digest)).FirstOrDefault() ?? throw new KeyNotFoundException(uid);
            var record = JsonNode.Parse(row.Text("record_json"))!.AsObject(); var resolution = Resolution(c, record); var current = ResolvedRecord(record, resolution); var targets = CatalogueIdentity.Targets(c, [current.S("uid")]); var view = RecordView(c, row, _native.Read(c));
            if (view.S("revision") != revision) throw new ArgumentException("Incoming version or local gallery rules changed; refresh before reviewing");
            if (row.Text("state") != "review") return new JsonObject { ["added"] = 0, ["state"] = row.Text("state"), ["entry_ids"] = Numbers(targets) };
            var created = new List<long>(); string state; string message;
            if (action == "ignore") { resolution = new() { ["ignored"] = true }; state = "ignored"; message = "Local records retained; incoming version kept in history."; }
            else
            {
                foreach (var unit in Units(c, record))
                {
                    var keys = SourceKeys(unit); JsonNode key = keys.Count != 0 ? keys : JsonValue.Create(CatalogueFormat.Digest(unit))!;
                    var chosen = PortableUuid.Entry(unit.S("uid"), "separate:" + CatalogueFormat.Canonical(key));
                    var remap = new JsonObject { ["uid"] = chosen, ["aliases"] = new JsonArray(chosen), ["lineage"] = new JsonArray(new JsonObject { ["uid"] = chosen, ["records"] = Objects(unit.A("sources").Select(fact => new JsonObject { ["source_id"] = fact.S("source_id"), ["reference"] = fact.S("reference") })) }) };
                    var value = ResolvedRecord(unit, new() { ["remap"] = remap.DeepClone() }); var existing = CatalogueIdentity.Targets(c, [chosen]); var id = existing.Count == 1 ? existing[0] : Add(c, value); Associate(c, id, value); created.Add(id);
                    var part = new JsonObject { ["remap"] = remap.DeepClone(), ["source_keys"] = keys.DeepClone() }; c.Execute("INSERT OR REPLACE INTO catalogue_record_resolutions VALUES($p0,$p1,$p2)", P(unit.S("uid"), CatalogueFormat.Digest(unit), CatalogueFormat.Canonical(part))); if (unit.S("uid") == record.S("uid")) resolution = part;
                }
                state = "imported"; message = "Added separately under a new portable identity; original IDs and local records are unchanged.";
            }
            if (resolution is not null) c.Execute("INSERT OR REPLACE INTO catalogue_record_resolutions VALUES($p0,$p1,$p2)", P(uid, digest, CatalogueFormat.Canonical(resolution)));
            Record(action == "ignore" ? "catalogue.record.ignore" : "catalogue.record.separate", actor, created, [], message, new JsonObject { ["uid"] = uid, ["digest"] = digest });
            c.Execute("UPDATE catalogue_import_records SET state=$p0,message=$p1 WHERE uid=$p2 AND digest=$p3", P(state, message, uid, digest)); return new JsonObject { ["added"] = created.Count, ["state"] = state, ["entry_ids"] = Numbers(created) };
        });
        _invalidate?.Invoke(); return result;
    }
    public JsonObject PrepareLink(string uid, string digest, string revision, long target, DecisionActor? actor = null)
    {
        lock (_sourceWriteGate) return _database.Write(DatabaseFile.Catalog, c =>
        {
            var row = c.Query("SELECT * FROM catalogue_import_records WHERE uid=$p0 AND digest=$p1", P(uid, digest)).FirstOrDefault() ?? throw new KeyNotFoundException(uid); var native = _native.Read(c); var view = RecordView(c, row, native);
            if (view.S("revision") != revision) throw new ArgumentException("Local source records changed; refresh before linking");
            var record = JsonNode.Parse(row.Text("record_json"))!.AsObject(); var current = ResolvedRecord(record, Resolution(c, record)); var targets = CatalogueIdentity.Targets(c, [current.S("uid")]); var candidates = record.A("sources").SelectMany(fact => CatalogueNativeRecords.Resolve(native, fact!.AsObject())).Select(record => record.EntryId).Distinct().ToList();
            if (targets.Count != 1 || !CatalogueIdentity.Unverified(c, targets[0]) || !candidates.Contains(target)) throw new ArgumentException("Choose a verified counterpart for one unverified local entry");
            if (candidates.Any(id => id != target)) throw new ArgumentException("The references span several local records; review their grouping first");
            var local = c.Scalar("SELECT title FROM preferred_title_overrides WHERE entry_id=$p0", P(target)) as string; var imported = c.Scalar("SELECT title FROM preferred_title_overrides WHERE entry_id=$p0", P(targets[0])) as string;
            if (local is not null && imported is not null && local != imported) throw new ArgumentException("The two manual titles differ; retain the native local title and review the imported override before linking");
            var fact = current.A("sources").First(fact => CatalogueNativeRecords.Resolve(native, fact!.AsObject()).Any(record => record.EntryId == target));
            var choice = CatalogueFormat.Decision(new JsonObject { ["uid"] = CatalogueFormat.Uid("decision"), ["kind"] = "source-match", ["entries"] = new JsonArray(current.S("uid")), ["records"] = new JsonArray(fact!.DeepClone()), ["value"] = new JsonObject { ["note"] = "Verified local counterpart explicitly selected in Database sharing" } });
            var key = CatalogueFormat.Digest(choice); c.Execute("INSERT INTO catalogue_decisions(uid,digest,decision_json,state,message) VALUES($p0,$p1,$p2,'pending','Verified local source link requested')", P(choice.S("uid"), key, CatalogueFormat.Canonical(choice)));
            Record("catalogue.record.link", actor, [target, targets[0]], [], "Verified local counterpart selected for an imported entry", new JsonObject { ["uid"] = choice.S("uid"), ["digest"] = key, ["record_uid"] = uid }); return DecisionView(c, FindDecision(c, choice.S("uid"), key), native);
        });
    }
}
