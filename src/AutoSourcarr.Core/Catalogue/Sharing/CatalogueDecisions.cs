using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

internal static class CatalogueDecisions
{
    public static JsonObject Status(string state, string message) => new() { ["state"] = state, ["message"] = message };
    public static JsonObject Inspect(DatabaseSession c, JsonObject decision, IReadOnlyList<CatalogueNativeRecord> native)
    {
        var mapped = decision.A("entries").Select(uid => CatalogueIdentity.Targets(c, [uid!.GetValue<string>()])).ToList();
        var unique = mapped.Where(values => values.Count == 1).Select(values => values[0]).ToList();
        var resolved = decision.A("records").Select(locator => CatalogueNativeRecords.Resolve(native, locator!.AsObject())).ToList();
        var records = resolved.Where(values => values.Count == 1).Select(values => values[0]).ToList();
        var kind = decision.S("kind"); var value = decision.O("value");
        var pending = Status("pending", "Needs a local entry or source identity before this choice can be applied.");
        if (mapped.Any(values => values.Count != 1)) return pending;
        if (kind == "title" && unique.Count == 1)
        {
            var existing = c.Scalar("SELECT title FROM preferred_title_overrides WHERE entry_id=$p0", P(unique[0])) as string;
            var entry = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$p0", P(unique[0])).FirstOrDefault();
            var current = existing ?? (entry?.Flag("manual_preferred_title") == true ? entry.Text("preferred_title") : null);
            return !string.IsNullOrEmpty(current) && current != value.S("title") ? Status("conflict", "A different local manual title is retained.") : Status("ready", "Apply the saved title to this mapped entry.");
        }
        if (kind == "entry-separate" && unique.Count == 2) return unique[0] == unique[1] ? Status("conflict", "Both foreign identities currently resolve to one local entry; review its separation first.") : Status("ready", "Keep these two mapped entries separate.");
        if (kind == "import-rejection") return value.S("signature").Length != 0 && value.S("backup_key").Length != 0 ? Status("ready", "Retain the portable rejected-import signature.") : pending;
        if (kind == "identity-review" && unique.Count == 1) return c.Scalar("SELECT decision FROM portable_identity_reviews WHERE entry_id=$p0 AND reference=$p1", P(unique[0], value.S("reference"))) is not null ? Status("conflict", "An existing local identity review remains authoritative.") : Status("ready", "Retain the exclusion or require a fresh local confirmation.");
        if (records.Count != decision.A("records").Count || records.Count == 0) return pending;
        if (kind == "import-choice")
        {
            if (value.S("backup_key").Length == 0) return pending;
            var existing = c.Query("SELECT target_source_id,target_source_manga_id FROM import_manual_autoresolve WHERE backup_key=$p0", P(value.S("backup_key"))).FirstOrDefault();
            return existing is not null && (existing.Text("target_source_id") != records[0].SourceId || existing.Integer("target_source_manga_id") != records[0].MangaId) ? Status("conflict", "A different local import mapping is retained.") : Status("ready", "Use the resolved local record for this saved import choice.");
        }
        if (kind is "group" or "source-match")
        {
            if (kind == "group" && records.Count > 1 && records.Any(record => record.Fact["gallery"]!.GetValue<bool>() && !CatalogueNativeRecords.GalleryEnabled(c, record.SourceId))) return Status("pending", "Gallery combining is disabled for a member source; its imported group is retained separately.");
            var owners = records.Select(record => record.EntryId).ToHashSet();
            if (owners.Count != 1 || unique.Count != 0 && !owners.SetEquals([unique[0]])) return Status("pending", "This choice requires moving or combining local records; inspect and apply it explicitly.");
            if (records.Any(record => c.Scalar("SELECT 1 FROM manual_match_bindings WHERE source_id=$p0 AND source_manga_id=$p1", P(record.SourceId, record.MangaId)) is not null)) return Status("conflict", "A local manual binding or group remains authoritative.");
            return Status("ready", "Persist the choice against the already mapped local record numbers.");
        }
        if (kind == "separate" && unique.Count != 0) return records[0].EntryId == unique[0] ? Status("pending", "This source is still attached to the target entry; an explicit local split is required.") : Status("ready", "Keep this resolved source record separate from the target.");
        return pending;
    }
    public static JsonObject ApplyReady(DatabaseSession c, JsonObject decision, IReadOnlyList<CatalogueNativeRecord> native)
    {
        var status = Inspect(c, decision, native); if (status.S("state") != "ready") return status;
        var value = decision.O("value"); var kind = decision.S("kind"); var stamp = Stamp();
        var entries = decision.A("entries").Select(uid => CatalogueIdentity.Targets(c, [uid!.GetValue<string>()])[0]).ToList();
        var records = decision.A("records").Select(locator => CatalogueNativeRecords.Resolve(native, locator!.AsObject())[0]).ToList();
        switch (kind)
        {
            case "title":
                var title = value.S("title").Trim(); if (!TextMatcher.IsMeaningfulTitle(title)) throw new ArgumentException("Main title must be a meaningful title");
                c.Execute("UPDATE entries SET preferred_title=$p0,manual_preferred_title=1,updated_at=$p1 WHERE id=$p2", P(title, stamp, entries[0]));
                c.Execute("INSERT INTO preferred_title_overrides VALUES($p0,$p1,$p2) ON CONFLICT(entry_id) DO UPDATE SET title=excluded.title,updated_at=excluded.updated_at", P(entries[0], title, stamp));
                c.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$p0", P(entries[0]));
                c.Execute("INSERT INTO manual_title_scopes SELECT $p0,source_id,source_manga_id,$p1,$p2 FROM (SELECT source_id,source_manga_id FROM source_matches WHERE entry_id=$p0 AND active=1 UNION SELECT source_id,source_manga_id FROM source_variants WHERE entry_id=$p0 AND active=1)", P(entries[0], title, stamp));
                c.Execute("DELETE FROM preferred_title_override_sources"); break;
            case "entry-separate": Separate(c, entries[0], entries[1], stamp); break;
            case "import-rejection": c.Execute("INSERT OR IGNORE INTO import_match_rejections VALUES($p0,$p1,$p2,$p3,$p4,$p5)", P(value.S("backup_key"), value.S("signature"), value.S("backup_title"), value.S("backup_source"), value.S("rejected_title"), stamp)); break;
            case "identity-review":
                var reference = value.S("reference"); var verb = value.S("decision") == "excluded" ? "excluded" : "needs-review";
                var note = value.S("note"); if (note.Length == 0) note = "Imported review; confirm against local evidence";
                if (note.EnumerateRunes().Count() > 1200) throw new ArgumentException("Review notes must be at most1200characters");
                c.Execute("INSERT OR IGNORE INTO catalogue_public_refs VALUES($p0,$p1)", P(entries[0], reference)); var evidence = ReviewEvidence(c, entries[0]);
                c.Execute("INSERT INTO portable_identity_review_history(entry_id,old_entry_id,reference,decision,evidence_sha256,note,reason,created_at) VALUES($p0,$p0,$p1,'inherited',$p2,'',$p3,$p4)", P(entries[0], reference, evidence, verb, stamp));
                c.Execute("INSERT INTO portable_identity_reviews VALUES($p0,$p1,$p2,$p3,$p4,$p5)", P(entries[0], reference, verb, evidence, note.Trim(), stamp)); break;
            case "group":
            case "source-match":
                var group = kind == "group" ? PortableUuid.V5(Guid.Parse(CatalogueIdentity.Origin(c).Split(':')[^1]), decision.S("uid")).ToString("N") : null;
                if (group is not null) c.Execute("INSERT OR IGNORE INTO manual_match_groups VALUES($p0,$p1,$p2,$p2)", P(group, value.S("group_title"), stamp));
                foreach (var record in records)
                {
                    c.Execute("INSERT OR IGNORE INTO manual_match_bindings(source_id,source_manga_id,group_id,grouping_suppressed,entry_id_hint,source_name,source_title,record_json,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p8)", P(record.SourceId, record.MangaId, group, value.S("grouping_suppressed", "0") == "1" ? 1 : 0, record.EntryId, record.Row.Text("source_name"), record.Row.Text("source_title"), CatalogueFormat.Canonical(CatalogueNativeRecords.RecordPayload(record)), stamp));
                    foreach (var table in new[] { "source_matches", "source_variants" }) c.Execute($"UPDATE {table} SET locked=1 WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND active=1", P(record.EntryId, record.SourceId, record.MangaId));
                }
                break;
            case "separate":
                var separate = records[0];
                c.Execute("INSERT INTO manual_separate_records VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p5) ON CONFLICT(target_entry_id,source_id,source_manga_id) DO UPDATE SET separate_entry_id_hint=excluded.separate_entry_id_hint,source_title=excluded.source_title,updated_at=excluded.updated_at", P(entries[0], separate.SourceId, separate.MangaId, separate.EntryId, separate.Row.Text("source_title"), stamp));
                Separate(c, entries[0], separate.EntryId, stamp); break;
            case "import-choice":
                var target = records[0];
                c.Execute("INSERT OR IGNORE INTO import_manual_autoresolve(backup_key,backup_title,backup_source,backup_source_id,backup_source_url,target_source_id,target_source_manga_id,target_source_name,target_title,target_entry_id_hint,target_record_json,created_at,updated_at) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p11)", P(value.S("backup_key"), value.S("backup_title"), value.S("backup_source"), value.S("backup_source_id"), value.S("backup_source_url"), target.SourceId, target.MangaId, target.Row.Text("source_name"), target.Row.Text("source_title"), target.EntryId, CatalogueFormat.Canonical(CatalogueNativeRecords.RecordPayload(target)), stamp)); break;
            default: throw new InvalidOperationException("Unsupported ready catalogue decision");
        }
        return Status("applied", "Applied through mapped local identities; original numeric IDs were retained.");
    }
    private static void Separate(DatabaseSession c, long left, long right, string stamp)
    {
        if (left == right) throw new ArgumentException("Separate choices require two distinct local entries");
        if (left > right) (left, right) = (right, left);
        c.Execute("INSERT INTO duplicate_review_history(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($p0,$p1,'not_duplicate',$p2)", P(left, right, stamp));
        c.Execute("INSERT INTO duplicate_reviews VALUES($p0,$p1,'not_duplicate',$p2) ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision=excluded.decision,reviewed_at=excluded.reviewed_at", P(left, right, stamp));
    }
    internal static string ReviewEvidence(DatabaseSession c, long entryId)
    {
        var sources = c.Query("SELECT source_id,source_manga_url,source_title FROM source_matches WHERE entry_id=$p0 AND active=1 UNION ALL SELECT source_id,source_manga_url,source_title FROM source_variants WHERE entry_id=$p0 AND active=1 ORDER BY source_id,source_manga_url,source_title", P(entryId));
        var providers = c.Query("SELECT provider,external_id,identity_scope FROM identity_links WHERE entry_id=$p0 ORDER BY provider,external_id,identity_scope", P(entryId));
        var evidence = CatalogueFormat.Digest(new JsonObject { ["entry"] = entryId, ["sources"] = new JsonArray(sources.Select(row => (JsonNode?)new JsonArray(row.Text("source_id"), row.Text("source_manga_url"), row.Text("source_title"))).ToArray()), ["providers"] = new JsonArray(providers.Select(row => (JsonNode?)new JsonArray(row.Text("provider"), row.Text("external_id"), row.Text("identity_scope"))).ToArray()) });
        var shared = c.Query("SELECT reference FROM catalogue_public_refs WHERE entry_id=$p0 ORDER BY reference", P(entryId));
        return shared.Count == 0 ? evidence : CatalogueFormat.Digest(new JsonArray(evidence, Strings(shared.Select(row => row.Text("reference")))));
    }
    internal static string? Effect(DatabaseSession c, JsonObject choice, bool imported = false)
    {
        var targets = choice.A("entries").Select(uid => CatalogueIdentity.Targets(c, [uid!.GetValue<string>()])).ToList(); if (targets.Any(values => values.Count != 1)) return null;
        var entries = targets.Select(values => values[0]).ToList(); var value = choice.O("value"); var kind = choice.S("kind");
        var records = new JsonArray(choice.A("records").Select(locator => (locator!.S("source_id"), locator.S("reference"))).OrderBy(pair => pair.Item1, StringComparer.Ordinal).ThenBy(pair => pair.Item2, StringComparer.Ordinal).Select(pair => (JsonNode?)new JsonArray(pair.Item1, pair.Item2)).ToArray());
        JsonNode effect = kind switch
        {
            "title" => new JsonArray(Numbers(entries), value["title"]?.DeepClone()), "group" => records,
            "source-match" => new JsonArray(Numbers(entries), records, value.S("grouping_suppressed", "0")),
            "entry-separate" => Numbers(entries.Order()), "separate" => new JsonArray(Numbers(entries.Take(1)), records),
            "import-choice" or "import-rejection" => new JsonArray(value["backup_key"]?.DeepClone(), records, value["signature"]?.DeepClone()),
            _ => new JsonArray(Numbers(entries), value["reference"]?.DeepClone(), imported && value.S("decision") == "confirmed" ? "needs-review" : value["decision"]?.DeepClone())
        };
        return CatalogueFormat.Canonical(new JsonArray(kind, effect));
    }
}
