using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

public sealed partial class CatalogueExchange
{
    private static HashSet<(string Source, string Reference)> Anchors(JsonObject record) => record.A("sources").Select(fact => (fact.S("source_id"), fact.S("reference"))).ToHashSet();
    private static HashSet<(string Source, string Reference)> ExistingAnchors(DatabaseSession c, IReadOnlyList<CatalogueNativeRecord> native, long id) => c.Query("SELECT source_id,reference FROM catalogue_source_facts WHERE entry_id=$p0", P(id)).Select(row => (row.Text("source_id"), row.Text("reference"))).Concat(native.Where(row => row.EntryId == id).Select(row => (row.SourceId, row.Fact.S("reference")))).ToHashSet();
    private JsonObject Plan(DatabaseSession c, JsonObject bundle)
    {
        var native = _native.Read(c); var byReference = new Dictionary<(string, string), HashSet<long>>();
        void AddReference(string source, string reference, long id) { if (!byReference.TryGetValue((source, reference), out var ids)) byReference[(source, reference)] = ids = []; ids.Add(id); }
        foreach (var record in native) AddReference(record.SourceId, record.Fact.S("reference"), record.EntryId);
        foreach (var row in c.Query("SELECT r.reference,r.source_id,r.entry_id FROM catalogue_source_facts r JOIN entries e ON e.id=r.entry_id")) AddReference(row.Text("source_id"), row.Text("reference"), row.Integer("entry_id"));
        var items = new JsonArray(); var seenPrincipals = new Dictionary<string, (string Digest, HashSet<(string, string)> Sources)>(); var virtualSources = new Dictionary<(string, string), HashSet<string>>(); var virtualUids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in bundle.A("entries"))
        {
            var original = ResolvedRecord(raw!.AsObject(), Resolution(c, raw.AsObject())); var originalOwners = CatalogueIdentity.Targets(c, [original.S("uid")]); var originalRefs = Anchors(original);
            var knownRefs = originalOwners.SelectMany(id => ExistingAnchors(c, native, id)).ToHashSet(); var parentConflict = knownRefs.Count != 0 && originalRefs.Count != 0 && !knownRefs.Overlaps(originalRefs);
            foreach (var incoming in Units(c, original))
            {
                var resolution = Resolution(c, incoming); var unit = CatalogueFormat.Entry(ResolvedRecord(incoming, resolution)); var exact = CatalogueIdentity.Targets(c, [unit.S("uid")]); var refs = Anchors(unit);
                var sourceIds = refs.SelectMany(reference => byReference.GetValueOrDefault(reference) ?? []).ToHashSet(); var virtualIds = refs.SelectMany(reference => virtualSources.GetValueOrDefault(reference) ?? []).ToHashSet(StringComparer.Ordinal);
                var partitioned = incoming.S("uid") != original.S("uid");
                var targets = exact.Count != 0 ? exact : sourceIds.Count != 0 ? sourceIds.Order().ToList() : partitioned ? [] : CatalogueIdentity.Targets(c, unit.A("aliases").Select(uid => uid!.GetValue<string>()));
                if (targets.Count > 1 && sourceIds.Count != 0) targets = targets.Where(sourceIds.Contains).Order().ToList();
                var action = targets.Count == 0 ? "new" : targets.Count == 1 ? "existing" : "review";
                var message = action == "new" ? "New catalogue entry; source references remain unverified." : "Keep the mapped local entry and add references.";
                if (action == "review") message = "References identify several local entries; retained for review without combining them.";
                if (targets.Count == 1 && refs.Count != 0)
                { var previousRefs = ExistingAnchors(c, native, targets[0]); if (previousRefs.Count != 0 && !previousRefs.Overlaps(refs)) { action = "review"; message = "A shared ID was reused with different source identities; keep local data and review this incoming version."; } }
                if (parentConflict && resolution?["remap"] is not JsonObject) { action = "review"; message = "The parent shared ID belongs to different source records; retain this version for review."; }
                if (seenPrincipals.TryGetValue(unit.S("uid"), out var previous) && previous.Digest != CatalogueFormat.Digest(unit) && !previous.Sources.SetEquals(refs))
                { action = "review"; message = "This file reuses one shared ID for differing source records; the additional version is retained for review."; }
                seenPrincipals[unit.S("uid")] = (CatalogueFormat.Digest(unit), refs); string? batchUid = null;
                if (action == "new" && virtualUids.Contains(unit.S("uid"))) { action = "batch"; batchUid = unit.S("uid"); message = "Retain another version of the same incoming entry without replacing its fields."; }
                if (action == "new" && virtualIds.Count != 0)
                { if (virtualIds.Count == 1) { action = "batch"; batchUid = virtualIds.Single(); message = "Use the exact source record already being added from this file."; } else { action = "review"; message = "This incoming group spans several separate records in the same file; retained for review."; } }
                if (action == "new") foreach (var reference in refs) { if (!virtualSources.TryGetValue(reference, out var values)) virtualSources[reference] = values = new(StringComparer.Ordinal); values.Add(unit.S("uid")); }
                if (resolution?["ignored"]?.GetValue<bool>() == true) { action = "ignored"; message = "This incoming version was explicitly ignored on this instance."; }
                if (action is "new" or "batch") virtualUids.Add(unit.S("uid"));
                var proof = new JsonArray();
                foreach (var id in targets)
                {
                    var entry = c.Query("SELECT id,preferred_title,manual_preferred_title FROM entries WHERE id=$p0", P(id)).FirstOrDefault(); var title = c.Query("SELECT title,updated_at FROM preferred_title_overrides WHERE entry_id=$p0", P(id)).FirstOrDefault();
                    proof.Add(new JsonArray(entry is null ? null : Row(entry), title is null ? null : Row(title)));
                }
                items.Add(new JsonObject { ["uid"] = unit.S("uid"), ["group_root"] = partitioned && !parentConflict ? original.S("uid") : null, ["claimed_uid"] = incoming.S("uid"), ["title"] = unit.S("title"), ["action"] = action, ["batch_uid"] = batchUid, ["target_ids"] = Numbers(targets), ["message"] = message, ["record"] = unit, ["incoming"] = incoming.Copy(), ["proof"] = proof });
            }
        }
        var decisions = new JsonArray();
        foreach (var node in bundle.A("decisions"))
        {
            var choice = node!.AsObject(); var old = c.Query("SELECT state,message FROM catalogue_decisions WHERE uid=$p0 AND digest=$p1", P(choice.S("uid"), CatalogueFormat.Digest(choice))).FirstOrDefault();
            var status = old is not null && old.Text("state") is "applied" or "ignored" ? Row(old) : CatalogueDecisions.Inspect(c, choice, native);
            status["uid"] = choice.S("uid"); status["digest"] = CatalogueFormat.Digest(choice); status["kind"] = choice.S("kind"); decisions.Add(status);
        }
        var projection = new JsonArray();
        foreach (var node in items)
        { var item = node!.AsObject().Copy(); item["record_digest"] = CatalogueFormat.Digest(item["record"]); item.Remove("record"); item.Remove("incoming"); projection.Add(item); }
        var revision = CatalogueFormat.Digest(new JsonArray(CatalogueFormat.Digest(bundle), projection, decisions.DeepClone(), CatalogueNativeRecords.Policies(c)));
        return new() { ["revision"] = revision, ["items"] = items, ["decisions"] = decisions, ["added"] = items.Count(item => item.S("action") == "new"), ["reused"] = items.Count(item => item.S("action") is "existing" or "batch"), ["retained"] = items.Count(item => item.S("action") == "review") };
    }
    private static JsonObject PublicPlan(JsonObject plan, int offset = 0, int limit = 100)
    {
        var result = plan.Copy(); var items = new JsonArray();
        foreach (var node in plan.A("items").Skip(offset).Take(limit)) { var item = node!.AsObject().Copy(); item.Remove("record"); item.Remove("incoming"); item.Remove("proof"); items.Add(item); }
        result["items"] = items; result["decisions"] = Objects(plan.A("decisions").Take(100).Select(node => node!.AsObject())); result["item_total"] = plan.A("items").Count; result["decision_total"] = plan.A("decisions").Count; result["offset"] = offset; result["limit"] = limit; return result;
    }
    private static JsonObject? AlreadyAdded(DatabaseSession c, JsonObject bundle, JsonObject plan)
    {
        if (c.Scalar("SELECT result_json FROM catalogue_exchange_receipts WHERE digest=$p0", P(CatalogueFormat.Digest(bundle))) is not string raw) return null;
        foreach (var item in plan.A("items"))
        {
            if (c.Scalar("SELECT 1 FROM catalogue_entry_ids i JOIN entries e ON e.id=i.entry_id WHERE i.uid=$p0", P(item.S("uid"))) is not null) continue;
            var incoming = item.O("incoming"); var known = c.Scalar("SELECT state FROM catalogue_import_records WHERE uid=$p0 AND digest=$p1", P(incoming.S("uid"), CatalogueFormat.Digest(incoming))) as string;
            if (known is not ("review" or "ignored")) return null;
        }
        var previous = JsonNode.Parse(raw)!.AsObject(); previous["added"] = 0; previous["decisions_applied"] = 0; previous["already_applied"] = true;
        previous["entry_ids"] = Numbers(plan.A("items").SelectMany(item => CatalogueIdentity.Targets(c, [item.S("uid")])).Distinct().Order()); return previous;
    }
    /// <summary>Stages a catalogue file. A file needing a newer reader is refused before anything is written
    /// (<see cref="CatalogueVersionMismatchException"/>: "Version mismatch: version X required").</summary>
    public JsonObject Preview(ReadOnlySpan<byte> raw)
    {
        var decoded = CatalogueFormat.DecodeFile(raw); var bundle = decoded.Body; lock (_sourceWriteGate) return _database.Write(DatabaseFile.Catalog, c =>
        {
            var plan = Plan(c, bundle); var id = Guid.NewGuid().ToString("N"); c.Execute("DELETE FROM catalogue_exchange_stages WHERE created_at<$p0", P(Now - 86400));
            foreach (var stage in c.Query("SELECT id FROM catalogue_exchange_stages ORDER BY created_at DESC").Skip(3)) c.Execute("DELETE FROM catalogue_exchange_stages WHERE id=$p0", P(stage.Text("id")));
            c.Execute("INSERT INTO catalogue_exchange_stages(id,payload_json,revision,created_at) VALUES($p0,$p1,$p2,$p3)", P(id, CatalogueFormat.Canonical(bundle), plan.S("revision"), Now));
            var result = PublicPlan(plan); result["id"] = id; result["already_applied"] = AlreadyAdded(c, bundle, plan) is not null; result["file"] = decoded.Header.DeepClone(); return result;
        });
    }
    private (DatabaseRow Row, JsonObject Bundle) StageData(DatabaseSession c, string id)
    {
        var row = c.Query("SELECT * FROM catalogue_exchange_stages WHERE id=$p0", P(id)).FirstOrDefault();
        if (row is null || row.Number("created_at") < Now - 86400) throw new ArgumentException("Catalogue preview expired; select the file again");
        return (row, JsonNode.Parse(row.Text("payload_json"))!.AsObject());
    }
    public JsonObject Stage(string id, int offset = 0, int limit = 100)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Invalid catalogue preview page");
        lock (_sourceWriteGate) return _database.Read(DatabaseFile.Catalog, c =>
        {
            var (row, bundle) = StageData(c, id); if (row.Text("result_json").Length != 0) return new() { ["id"] = id, ["state"] = "applied", ["result"] = JsonNode.Parse(row.Text("result_json")) };
            var plan = Plan(c, bundle); var result = PublicPlan(plan, offset, limit); result["id"] = id; result["state"] = row.Text("revision") == plan.S("revision") ? "preview" : "stale"; return result;
        });
    }
    /// <summary>Adds a staged file in one catalogue write; a failure rolls back every row, alias, decision and the receipt.
    /// Never merges or splits: structural choices stay pending for their own reviewed task.</summary>
    public JsonObject Apply(string id, string revision, bool includeDecisions = true, DecisionActor? actor = null)
    {
        JsonObject result;
        lock (_sourceWriteGate) result = _database.Write(DatabaseFile.Catalog, c =>
        {
            var (stage, bundle) = StageData(c, id); var plan = Plan(c, bundle);
            if (stage.Text("result_json").Length != 0 && stage.Text("revision") == revision) { var previous = JsonNode.Parse(stage.Text("result_json"))!.AsObject(); previous["added"] = 0; previous["already_applied"] = true; return previous; }
            if (stage.Text("revision") != revision || plan.S("revision") != revision) throw new ArgumentException("Catalogue or local choices changed; refresh the preview before applying");
            var already = AlreadyAdded(c, bundle, plan); if (already is not null) { c.Execute("UPDATE catalogue_exchange_stages SET result_json=$p0 WHERE id=$p1", P(CatalogueFormat.Canonical(already), id)); return already; }
            var counts = new JsonObject { ["added"] = 0, ["reused"] = 0, ["retained"] = 0, ["decisions_applied"] = 0, ["decisions_pending"] = 0, ["entry_ids"] = new JsonArray() };
            void Increment(string key) => counts[key] = counts[key]!.GetValue<int>() + 1;
            foreach (var item in plan.A("items"))
            {
                var record = item.O("record"); var incoming = item.O("incoming"); var action = item.S("action"); var state = "review";
                if (action == "ignored") state = "ignored";
                else if (action == "review") Increment("retained");
                else
                {
                    var eid = action == "new" ? Add(c, record) : action == "batch" ? CatalogueIdentity.Targets(c, [item.S("batch_uid")])[0] : item.A("target_ids")[0]!.GetValue<long>();
                    Increment(action == "new" ? "added" : "reused"); counts.A("entry_ids").Add(eid); Associate(c, eid, record); state = "imported";
                }
                c.Execute("INSERT INTO catalogue_import_records VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(uid,digest) DO UPDATE SET state=excluded.state,message=excluded.message", P(incoming.S("uid"), CatalogueFormat.Digest(incoming), CatalogueFormat.Canonical(incoming), state, item.S("message")));
            }
            CatalogueIdentity.RefreshGroups(c, plan.A("items").Where(item => item?["group_root"] is not null && item.S("action") is not ("review" or "ignored")).Select(item => item.S("group_root")));
            var native = _native.Read(c);
            foreach (var node in bundle.A("decisions"))
            {
                var choice = node!.AsObject(); var digest = CatalogueFormat.Digest(choice); var old = c.Scalar("SELECT state FROM catalogue_decisions WHERE uid=$p0 AND digest=$p1", P(choice.S("uid"), digest)) as string;
                var status = old is "applied" or "ignored" ? CatalogueDecisions.Status(old, "Previously retained local result.") : CatalogueDecisions.Inspect(c, choice, native);
                if (includeDecisions && status.S("state") == "ready")
                {
                    status = CatalogueDecisions.ApplyReady(c, choice, native);
                    if (status.S("state") == "applied")
                        Record("catalogue.decision." + choice.S("kind"), actor, CatalogueIdentity.Targets(c, choice.A("entries").Select(uid => uid!.GetValue<string>())),
                            choice.A("records").SelectMany(fact => CatalogueNativeRecords.Resolve(native, fact!.AsObject())).Select(record => record.SourceId + ":" + record.MangaId),
                            "Shared catalogue choice applied while adding a catalogue file", new JsonObject { ["uid"] = choice.S("uid"), ["digest"] = digest });
                }
                Increment(status.S("state") == "applied" ? "decisions_applied" : "decisions_pending");
                c.Execute("INSERT INTO catalogue_decisions(uid,digest,decision_json,state,message) VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(uid,digest) DO UPDATE SET state=excluded.state,message=excluded.message", P(choice.S("uid"), digest, CatalogueFormat.Canonical(choice), status.S("state"), status.S("message")));
            }
            Record("catalogue.import", actor, counts.A("entry_ids").Select(value => value!.GetValue<long>()), [],
                "Catalogue file added: " + counts["added"]!.ToJsonString() + " new, " + counts["reused"]!.ToJsonString() + " reused, " + counts["retained"]!.ToJsonString() + " retained for review, " + counts["decisions_applied"]!.ToJsonString() + " choices applied",
                (JsonObject)counts.DeepClone());
            var encoded = CatalogueFormat.Canonical(counts); c.Execute("UPDATE catalogue_exchange_stages SET result_json=$p0 WHERE id=$p1", P(encoded, id)); c.Execute("INSERT OR REPLACE INTO catalogue_exchange_receipts VALUES($p0,$p1,$p2)", P(CatalogueFormat.Digest(bundle), encoded, Stamp())); return counts;
        });
        _invalidate?.Invoke(); return result;
    }
}
