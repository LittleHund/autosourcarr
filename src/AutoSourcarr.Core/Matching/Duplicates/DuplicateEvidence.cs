using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Duplicates.DuplicateJson;

namespace AutoSourcarr.Core.Matching.Duplicates;

/// <summary>
/// Frozen evidence of entries for duplicate decisions (port of _entry_duplicate_evidence_snapshot,
/// _automatic_merge_member_snapshots, _entry_auto_merge_restore_snapshot and _entry_merge_score).
/// The native snapshot (NativeCatalogueEvidence.Snapshot) is extended with source-native <c>authors</c> and <c>year</c>.
/// </summary>
public sealed class DuplicateEvidence(Func<DatabaseSession, long, JsonObject> nativeSnapshot)
{
    public static long Canonical(DatabaseSession c, long id) => PortableIdentityResolver.Canonical(c, id);

    public JsonObject Snapshot(DatabaseSession c, long entryId)
    {
        ArgumentNullException.ThrowIfNull(c);
        entryId = Canonical(c, entryId);
        var snapshot = nativeSnapshot(c, entryId) ?? throw new InvalidDataException("Native snapshot owner returned no evidence");
        var authors = new List<string>(); long? year = null;
        foreach (var table in new[] { "source_matches", "source_variants" })
            foreach (var row in c.Query($"SELECT author,metadata_json FROM {table} WHERE entry_id=$p0 AND active=1 ORDER BY id", P(entryId)))
            {
                if (row.Text("author").Trim().Length != 0) authors.Add(row.Text("author").Trim());
                if (year is not null) continue;
                var found = Long(Metadata(row.Text("metadata_json"))["year"]);
                if (found is >= 1800 and <= 2200) year = found;
            }
        year ??= c.Query("SELECT year FROM identity_links WHERE entry_id=$p0 AND (locked=1 OR match_type='source-exact' OR match_type LIKE 'manual%') ORDER BY locked DESC,confidence DESC", P(entryId))
            .Select(row => row.Integer("year")).Where(value => value is >= 1800 and <= 2200).Select(value => (long?)value).FirstOrDefault();
        snapshot["authors"] = JArray(authors.Distinct(StringComparer.OrdinalIgnoreCase));
        if (year is { } y) snapshot["year"] = y;
        return snapshot;
    }

    /// <summary>Original members of an automatic group (automatic_merge_lineage), or the entry itself.</summary>
    public List<JsonObject> MemberSnapshots(DatabaseSession c, long entryId)
    {
        ArgumentNullException.ThrowIfNull(c);
        entryId = Canonical(c, entryId);
        var result = new List<JsonObject>();
        foreach (var row in c.Query("SELECT member_entry_id,snapshot_json FROM automatic_merge_lineage WHERE canonical_entry_id=$p0 ORDER BY member_entry_id", P(entryId)))
        {
            JsonObject? value;
            try { value = JsonNode.Parse(row.Text("snapshot_json", "{}")) as JsonObject; }
            catch (JsonException) { value = null; }
            if (value is null || value.Count == 0) continue;
            if (!value.ContainsKey("entry_id")) value["entry_id"] = row.Integer("member_entry_id");
            result.Add(value);
        }
        return result.Count != 0 ? result : [Snapshot(c, entryId)];
    }

    /// <summary>Enough ownership data to reverse an automatic merge (stored in automatic_merge_events).</summary>
    public JsonObject RestoreSnapshot(DatabaseSession c, long entryId)
    {
        ArgumentNullException.ThrowIfNull(c);
        entryId = Canonical(c, entryId);
        var entry = c.Query("SELECT * FROM entries WHERE id=$p0", P(entryId)).SingleOrDefault();
        if (entry is null) return new JsonObject();
        JsonArray Rows(string sql) => new(c.Query(sql, P(entryId)).Select(row => (JsonNode?)StructureSql.Json(row)).ToArray());
        return new JsonObject
        {
            ["entry"] = StructureSql.Json(entry),
            ["titles"] = Rows("SELECT * FROM titles WHERE entry_id=$p0 ORDER BY id"),
            ["matches"] = Rows("SELECT * FROM source_matches WHERE entry_id=$p0 AND active=1 ORDER BY id"),
            ["variants"] = Rows("SELECT * FROM source_variants WHERE entry_id=$p0 AND active=1 ORDER BY id"),
            ["identities"] = Rows("SELECT * FROM identity_links WHERE entry_id=$p0 ORDER BY id"),
            ["evidence"] = Snapshot(c, entryId),
        };
    }

    /// <summary>Review score of a candidate pair (_entry_merge_score); not an automatic merge permission.</summary>
    public static (double Score, string Reason) MergeScore(DatabaseSession c, long leftId, long rightId, JsonObject leftSnapshot, JsonObject rightSnapshot)
    {
        ArgumentNullException.ThrowIfNull(c); ArgumentNullException.ThrowIfNull(leftSnapshot); ArgumentNullException.ThrowIfNull(rightSnapshot);
        if (leftId == rightId) return (1, "same-entry");
        leftId = Canonical(c, leftId); rightId = Canonical(c, rightId);
        if (leftId == rightId) return (1, "same-entry-alias");
        if (Review(c, leftId, rightId) == "not_duplicate") return (0, "reviewed-not-duplicate");
        var edition = TextAutomaticMatchGate.EditionConflict(leftSnapshot, rightSnapshot);
        if (edition.Length != 0) return (0, edition.StartsWith("colored", StringComparison.Ordinal) ? "colored-monochrome-conflict" : "manga-webtoon-conflict");
        var leftAuthor = c.Scalar("SELECT author FROM entries WHERE id=$p0", P(leftId)) as string ?? "";
        var rightAuthor = c.Scalar("SELECT author FROM entries WHERE id=$p0", P(rightId)) as string ?? "";
        var leftMatches = c.Query("SELECT source_id,source_manga_id,source_manga_url,real_url,source_title,thumbnail_url,description,author FROM source_matches WHERE entry_id=$p0 AND active=1", P(leftId));
        var rightMatches = c.Query("SELECT source_id,source_manga_id,source_manga_url,real_url,source_title,thumbnail_url,description,author FROM source_matches WHERE entry_id=$p0 AND active=1", P(rightId));
        HashSet<(string, string)> Ids(long id) => c.Query("SELECT provider,external_id FROM identity_links WHERE entry_id=$p0 AND confidence>=0.94", P(id))
            .Where(row => row.Text("provider").Length != 0 && row.Text("external_id").Length != 0).Select(row => (row.Text("provider"), row.Text("external_id"))).ToHashSet();
        var sharedIdentity = Ids(leftId).Intersect(Ids(rightId)).Order().ToArray();
        var leftRows = CatalogueMetadata.NativeTitleRows(c, leftId); var rightRows = CatalogueMetadata.NativeTitleRows(c, rightId);
        static string Field(JsonObject row, string name) => Str(row[name]);
        static bool Evidence(JsonObject row) => TextMatcher.IsMatchEvidenceTitle(Field(row, "title"), Field(row, "title_type")) && !Field(row, "title_type").StartsWith("translated-", StringComparison.Ordinal);
        var allLeft = leftRows.Select(row => Field(row, "normalized")).Where(value => value.Length != 0).ToHashSet(StringComparer.Ordinal);
        var allRight = rightRows.Select(row => Field(row, "normalized")).Where(value => value.Length != 0).ToHashSet(StringComparer.Ordinal);
        var normalizedOverlap = allLeft.Overlaps(allRight);
        var leftTitles = leftRows.Where(Evidence).Select(row => Field(row, "title")).ToArray();
        var rightTitles = rightRows.Where(Evidence).Select(row => Field(row, "title")).ToArray();
        var realOverlap = leftRows.Where(Evidence).Select(row => Field(row, "normalized")).ToHashSet(StringComparer.Ordinal)
            .Overlaps(rightRows.Where(Evidence).Select(row => Field(row, "normalized")));
        var machineOnly = normalizedOverlap && !realOverlap;
        var leftDescriptions = Descriptions(c, leftId); var rightDescriptions = Descriptions(c, rightId);
        var bestDescription = 0.0;
        foreach (var a in leftDescriptions) foreach (var b in rightDescriptions) bestDescription = Math.Max(bestDescription, TextMatcher.DescriptionSimilarity(a, b));
        var decision = TextMatcher.ScoreMatch(rightTitles, leftTitles, rightAuthor, leftAuthor, rightDescriptions.FirstOrDefault() ?? "", leftDescriptions);
        var score = decision.Score; var reason = decision.Reason;
        if (machineOnly)
        {
            if (bestDescription < 0.90) { score = Math.Min(score, 0.94); reason = "translated-title-needs-summary-corroboration"; }
            else reason = "translated-title+summary+" + reason;
        }
        if (bestDescription >= 0.90)
        {
            score = Math.Max(score, 0.995); reason = "description>=90%";
            if (leftAuthor.Length != 0 && rightAuthor.Length != 0 && TextMatcher.NameSimilarity(leftAuthor, rightAuthor) < 0.35) { score -= 0.08; reason += "-author"; }
        }
        var leftSources = leftMatches.GroupBy(row => row.Text("source_id"), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var rightSources = rightMatches.GroupBy(row => row.Text("source_id"), StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var sameSource = false;
        foreach (var sid in leftSources.Keys.Intersect(rightSources.Keys, StringComparer.Ordinal))
        {
            var a = leftSources[sid]; var b = rightSources[sid];
            if (a.Integer("source_manga_id") == b.Integer("source_manga_id")) continue;
            var sameCover = a.Text("thumbnail_url").Length != 0 && a.Text("thumbnail_url") == b.Text("thumbnail_url");
            var samePage = a.Text("real_url").Length != 0 && a.Text("real_url") == b.Text("real_url") || a.Text("source_manga_url").Length != 0 && a.Text("source_manga_url") == b.Text("source_manga_url");
            var sameTitle = TextMatcher.NormalizeTitle(a.Text("source_title")) is { Length: > 0 } na && na == TextMatcher.NormalizeTitle(b.Text("source_title"));
            var authorOk = a.Text("author").Length == 0 || b.Text("author").Length == 0 || TextMatcher.NameSimilarity(a.Text("author"), b.Text("author")) >= 0.90;
            var descriptionOk = TextMatcher.DescriptionSimilarity(a.Text("description"), b.Text("description")) >= 0.90;
            var strong = samePage || authorOk && (sameTitle && (sameCover || descriptionOk) || normalizedOverlap && bestDescription >= 0.95);
            if (!strong) return (0, "same-source-conflict");
            sameSource = true;
        }
        if (sameSource) return (Math.Max(score, 0.999), "same-source-duplicate");
        if (sharedIdentity.Length != 0) return (Math.Max(score, 0.999), "shared-" + sharedIdentity[0].Item1 + "-identity:" + sharedIdentity[0].Item2);
        return (Math.Clamp(score, 0, 1), reason);
    }

    /// <summary>Entry, source and confident identity descriptions (_entry_descriptions).</summary>
    public static IReadOnlyList<string> Descriptions(DatabaseSession c, long entryId)
    {
        ArgumentNullException.ThrowIfNull(c);
        var values = new List<string>();
        if (c.Scalar("SELECT description FROM entries WHERE id=$p0", P(entryId)) is string own && own.Length != 0) values.Add(own);
        foreach (var sql in new[] { "SELECT description FROM source_matches WHERE entry_id=$p0 AND active=1 AND description<>''", "SELECT description FROM source_variants WHERE entry_id=$p0 AND active=1 AND description<>''", "SELECT description FROM identity_links WHERE entry_id=$p0 AND confidence>=0.90 AND description<>''" })
            values.AddRange(c.Query(sql, P(entryId)).Select(row => row.Text("description")).Where(value => value.Length != 0));
        return values.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The stored review of a pair: duplicate (same entry), not_duplicate, or "".</summary>
    public static string Review(DatabaseSession c, long left, long right)
    {
        ArgumentNullException.ThrowIfNull(c);
        left = Canonical(c, left); right = Canonical(c, right);
        if (left == right) return "duplicate";
        var (a, b) = Pair(left, right);
        return c.Scalar("SELECT decision FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(a, b)) as string ?? "";
    }

    internal static JsonObject Metadata(string raw)
    {
        try { return JsonNode.Parse(raw.Length == 0 ? "{}" : raw) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}
