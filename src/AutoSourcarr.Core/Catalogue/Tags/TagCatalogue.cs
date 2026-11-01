using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Tags;

/// <summary>A tag review that cannot be saved (unknown tag, different namespaces, contradicts a Separate decision).</summary>
public sealed class TagReviewException(string message) : InvalidOperationException(message);

/// <summary>
/// tag_store.py for the Tags page: the indexed source tags grouped by their canonical form (built-in and user synonyms
/// plus reviewed Same/Separate pairs, see <see cref="TagSynonyms"/>), spelling suggestions (sorted neighbourhood, difflib
/// ratio ≥ 0.84, never already reviewed or already equivalent), the reviewed pairs, review_tags (recorded in the decision
/// log; a review never merges or splits entries), the tags of one entry and the reindex of saved source records as a job.
/// Local reads only: nothing contacts a source.
/// </summary>
public sealed class TagCatalogue(SqliteDatabase database, object sourceWriteGate, IDecisionLog decisions, TimeProvider? clock = null)
{
    public const string IndexVersionKey = "tag_index_version";
    public const string IndexVersion = "0.3.9";
    public static readonly JobType ReindexJob = new("tag-reindex", "Index source tags", JobPriority.Low, JobFlags.LongRunning, StallTimeout: TimeSpan.FromMinutes(10));
    private const string Active = "SELECT entry_id,source_id,source_manga_id FROM source_matches WHERE active=1 UNION SELECT entry_id,source_id,source_manga_id FROM source_variants WHERE active=1";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>After a review changed tag equivalence (duplicate snapshot and search caches).</summary>
    public Action? Changed { get; init; }

    /// <summary>list_tags: one page of tags (search over "namespace name"), with canonical id/name, aliases and record/entry counts.</summary>
    public JsonObject List(string search = "", int limit = 100, int offset = 0)
    {
        var query = GalleryTagEquivalence.Normalize(search ?? "");
        limit = Math.Clamp(limit, 1, 200); offset = Math.Max(0, offset);
        return database.Read(DatabaseFile.Catalog, s =>
        {
            var rows = Catalogue(s);
            var filtered = rows.Where(row => query.Length == 0 || GalleryTagEquivalence.Normalize(Text(row, "namespace") + " " + Text(row, "name")).Contains(query, StringComparison.Ordinal)).ToList();
            var page = filtered.Skip(offset).Take(limit).ToList();
            Aliases(rows, page);
            var counts = new Dictionary<long, (long Sources, long Entries)>();
            if (page.Count > 0)
            {
                var marks = string.Join(',', page.Select((_, index) => "$t" + index.ToString(CultureInfo.InvariantCulture)));
                foreach (var row in s.Query("WITH active AS (" + Active + ") SELECT t.tag_id,count(DISTINCT t.source_id) source_count,count(DISTINCT a.entry_id) entry_count "
                    + "FROM source_record_tags t JOIN active a USING(source_id,source_manga_id) WHERE t.tag_id IN (" + marks + ") GROUP BY t.tag_id",
                    page.Select((row, index) => ("$t" + index.ToString(CultureInfo.InvariantCulture), (object?)Id(row))).ToArray()))
                    counts[row.Integer("tag_id")] = (row.Integer("source_count"), row.Integer("entry_count"));
            }
            var items = new JsonArray();
            foreach (var row in page)
            {
                var (sources, entries) = counts.GetValueOrDefault(Id(row));
                row["source_count"] = sources; row["entry_count"] = entries; items.Add(row);
            }
            return new JsonObject { ["items"] = items, ["total"] = filtered.Count, ["limit"] = limit, ["offset"] = offset };
        });
    }

    /// <summary>suggestions: spelling neighbours in one namespace (the next twelve in sorted order) at ratio ≥ 0.84, best first.</summary>
    public JsonObject Suggestions(int limit = 100)
    {
        return database.Read(DatabaseFile.Catalog, s =>
        {
            var rows = Catalogue(s);
            var reviewed = s.Query("SELECT left_id,right_id FROM tag_reviews").Select(row => (row.Integer("left_id"), row.Integer("right_id"))).ToHashSet();
            var found = new List<(JsonObject Left, JsonObject Right, double Score)>();
            for (var i = 0; i < rows.Count; i++)
                for (var j = i + 1; j < Math.Min(rows.Count, i + 13); j++)
                {
                    var left = rows[i]; var right = rows[j];
                    if (Text(left, "namespace") != Text(right, "namespace")) break;
                    var pair = Id(left) < Id(right) ? (Id(left), Id(right)) : (Id(right), Id(left));
                    if (reviewed.Contains(pair) || Long(left, "canonical_id") == Long(right, "canonical_id")) continue;
                    var score = Ratio(Text(left, "normalized"), Text(right, "normalized"));
                    if (score >= .84) found.Add((left, right, Math.Round(score, 3, MidpointRounding.ToEven)));
                }
            var selected = found.OrderByDescending(item => item.Score).Take(Math.Clamp(limit, 1, 200)).ToList();
            var displayed = selected.SelectMany(item => new[] { item.Left, item.Right }).Distinct().ToList();
            Aliases(rows, displayed);
            return new JsonObject
            {
                ["items"] = new JsonArray(selected.Select(item => (JsonNode)new JsonObject
                {
                    ["left"] = item.Left.DeepClone(), ["right"] = item.Right.DeepClone(), ["score"] = item.Score,
                    ["reason"] = "Similar spelling; review before treating these tags as equivalent",
                }).ToArray()),
            };
        });
    }

    /// <summary>reviews: every reviewed pair, newest first.</summary>
    public JsonObject Reviews() => database.Read(DatabaseFile.Catalog, s => new JsonObject
    {
        ["items"] = new JsonArray(s.Query("""
            SELECT r.left_id,r.right_id,r.decision,r.updated_at,l.namespace lns,l.name lname,t.namespace rns,t.name rname
            FROM tag_reviews r JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id ORDER BY r.updated_at DESC
            """).Select(row => (JsonNode)new JsonObject
        {
            ["left"] = new JsonObject { ["id"] = row.Integer("left_id"), ["namespace"] = row.Text("lns"), ["name"] = row.Text("lname") },
            ["right"] = new JsonObject { ["id"] = row.Integer("right_id"), ["namespace"] = row.Text("rns"), ["name"] = row.Text("rname") },
            ["decision"] = row.Text("decision"), ["updated_at"] = row.Text("updated_at"),
        }).ToArray()),
    });

    /// <summary>
    /// review_tags: Same, Separate ("different") or Clear for two tags. Same needs one namespace; a decision that would
    /// contradict a Separate decision is refused. Recorded as "tags.review" with the actor. TagReviewException when refused.
    /// </summary>
    public JsonObject Review(long leftId, long rightId, string decision, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (decision is not ("same" or "different" or "clear") || leftId == rightId || leftId <= 0 || rightId <= 0)
            throw new TagReviewException("Choose two different tags and Same, Separate, or Clear.");
        if (leftId > rightId) (leftId, rightId) = (rightId, leftId);
        lock (sourceWriteGate)
            database.Write(DatabaseFile.Catalog, s =>
            {
                var found = s.Query("SELECT id,namespace,name FROM tags WHERE id IN ($l,$r)", ("$l", leftId), ("$r", rightId));
                if (found.Count != 2) throw new TagReviewException("One of these tags no longer exists. Refresh the list.");
                if (decision == "same" && found[0].Text("namespace") != found[1].Text("namespace"))
                    throw new TagReviewException("Different namespaces describe different metadata roles and cannot be combined.");
                var before = s.Scalar("SELECT decision FROM tag_reviews WHERE left_id=$l AND right_id=$r", ("$l", leftId), ("$r", rightId)) as string;
                s.Execute("DELETE FROM tag_reviews WHERE left_id=$l AND right_id=$r", ("$l", leftId), ("$r", rightId));
                if (decision != "clear")
                    s.Execute("INSERT INTO tag_reviews VALUES($l,$r,$d,$at)", ("$l", leftId), ("$r", rightId), ("$d", decision),
                        ("$at", _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)));
                var tags = s.Query("SELECT id,namespace,name FROM tags WHERE id IN (SELECT left_id FROM tag_reviews UNION SELECT right_id FROM tag_reviews)")
                    .Select(row => new JsonObject { ["id"] = row.Integer("id"), ["namespace"] = row.Text("namespace"), ["name"] = row.Text("name") }).ToArray();
                var reviews = s.Query("SELECT left_id,right_id,decision FROM tag_reviews ORDER BY left_id,right_id")
                    .Select(row => new JsonObject { ["left_id"] = row.Integer("left_id"), ["right_id"] = row.Integer("right_id"), ["decision"] = row.Text("decision") }).ToArray();
                try { _ = GalleryTagEquivalence.Mapping(tags, reviews, strict: true); }
                catch (ArgumentException error) { throw new TagReviewException(error.Message); }
                string Label(DatabaseRow row) => row.Text("namespace") + ":" + row.Text("name");
                decisions.Record(new DecisionEvent("tags.review", actor, [], [],
                    "Tags " + Label(found[0]) + " / " + Label(found[1]) + ": " + (decision == "different" ? "separate" : decision),
                    new JsonObject { ["left_id"] = leftId, ["right_id"] = rightId, ["decision"] = before },
                    new JsonObject { ["left_id"] = leftId, ["right_id"] = rightId, ["decision"] = decision == "clear" ? null : decision }));
                return 0;
            });
        Changed?.Invoke();
        return new JsonObject { ["left_id"] = leftId, ["right_id"] = rightId, ["decision"] = decision };
    }

    /// <summary>entry_metadata: the tags and gallery metadata of an entry's active records (aliases resolved). KeyNotFoundException when missing.</summary>
    public JsonObject EntryTags(NativeTagStore store, long entryId)
    {
        ArgumentNullException.ThrowIfNull(store);
        var canonical = database.Read(DatabaseFile.Catalog, s => CatalogueTitles.Canonical(s, entryId));
        return store.EntryMetadata(canonical);
    }

    /// <summary>Whether the saved-record tag index is current (startup skips the reindex then).</summary>
    public bool IndexCurrent() => database.Read(DatabaseFile.Catalog, s => s.Scalar("SELECT value FROM settings WHERE key=$k", ("$k", IndexVersionKey)) as string) == IndexVersion;

    /// <summary>
    /// queue_reindex: re-reads the saved metadata of every active record (no website requests) in batches of 100 as one
    /// job; a second request joins the running one. Answers {state, queued, already_running, task_id}.
    /// </summary>
    public JsonObject QueueReindex(JobQueue jobs, NativeTagStore store, string origin = "manual")
    {
        ArgumentNullException.ThrowIfNull(jobs); ArgumentNullException.ThrowIfNull(store);
        var reindex = new NativeTagReindex(store);
        var handle = jobs.Enqueue(ReindexJob, "", job =>
        {
            var count = 0L;
            foreach (var table in new[] { "source_matches", "source_variants" })
            {
                var after = 0L;
                while (true)
                {
                    job.CancellationToken.ThrowIfCancellationRequested();
                    var batch = reindex.Run(table, after, 100, job.CancellationToken);
                    if (batch.Count == 0) break;
                    after = batch.Cursor; count += batch.Count;
                    job.Report((int)Math.Min(int.MaxValue, count), null, "Indexed " + count.ToString(CultureInfo.InvariantCulture) + " saved source records");
                }
            }
            database.Write(DatabaseFile.Catalog, s => s.Execute("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$k", IndexVersionKey), ("$v", IndexVersion)));
            job.Result("Indexed " + count.ToString(CultureInfo.InvariantCulture) + " saved source records");
            Changed?.Invoke();
            return Task.CompletedTask;
        }, origin: origin, title: "Index source tags");
        return new JsonObject { ["state"] = "queued", ["queued"] = !handle.Merged, ["already_running"] = handle.Merged, ["task_id"] = handle.Id };
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>_catalog: every tag (namespace, normalized, id order) with canonical_id/canonical_name of its equivalence group.</summary>
    private static List<JsonObject> Catalogue(DatabaseSession s)
    {
        var map = TagSynonyms.Load(s);
        var rows = s.Query("SELECT id,namespace,name,normalized FROM tags ORDER BY namespace,normalized,id")
            .Select(row => new JsonObject { ["id"] = row.Integer("id"), ["namespace"] = row.Text("namespace"), ["name"] = row.Text("name"), ["normalized"] = row.Text("normalized") })
            .ToList();
        foreach (var group in rows.GroupBy(row => map.CanonicalKey(new JsonObject { ["namespace"] = Text(row, "namespace"), ["name"] = Text(row, "name") })
            ?? Text(row, "namespace") + ":" + Text(row, "normalized"), StringComparer.Ordinal))
        {
            var canonical = group.MinBy(Id)!;
            foreach (var row in group) { row["canonical_id"] = Id(canonical); row["canonical_name"] = Text(canonical, "name"); }
        }
        return rows;
    }

    /// <summary>_hydrate_aliases: aliases only for the displayed rows.</summary>
    private static void Aliases(IReadOnlyList<JsonObject> rows, IEnumerable<JsonObject> selected)
    {
        var groups = rows.GroupBy(row => Long(row, "canonical_id")).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var row in selected)
            row["aliases"] = new JsonArray(groups.GetValueOrDefault(Long(row, "canonical_id"), []).Where(other => Id(other) != Id(row))
                .Select(other => (JsonNode)new JsonObject { ["id"] = Id(other), ["name"] = Text(other, "name"), ["namespace"] = Text(other, "namespace") }).ToArray());
    }

    /// <summary>difflib.SequenceMatcher(None, a, b).ratio() (with its automatic junk heuristic for long strings).</summary>
    public static double Ratio(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a); ArgumentNullException.ThrowIfNull(b);
        if (a.Length + b.Length == 0) return 1.0;
        var b2j = new Dictionary<char, List<int>>();
        for (var j = 0; j < b.Length; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = [];
            list.Add(j);
        }
        if (b.Length >= 200)
        {
            var popular = b.Length / 100 + 1;
            foreach (var key in b2j.Where(pair => pair.Value.Count > popular).Select(pair => pair.Key).ToArray()) b2j.Remove(key);
        }
        var matched = 0;
        var queue = new Stack<(int ALo, int AHi, int BLo, int BHi)>();
        queue.Push((0, a.Length, 0, b.Length));
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var (i, j, k) = Longest(a, b, b2j, alo, ahi, blo, bhi);
            if (k == 0) continue;
            matched += k;
            if (alo < i && blo < j) queue.Push((alo, i, blo, j));
            if (i + k < ahi && j + k < bhi) queue.Push((i + k, ahi, j + k, bhi));
        }
        return 2.0 * matched / (a.Length + b.Length);
    }

    private static (int I, int J, int Size) Longest(string a, string b, Dictionary<char, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, best = 0;
        var lengths = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var next = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var positions))
                foreach (var j in positions)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    var k = next[j] = lengths.GetValueOrDefault(j - 1) + 1;
                    if (k > best) { besti = i - k + 1; bestj = j - k + 1; best = k; }
                }
            lengths = next;
        }
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1]) { besti--; bestj--; best++; }
        while (besti + best < ahi && bestj + best < bhi && a[besti + best] == b[bestj + best]) best++;
        return (besti, bestj, best);
    }

    private static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static long Long(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
    private static long Id(JsonObject row) => Long(row, "id");
}
