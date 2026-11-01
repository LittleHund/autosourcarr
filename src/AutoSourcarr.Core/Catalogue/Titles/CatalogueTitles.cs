using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Core.Catalogue.Titles;

/// <summary>A refused title change: 400 (not a meaningful title) or 404 (entry not found).</summary>
public sealed class CatalogueTitleException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>One observed title of an entry (preferred, stored alias, or a source record's display title).</summary>
public sealed record EntryTitleRow(string Title, string Normalized, string TitleType, string Language, string SourceId, long SourceMangaId)
{
    public JsonObject ToJson() => new()
    {
        ["title"] = Title, ["normalized"] = Normalized, ["title_type"] = TitleType, ["language"] = Language,
        ["source_id"] = SourceId, ["source_manga_id"] = SourceMangaId,
    };
}

/// <summary>
/// Catalogue titles (port-map "Catalogue/Titles", 22 methods): persistent main-title overrides, the merge alias cache,
/// the preferred title and its bound-record consensus (<see cref="CatalogueTitleConsensus"/>), stored aliases, the strict
/// automatic title evidence gate (through <see cref="MatchScorer"/>) and the provenance-bound source title aliases
/// (whole titles, <see cref="WholeTitleAliases"/>). Manual titles change only on an explicit user action, which is
/// recorded in the decision log.
/// </summary>
public sealed class CatalogueTitles
{
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDecisionLog _decisions;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<long, long> _aliases = new();
    private volatile bool _aliasesLoaded;

    public CatalogueTitles(SqliteDatabase database, object sourceWriteGate, IDecisionLog decisions, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(decisions);
        _database = database; _gate = sourceWriteGate; _decisions = decisions; _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Called after a title change so caches (duplicate snapshot, search) can be dropped.</summary>
    public Action<long>? TitlesChanged { get; init; }

    // ---- persistent overrides --------------------------------------------------------------------------------------

    /// <summary>_apply_persistent_title_overrides: re-applies user-selected main titles kept outside resettable rows.</summary>
    public int ApplyPersistentTitleOverrides() => Write(session =>
    {
        var rows = session.Query("""
            SELECT o.entry_id,o.title FROM preferred_title_overrides o JOIN entries e ON e.id=o.entry_id
            WHERE e.preferred_title<>o.title OR e.manual_preferred_title<>1
            """);
        foreach (var row in rows)
            session.Execute("UPDATE entries SET preferred_title=$title,manual_preferred_title=1 WHERE id=$id", ("$title", row.Text("title")), ("$id", row.Integer("entry_id")));
        return rows.Count;
    });

    /// <summary>_persistent_title_override: the user's main title for this exact entry, or "".</summary>
    public string PersistentTitleOverride(long entryId) => Read(session =>
        (session.Scalar("SELECT title FROM preferred_title_overrides WHERE entry_id=$id", ("$id", Canonical(session, entryId))) as string ?? "").Trim());

    /// <summary>
    /// _apply_source_title_override: a manual title is scoped to the entry it was set on and is never inherited through a
    /// source record (manual_title_scopes is audit only), so this always returns "".
    /// </summary>
    public static string ApplySourceTitleOverride(long entryId, string sourceId, long sourceMangaId)
    {
        _ = (entryId, sourceId, sourceMangaId);
        return "";
    }

    // ---- merge alias cache -----------------------------------------------------------------------------------------

    /// <summary>_load_alias_cache.</summary>
    public void LoadAliasCache()
    {
        var rows = Read(session => session.Query("SELECT old_entry_id,canonical_entry_id FROM entry_aliases"));
        _aliases.Clear();
        foreach (var row in rows) _aliases[row.Integer("old_entry_id")] = row.Integer("canonical_entry_id");
        _aliasesLoaded = true;
    }

    /// <summary>canonical_entry_id over the cache with path compression (loads the cache on first use).</summary>
    public long CanonicalEntryId(long entryId)
    {
        if (!_aliasesLoaded) LoadAliasCache();
        var current = entryId; var seen = new List<long>();
        while (!seen.Contains(current))
        {
            seen.Add(current);
            if (!_aliases.TryGetValue(current, out var next)) break;
            current = next;
        }
        foreach (var old in seen.Take(seen.Count - 1)) _aliases[old] = current;
        return current;
    }

    /// <summary>_remember_entry_alias: persists old → canonical, moves separate-record exclusions, then publishes it.</summary>
    public void RememberEntryAlias(long oldEntryId, long canonicalEntryId, string reason, bool publish = true)
    {
        var canonical = CanonicalEntryId(canonicalEntryId);
        if (oldEntryId == canonical) return;
        var stamp = Now();
        Write(session =>
        {
            session.Execute("""
                INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES($old,$new,$reason,$time)
                ON CONFLICT(old_entry_id) DO UPDATE SET canonical_entry_id=excluded.canonical_entry_id,reason=excluded.reason,merged_at=excluded.merged_at
                """, ("$old", oldEntryId), ("$new", canonical), ("$reason", reason), ("$time", stamp));
            session.Execute("UPDATE entry_aliases SET canonical_entry_id=$new WHERE canonical_entry_id=$old AND old_entry_id<>$new", ("$new", canonical), ("$old", oldEntryId));
            foreach (var row in session.Query("SELECT * FROM manual_separate_records WHERE target_entry_id=$old", ("$old", oldEntryId)))
                session.Execute("""
                    INSERT INTO manual_separate_records(target_entry_id,source_id,source_manga_id,separate_entry_id_hint,source_title,created_at,updated_at)
                    VALUES($target,$source,$manga,$hint,$title,$created,$time) ON CONFLICT(target_entry_id,source_id,source_manga_id) DO UPDATE SET
                      separate_entry_id_hint=COALESCE(excluded.separate_entry_id_hint,manual_separate_records.separate_entry_id_hint),
                      source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE manual_separate_records.source_title END,
                      updated_at=excluded.updated_at
                    """, ("$target", canonical), ("$source", row.Text("source_id")), ("$manga", row.Integer("source_manga_id")),
                    ("$hint", row["separate_entry_id_hint"]), ("$title", row.Text("source_title")), ("$created", row.Text("created_at")), ("$time", stamp));
            session.Execute("DELETE FROM manual_separate_records WHERE target_entry_id=$old", ("$old", oldEntryId));
            session.Execute("UPDATE manual_separate_records SET separate_entry_id_hint=$new,updated_at=$time WHERE separate_entry_id_hint=$old",
                ("$new", canonical), ("$time", stamp), ("$old", oldEntryId));
            return 0;
        });
        if (publish) PublishEntryAliasCache(oldEntryId, canonical);
    }

    /// <summary>_publish_entry_alias_cache.</summary>
    public void PublishEntryAliasCache(long oldEntryId, long canonicalEntryId)
    {
        _aliases[oldEntryId] = canonicalEntryId;
        foreach (var (alias, target) in _aliases.ToArray())
            if (target == oldEntryId && alias != canonicalEntryId) _aliases[alias] = canonicalEntryId;
        TitlesChanged?.Invoke(canonicalEntryId);
    }

    /// <summary>_repair_entry_alias_integrity: rebuilds alias chains whose target vanished, from merge history.</summary>
    public JsonObject RepairEntryAliasIntegrity()
    {
        var (repaired, removed) = Write(session =>
        {
            var aliasMap = session.Query("SELECT old_entry_id,canonical_entry_id FROM entry_aliases")
                .ToDictionary(row => row.Integer("old_entry_id"), row => row.Integer("canonical_entry_id"));
            var history = new Dictionary<long, long>();
            foreach (var row in session.Query("""
                SELECT old_source_manga_id old_id,new_source_manga_id new_id FROM match_history
                WHERE source_id='*' AND change_type LIKE 'merge:%' AND old_source_manga_id IS NOT NULL AND new_source_manga_id IS NOT NULL ORDER BY id
                """))
            {
                var (oldId, newId) = (row.Integer("old_id"), row.Integer("new_id"));
                if (oldId > 0 && newId > 0 && oldId != newId) history[oldId] = newId;
            }
            var referenced = aliasMap.Keys.Concat(aliasMap.Values).Concat(history.Keys).Concat(history.Values).Distinct().Order().ToArray();
            var existing = new HashSet<long>();
            foreach (var batch in referenced.Chunk(500))
                existing.UnionWith(session.Query("SELECT id FROM entries WHERE id IN (" + string.Join(',', batch.Select(id => id.ToString(CultureInfo.InvariantCulture))) + ")")
                    .Select(row => row.Integer("id")));
            var resolved = new Dictionary<long, long?>();
            long? Resolve(long start)
            {
                var current = start; var path = new List<long>(); var seen = new HashSet<long>(); long? target = null;
                while (!seen.Contains(current))
                {
                    if (existing.Contains(current)) { target = current; break; }
                    if (resolved.TryGetValue(current, out var known)) { target = known; break; }
                    seen.Add(current); path.Add(current);
                    long? next = aliasMap.TryGetValue(current, out var aliased) ? aliased : null;
                    if (next is null || next == current) next = history.TryGetValue(current, out var merged) ? merged : null;
                    if (next is null || next == current) break;
                    current = next.Value;
                }
                foreach (var item in path) resolved[item] = target;
                return target;
            }
            int fixedCount = 0, removedCount = 0; var stamp = Now();
            foreach (var oldId in aliasMap.Keys.Concat(history.Keys).Distinct().Order().ToArray())
            {
                if (existing.Contains(oldId))
                {
                    if (aliasMap.ContainsKey(oldId)) { session.Execute("DELETE FROM entry_aliases WHERE old_entry_id=$id", ("$id", oldId)); removedCount++; }
                    continue;
                }
                var target = Resolve(oldId);
                if (target is null || target == oldId)
                {
                    if (aliasMap.ContainsKey(oldId)) { session.Execute("DELETE FROM entry_aliases WHERE old_entry_id=$id", ("$id", oldId)); removedCount++; }
                    continue;
                }
                if (!aliasMap.TryGetValue(oldId, out var current) || current != target)
                {
                    session.Execute("""
                        INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES($old,$new,'recovered-from-merge-history',$time)
                        ON CONFLICT(old_entry_id) DO UPDATE SET canonical_entry_id=excluded.canonical_entry_id,reason=excluded.reason,merged_at=excluded.merged_at
                        """, ("$old", oldId), ("$new", target.Value), ("$time", stamp));
                    fixedCount++;
                }
            }
            return (fixedCount, removedCount);
        });
        LoadAliasCache();
        return new JsonObject { ["repaired"] = repaired, ["removed"] = removed };
    }

    // ---- preferred title -------------------------------------------------------------------------------------------

    /// <summary>entry_preferred_title ("No cover" when empty, as released).</summary>
    public string EntryPreferredTitle(long entryId) => Read(session =>
    {
        var id = Canonical(session, entryId);
        var row = session.Query("SELECT preferred_title FROM entries WHERE id=$id", ("$id", id)).FirstOrDefault()
            ?? throw new CatalogueTitleException(404, "Entry not found");
        return row.Text("preferred_title") is { Length: > 0 } title ? title : "No cover";
    });

    /// <summary>_most_bound_title_from_records: one vote per bound record (see <see cref="CatalogueTitleConsensus"/>).</summary>
    public static string MostBoundTitleFromRecords(IEnumerable<NativeTitleVote> records, string currentTitle = "") =>
        CatalogueTitleConsensus.MostBoundTitle(records, currentTitle);

    /// <summary>_sync_preferred_title_from_bound_records: refreshes an automatic main title; manual titles are untouched.</summary>
    public string SyncPreferredTitleFromBoundRecords(long entryId) => Write(session => SyncPreferredTitle(session, Canonical(session, entryId), Now()));

    internal static string SyncPreferredTitle(DatabaseSession session, long entryId, string stamp)
    {
        var entry = session.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", entryId)).FirstOrDefault();
        if (entry is null) return "";
        var current = entry.Text("preferred_title");
        if (entry.Flag("manual_preferred_title")) return current;
        var wanted = CatalogueTitleConsensus.MostBoundTitle(BoundVotes(session, entryId), current);
        if (wanted.Length > 0 && TextMatcher.NormalizeTitle(wanted) != TextMatcher.NormalizeTitle(current))
        {
            var changed = session.Execute("UPDATE entries SET preferred_title=$title,updated_at=$time WHERE id=$id AND manual_preferred_title=0 AND preferred_title=$old",
                ("$title", wanted), ("$time", stamp), ("$id", entryId), ("$old", current));
            if (changed == 0) return session.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) as string ?? "";
        }
        return wanted.Length > 0 ? wanted : current;
    }

    private static IReadOnlyList<NativeTitleVote> BoundVotes(DatabaseSession session, long entryId) => session.Query("""
        SELECT sm.source_title,COALESCE(sc.lang,'') lang,COALESCE(sc.priority,1000) priority,0 is_variant
          FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1
        UNION ALL
        SELECT sv.source_title,COALESCE(sc.lang,'') lang,COALESCE(sc.priority,1000) priority,1 is_variant
          FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1
        """, ("$id", entryId)).Select(row => new NativeTitleVote(row.Text("source_title"), row.Text("lang"), row.Integer("priority", 1000), row.Flag("is_variant"))).ToArray();

    /// <summary>_refresh_bound_preferred_titles: bulk refresh of every automatic main title.</summary>
    public JsonObject RefreshBoundPreferredTitles() => Write(session =>
    {
        var entries = session.Query("SELECT id,preferred_title FROM entries WHERE manual_preferred_title=0");
        var changed = 0; var stamp = Now();
        foreach (var entry in entries)
        {
            var id = entry.Integer("id"); var current = entry.Text("preferred_title");
            var wanted = CatalogueTitleConsensus.MostBoundTitle(BoundVotes(session, id), current);
            if (wanted.Length == 0 || TextMatcher.NormalizeTitle(wanted) == TextMatcher.NormalizeTitle(current)) continue;
            session.Execute("UPDATE entries SET preferred_title=$title,updated_at=$time WHERE id=$id", ("$title", wanted), ("$time", stamp), ("$id", id));
            changed++;
        }
        return new JsonObject { ["checked"] = entries.Count, ["changed"] = changed };
    });

    /// <summary>run_bound_title_consensus_upgrade: the one-time 0.2.78 consensus pass (settings key bound_title_consensus_version).</summary>
    public JsonObject RunBoundTitleConsensusUpgrade(bool force = false)
    {
        const string version = "0.2.78"; const string key = "bound_title_consensus_version";
        lock (_gate)
        {
            var settings = new SettingsRepository(_database);
            if (!force && settings.Get(key) is JsonValue stored && stored.TryGetValue<string>(out var done) && done == version)
                return new JsonObject { ["skipped"] = true, ["version"] = version };
            var result = RefreshBoundPreferredTitles();
            settings.Set(key, JsonValue.Create(version));
            result["skipped"] = false; result["version"] = version;
            return result;
        }
    }

    /// <summary>_repair_source_title_suffixes: drops "Title – SourceName" suffixes that name the owning source.</summary>
    public JsonObject RepairSourceTitleSuffixes() => Write(session =>
    {
        int rows = 0, preferred = 0; var stamp = Now(); var touched = new HashSet<long>();
        foreach (var table in new[] { "source_matches", "source_variants" })
            foreach (var row in session.Query("SELECT id,entry_id,source_id,source_manga_id,source_title,metadata_json FROM " + table + " WHERE active=1"))
            {
                var old = row.Text("source_title").Trim();
                var cleaned = TextMatcher.StripKnownSourceSuffix(old, SourceTitleNames(session, row.Text("source_id")));
                if (cleaned.Length == 0 || cleaned == old) continue;
                JsonObject metadata;
                try { metadata = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject ?? new JsonObject(); }
                catch (JsonException) { metadata = new JsonObject(); }
                if (metadata["title"] is JsonValue title && title.TryGetValue<string>(out var text) && text.Trim() == old)
                {
                    if (!metadata.ContainsKey("_autosourcarr_raw_title")) metadata["_autosourcarr_raw_title"] = old;
                    metadata["title"] = cleaned;
                }
                session.Execute("UPDATE " + table + " SET source_title=$title,metadata_json=$metadata WHERE id=$id",
                    ("$title", cleaned), ("$metadata", metadata.ToJsonString()), ("$id", row.Integer("id")));
                rows++;
                var entryId = Canonical(session, row.Integer("entry_id")); touched.Add(entryId);
                var entry = session.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", entryId)).FirstOrDefault();
                if (entry is not null && !entry.Flag("manual_preferred_title") && TextMatcher.NormalizeTitle(entry.Text("preferred_title")) == TextMatcher.NormalizeTitle(old))
                {
                    session.Execute("UPDATE entries SET preferred_title=$title,updated_at=$time WHERE id=$id", ("$title", cleaned), ("$time", stamp), ("$id", entryId));
                    preferred++;
                }
                metadata["id"] = row.Integer("source_manga_id"); metadata["title"] = cleaned;
                RefreshAttachedSourceTitles(session, entryId, row.Text("source_id"), metadata);
            }
        return new JsonObject { ["source_records"] = rows, ["preferred_titles"] = preferred, ["entries"] = touched.Count };
    });

    /// <summary>_first_source_bootstrap_title: for a record-less entry, its first main-library / manual / preferred title.</summary>
    public string FirstSourceBootstrapTitle(long entryId) => Read(session =>
    {
        var id = Canonical(session, entryId);
        if (session.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$id AND active=1 UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND active=1 LIMIT 1", ("$id", id)) is not null)
            return "";
        var native = session.Query("SELECT title FROM titles WHERE entry_id=$id AND title_type IN ('main-library','manual') ORDER BY CASE title_type WHEN 'main-library' THEN 0 ELSE 1 END,id", ("$id", id))
            .Select(row => row.Text("title")).ToList();
        if (native.Count == 0 && session.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", id)) is string preferred) native.Add(preferred);
        var first = native.FirstOrDefault(text => TextMatcher.IsMatchEvidenceTitle(text, "", primary: true));
        return first is null ? "" : string.Join(' ', first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    });

    /// <summary>set_preferred_title: the user's main title, kept in preferred_title_overrides (survives resets of catalogue rows).</summary>
    public long SetPreferredTitle(long entryId, string title, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        title = (title ?? "").Trim();
        if (!TextMatcher.IsMeaningfulTitle(title)) throw new CatalogueTitleException(400, "Main title must be a meaningful title");
        var stamp = Now();
        var id = Write(session =>
        {
            var canonical = Canonical(session, entryId);
            var before = session.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", canonical)).FirstOrDefault()
                ?? throw new CatalogueTitleException(404, "Entry not found");
            session.Execute("UPDATE entries SET preferred_title=$title,manual_preferred_title=1,updated_at=$time WHERE id=$id", ("$title", title), ("$time", stamp), ("$id", canonical));
            session.Execute("""
                INSERT INTO preferred_title_overrides(entry_id,title,updated_at) VALUES($id,$title,$time)
                ON CONFLICT(entry_id) DO UPDATE SET title=excluded.title,updated_at=excluded.updated_at
                """, ("$id", canonical), ("$title", title), ("$time", stamp));
            var records = session.Query("""
                SELECT source_id,source_manga_id FROM source_matches WHERE entry_id=$id AND active=1
                UNION SELECT source_id,source_manga_id FROM source_variants WHERE entry_id=$id AND active=1
                """, ("$id", canonical));
            session.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$id", ("$id", canonical));
            foreach (var record in records)
                session.Execute("""
                    INSERT INTO manual_title_scopes(entry_id,source_id,source_manga_id,title,captured_at) VALUES($id,$source,$manga,$title,$time)
                    ON CONFLICT(entry_id,source_id,source_manga_id) DO UPDATE SET title=excluded.title,captured_at=excluded.captured_at
                    """, ("$id", canonical), ("$source", record.Text("source_id")), ("$manga", record.Integer("source_manga_id")), ("$title", title), ("$time", stamp));
            session.Execute("DELETE FROM preferred_title_override_sources");
            _decisions.Record(new("title.preferred", actor, [canonical], records.Select(RecordKey).ToArray(), "main title set to " + title,
                new JsonObject { ["preferred_title"] = before.Text("preferred_title"), ["manual"] = before.Flag("manual_preferred_title") },
                new JsonObject { ["preferred_title"] = title, ["manual"] = true }));
            return canonical;
        });
        TitlesChanged?.Invoke(id);
        return id;
    }

    /// <summary>clear_manual_titles: removes the user's main title and manual aliases; the entry returns to bound-title consensus.</summary>
    public long ClearManualTitles(long entryId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var stamp = Now();
        var id = Write(session =>
        {
            var canonical = Canonical(session, entryId);
            var before = session.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", canonical)).FirstOrDefault()
                ?? throw new CatalogueTitleException(404, "Entry not found");
            var manual = session.Query("SELECT title,language FROM titles WHERE entry_id=$id AND title_type='manual' ORDER BY id", ("$id", canonical));
            session.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$id", ("$id", canonical));
            session.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$id", ("$id", canonical));
            session.Execute("DELETE FROM titles WHERE entry_id=$id AND title_type='manual'", ("$id", canonical));
            session.Execute("DELETE FROM preferred_title_override_sources");
            session.Execute("UPDATE entries SET manual_preferred_title=0,updated_at=$time WHERE id=$id", ("$time", stamp), ("$id", canonical));
            var after = SyncPreferredTitle(session, canonical, stamp);
            _decisions.Record(new("title.manual.clear", actor, [canonical], [], "manual titles cleared",
                new JsonObject
                {
                    ["preferred_title"] = before.Text("preferred_title"), ["manual"] = before.Flag("manual_preferred_title"),
                    ["aliases"] = new JsonArray(manual.Select(row => (JsonNode)new JsonObject { ["title"] = row.Text("title"), ["language"] = row.Text("language") }).ToArray()),
                },
                new JsonObject { ["preferred_title"] = after, ["manual"] = false }));
            return canonical;
        });
        TitlesChanged?.Invoke(id);
        return id;
    }

    /// <summary>The alias route: add_title(entry, title, language, "manual"), recorded as "title.alias.add".</summary>
    public long AddManualAlias(long entryId, string title, string language, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        title = (title ?? "").Trim();
        if (!TextMatcher.IsMeaningfulTitle(title)) throw new CatalogueTitleException(400, "An alternative title must be a meaningful title");
        var id = Write(session =>
        {
            var canonical = Canonical(session, entryId);
            if (session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", canonical)) is null) throw new CatalogueTitleException(404, "Entry not found");
            if (AddTitle(session, canonical, title, language ?? "", "manual", "", 0))
                _decisions.Record(new("title.alias.add", actor, [canonical], [], "alternative title " + title, null,
                    new JsonObject { ["title"] = title, ["language"] = language ?? "" }));
            return canonical;
        });
        TitlesChanged?.Invoke(id);
        return id;
    }

    // ---- titles of an entry ----------------------------------------------------------------------------------------

    /// <summary>_entry_titles: every meaningful observed title.</summary>
    public IReadOnlyList<string> EntryTitles(long entryId) => EntryTitleRows(entryId).Select(row => row.Title).ToArray();

    /// <summary>_entry_title_rows: preferred title, stored aliases, then active source and additional-record titles (deduplicated).</summary>
    public IReadOnlyList<EntryTitleRow> EntryTitleRows(long entryId) => Read(session => TitleRows(session, Canonical(session, entryId)));

    internal static IReadOnlyList<EntryTitleRow> TitleRows(DatabaseSession session, long entryId)
    {
        var rows = new List<EntryTitleRow>(); var seen = new HashSet<(string, string, string, long)>();
        void Add(string title, string type, string language, string sourceId, long mangaId)
        {
            var value = title.Trim(); var normalized = TextMatcher.NormalizeTitle(value);
            if (normalized.Length == 0 || !TextMatcher.IsMeaningfulTitle(value) || !seen.Add((normalized, type, sourceId, mangaId))) return;
            rows.Add(new(value, normalized, type, language, sourceId, mangaId));
        }
        if (session.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) is string { Length: > 0 } preferred) Add(preferred, "preferred", "", "", 0);
        foreach (var row in session.Query("SELECT title,title_type,language,source_id,source_manga_id FROM titles WHERE entry_id=$id ORDER BY id", ("$id", entryId)))
            Add(row.Text("title"), row.Text("title_type"), row.Text("language"), row.Text("source_id"), row.Integer("source_manga_id"));
        foreach (var row in session.Query("""
            SELECT sm.source_title title,COALESCE(sc.lang,'') language,'source' title_type,sm.source_id,sm.source_manga_id
              FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1
            UNION ALL
            SELECT sv.source_title title,COALESCE(sc.lang,'') language,'source-variant' title_type,sv.source_id,sv.source_manga_id
              FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1
            """, ("$id", entryId)))
            Add(row.Text("title"), row.Text("title_type"), row.Text("language"), row.Text("source_id"), row.Integer("source_manga_id"));
        return rows;
    }

    /// <summary>add_title (standalone write).</summary>
    public bool AddTitle(long entryId, string title, string language = "", string titleType = "alias", string sourceId = "", long sourceMangaId = 0)
    {
        var changed = Write(session => AddTitle(session, Canonical(session, entryId), title, language, titleType, sourceId, sourceMangaId));
        if (changed) TitlesChanged?.Invoke(entryId);
        return changed;
    }

    internal static bool AddTitle(DatabaseSession session, long entryId, string title, string language, string titleType, string sourceId, long sourceMangaId)
    {
        title = (title ?? "").Trim();
        if (!TextMatcher.IsMeaningfulTitle(title)) return false;
        var normalized = TextMatcher.NormalizeTitle(title);
        if (normalized.Length == 0) return false;
        return session.Execute("""
            INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($entry,$title,$normalized,$language,$type,$source,$manga)
            ON CONFLICT(entry_id,normalized,source_id,title_type) DO UPDATE SET
              title=excluded.title,
              language=CASE WHEN excluded.language<>'' THEN excluded.language ELSE titles.language END,
              source_manga_id=CASE WHEN excluded.source_manga_id<>0 THEN excluded.source_manga_id ELSE titles.source_manga_id END
            """, ("$entry", entryId), ("$title", title), ("$normalized", normalized), ("$language", language ?? ""), ("$type", titleType),
            ("$source", sourceId ?? ""), ("$manga", sourceMangaId)) > 0;
    }

    /// <summary>_purge_source_record_titles: drops aliases contributed by detached records (and, when asked, their legacy override rows).</summary>
    public int PurgeSourceRecordTitles(long entryId, string sourceId, IEnumerable<long> sourceMangaIds, bool dropPreferredOverride = false)
    {
        var ids = sourceMangaIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0) return 0;
        var removed = Write(session => PurgeSourceRecordTitles(session, Canonical(session, entryId), sourceId, ids, dropPreferredOverride));
        if (removed > 0) TitlesChanged?.Invoke(entryId);
        return removed;
    }

    internal static int PurgeSourceRecordTitles(DatabaseSession session, long entryId, string sourceId, IReadOnlyList<long> mangaIds, bool dropPreferredOverride)
    {
        var removed = 0;
        foreach (var mangaId in mangaIds)
        {
            removed += session.Execute("DELETE FROM titles WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga",
                ("$entry", entryId), ("$source", sourceId), ("$manga", mangaId));
            if (dropPreferredOverride)
                session.Execute("DELETE FROM preferred_title_override_sources WHERE source_id=$source AND source_manga_id=$manga", ("$source", sourceId), ("$manga", mangaId));
        }
        return removed;
    }

    /// <summary>
    /// _refresh_attached_source_titles: replaces the record's matched-alias and (when the payload has a description) its
    /// description aliases. Description aliases keep whole titles (<see cref="WholeTitleAliases"/>).
    /// </summary>
    internal static void RefreshAttachedSourceTitles(DatabaseSession session, long entryId, string sourceId, JsonObject manga)
    {
        var mangaId = MangaId(manga["id"]);
        if (mangaId <= 0) return;
        var title = Text(manga["title"]); var description = Text(manga["description"]);
        session.Execute("DELETE FROM titles WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga AND title_type='matched-alias'",
            ("$entry", entryId), ("$source", sourceId), ("$manga", mangaId));
        var hasDescription = description.Trim().Length > 0;
        if (hasDescription)
            session.Execute("DELETE FROM titles WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga AND title_type='description-alias'",
                ("$entry", entryId), ("$source", sourceId), ("$manga", mangaId));
        var language = (session.Scalar("SELECT lang FROM source_config WHERE source_id=$source", ("$source", sourceId)) as string ?? "").ToLowerInvariant();
        AddTitle(session, entryId, title, language, "matched-alias", sourceId, mangaId);
        if (hasDescription)
            foreach (var alias in WholeTitleAliases.Extract(description, [title]))
                AddTitle(session, entryId, alias, language, "description-alias", sourceId, mangaId);
    }

    /// <summary>Standalone form of <see cref="RefreshAttachedSourceTitles(DatabaseSession,long,string,JsonObject)"/>.</summary>
    public void RefreshAttachedSourceTitles(long entryId, string sourceId, JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        Write(session => { RefreshAttachedSourceTitles(session, Canonical(session, entryId), sourceId, manga); return 0; });
        TitlesChanged?.Invoke(entryId);
    }

    // ---- strict automatic title evidence ---------------------------------------------------------------------------

    /// <summary>
    /// _strict_automatic_title_evidence(_base): the one non-transitive gate for automatic source attachment. Only trusted
    /// titles (ingestion anchor, manual / main-library titles, locked records) may authorize; summaries corroborate but
    /// never authorize alone; creators that confidently differ refuse. Policy rules raise the thresholds through
    /// <paramref name="options"/>.
    /// </summary>
    public static ScoredMatchDecision StrictAutomaticTitleEvidence(IEnumerable<string> candidateTitles, IEnumerable<string> trustedTitles,
        string candidateDescription, IEnumerable<string> trustedDescriptions, string candidateAuthor = "", string trustedAuthor = "",
        MatchScoringOptions? options = null, MatchCandidate? candidateExtras = null, MatchCandidate? trustedExtras = null)
    {
        var candidate = (candidateExtras ?? new MatchCandidate()) with
        {
            Titles = candidateTitles.Select(title => (title ?? "").Trim()).Where(title => TextMatcher.IsMatchEvidenceTitle(title, "", primary: true)).ToArray(),
            Authors = candidateAuthor.Length > 0 ? [candidateAuthor] : candidateExtras?.Authors ?? [],
            Description = candidateDescription ?? "",
        };
        var trustedDescription = trustedDescriptions.FirstOrDefault(text => text.Length > 0 && TextMatcher.BestDescriptionScore(candidateDescription ?? "", [text]) >= .82)
            ?? trustedDescriptions.FirstOrDefault(text => text.Length > 0) ?? "";
        var trusted = (trustedExtras ?? new MatchCandidate()) with
        {
            Titles = trustedTitles.Select(title => (title ?? "").Trim()).Where(title => TextMatcher.IsMatchEvidenceTitle(title, "", primary: true)).ToArray(),
            Authors = trustedAuthor.Length > 0 ? [trustedAuthor] : trustedExtras?.Authors ?? [],
            Description = trustedDescription,
        };
        return MatchScorer.Score(candidate, trusted, options);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    internal static IReadOnlyList<string> SourceTitleNames(DatabaseSession session, string sourceId)
    {
        var row = session.Query("SELECT name,display_name FROM source_config WHERE source_id=$id", ("$id", sourceId)).FirstOrDefault();
        return row is null ? [] : new[] { row.Text("name"), row.Text("display_name") }.Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static long Canonical(DatabaseSession session, long entryId)
    {
        var current = entryId; var seen = new HashSet<long>();
        while (seen.Add(current))
        {
            if (session.Scalar("SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=$id", ("$id", current)) is not { } next) return current;
            current = Convert.ToInt64(next, CultureInfo.InvariantCulture);
        }
        return current;
    }

    internal static string RecordKey(DatabaseRow row) => row.Text("source_id") + ":" + row.Integer("source_manga_id").ToString(CultureInfo.InvariantCulture);

    internal static string Text(JsonNode? node)
    {
        if (node is not JsonValue value) return "";
        if (value.TryGetValue<string>(out var text)) return text;
        return value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : "";
    }

    internal static long MangaId(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return value.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? (long)real : 0;
    }

    private string Now() => SourceConfigurationJson.Timestamp(_clock);

    private T Read<T>(Func<DatabaseSession, T> operation)
    {
        lock (_gate) return _database.Read(DatabaseFile.Catalog, operation);
    }

    private T Write<T>(Func<DatabaseSession, T> operation)
    {
        lock (_gate) return _database.Write(DatabaseFile.Catalog, operation);
    }
}
