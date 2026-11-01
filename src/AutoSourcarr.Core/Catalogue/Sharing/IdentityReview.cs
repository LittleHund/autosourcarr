using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Unicode;
using static AutoSourcarr.Core.Catalogue.Sharing.SharingJson;

namespace AutoSourcarr.Core.Catalogue.Sharing;

/// <summary>
/// Private review of portable references on this instance (0.4.5 identity_review.py: listing, read, save). A review
/// changes only which portable IDs resolve to an entry; source bindings, merges and chapters are never touched. Every
/// save is recorded in the decision log with its actor, inside the same catalogue write.
/// </summary>
public sealed class IdentityReview(SqliteDatabase database, object sourceWriteGate, PortableIdentityResolver resolver)
{
    public const int MaximumNote = 1200;
    public const string Note = "Reviews affect portable-ID associations on this instance only. Original provider evidence, source bindings, merges and chapters are unchanged. Shared work IDs can legitimately cover different editions.";

    /// <summary>The phase-8 decision log; null records nothing (tests of the read side).</summary>
    public IDecisionLog? Log { get; init; }

    public JsonObject Listing(string search = "", int offset = 0, int limit = 30)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Length > 200 || offset < 0 || limit is < 1 or > 50) throw new ArgumentException("Invalid identity review page");
        var term = "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        return database.Read(DatabaseFile.Catalog, c => new JsonObject
        {
            ["items"] = Objects(c.Query("""
                SELECT e.id,e.preferred_title,
                (SELECT COUNT(*) FROM identity_links i WHERE i.entry_id=e.id) provider_count,
                (SELECT COUNT(*) FROM portable_identity_reviews r WHERE r.entry_id=e.id) saved_decisions
                FROM entries e WHERE preferred_title LIKE $p0 ESCAPE '\' ORDER BY e.preferred_title COLLATE NOCASE,e.id LIMIT $p1 OFFSET $p2
                """, P(term, limit, offset)).Select(Row)),
            ["total"] = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM entries WHERE preferred_title LIKE $p0 ESCAPE '\\'", P(term)), System.Globalization.CultureInfo.InvariantCulture),
            ["offset"] = offset, ["limit"] = limit,
        });
    }

    /// <summary>One entry's portable references with the saved local decisions, which other entries share each one, and recent history.</summary>
    public JsonObject Read(long entryId)
    {
        var view = database.Read(DatabaseFile.Catalog, c => View(c, entryId));
        var owner = view["entry_id"]!.GetValue<long>();
        foreach (var node in view.A("references")) node!["shared_with"] = SharedWith(node.S("id"), owner);
        return view;
    }

    /// <summary>Saves confirmed / excluded / needs-review, resets to the inherited association, or restores a retained review for review.</summary>
    public JsonObject Save(long entryId, string reference, string decision, string revision, string note = "", long? restoreId = null, DecisionActor? actor = null)
    {
        ArgumentNullException.ThrowIfNull(reference); ArgumentNullException.ThrowIfNull(revision); ArgumentNullException.ThrowIfNull(note);
        if (decision is not ("confirmed" or "excluded" or "needs-review" or "reset" or "restore")) throw new ArgumentException("Choose a supported identity review action");
        if (note.EnumerateRunes().Count() > MaximumNote) throw new ArgumentException("Review notes must be at most 1200 characters");
        long saved;
        lock (sourceWriteGate) saved = database.Write(DatabaseFile.Catalog, c =>
        {
            var old = View(c, entryId); var id = old["entry_id"]!.GetValue<long>();
            if (revision != old.S("revision")) throw new ArgumentException("Entry identity or review changed; reload before saving");
            var target = reference; var verb = decision; var text = note;
            if (verb == "restore")
            {
                var retained = (restoreId is { } restore ? c.Query("SELECT * FROM portable_identity_review_history WHERE entry_id=$p0 AND id=$p1", P(id, restore)).FirstOrDefault() : null)
                    ?? throw new ArgumentException("Retained review is unavailable");
                target = retained.Text("reference"); verb = "needs-review"; text = retained.Text("note");
            }
            var scope = PortableIdentity.Parse(target).Scope;
            if (scope == "entry") throw new ArgumentException("Portable entry lineage is retained automatically; review public work/source associations instead");
            var known = old.A("references").Select(node => node.S("id")).ToHashSet(StringComparer.Ordinal);
            if (!known.Contains(target) && (scope is not ("work" or "edition" or "gallery") || verb != "needs-review" || text.Trim().Length < 8))
                throw new ArgumentException("Add a public catalogue/gallery reference for review with a short provenance note first");
            var previous = c.Query("SELECT * FROM portable_identity_reviews WHERE entry_id=$p0 AND reference=$p1", P(id, target)).FirstOrDefault();
            var stamp = Stamp();
            c.Execute("INSERT INTO portable_identity_review_history(entry_id,old_entry_id,reference,decision,evidence_sha256,note,reason,created_at) VALUES($p0,$p0,$p1,$p2,$p3,$p4,$p5,$p6)",
                P(id, target, previous?.Text("decision") ?? "inherited", previous?.Text("evidence_sha256") ?? old.S("evidence_sha256"), previous?.Text("note") ?? "", verb, stamp));
            if (verb == "reset") c.Execute("DELETE FROM portable_identity_reviews WHERE entry_id=$p0 AND reference=$p1", P(id, target));
            else c.Execute("INSERT OR REPLACE INTO portable_identity_reviews VALUES($p0,$p1,$p2,$p3,$p4,$p5)", P(id, target, verb, old.S("evidence_sha256"), text.Trim(), stamp));
            Log?.Record(new DecisionEvent("identity.review", actor ?? DecisionActor.Automatic, [id], [],
                verb + " " + target + (text.Trim().Length == 0 ? "" : ": " + text.Trim()),
                Before: new JsonObject { ["reference"] = target, ["decision"] = previous?.Text("decision") ?? "inherited" },
                After: new JsonObject { ["reference"] = target, ["decision"] = verb == "reset" ? "inherited" : verb }));
            return id;
        });
        return Read(saved);
    }

    private JsonArray SharedWith(string reference, long owner)
    {
        // Shared references are warnings about association/editions, not duplicate proof.
        try
        {
            return Objects(resolver.Resolve(reference).A("items").Select(node => node!.AsObject())
                .Where(item => item["id"]!.GetValue<long>() != owner).Take(12)
                .Select(item => new JsonObject { ["id"] = item["id"]!.DeepClone(), ["title"] = item["preferred_title"]?.DeepClone() }));
        }
        catch (ArgumentException) { return []; }
    }

    private static JsonObject View(DatabaseSession c, long requested)
    {
        var eid = PortableIdentityResolver.Canonical(c, requested);
        var entry = c.Query("SELECT id,preferred_title FROM entries WHERE id=$p0", P(eid)).FirstOrDefault() ?? throw new KeyNotFoundException("Entry " + eid + " no longer exists");
        var proof = CatalogueDecisions.ReviewEvidence(c, eid);
        var rows = c.Query("SELECT * FROM portable_identity_reviews WHERE entry_id=$p0 ORDER BY reference", P(eid));
        var refs = new SortedDictionary<string, JsonObject>(UnicodeCodePointComparer.Instance);
        foreach (var row in c.Query("SELECT reference FROM catalogue_public_refs WHERE entry_id=$p0 ORDER BY reference", P(eid)))
        {
            var id = row.Text("reference"); var parts = id.Split(':');
            if (parts.Length < 3 || parts[1] == "entry") continue;
            refs[id] = new JsonObject { ["id"] = id, ["scope"] = parts[1], ["provider"] = parts[2], ["association"] = "shared catalogue; not native source proof", ["decision"] = "inherited", ["note"] = "", ["current_evidence"] = true, ["manual"] = false };
        }
        foreach (var node in PortableIdentity.Describe(Evidence(c, eid)).A("references"))
        {
            var item = node!.AsObject().Copy(); if (item.S("scope") == "entry") continue;
            item["decision"] = "inherited"; item["note"] = ""; item["current_evidence"] = true; item["manual"] = false; refs[item.S("id")] = item;
        }
        foreach (var row in rows)
        {
            var id = row.Text("reference");
            if (!refs.TryGetValue(id, out var item))
            {
                PortableReference parsed;
                try { parsed = PortableIdentity.Parse(id); } catch (ArgumentException) { parsed = new PortableReference("unknown", "", ""); }
                item = new JsonObject { ["id"] = id, ["scope"] = parsed.Scope, ["provider"] = parsed.Provider, ["external_id"] = parsed.ExternalId, ["association"] = "local reference", ["manual"] = true };
                refs[id] = item;
            }
            item["decision"] = row.Text("decision"); item["note"] = row.Text("note"); item["current_evidence"] = row.Text("evidence_sha256") == proof;
        }
        var history = c.Query("SELECT * FROM portable_identity_review_history WHERE entry_id=$p0 ORDER BY id DESC LIMIT 40", P(eid));
        return new JsonObject
        {
            ["entry_id"] = eid, ["title"] = entry.Text("preferred_title"),
            ["revision"] = CatalogueFormat.Digest(new JsonArray(proof, Objects(rows.Select(Row)))), ["evidence_sha256"] = proof,
            ["references"] = Objects(refs.Values), ["history"] = Objects(history.Select(Row)), ["note"] = Note,
        };
    }

    /// <summary>The entry shape PortableIdentity.Describe reads: provider identities and the active source records.</summary>
    private static JsonObject Evidence(DatabaseSession c, long eid)
    {
        JsonArray Rows(string sql) => Objects(c.Query(sql, P(eid)).Select(Row));
        const string Records = "SELECT m.source_id,m.source_manga_url,m.real_url,m.source_title,COALESCE(sc.base_url,'') base_url FROM {0} m LEFT JOIN source_config sc ON sc.source_id=m.source_id WHERE m.entry_id=$p0 AND m.active=1 ORDER BY m.id";
        return new JsonObject
        {
            ["id"] = eid,
            ["identities"] = Rows("SELECT provider,external_id,identity_scope,locked FROM identity_links WHERE entry_id=$p0 ORDER BY id"),
            ["matches"] = Rows(Records.Replace("{0}", "source_matches", StringComparison.Ordinal)),
            ["source_variants"] = Rows(Records.Replace("{0}", "source_variants", StringComparison.Ordinal)),
        };
    }
}
