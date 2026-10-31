using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Reader;

/// <summary>Reader projection of the existing catalogue/origins schema; never creates source membership.</summary>
public sealed class ReaderRepository
{
    private readonly SqliteDatabase _database;
    private readonly object _sourceWriteGate;
    private readonly ServingProfileRepository _profiles;
    private readonly TimeProvider _clock;

    /// <param name="sourceWriteGate">Required shared gate: all catalogue membership, deletion, merge and split writers must acquire this same object. No await/network work may occur while holding it.</param>
    public ReaderRepository(SqliteDatabase database, object sourceWriteGate, TimeProvider? timeProvider = null)
    {
        _database = database; _sourceWriteGate = sourceWriteGate; _clock = timeProvider ?? TimeProvider.System;
        _profiles = new(database, sourceWriteGate, _clock);
    }

    public long CanonicalEntryId(long entryId) => _database.Read(DatabaseFile.Catalog, session => Canonical(session, entryId));

    public IReadOnlyList<JsonObject> ListChapters(long entryId)
    {
        lock (_sourceWriteGate)
        {
            var context = Context(entryId); entryId = context.EntryId;
            if (context.Enabled.Count == 0) return [];
            var chapters = _database.Read(DatabaseFile.Origins, session => session.Query("""
                SELECT * FROM virtual_chapters WHERE entry_id=$id AND hidden=0
                ORDER BY CASE WHEN chapter_number>=0 THEN 0 ELSE 1 END,chapter_number DESC,id DESC
                """, ("$id", entryId)));
            var candidates = _database.Read(DatabaseFile.Origins, session => session.Query("""
                SELECT cc.id,cc.virtual_chapter_id,cc.source_id,cc.source_name,cc.name,cc.upload_date,cc.scanlator,
                       cc.page_count,cc.quality_score,cc.source_manga_id,cc.source_chapter_id
                FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id
                WHERE vc.entry_id=$id AND cc.active=1
                """, ("$id", entryId))).Select(Json).Where(row => Admitted(row, context)).ToArray();
            var grouped = candidates.GroupBy(row => ReaderValue.Integer(row, "virtual_chapter_id")).ToDictionary(group => group.Key, group => group.ToList());
            var result = new List<JsonObject>();
            foreach (var stored in chapters)
            {
                if (!grouped.TryGetValue(stored.Integer("id"), out var active)) continue;
                Decorate(active, context); Rank(active, context, stored.Integer("id")); var chosen = active[0]; var row = Json(stored);
                row["candidate_count"] = active.Count; row["upload_date"] = active.Max(item => ReaderValue.Integer(item, "upload_date"));
                row["url"] = stored.Integer("id").ToString(CultureInfo.InvariantCulture);
                row["display_name"] = ChapterResolver.DisplayName(stored.Number("chapter_number", -1), ReaderValue.Text(chosen, "name"));
                row["serving_candidate_id"] = ReaderValue.Integer(chosen, "id"); row["serving_source_id"] = ReaderValue.Text(chosen, "source_id");
                row["serving_source_name"] = ReaderValue.Text(chosen, "source_name"); row["serving_source_entry_id"] = ReaderValue.Integer(chosen, "source_entry_id", entryId);
                row["serving_source_entry_title"] = ReaderValue.Text(chosen, "source_entry_title"); row["serving_official"] = ReaderValue.Flag(chosen, "official"); result.Add(row);
            }
            return result;
        }
    }

    public JsonObject ChapterDetails(long chapterId)
    {
        lock (_sourceWriteGate)
        {
            var snapshot = _database.Read(DatabaseFile.Origins, session =>
            {
                var chapter = session.Query("SELECT * FROM virtual_chapters WHERE id=$id", ("$id", chapterId)).FirstOrDefault() ?? throw new KeyNotFoundException("Chapter not found");
                return (Chapter: chapter, Active: session.Query("SELECT * FROM chapter_candidates WHERE virtual_chapter_id=$id AND active=1", ("$id", chapterId)),
                    Rejected: session.Query("""
                        SELECT cc.*,r.rejected_at FROM chapter_candidates cc JOIN chapter_candidate_rejections r
                        ON r.source_id=cc.source_id AND r.source_chapter_id=cc.source_chapter_id
                        WHERE cc.virtual_chapter_id=$id AND cc.active=0
                        """, ("$id", chapterId)), Served: session.Query("SELECT * FROM served_origin WHERE virtual_chapter_id=$id", ("$id", chapterId)).FirstOrDefault());
            });
            var context = Context(snapshot.Chapter.Integer("entry_id"));
            var active = snapshot.Active.Select(Json).Where(row => Admitted(row, context)).ToList();
            var rejected = snapshot.Rejected.Select(Json).ToList();
            Decorate(active, context); Decorate(rejected, context);
            var maximum = active.Select(row => Math.Max(0, ReaderValue.Integer(row, "page_count"))).DefaultIfEmpty().Max();
            Rank(active, context, chapterId); Rank(rejected, context, chapterId, applyProfile: false, maximumPages: maximum);
            return new() { ["chapter"] = Json(snapshot.Chapter), ["candidates"] = Array(active), ["rejected_candidates"] = Array(rejected),
                ["served"] = snapshot.Served is null ? null : Json(snapshot.Served), ["would_serve_candidate_id"] = active.Count == 0 ? null : JsonValue.Create(ReaderValue.Integer(active[0], "id")) };
        }
    }

    public ReaderPageSnapshot PageSnapshot(long chapterId)
    {
        lock (_sourceWriteGate)
        {
            var detail = ChapterDetails(chapterId); var candidates = detail["candidates"]!.AsArray().OfType<JsonObject>().Select(row => (JsonObject)row.DeepClone()).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("No active source candidate for this chapter");
            var cached = _database.Read(DatabaseFile.Origins, session => session.Query("""
                SELECT cp.candidate_id,cp.page_index,cp.private_url,cp.updated_at FROM candidate_pages cp
                JOIN chapter_candidates cc ON cc.id=cp.candidate_id WHERE cc.virtual_chapter_id=$id
                ORDER BY cp.candidate_id,cp.page_index
                """, ("$id", chapterId))).GroupBy(row => row.Integer("candidate_id"))
                .ToDictionary(group => group.Key, group => (IReadOnlyList<JsonObject>)group.Select(Json).ToArray());
            return new(ReaderValue.Integer(detail["chapter"]!.AsObject(), "entry_id"), candidates, cached);
        }
    }

    public JsonObject PageFetchInfo(long candidateId, int pageIndex)
    {
        if (pageIndex < 0) throw new KeyNotFoundException("Page not found");
        lock (_sourceWriteGate)
        {
            RequireMembership(candidateId);
            var row = _database.Read(DatabaseFile.Origins, session => session.Query("""
                SELECT cp.private_url,cc.source_manga_id,cc.source_chapter_id,cc.source_chapter_index,cc.source_chapter_url
                FROM candidate_pages cp JOIN chapter_candidates cc ON cc.id=cp.candidate_id
                WHERE cp.candidate_id=$id AND cp.page_index=$index
                """, ("$id", candidateId), ("$index", pageIndex)).FirstOrDefault());
            return row is null ? throw new KeyNotFoundException("Page not found") : Json(row);
        }
    }

    public void SetChapterIndex(long candidateId, int chapterIndex)
    {
        lock (_sourceWriteGate)
        {
            RequireMembership(candidateId);
            _database.Write(DatabaseFile.Origins, session => session.Execute("UPDATE chapter_candidates SET source_chapter_index=$index WHERE id=$id", ("$index", chapterIndex), ("$id", candidateId)));
        }
    }

    public void RecordServed(long chapterId, JsonObject candidate)
    {
        lock (_sourceWriteGate)
        {
            var row = RequireMembership(ReaderValue.Integer(candidate, "id"), chapterId, true);
            RequireSnapshot(candidate, row);
            _database.Write(DatabaseFile.Origins, session => RecordServed(session, chapterId, row, Timestamp()));
        }
    }

    public void PersistPages(long chapterId, JsonObject candidate, IReadOnlyList<string> pages, int chapterIndex)
    {
        lock (_sourceWriteGate)
        {
            var row = RequireMembership(ReaderValue.Integer(candidate, "id"), chapterId, true); var timestamp = Timestamp();
            RequireSnapshot(candidate, row);
            _database.Write(DatabaseFile.Origins, session =>
            {
                session.Execute("DELETE FROM candidate_pages WHERE candidate_id=$id", ("$id", row.Integer("id")));
                for (var index = 0; index < pages.Count; index++) session.Execute("INSERT INTO candidate_pages(candidate_id,page_index,private_url,updated_at) VALUES($id,$index,$url,$time)",
                    ("$id", row.Integer("id")), ("$index", index), ("$url", pages[index]), ("$time", timestamp));
                session.Execute("UPDATE chapter_candidates SET page_count=$count,source_chapter_index=$index,last_seen=$time WHERE id=$id", ("$count", pages.Count), ("$index", chapterIndex), ("$time", timestamp), ("$id", row.Integer("id")));
                RecordServed(session, chapterId, row, timestamp); return 0;
            });
            ReconcileSplitChapters(row.Integer("entry_id"));
        }
    }

    private DatabaseRow RequireMembership(long candidateId, long? chapterId = null, bool active = false)
    {
        var row = _database.Read(DatabaseFile.Origins, session => session.Query("""
            SELECT cc.*,vc.entry_id FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE cc.id=$id
            """, ("$id", candidateId)).FirstOrDefault());
        if (row is null || chapterId.HasValue && row.Integer("virtual_chapter_id") != chapterId.Value || active && !row.Flag("active")) throw new KeyNotFoundException("Candidate no longer belongs to this chapter");
        var owner = _database.Read(DatabaseFile.Catalog, session =>
        {
            var expected = Canonical(session, row.Integer("entry_id"));
            var owners = session.Query("""
                SELECT entry_id FROM source_matches WHERE source_id=$source AND source_manga_id=$manga AND active=1
                UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1
                """, ("$source", row.Text("source_id")), ("$manga", row.Integer("source_manga_id"))).Select(item => Canonical(session, item.Integer("entry_id"))).Distinct().ToArray();
            return owners.Length == 1 && owners[0] == expected && session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", expected)) is not null;
        });
        if (!owner) throw new KeyNotFoundException("Candidate source record was detached or moved");
        return row;
    }

    private ReaderContext Context(long entryId) => _database.Read(DatabaseFile.Catalog, session =>
    {
        var canonical = Canonical(session, entryId);
        var sources = session.Query("SELECT * FROM source_config WHERE available=1");
        var labels = session.Query("""
            SELECT source_id,source_manga_id,source_title,entry_id FROM source_matches WHERE entry_id=$id AND active=1
            UNION ALL SELECT source_id,source_manga_id,source_title,entry_id FROM source_variants WHERE entry_id=$id AND active=1
            """, ("$id", canonical)).GroupBy(row => (row.Text("source_id"), row.Integer("source_manga_id"))).ToDictionary(group => group.Key, group => group.Last());
        var profile = _profiles.Context(canonical);
        return new ReaderContext(canonical, sources.Where(row => row.Flag("enabled_language") && row.Flag("enabled_chapters")).Select(row => row.Text("source_id")).ToHashSet(StringComparer.Ordinal),
            sources.ToDictionary(row => row.Text("source_id"), row => row.Integer("priority"), StringComparer.Ordinal),
            sources.Where(row => ChapterResolver.ProbablyOfficialSource(row.Text("name"), row.Text("base_url"))).Select(row => row.Text("source_id")).ToHashSet(StringComparer.Ordinal), labels, profile.Profile, profile.Languages);
    });

    private static bool Admitted(JsonObject candidate, ReaderContext context) => context.Enabled.Contains(ReaderValue.Text(candidate, "source_id"))
        && context.Labels.ContainsKey((ReaderValue.Text(candidate, "source_id"), ReaderValue.Integer(candidate, "source_manga_id")));

    private static void RequireSnapshot(JsonObject expected, DatabaseRow current)
    {
        if (ReaderValue.Text(expected, "source_id") != current.Text("source_id") || ReaderValue.Integer(expected, "source_manga_id") != current.Integer("source_manga_id")
            || ReaderValue.Integer(expected, "source_chapter_id") != current.Integer("source_chapter_id"))
            throw new KeyNotFoundException("Candidate source provenance changed during page lookup");
    }

    public void ReconcileVisibility(long entryId)
    {
        lock (_sourceWriteGate) ReconcileSplitChapters(entryId);
    }

    private void ReconcileSplitChapters(long entryId)
    {
        var context = Context(entryId); entryId = context.EntryId; var enabled = context.Enabled;
        _database.Write(DatabaseFile.Origins, session =>
        {
            var chapters = session.Query("SELECT * FROM virtual_chapters WHERE entry_id=$id AND chapter_number>=0 AND chapter_key NOT LIKE 'g:%'", ("$id", entryId));
            var pages = session.Query("""
                SELECT cc.virtual_chapter_id,cc.source_id,MAX(cc.page_count) max_pages FROM chapter_candidates cc
                JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id WHERE vc.entry_id=$id AND cc.active=1 AND cc.page_count>0
                GROUP BY cc.virtual_chapter_id,cc.source_id
                """, ("$id", entryId)).Where(row => enabled.Contains(row.Text("source_id"))).GroupBy(row => row.Integer("virtual_chapter_id"))
                .ToDictionary(group => group.Key, group => group.Max(row => row.Integer("max_pages")));
            session.Execute("UPDATE virtual_chapters SET hidden=0 WHERE entry_id=$id", ("$id", entryId));
            foreach (var group in chapters.GroupBy(row => Math.Floor(row.Number("chapter_number"))))
            {
                var whole = group.FirstOrDefault(row => Math.Abs(row.Number("chapter_number") - group.Key) < 1e-6);
                var parts = group.Where(row => row.Number("chapter_number") > group.Key && row.Number("chapter_number") < group.Key + 1).ToArray();
                if (whole is null || parts.Length < 2) continue;
                var wholePages = pages.GetValueOrDefault(whole.Integer("id")); var partPages = parts.Select(row => pages.GetValueOrDefault(row.Integer("id"))).ToArray();
                if (wholePages > 0 && partPages.All(count => count > 0) && Math.Abs(partPages.Sum() - wholePages) <= 1)
                    foreach (var part in parts) session.Execute("UPDATE virtual_chapters SET hidden=1 WHERE id=$id", ("$id", part.Integer("id")));
            }
            return 0;
        });
    }

    private static void Decorate(IEnumerable<JsonObject> candidates, ReaderContext context)
    {
        foreach (var row in candidates)
        {
            context.Labels.TryGetValue((ReaderValue.Text(row, "source_id"), ReaderValue.Integer(row, "source_manga_id")), out var label);
            row["source_entry_id"] = label?.Integer("entry_id") ?? context.EntryId; row["source_entry_title"] = label?.Text("source_title") ?? "";
            row["official"] = ChapterResolver.OfficialRelease(row, context.Official);
        }
    }

    private void Rank(List<JsonObject> rows, ReaderContext context, long chapterId, bool applyProfile = true, long? maximumPages = null)
    {
        var maxPages = maximumPages ?? rows.Select(row => Math.Max(0, ReaderValue.Integer(row, "page_count"))).DefaultIfEmpty().Max();
        foreach (var row in rows) row["resolver_score"] = ChapterResolver.Score(row, context.Priorities, maxPages, context.Official);
        if (applyProfile) context.Profile.Rank(rows, context.Languages);
        // LINQ ordering is stable, matching Python list.sort on tied scores/names.
        var sorted = rows.OrderByDescending(row => ReaderValue.Number(row, "resolver_score")).ThenBy(row => ReaderValue.Text(row, "source_name"), AutoSourcarr.Core.Text.UnicodeCodePointComparer.Instance).ToArray();
        rows.Clear(); rows.AddRange(sorted);
        if (!applyProfile || rows.Count < 2) return;
        var preferred = _database.Read(DatabaseFile.Catalog, session =>
        {
            var policy = session.Query("SELECT policy_json FROM serving_upgrade_policies WHERE entry_id=$id", ("$id", context.EntryId)).FirstOrDefault();
            if (policy is null || JsonNode.Parse(policy.Text("policy_json"))?["enabled"]?.GetValue<bool>() != true) return (long?)null;
            if (session.Scalar("""
                SELECT 1 FROM source_matches WHERE entry_id=$id AND active=1 AND locked=1
                UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND active=1 AND locked=1 LIMIT 1
                """, ("$id", context.EntryId)) is not null) return null;
            return session.Query("SELECT candidate_id FROM serving_choices WHERE virtual_chapter_id=$chapter AND entry_id=$id", ("$chapter", chapterId), ("$id", context.EntryId)).FirstOrDefault()?.Integer("candidate_id");
        });
        var index = preferred.HasValue ? rows.FindIndex(row => ReaderValue.Integer(row, "id") == preferred.Value) : -1;
        if (index > 0) { var row = rows[index]; rows.RemoveAt(index); rows.Insert(0, row); }
    }

    private static int RecordServed(DatabaseSession session, long chapterId, DatabaseRow candidate, string timestamp)
    {
        var old = session.Query("SELECT candidate_id FROM served_origin WHERE virtual_chapter_id=$id", ("$id", chapterId)).FirstOrDefault();
        var reason = old is null ? "initial" : old.Integer("candidate_id") != candidate.Integer("id") ? "resolver-upgrade" : "repeat";
        session.Execute("""
            INSERT INTO served_origin(virtual_chapter_id,candidate_id,selected_at,reason) VALUES($chapter,$id,$time,$reason)
            ON CONFLICT(virtual_chapter_id) DO UPDATE SET candidate_id=excluded.candidate_id,selected_at=excluded.selected_at,reason=excluded.reason
            """, ("$chapter", chapterId), ("$id", candidate.Integer("id")), ("$time", timestamp), ("$reason", reason));
        if (old is null || old.Integer("candidate_id") != candidate.Integer("id")) session.Execute("""
            INSERT INTO origin_history(virtual_chapter_id,candidate_id,source_id,source_name,source_manga_id,source_chapter_id,served_at,reason)
            VALUES($chapter,$id,$source,$name,$manga,$sourceChapter,$time,$reason)
            """, ("$chapter", chapterId), ("$id", candidate.Integer("id")), ("$source", candidate.Text("source_id")), ("$name", candidate.Text("source_name")),
            ("$manga", candidate.Integer("source_manga_id")), ("$sourceChapter", candidate.Integer("source_chapter_id")), ("$time", timestamp), ("$reason", reason));
        return 0;
    }

    private static long Canonical(DatabaseSession session, long entryId)
    {
        var seen = new HashSet<long>(); var current = entryId;
        while (seen.Add(current))
        {
            var row = session.Query("SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=$id", ("$id", current)).FirstOrDefault();
            if (row is null) return current; current = row.Integer("canonical_entry_id");
        }
        throw new InvalidDataException("Entry alias cycle; catalogue identity requires repair");
    }
    private string Timestamp() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
    private static JsonObject Json(DatabaseRow row) => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))!.AsObject();
    private static JsonArray Array(IEnumerable<JsonObject> rows) => new(rows.Select(row => (JsonNode)row).ToArray());
    private sealed record ReaderContext(long EntryId, HashSet<string> Enabled, Dictionary<string, long> Priorities, HashSet<string> Official,
        Dictionary<(string, long), DatabaseRow> Labels, ServingProfile Profile, IReadOnlyDictionary<string, string> Languages);
}

public sealed record ReaderPageSnapshot(long EntryId, IReadOnlyList<JsonObject> Candidates, IReadOnlyDictionary<long, IReadOnlyList<JsonObject>> Cached);
