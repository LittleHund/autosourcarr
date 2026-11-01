using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Duplicates.DuplicateJson;

namespace AutoSourcarr.Core.Matching.Duplicates;

public sealed partial class DuplicateMatching
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "the", "a", "an", "of", "to", "in", "and", "or", "my", "i", "is", "was", "with", "for", "on", "at", "from", "by", "no" };
    private static readonly string[] StatusLabels = ["Unknown", "Ongoing", "Completed", "Licensed", "Publishing finished", "Cancelled", "Hiatus"];

    /// <summary>
    /// Selective candidate shortlist (_duplicate_shortlist): exact native titles, rare-title signatures, description
    /// prefixes, shared confident identities and exact thumbnails, connected as stars (never O(n²) common buckets).
    /// Machine translations never create edges; reviewed not-duplicate pairs are skipped.
    /// </summary>
    public (IReadOnlyDictionary<(long Left, long Right), SortedSet<string>> Pairs, IReadOnlyDictionary<long, List<string>> Titles) Shortlist(IReadOnlyCollection<long>? entryIds = null)
    {
        var maxBucket = Math.Max(4, _settings().DuplicateSignatureMaxBucket);
        return Read(c =>
        {
            var canonicalCache = new Dictionary<long, long>();
            long Canon(long id) { if (!canonicalCache.TryGetValue(id, out var value)) canonicalCache[id] = value = DuplicateEvidence.Canonical(c, id); return value; }
            HashSet<long>? wanted = entryIds is { Count: > 0 } ? entryIds.Select(Canon).ToHashSet() : null;
            var active = c.Query("SELECT entry_id FROM source_matches WHERE active=1 UNION SELECT entry_id FROM source_variants WHERE active=1").Select(row => Canon(row.Integer("entry_id"))).ToHashSet();
            var reviewed = c.Query("SELECT left_entry_id,right_entry_id FROM duplicate_reviews WHERE decision='not_duplicate'")
                .Select(row => Pair(Canon(row.Integer("left_entry_id")), Canon(row.Integer("right_entry_id")))).ToHashSet();
            var titles = new Dictionary<long, List<string>>();
            var exact = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            var tokenDocs = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            var normalizedRows = new List<(long Entry, string Normalized)>();
            foreach (var row in c.Query("""
                SELECT entry_id,title,normalized,title_type FROM titles WHERE normalized<>''
                UNION ALL SELECT id entry_id,preferred_title title,'' normalized,'preferred' title_type FROM entries WHERE preferred_title<>''
                UNION ALL SELECT entry_id,source_title title,'' normalized,'source' title_type FROM source_matches WHERE active=1 AND source_title<>''
                UNION ALL SELECT entry_id,source_title title,'' normalized,'source-variant' title_type FROM source_variants WHERE active=1 AND source_title<>''
                """))
            {
                var id = Canon(row.Integer("entry_id"));
                if (!active.Contains(id)) continue;
                var title = row.Text("title").Trim(); var kind = row.Text("title_type");
                var normalized = row.Text("normalized").Trim(); if (normalized.Length == 0) normalized = TextMatcher.NormalizeTitle(title);
                if (normalized.Length == 0 || !TextMatcher.IsMatchEvidenceTitle(title, kind)) continue;
                if (!titles.TryGetValue(id, out var list)) titles[id] = list = [];
                if (title.Length != 0 && !list.Contains(title)) list.Add(title);
                if (kind.StartsWith("translated-", StringComparison.Ordinal)) continue;
                Add(exact, normalized, id); normalizedRows.Add((id, normalized));
                foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
                    if (token.Length >= 3 && !StopWords.Contains(token)) Add(tokenDocs, token, id);
            }
            int Docs(string token) => tokenDocs.TryGetValue(token, out var set) ? set.Count : 0;
            var fuzzy = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            foreach (var (id, normalized) in normalizedRows)
            {
                var selective = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 3 && !StopWords.Contains(token)).Distinct(StringComparer.Ordinal)
                    .OrderBy(Docs).ThenByDescending(token => token.Length).ThenBy(token => token, StringComparer.Ordinal)
                    .Where(token => Docs(token) <= maxBucket * 4).Take(5).ToArray();
                if (selective.Length == 1 && Docs(selective[0]) <= maxBucket) Add(fuzzy, "1:" + selective[0], id);
                for (var i = 0; i < selective.Length; i++)
                    for (var j = i + 1; j < selective.Length; j++)
                        Add(fuzzy, "2:" + string.Join(' ', new[] { selective[i], selective[j] }.Order(StringComparer.Ordinal)), id);
            }
            var identity = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            foreach (var row in c.Query("SELECT entry_id,provider,external_id FROM identity_links WHERE confidence>=0.94 AND provider<>'' AND external_id<>''"))
                if (active.Contains(Canon(row.Integer("entry_id")))) Add(identity, row.Text("provider") + ":" + row.Text("external_id"), Canon(row.Integer("entry_id")));
            var thumbnails = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            foreach (var row in c.Query("""
                SELECT sm.entry_id,tf.fingerprint FROM source_matches sm JOIN thumbnail_fingerprints tf ON tf.subject_key=('source:' || sm.source_id || ':' || sm.source_manga_id) WHERE sm.active=1 AND tf.fingerprint<>''
                UNION ALL SELECT sv.entry_id,tf.fingerprint FROM source_variants sv JOIN thumbnail_fingerprints tf ON tf.subject_key=('source:' || sv.source_id || ':' || sv.source_manga_id) WHERE sv.active=1 AND tf.fingerprint<>''
                """))
                if (active.Contains(Canon(row.Integer("entry_id")))) Add(thumbnails, row.Text("fingerprint"), Canon(row.Integer("entry_id")));
            var descriptions = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            foreach (var row in c.Query("""
                SELECT id entry_id,description FROM entries WHERE description<>''
                UNION ALL SELECT entry_id,description FROM source_matches WHERE active=1 AND description<>''
                UNION ALL SELECT entry_id,description FROM source_variants WHERE active=1 AND description<>''
                """))
            {
                var id = Canon(row.Integer("entry_id")); if (!active.Contains(id)) continue;
                var normalized = TextMatcher.NormalizeDescription(row.Text("description"));
                if (normalized.Length >= 80) Add(descriptions, string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(12)), id);
            }
            var reasons = new Dictionary<(long Left, long Right), SortedSet<string>>();
            void Bucket(HashSet<long> members, string reason, int? hardLimit)
            {
                var ids = members.Select(Canon).Distinct().Order().ToArray();
                if (ids.Length < 2 || hardLimit is { } limit && ids.Length > limit) return;
                if (wanted is not null && !ids.Any(wanted.Contains)) return;
                var anchor = wanted is null ? ids[0] : ids.FirstOrDefault(wanted.Contains, ids[0]);
                var blocked = new List<long>();
                void Edge(long a, long b)
                {
                    var pair = Pair(a, b);
                    if (!reasons.TryGetValue(pair, out var set)) reasons[pair] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add(reason);
                }
                foreach (var other in ids)
                {
                    if (other == anchor) continue;
                    if (reviewed.Contains(Pair(anchor, other))) { blocked.Add(other); continue; }
                    Edge(anchor, other);
                }
                if (blocked.Count == 0) return;
                foreach (var alternative in ids.Where(id => id != anchor && (wanted is null || wanted.Contains(id))).Take(4))
                    foreach (var other in blocked)
                        if (other != alternative && !reviewed.Contains(Pair(alternative, other))) Edge(alternative, other);
            }
            foreach (var members in exact.Values) Bucket(members, "exact-title", null);
            foreach (var members in fuzzy.Values) Bucket(members, "rare-title-signature", maxBucket);
            foreach (var members in descriptions.Values) Bucket(members, "description-prefix", maxBucket);
            foreach (var members in identity.Values) Bucket(members, "shared-metadata-identity", null);
            foreach (var members in thumbnails.Values) Bucket(members, "exact-thumbnail", maxBucket * 4);
            return ((IReadOnlyDictionary<(long Left, long Right), SortedSet<string>>)reasons, (IReadOnlyDictionary<long, List<string>>)titles);
        });
    }

    private static void Add(Dictionary<string, HashSet<long>> map, string key, long id)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = [];
        set.Add(id);
    }

    /// <summary>
    /// Unresolved duplicate candidates per entry (duplicate_candidates): title and review scores, and whether the
    /// automatic gate would merge the pair across every original member.
    /// </summary>
    public Dictionary<long, JsonObject> Candidates(IReadOnlyCollection<long>? entryIds = null, CancellationToken cancellationToken = default)
    {
        var wanted = entryIds is { Count: > 0 } requested ? Read(c => requested.Select(id => DuplicateEvidence.Canonical(c, id)).ToHashSet()) : null;
        var (pairs, titles) = Shortlist(entryIds);
        var snapshots = new Dictionary<long, JsonObject>(); var members = new Dictionary<long, List<JsonObject>>();
        JsonObject Snap(long id) { if (!snapshots.TryGetValue(id, out var value)) snapshots[id] = value = Read(c => _evidence.Snapshot(c, id)); return value; }
        var result = new Dictionary<long, (HashSet<long> Ids, SortedSet<string> Titles, double Best, List<JsonObject> Items)>();
        foreach (var ((left, right), reasons) in pairs.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftTitles = titles.GetValueOrDefault(left) ?? []; var rightTitles = titles.GetValueOrDefault(right) ?? [];
            var best = 0.0; var bestPair = ("", "");
            foreach (var a in leftTitles) foreach (var b in rightTitles) { var score = TextMatcher.TitleSimilarity(a, b); if (score > best) { best = score; bestPair = (a, b); } }
            double mergeScore; string mergeReason; JsonObject direct, group;
            try
            {
                var ls = Snap(left); var rs = Snap(right);
                (mergeScore, mergeReason) = Read(c => DuplicateEvidence.MergeScore(c, left, right, ls, rs));
                direct = Read(c => DuplicateEvidence.Review(c, left, right)) == "not_duplicate" ? AutomaticMatchDecision.No("reviewed-not-duplicate").Evidence : _matchGate.Decide(ls, rs).Evidence;
                group = GroupEvidence(left, right, members);
            }
            catch (KeyNotFoundException) { continue; }
            if (best < 0.90 && mergeScore < 0.97 && !reasons.Overlaps(["description-prefix", "shared-metadata-identity", "exact-thumbnail"])) continue;
            var eligible = Bool(group["eligible"]); var autoReason = Str(group["reason"], Str(direct["reason"]));
            foreach (var (id, other, own, theirs) in new[] { (left, right, bestPair.Item1, bestPair.Item2), (right, left, bestPair.Item2, bestPair.Item1) })
            {
                if (wanted is not null && !wanted.Contains(id)) continue;
                if (!result.TryGetValue(id, out var record)) result[id] = record = ([], new SortedSet<string>(StringComparer.Ordinal), 0, []);
                record.Ids.Add(other); if (own.Length != 0) record.Titles.Add(own);
                record.Best = Math.Max(record.Best, Math.Max(best, mergeScore)); result[id] = record;
                record.Items.Add(new JsonObject
                {
                    ["entry_id"] = other, ["title_score"] = best, ["merge_score"] = mergeScore, ["reason"] = mergeReason, ["shortlist_reason"] = string.Join('+', reasons),
                    ["matched_title"] = own, ["other_title"] = theirs, ["auto_merge_eligible"] = eligible, ["exact_title_count"] = Long(direct["exact_title_count"]),
                    ["exact_titles"] = direct["exact_titles"]?.DeepClone() ?? new JsonArray(), ["summary_similarity"] = Num(direct["summary_similarity"]),
                    ["shared_identities"] = direct["shared_identities"]?.DeepClone() ?? new JsonArray(), ["exact_thumbnail_match"] = Bool(direct["exact_thumbnail_match"]),
                    ["auto_merge_reason"] = autoReason,
                });
            }
        }
        return result.ToDictionary(pair => pair.Key, pair => new JsonObject
        {
            ["entry_ids"] = JArray(pair.Value.Ids.Order()), ["titles"] = JArray(pair.Value.Titles), ["best_score"] = pair.Value.Best,
            ["candidates"] = new JsonArray(pair.Value.Items.OrderByDescending(item => Math.Max(Num(item["title_score"]), Num(item["merge_score"]))).ThenBy(item => Long(item["entry_id"])).Select(item => (JsonNode?)item).ToArray()),
        });
    }

    /// <summary>Human-reviewable duplicate clusters (duplicate_groups); refreshes and persists the review snapshot.</summary>
    public JsonArray Groups(bool force = false, CancellationToken cancellationToken = default)
    {
        _scan.Wait(cancellationToken);
        try
        {
            long revision;
            lock (_snapshotLock)
            {
                revision = _snapshotRevision;
                if (!force && _snapshot is { } current && !_snapshotStale && _clock.GetElapsedTime(current.At) <= TimeSpan.FromSeconds(Math.Max(1, _settings().DuplicateSnapshotTtlSeconds)))
                    return (JsonArray)current.Groups.DeepClone();
            }
            var candidates = Candidates(null, cancellationToken);
            var graph = new Dictionary<long, HashSet<long>>();
            foreach (var (id, record) in candidates)
                foreach (var other in record["entry_ids"]!.AsArray().Select(node => Long(node)))
                {
                    if (!graph.TryGetValue(id, out var a)) graph[id] = a = []; a.Add(other);
                    if (!graph.TryGetValue(other, out var b)) graph[other] = b = []; b.Add(id);
                }
            var seen = new HashSet<long>(); var components = new List<long[]>();
            foreach (var start in graph.Keys.Order())
            {
                if (seen.Contains(start)) continue;
                var stack = new Stack<long>([start]); var ids = new List<long>();
                while (stack.Count != 0) { var id = stack.Pop(); if (!seen.Add(id)) continue; ids.Add(id); foreach (var next in graph[id]) if (!seen.Contains(next)) stack.Push(next); }
                if (ids.Count >= 2) components.Add(ids.Order().ToArray());
            }
            var summaries = Read(c => Summaries(c, components.SelectMany(ids => ids).Distinct().ToArray()));
            var groups = new List<JsonObject>();
            foreach (var ids in components)
            {
                var pairs = new List<JsonObject>();
                for (var i = 0; i < ids.Length; i++)
                {
                    var map = candidates.TryGetValue(ids[i], out var record) ? record["candidates"]!.AsArray().OfType<JsonObject>().GroupBy(item => Long(item["entry_id"])).ToDictionary(group => group.Key, group => group.First()) : new Dictionary<long, JsonObject>();
                    for (var j = i + 1; j < ids.Length; j++)
                        if (map.TryGetValue(ids[j], out var hit))
                        {
                            var pair = new JsonObject { ["left_id"] = ids[i], ["right_id"] = ids[j] };
                            foreach (var (key, value) in hit) pair[key] = value?.DeepClone();
                            pairs.Add(pair);
                        }
                }
                var sorted = pairs.OrderByDescending(pair => Num(pair["title_score"])).ThenBy(pair => Long(pair["left_id"])).ThenBy(pair => Long(pair["right_id"])).ToArray();
                groups.Add(new JsonObject
                {
                    ["group_id"] = string.Join('-', ids.Select(id => id.ToString(CultureInfo.InvariantCulture))), ["entry_ids"] = JArray(ids),
                    ["entries"] = new JsonArray(ids.Where(summaries.ContainsKey).Select(id => (JsonNode?)summaries[id].DeepClone()).ToArray()),
                    ["pairs"] = new JsonArray(sorted.Select(pair => (JsonNode?)pair).ToArray()), ["best_score"] = sorted.Length == 0 ? 0.0 : sorted.Max(pair => Num(pair["title_score"])),
                });
            }
            var ordered = new JsonArray(groups.OrderByDescending(group => Num(group["best_score"])).ThenByDescending(group => group["entry_ids"]!.AsArray().Count)
                .ThenBy(group => Str(group["group_id"]), StringComparer.Ordinal).Select(group => (JsonNode?)group).ToArray());
            lock (_snapshotLock)
            {
                if (revision != _snapshotRevision)
                {
                    // Never replace a newer manual decision with an in-flight scan.
                    _snapshotStale = true;
                    return _snapshot is { } newer ? (JsonArray)newer.Groups.DeepClone() : [];
                }
                _snapshot = (_clock.GetTimestamp(), (JsonArray)ordered.DeepClone()); _snapshotStale = false;
            }
            try { _stored.Set(SnapshotSetting, new JsonObject { ["saved_at"] = _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0, ["groups"] = ordered.DeepClone() }); }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException) { }
            return ordered;
        }
        finally { _scan.Release(); }
    }

    private static Dictionary<long, JsonObject> Summaries(DatabaseSession c, IReadOnlyList<long> ids)
    {
        var result = new Dictionary<long, JsonObject>();
        foreach (var id in ids)
        {
            var entry = c.Query("SELECT id,preferred_title,description,author,status FROM entries WHERE id=$p0", P(id)).SingleOrDefault();
            if (entry is null) continue;
            var records = ActiveRecords(c, id);
            var titles = c.Query("SELECT title FROM titles WHERE entry_id=$p0 ORDER BY title_type,title COLLATE NOCASE", P(id)).Select(row => row.Text("title").Trim())
                .Where(TextMatcher.IsMeaningfulTitle).Distinct(StringComparer.Ordinal).Take(20).ToArray();
            var status = entry.Integer("status");
            result[id] = new JsonObject
            {
                ["id"] = id, ["preferred_title"] = entry.Text("preferred_title"), ["description"] = entry.Text("description"), ["author"] = entry.Text("author"),
                ["status"] = status, ["status_label"] = status is >= 0 and < 7 ? StatusLabels[status] : "Unknown",
                ["match_count"] = records.Count(row => row.Text("kind") == "primary"), ["titles"] = JArray(titles),
                ["sources"] = new JsonArray(records.Where(row => row.Text("kind") == "primary").Select(row => (JsonNode?)new JsonObject
                {
                    ["source_id"] = row.Text("source_id"), ["source_name"] = row.Text("source_name"), ["source_manga_id"] = row.Integer("source_manga_id"),
                    ["source_title"] = row.Text("source_title"), ["match_type"] = row.Text("match_type"), ["locked"] = row.Flag("locked"),
                }).ToArray()),
                ["additional_records"] = records.Count(row => row.Text("kind") == "additional"),
                ["cover_url"] = "/api/v1/covers/" + id.ToString(CultureInfo.InvariantCulture),
            };
        }
        return result;
    }

    // ---- snapshot (persist / load / invalidate / filter / prune / summary / cached) ----

    /// <summary>Marks the review snapshot stale; the last completed snapshot stays readable.</summary>
    public void Invalidate()
    {
        lock (_snapshotLock) { _snapshotRevision++; _snapshotStale = true; }
    }

    /// <summary>Restores the persisted snapshot at startup as stale, dropping dead, absorbed or since-reviewed pairs.</summary>
    public void LoadPersisted()
    {
        JsonArray? groups;
        try { groups = _stored.Get(SnapshotSetting) is JsonObject saved ? saved["groups"] as JsonArray : null; }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or JsonException or InvalidOperationException) { return; }
        if (groups is null) return;
        var ids = groups.OfType<JsonObject>().SelectMany(group => group["entry_ids"] is JsonArray array ? array.Select(node => Long(node)) : []).Distinct().ToArray();
        var state = Read(c => (
            Live: ids.Where(id => Exists(c, id) && DuplicateEvidence.Canonical(c, id) == id).ToHashSet(),
            Dismissed: c.Query("SELECT left_entry_id,right_entry_id FROM duplicate_reviews WHERE decision='not_duplicate'").Select(row => Pair(DuplicateEvidence.Canonical(c, row.Integer("left_entry_id")), DuplicateEvidence.Canonical(c, row.Integer("right_entry_id")))).ToHashSet()));
        bool Keep(JsonObject pair)
        {
            var left = Long(pair["left_id"]); var right = Long(pair["right_id"]);
            return left != right && state.Live.Contains(left) && state.Live.Contains(right) && !state.Dismissed.Contains(Pair(left, right));
        }
        lock (_snapshotLock) { _snapshot = (_clock.GetTimestamp(), Filter(groups, Keep)); _snapshotStale = true; }
    }

    /// <summary>Prunes and splits cached components without rescoring (_filter_duplicate_snapshot_groups).</summary>
    public static JsonArray Filter(JsonArray groups, Func<JsonObject, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(groups); ArgumentNullException.ThrowIfNull(keep);
        var result = new JsonArray();
        foreach (var group in groups.OfType<JsonObject>())
        {
            var pairs = group["pairs"] is JsonArray all ? all.OfType<JsonObject>().Where(keep).Select(pair => (JsonObject)pair.DeepClone()).ToList() : [];
            var graph = new Dictionary<long, HashSet<long>>();
            foreach (var pair in pairs)
            {
                var a = Long(pair["left_id"]); var b = Long(pair["right_id"]);
                if (!graph.TryGetValue(a, out var x)) graph[a] = x = []; x.Add(b);
                if (!graph.TryGetValue(b, out var y)) graph[b] = y = []; y.Add(a);
            }
            var seen = new HashSet<long>();
            foreach (var start in graph.Keys.Order())
            {
                if (seen.Contains(start)) continue;
                var stack = new Stack<long>([start]); var members = new HashSet<long>();
                while (stack.Count != 0) { var id = stack.Pop(); if (!members.Add(id)) continue; seen.Add(id); foreach (var next in graph[id]) if (!members.Contains(next)) stack.Push(next); }
                var remaining = pairs.Where(pair => members.Contains(Long(pair["left_id"]))).ToArray();
                var copy = (JsonObject)group.DeepClone();
                copy["group_id"] = string.Join('-', members.Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
                copy["entry_ids"] = JArray(members.Order());
                copy["entries"] = new JsonArray((group["entries"] as JsonArray ?? []).OfType<JsonObject>().Where(entry => members.Contains(Long(entry["id"]))).Select(entry => (JsonNode?)entry.DeepClone()).ToArray());
                copy["pairs"] = new JsonArray(remaining.Select(pair => (JsonNode?)pair.DeepClone()).ToArray());
                copy["best_score"] = remaining.Length == 0 ? 0.0 : remaining.Max(pair => Num(pair["title_score"]));
                result.Add(copy);
            }
        }
        return result;
    }

    /// <summary>Applies a review to the completed snapshot at once instead of rescoring (_prune_reviewed_duplicate_snapshot).</summary>
    private JsonArray Prune(IReadOnlyCollection<long> affectedIds, string decision)
    {
        var affected = Read(c => affectedIds.Select(id => DuplicateEvidence.Canonical(c, id)).ToHashSet());
        var canonical = new Dictionary<long, long>();
        long Canon(long id) { if (!canonical.TryGetValue(id, out var value)) canonical[id] = value = Read(c => DuplicateEvidence.Canonical(c, id)); return value; }
        bool Keep(JsonObject pair)
        {
            var members = new HashSet<long> { Canon(Long(pair["left_id"])), Canon(Long(pair["right_id"])) };
            if (decision == "not_duplicate" && members.SetEquals(affected)) return false;
            return decision != "duplicate" || !affected.Overlaps(members);
        }
        lock (_snapshotLock)
        {
            if (_snapshot is not { } current) return [];
            var groups = Filter(current.Groups, Keep);
            _snapshot = (current.At, groups); _snapshotStale = true;
            return (JsonArray)groups.DeepClone();
        }
    }

    /// <summary>The last completed snapshot without catalogue work (duplicate_snapshot_summary).</summary>
    public JsonObject SnapshotSummary()
    {
        lock (_snapshotLock)
        {
            if (_snapshot is not { } current) return new JsonObject { ["ready"] = false, ["groups"] = 0, ["entries"] = 0, ["age_seconds"] = null, ["stale"] = true };
            return new JsonObject
            {
                ["ready"] = true, ["groups"] = current.Groups.Count,
                ["entries"] = current.Groups.OfType<JsonObject>().SelectMany(group => group["entry_ids"] is JsonArray ids ? ids.Select(node => Long(node)) : []).Distinct().Count(),
                ["age_seconds"] = Math.Round(_clock.GetElapsedTime(current.At).TotalSeconds, 3), ["stale"] = _snapshotStale,
            };
        }
    }

    /// <summary>The completed review snapshot, or null before the first scan (duplicate_groups_cached).</summary>
    public JsonArray? GroupsCached()
    {
        lock (_snapshotLock) return _snapshot is { } current ? (JsonArray)current.Groups.DeepClone() : null;
    }

    /// <summary>Matrix badges filtered from the completed snapshot in O(snapshot) (duplicate_candidates_cached).</summary>
    public JsonObject? CandidatesCached(IReadOnlyCollection<long>? entryIds)
    {
        var groups = GroupsCached();
        if (groups is null) return null;
        var wanted = entryIds is { Count: > 0 } requested ? Read(c => requested.Where(id => id > 0).Select(id => DuplicateEvidence.Canonical(c, id)).ToHashSet()) : [];
        var result = new SortedDictionary<long, (HashSet<long> Ids, double Best, List<JsonObject> Items)>();
        foreach (var pair in groups.OfType<JsonObject>().SelectMany(group => group["pairs"] is JsonArray pairs ? pairs.OfType<JsonObject>() : []))
        {
            var left = Long(pair["left_id"]); var right = Long(pair["right_id"]);
            if (left <= 0 || right <= 0) continue;
            foreach (var (id, other) in new[] { (left, right), (right, left) })
            {
                if (wanted.Count != 0 && !wanted.Contains(id)) continue;
                if (!result.TryGetValue(id, out var record)) record = ([], 0, []);
                record.Ids.Add(other);
                var candidate = (JsonObject)pair.DeepClone(); candidate["entry_id"] = other;
                if (id == right) { var matched = candidate["matched_title"]?.DeepClone(); candidate["matched_title"] = candidate["other_title"]?.DeepClone() ?? ""; candidate["other_title"] = matched ?? ""; }
                record.Items.Add(candidate);
                record.Best = Math.Max(record.Best, Math.Max(Num(pair["title_score"]), Num(pair["merge_score"]))); result[id] = record;
            }
        }
        var output = new JsonObject();
        foreach (var (id, record) in result)
            output[id.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["entry_ids"] = JArray(record.Ids.Order()), ["titles"] = new JsonArray(), ["best_score"] = record.Best,
                ["candidates"] = new JsonArray(record.Items.OrderByDescending(item => Math.Max(Num(item["title_score"]), Num(item["merge_score"]))).ThenBy(item => Long(item["entry_id"])).Select(item => (JsonNode?)item).ToArray()),
            };
        return output;
    }

    /// <summary>Exactly which pairs are automatic-merge eligible now (auto_merge_preview); the merge re-checks every pair.</summary>
    public JsonObject AutoMergePreview(int limit = 10000, CancellationToken cancellationToken = default)
    {
        var settings = _settings();
        var groups = Groups(false, cancellationToken);
        var preview = new JsonArray(); var pairCount = 0; var entryIds = new HashSet<long>();
        foreach (var group in groups.OfType<JsonObject>())
        {
            var eligible = (group["pairs"] as JsonArray ?? []).OfType<JsonObject>().Where(pair => Bool(pair["auto_merge_eligible"])).Select(pair => (JsonObject)pair.DeepClone()).ToArray();
            if (eligible.Length == 0) continue;
            pairCount += eligible.Length;
            var ids = eligible.SelectMany(pair => new[] { Long(pair["left_id"]), Long(pair["right_id"]) }).ToHashSet(); entryIds.UnionWith(ids);
            preview.Add(new JsonObject
            {
                ["group_id"] = group["group_id"]?.DeepClone(),
                ["entries"] = new JsonArray((group["entries"] as JsonArray ?? []).OfType<JsonObject>().Where(entry => ids.Contains(Long(entry["id"]))).Select(entry => (JsonNode?)entry.DeepClone()).ToArray()),
                ["pairs"] = new JsonArray(eligible.Select(pair => (JsonNode?)pair).ToArray()),
            });
            if (pairCount >= Math.Max(1, limit)) break;
        }
        return new JsonObject
        {
            ["groups"] = preview, ["pair_count"] = pairCount, ["entry_count"] = entryIds.Count,
            ["min_exact_titles"] = Math.Max(1, settings.DuplicateAutoMergeMinExactTitles), ["summary_threshold"] = Math.Clamp(settings.DuplicateAutoMergeSummaryThreshold, 0, 1),
        };
    }

    /// <summary>Queues one snapshot rebuild unless an automatic sweep (which rebuilds it) is pending; false when nothing was queued.</summary>
    public bool ScheduleSnapshotRefresh()
    {
        if (Jobs is not { } jobs || Paused) { lock (_snapshotLock) _snapshotStale = true; return false; }
        try
        {
            jobs.Enqueue(SnapshotJob, "snapshot", job =>
            {
                job.Report(message: "Scoring duplicate candidates");
                var groups = Groups(true, job.CancellationToken);
                job.Result(groups.Count.ToString(CultureInfo.InvariantCulture) + " duplicate group" + (groups.Count == 1 ? "" : "s"));
                if (_settings().AutomaticDuplicateMatching && groups.OfType<JsonObject>().Any(group => (group["pairs"] as JsonArray ?? []).OfType<JsonObject>().Any(pair => Bool(pair["auto_merge_eligible"]))))
                    ScheduleAutomatic(TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            });
            return true;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { return false; }
    }

    // ---- merge history (matching_review.history / detail) ----

    private const string LegacyAutomatic = """
        source_id='*' AND typeof(old_source_manga_id)='integer' AND typeof(new_source_manga_id)='integer'
        AND old_source_manga_id>0 AND new_source_manga_id>0 AND old_source_manga_id<>new_source_manga_id
        AND (change_type='merge:automatic-consolidation' OR change_type LIKE 'merge:automatic-safe:%')
        """;

    /// <summary>Recorded automatic merges, newest first, with compact projections (no source snapshots).</summary>
    public JsonObject History(int offset = 0, int limit = 50, string query = "")
    {
        offset = Math.Clamp(offset, 0, 1_000_000); limit = Math.Clamp(limit, 1, 100); query = (query ?? "").Trim();
        if (query.Length > 200) query = query[..200];
        var projection = """
            SELECT id,target_entry_id,duplicate_entry_id,reason,merged_at,repaired_at,
              CASE WHEN json_valid(target_snapshot_json) THEN json_extract(target_snapshot_json,'$.entry.preferred_title') END left_title,
              CASE WHEN json_valid(duplicate_snapshot_json) THEN json_extract(duplicate_snapshot_json,'$.entry.preferred_title') END right_title,
              0 legacy
            FROM automatic_merge_events
            UNION ALL
            SELECT -h.id,h.new_source_manga_id,h.old_source_manga_id,substr(h.change_type,7),h.changed_at,NULL,
              'Entry '||h.new_source_manga_id,'Entry '||h.old_source_manga_id,1
            FROM match_history h WHERE
            """ + " " + LegacyAutomatic + """

            AND NOT EXISTS (SELECT 1 FROM automatic_merge_events e WHERE e.target_entry_id=h.new_source_manga_id AND e.duplicate_entry_id=h.old_source_manga_id)
            """;
        var term = "%" + query.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        const string filter = " WHERE ($p0='' OR left_title LIKE $p1 ESCAPE '\\' OR right_title LIKE $p1 ESCAPE '\\' OR CAST(target_entry_id AS TEXT)=$p0 OR CAST(duplicate_entry_id AS TEXT)=$p0)";
        return Read(c =>
        {
            var total = Convert.ToInt64(c.Scalar("SELECT COUNT(*) FROM (" + projection + ")" + filter, P(query, term)), CultureInfo.InvariantCulture);
            var items = new JsonArray();
            foreach (var row in c.Query("SELECT * FROM (" + projection + ")" + filter + " ORDER BY merged_at DESC,id DESC LIMIT $p2 OFFSET $p3", P(query, term, limit, offset)))
            {
                var current = DuplicateEvidence.Canonical(c, row.Integer("target_entry_id"));
                var legacy = row.Integer("legacy") != 0; var repaired = row.Text("repaired_at");
                items.Add(new JsonObject
                {
                    ["id"] = row.Integer("id"), ["target_entry_id"] = row.Integer("target_entry_id"), ["duplicate_entry_id"] = row.Integer("duplicate_entry_id"),
                    ["reason"] = row.Text("reason"), ["merged_at"] = row.Text("merged_at"), ["repaired_at"] = row["repaired_at"] is null ? null : repaired,
                    ["left_title"] = row["left_title"] is null ? null : row.Text("left_title"), ["right_title"] = row["right_title"] is null ? null : row.Text("right_title"),
                    ["legacy"] = legacy, ["current_entry_id"] = Exists(c, current) ? current : null,
                    ["state"] = legacy ? "Older automatic merge · reason only" : repaired.Length != 0 ? "Re-evaluated" : "Recorded merge",
                });
            }
            return new JsonObject { ["items"] = items, ["total"] = total, ["offset"] = offset, ["limit"] = limit };
        });
    }

    /// <summary>One recorded merge with both before-snapshots and its evidence; throws <see cref="KeyNotFoundException"/> when unknown.</summary>
    public JsonObject HistoryDetail(long eventId) => Read(c =>
    {
        JsonObject row;
        var legacy = eventId < 0;
        if (legacy)
        {
            var older = c.Query("SELECT * FROM match_history WHERE id=$p0 AND " + LegacyAutomatic, P(-eventId)).SingleOrDefault();
            if (older is null || c.Scalar("SELECT 1 FROM automatic_merge_events WHERE target_entry_id=$p0 AND duplicate_entry_id=$p1", P(older.Integer("new_source_manga_id"), older.Integer("old_source_manga_id"))) is not null)
                throw new KeyNotFoundException("Merge event not found");
            row = new JsonObject
            {
                ["id"] = eventId, ["target_entry_id"] = older.Integer("new_source_manga_id"), ["duplicate_entry_id"] = older.Integer("old_source_manga_id"),
                ["reason"] = older.Text("change_type").Length > 6 ? older.Text("change_type")[6..] : "", ["merged_at"] = older.Text("changed_at"), ["repaired_at"] = null,
                ["evidence"] = new JsonObject { ["provenance"] = "Retained automatic merge marker; original snapshots and decision scores were not stored.", ["match_history_id"] = older.Integer("id") },
            };
        }
        else
        {
            var stored = c.Query("SELECT * FROM automatic_merge_events WHERE id=$p0", P(eventId)).SingleOrDefault() ?? throw new KeyNotFoundException("Merge event not found");
            row = new JsonObject
            {
                ["id"] = eventId, ["target_entry_id"] = stored.Integer("target_entry_id"), ["duplicate_entry_id"] = stored.Integer("duplicate_entry_id"), ["reason"] = stored.Text("reason"),
                ["merged_at"] = stored.Text("merged_at"), ["repaired_at"] = stored.Text("repaired_at"),
                ["evidence"] = DuplicateEvidence.Metadata(stored.ContainsKey("evidence_json") ? stored.Text("evidence_json", "{}") : "{}"),
                ["_target"] = DuplicateEvidence.Metadata(stored.Text("target_snapshot_json", "{}")), ["_duplicate"] = DuplicateEvidence.Metadata(stored.Text("duplicate_snapshot_json", "{}")),
            };
        }
        var target = Long(row["target_entry_id"]); var duplicate = Long(row["duplicate_entry_id"]);
        var snapshots = legacy
            ? new[] { new JsonObject { ["entry"] = new JsonObject { ["id"] = target } }, new JsonObject { ["entry"] = new JsonObject { ["id"] = duplicate } } }
            : new[] { (JsonObject)row["_target"]!, (JsonObject)row["_duplicate"]! };
        static JsonObject EntryOf(JsonObject snapshot) => snapshot["entry"] as JsonObject ?? new JsonObject();
        static IEnumerable<JsonObject> RowsOf(JsonObject snapshot, string key) => snapshot[key] is JsonArray array ? array.OfType<JsonObject>() : [];
        var before = new JsonArray();
        foreach (var snapshot in snapshots.OrderBy(snapshot => Long(EntryOf(snapshot)["id"]) == target ? 0 : 1))
        {
            var entry = EntryOf(snapshot); var id = Long(entry["id"]);
            var picked = new JsonObject(); foreach (var key in new[] { "id", "preferred_title", "description", "author", "artist", "thumbnail_url" }) picked[key] = entry[key]?.DeepClone();
            before.Add(new JsonObject
            {
                ["entry"] = picked, ["role"] = id == target ? "Surviving entry" : id == duplicate ? "Absorbed entry" : "Original entry",
                ["titles"] = JArray(RowsOf(snapshot, "titles").Select(title => Str(title["title"])).Distinct(StringComparer.Ordinal)),
                ["records"] = new JsonArray(RowsOf(snapshot, "matches").Concat(RowsOf(snapshot, "variants")).Select(record =>
                {
                    var value = new JsonObject(); foreach (var key in new[] { "source_id", "source_manga_id", "source_title", "description", "author", "artist" }) value[key] = record[key]?.DeepClone();
                    return (JsonNode?)value;
                }).ToArray()),
                ["available"] = entry.Count != 0 && !legacy,
            });
        }
        var current = DuplicateEvidence.Canonical(c, target);
        return new JsonObject
        {
            ["id"] = eventId, ["merged_at"] = row["merged_at"]?.DeepClone(), ["reason"] = row["reason"]?.DeepClone(), ["repaired_at"] = row["repaired_at"]?.DeepClone(),
            ["before"] = before, ["evidence"] = row["evidence"]?.DeepClone(), ["legacy"] = legacy, ["current_entry_id"] = Exists(c, current) ? current : null,
        };
    });
}
