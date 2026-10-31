using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>Read-only local associations. Lookup never merges entries or asks a source for data.</summary>
public sealed class PortableIdentityResolver(SqliteDatabase database)
{
    public JsonObject Resolve(string reference) => database.Read(DatabaseFile.Catalog, c =>
    {
        var parsed = PortableIdentity.Parse(reference); var ids = new HashSet<long>();
        if (parsed.Scope == "entry") ids.UnionWith(CatalogueIdentity.Targets(c, [reference]));
        else if (parsed.Scope is "gallery" or "record")
        {
            foreach (var table in new[] { "source_matches", "source_variants" })
            {
                var rows = parsed.Scope == "record" ? c.Query($"SELECT entry_id,source_manga_url FROM {table} WHERE source_id=$p0 AND active=1 AND substr(source_manga_url,-length($p1))=$p1", P(parsed.ExternalId, parsed.Path)) : c.Query($"SELECT m.entry_id,m.source_manga_url,m.real_url,sc.base_url FROM {table} m LEFT JOIN source_config sc ON sc.source_id=m.source_id WHERE m.active=1 AND (m.source_manga_url LIKE $p0 OR m.source_manga_url LIKE $p1 OR m.source_manga_url LIKE $p2 OR m.real_url LIKE $p0 OR m.real_url LIKE $p1 OR m.real_url LIKE $p2)", P("%/" + parsed.ExternalId + "/%", "%-" + parsed.ExternalId + ".html", "%/" + parsed.ExternalId + ".html"));
                foreach (var row in rows)
                    try { if (parsed.Scope == "gallery" ? PortableIdentity.GalleryReference(row.Text("source_manga_url"), row.Text("real_url"), row.Text("base_url")) == reference : PortableIdentity.SourcePath(row.Text("source_manga_url")) == parsed.Path) ids.Add(row.Integer("entry_id")); } catch (ArgumentException) { }
            }
        }
        else
        {
            var predicate = parsed.Provider == "mangadex" ? "lower(external_id)=$p2" : "(external_id=$p2 OR ltrim(external_id,'0')=$p2)";
            foreach (var row in c.Query("SELECT entry_id,external_id FROM identity_links WHERE provider=$p0 AND identity_scope=$p1 AND " + predicate, P(parsed.Provider, parsed.Scope, parsed.ExternalId)))
                try { if (PortableIdentity.ExternalId(parsed.Provider, row.Text("external_id")) == parsed.ExternalId) ids.Add(row.Integer("entry_id")); } catch (ArgumentException) { }
        }
        if (parsed.Scope != "entry") ids.UnionWith(c.Query("SELECT r.entry_id FROM catalogue_public_refs r JOIN entries e ON e.id=r.entry_id WHERE reference=$p0", P(reference)).Select(row => row.Integer("entry_id")));
        var canonical = ids.Select(id => Canonical(c, id)).ToHashSet(); var reviews = c.Query("SELECT * FROM portable_identity_reviews WHERE reference=$p0", P(reference));
        var excluded = reviews.Where(row => row.Text("decision") == "excluded").Select(row => Canonical(c, row.Integer("entry_id"))).ToHashSet(); canonical.ExceptWith(excluded);
        foreach (var review in reviews.Where(row => row.Text("decision") == "confirmed"))
        {
            var id = review.Integer("entry_id"); var current = Canonical(c, id);
            if (excluded.Contains(current) || canonical.Contains(current) || c.Scalar("SELECT 1 FROM entries WHERE id=$p0", P(current)) is null) continue;
            if (review.Text("evidence_sha256") == CatalogueDecisions.ReviewEvidence(c, current)) canonical.Add(current);
        }
        canonical.ExceptWith(excluded); var ordered = canonical.Order().ToList();
        return new JsonObject { ["reference"] = reference, ["items"] = Objects(ordered.Take(50).SelectMany(id => c.Query("SELECT id,preferred_title FROM entries WHERE id=$p0", P(id))).Select(Row)), ["ambiguous"] = ordered.Count > 1, ["total"] = ordered.Count, ["truncated"] = ordered.Count > 50,
            ["note"] = "Local associations only. Multiple editions/separate works remain separate; no automatic merge or source request." };
    });
    public static long Canonical(DatabaseSession c, long id)
    {
        var seen = new HashSet<long>(); var current = id;
        while (seen.Add(current))
        { var next = c.Scalar("SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=$p0", P(current)); if (next is null) return current; current = Convert.ToInt64(next, System.Globalization.CultureInfo.InvariantCulture); }
        throw new InvalidOperationException("Catalogue entry aliases contain a cycle");
    }
}
