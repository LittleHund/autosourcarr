using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

internal static class CatalogueDecisionExport
{
    public static JsonArray Collect(DatabaseSession c, CatalogueNativeRecords reader, IReadOnlyList<CatalogueNativeRecord> native, HashSet<long> selected, bool firstPage)
    {
        var result = new List<JsonObject>(); var retained = new List<JsonObject>(); var appliedEffects = new HashSet<string>(StringComparer.Ordinal); var appliedTitles = new HashSet<(long, string)>();
        foreach (var row in c.Query("SELECT decision_json,state FROM catalogue_decisions ORDER BY uid,digest"))
        {
            var choice = JsonNode.Parse(row.Text("decision_json"))!.AsObject(); retained.Add(choice);
            if (row.Text("state") != "applied") continue;
            var effect = CatalogueDecisions.Effect(c, choice, imported: true); if (effect is not null) appliedEffects.Add(effect);
            if (choice.S("kind") == "title") foreach (var entry in CatalogueIdentity.Targets(c, choice.A("entries").Select(uid => uid!.GetValue<string>()))) appliedTitles.Add((entry, choice.O("value").S("title")));
        }
        void Add(string kind, string key, IEnumerable<long>? owners = null, IEnumerable<JsonObject>? facts = null, JsonObject? value = null)
        {
            var entries = (owners ?? []).Where(entry => entry != 0).Distinct().ToList();
            if (entries.Count != 0 && !selected.Contains(entries.Min()) || entries.Count == 0 && !firstPage) return;
            var choice = new JsonObject { ["uid"] = CatalogueIdentity.DecisionUid(c, kind, key), ["kind"] = kind, ["entries"] = Strings(entries.Select(entry => CatalogueIdentity.Ensure(c, entry))), ["records"] = Objects(facts ?? []), ["value"] = value ?? new() };
            CatalogueFormat.Decision(choice); var effect = CatalogueDecisions.Effect(c, choice); if (effect is null || !appliedEffects.Contains(effect)) result.Add(choice);
        }
        (JsonObject? Fact, long Entry) FactFor(DatabaseRow binding)
        {
            var record = native.LastOrDefault(record => record.SourceId == binding.Text("source_id") && record.MangaId == binding.Integer("source_manga_id"));
            if (record is not null) return (record.Fact, record.EntryId);
            try
            {
                var fact = CatalogueNativeRecords.FallbackBindingFact(binding);
                fact["title"] = reader.Clean(fact.S("title")); fact["raw_title"] = reader.Clean(fact.S("raw_title"));
                foreach (var key in new[] { "description", "author", "artist", "genre" }) fact.O("metadata")[key] = reader.Clean(fact.O("metadata").S(key));
                return (fact, binding.Integer("entry_id_hint"));
            }
            catch (ArgumentException) { return (null, binding.Integer("entry_id_hint")); }
        }
        foreach (var row in c.Query("SELECT e.id,COALESCE(o.title,e.preferred_title) title FROM entries e LEFT JOIN preferred_title_overrides o ON o.entry_id=e.id WHERE o.entry_id IS NOT NULL OR e.manual_preferred_title=1"))
        {
            var id = row.Integer("id"); var title = reader.Clean(row.Text("title"));
            if (!appliedTitles.Contains((id, title))) Add("title", CatalogueIdentity.Ensure(c, id), [id], value: new() { ["title"] = title });
        }
        foreach (var row in c.Query("SELECT * FROM duplicate_reviews WHERE decision='not_duplicate'")) Add("entry-separate", row.Integer("left_entry_id") + ":" + row.Integer("right_entry_id"), [row.Integer("left_entry_id"), row.Integer("right_entry_id")]);
        var bindings = c.Query("SELECT * FROM manual_match_bindings ORDER BY source_id,source_manga_id");
        foreach (var group in c.Query("SELECT * FROM manual_match_groups"))
        {
            var facts = new List<JsonObject>(); var entries = new List<long>(); var missing = false;
            foreach (var binding in bindings.Where(binding => binding.Text("group_id") == group.Text("id")))
            { var (fact, entry) = FactFor(binding); if (fact is null) missing = true; else facts.Add(fact); if (entry != 0) entries.Add(entry); }
            var value = new JsonObject { ["group_title"] = reader.Clean(group.Text("title")) };
            if (missing) value["note"] = "Some original members have no portable source URL. Review this group against locally installed sources.";
            Add("group", group.Text("id"), entries, missing ? [] : facts, value);
        }
        foreach (var binding in bindings.Where(binding => binding.Text("group_id").Length == 0))
        {
            var (fact, entry) = FactFor(binding); var value = new JsonObject { ["grouping_suppressed"] = binding.Flag("grouping_suppressed") ? "1" : "0" };
            if (fact is null) value["note"] = "The original record has no portable public source identity; retained for review.";
            Add("source-match", binding.Text("source_id") + ":" + binding.Integer("source_manga_id"), entry == 0 ? [] : [entry], fact is null ? [] : [fact], value);
        }
        foreach (var row in c.Query("SELECT * FROM manual_separate_records"))
        {
            var (fact, _) = FactFor(row); var owners = new List<long> { row.Integer("target_entry_id") }; if (row.Integer("separate_entry_id_hint") != 0) owners.Add(row.Integer("separate_entry_id_hint"));
            Add("separate", row.Integer("target_entry_id") + ":" + row.Text("source_id") + ":" + row.Integer("source_manga_id"), owners, fact is null ? [] : [fact], new() { ["title"] = reader.Clean(row.Text("source_title")), ["note"] = fact is null ? "Original source identity is unavailable; local record selection is required." : "" });
        }
        foreach (var row in c.Query("SELECT * FROM import_manual_autoresolve"))
        {
            var record = native.LastOrDefault(record => record.SourceId == row.Text("target_source_id") && record.MangaId == row.Integer("target_source_manga_id")); var entry = record?.EntryId ?? row.Integer("target_entry_id_hint");
            var value = new JsonObject { ["backup_title"] = reader.Clean(row.Text("backup_title")), ["backup_source"] = reader.Clean(row.Text("backup_source")), ["backup_key"] = row.Text("backup_key"), ["backup_source_id"] = row.Text("backup_source_id") };
            var url = row.Text("backup_source_url");
            try { url = url.Length == 0 ? "" : PortableIdentity.SourcePath(url); } catch (ArgumentException) { url = ""; value["note"] = "Original backup URL is private or unavailable; retained for review."; }
            value["backup_source_url"] = url;
            if (record is null) value["note"] = "The target needs a locally resolved source record.";
            if (!CatalogueFormat.PublicIdentityText(value.S("backup_key")) || !CatalogueFormat.PublicIdentityText(url)) { value["backup_key"] = ""; value["backup_source_url"] = ""; value["note"] = "The original import identity contains a private access parameter; retain this choice for local review."; }
            Add("import-choice", row.Text("backup_key"), entry == 0 ? [] : [entry], record is null ? [] : [record.Fact], value);
        }
        foreach (var row in c.Query("SELECT * FROM import_match_rejections"))
        {
            var value = new JsonObject { ["backup_title"] = reader.Clean(row.Text("backup_title")), ["backup_source"] = reader.Clean(row.Text("backup_source")), ["rejected_title"] = reader.Clean(row.Text("rejected_title")), ["backup_key"] = row.Text("backup_key") };
            var signature = row.Text("signature");
            if (new[] { "source-url:", "source-title:", "entry-title:" }.Any(prefix => signature.StartsWith(prefix, StringComparison.Ordinal))) value["signature"] = signature;
            else value["note"] = "Legacy source-number rejection retained by title; resolve its public source identity before applying.";
            if (!CatalogueFormat.PublicIdentityText(value.S("backup_key")) || !CatalogueFormat.PublicIdentityText(value.S("signature")))
            { value["backup_key"] = ""; value.Remove("signature"); value["note"] = "The original rejection identity contains a private access parameter; retain its titles for local review."; }
            Add("import-rejection", row.Text("backup_key") + ":" + signature, value: value);
        }
        foreach (var row in c.Query("SELECT * FROM portable_identity_reviews"))
        {
            if (row.Text("reference").StartsWith("asc1:entry:", StringComparison.Ordinal)) continue;
            Add("identity-review", row.Integer("entry_id") + ":" + row.Text("reference"), [row.Integer("entry_id")], value: new() { ["reference"] = row.Text("reference"), ["decision"] = row.Text("decision"), ["note"] = reader.Clean(row.Text("note")) });
        }
        foreach (var choice in retained)
        { var owners = CatalogueIdentity.Targets(c, choice.A("entries").Select(uid => uid!.GetValue<string>())); if (owners.Count != 0 && !selected.Contains(owners.Min()) || owners.Count == 0 && !firstPage) continue; result.Add(choice); }
        return Objects(result.DistinctBy(choice => (choice.S("uid"), CatalogueFormat.Digest(choice))));
    }
}
