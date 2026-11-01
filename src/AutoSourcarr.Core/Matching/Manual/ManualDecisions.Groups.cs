using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Manual.ManualSql;

namespace AutoSourcarr.Core.Matching.Manual;

public sealed partial class ManualDecisions
{
    private static Dictionary<string, string> SourceNames(DatabaseSession c) => c.Query("SELECT source_id,name,display_name FROM source_config")
        .ToDictionary(row => row.Text("source_id"), row => row.Text("display_name") is { Length: > 0 } display ? display
            : row.Text("name") is { Length: > 0 } name ? name : row.Text("source_id"), StringComparer.Ordinal);

    /// <summary>manual_groups_payload: durable manual groups (two or more members) and individual manual bindings.</summary>
    public JsonObject ManualGroupsPayload() => Read(GroupsPayload);

    private static JsonObject GroupsPayload(DatabaseSession c)
    {
        var groups = c.Query("SELECT * FROM manual_match_groups ORDER BY updated_at DESC,title COLLATE NOCASE");
        var bindings = c.Query("SELECT * FROM manual_match_bindings ORDER BY updated_at DESC");
        var names = SourceNames(c);
        var members = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var individual = new JsonArray();
        foreach (var row in bindings)
        {
            var sourceId = row.Text("source_id"); var mangaId = row.Integer("source_manga_id");
            var group = row.Text("group_id");
            var item = new JsonObject
            {
                ["source_id"] = sourceId, ["source_manga_id"] = mangaId,
                ["source_name"] = names.TryGetValue(sourceId, out var name) ? name : row.Text("source_name", sourceId),
                ["source_title"] = row.Text("source_title"), ["entry_id"] = Owner(c, sourceId, mangaId),
                ["entry_id_hint"] = row.TryGetValue("entry_id_hint", out var hint) && hint is not null ? (long?)row.Integer("entry_id_hint") : null,
                ["group_id"] = group, ["cover_url"] = "/api/v1/source-covers/" + mangaId.ToString(CultureInfo.InvariantCulture),
                ["updated_at"] = row.Text("updated_at"),
            };
            if (group.Length > 0) (members.TryGetValue(group, out var list) ? list : members[group] = []).Add(item);
            else individual.Add(item);
        }
        var payload = new JsonArray();
        foreach (var group in groups)
        {
            var id = group.Text("id");
            if (!members.TryGetValue(id, out var list) || list.Count < 2) continue;
            var current = list.Select(item => item["entry_id"]).OfType<JsonValue>().Select(value => value.GetValue<long>()).FirstOrDefault();
            payload.Add(new JsonObject
            {
                ["id"] = id, ["title"] = group.Text("title") is { Length: > 0 } title ? title : "Manual group",
                ["members"] = JsonList(list), ["member_count"] = list.Count,
                ["entry_id"] = current > 0 ? (long?)Canonical(c, current) : null, ["updated_at"] = group.Text("updated_at"),
            });
        }
        return new JsonObject
        {
            ["groups"] = payload, ["individual"] = individual, ["group_count"] = payload.Count, ["manual_match_count"] = bindings.Count,
        };
    }

    /// <summary>manual_actions_payload: every durable user decision grouped by kind (the Saved decisions view).</summary>
    public JsonObject ManualActionsPayload()
    {
        var rejected = _database.Read(DatabaseFile.Origins, o => o.Query("""
            SELECT r.source_id,r.source_chapter_id,r.source_manga_id,r.source_name,r.name,r.rejected_at,
                   cc.id candidate_id,vc.entry_id,vc.display_name virtual_name
            FROM chapter_candidate_rejections r
            LEFT JOIN chapter_candidates cc ON cc.source_id=r.source_id AND cc.source_chapter_id=r.source_chapter_id
            LEFT JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id
            ORDER BY r.rejected_at DESC
            """));
        var rejectedEntries = System.Text.Json.JsonSerializer.Serialize(rejected.Where(row => row.TryGetValue("entry_id", out var value) && value is not null)
            .Select(row => row.Integer("entry_id")).Distinct().ToArray());
        var catalog = Read(c =>
        {
            var names = SourceNames(c);
            var titleRows = c.Query("SELECT o.entry_id,o.title,o.updated_at,e.preferred_title FROM preferred_title_overrides o LEFT JOIN entries e ON e.id=o.entry_id ORDER BY o.updated_at DESC");
            var scopes = c.Query("SELECT entry_id,source_id,source_manga_id,title,captured_at FROM manual_title_scopes ORDER BY captured_at DESC");
            var aliases = c.Query("SELECT t.entry_id,t.title,t.language,t.id,e.preferred_title FROM titles t LEFT JOIN entries e ON e.id=t.entry_id WHERE t.title_type='manual' ORDER BY t.id DESC");
            var separate = c.Query("""
                SELECT m.target_entry_id,m.source_id,m.source_manga_id,m.separate_entry_id_hint,m.source_title,m.created_at,m.updated_at,
                       te.preferred_title target_title,se.preferred_title separate_title
                FROM manual_separate_records m LEFT JOIN entries te ON te.id=m.target_entry_id LEFT JOIN entries se ON se.id=m.separate_entry_id_hint
                ORDER BY m.updated_at DESC
                """);
            var duplicates = c.Query("""
                SELECT d.left_entry_id,d.right_entry_id,d.decision,d.reviewed_at,le.preferred_title left_title,re.preferred_title right_title
                FROM duplicate_reviews d LEFT JOIN entries le ON le.id=d.left_entry_id LEFT JOIN entries re ON re.id=d.right_entry_id
                WHERE d.decision='not_duplicate' ORDER BY d.reviewed_at DESC
                """);
            var scopesByEntry = scopes.GroupBy(row => row.Integer("entry_id")).ToDictionary(group => group.Key, group => group.Select(row => (JsonNode?)new JsonObject
            {
                ["source_id"] = row.Text("source_id"), ["source_manga_id"] = row.Integer("source_manga_id"),
                ["source_name"] = names.TryGetValue(row.Text("source_id"), out var name) ? name : row.Text("source_id"), ["captured_at"] = row.Text("captured_at"),
            }).ToList());
            var aliasesByEntry = aliases.GroupBy(row => row.Integer("entry_id")).ToDictionary(group => group.Key, group => group.Select(row => (JsonNode?)new JsonObject
            {
                ["id"] = row.Integer("id"), ["title"] = row.Text("title"), ["language"] = row.Text("language"),
            }).ToList());
            var titles = new JsonArray(); var seen = new HashSet<long>();
            foreach (var row in titleRows)
            {
                var id = row.Integer("entry_id"); seen.Add(id);
                titles.Add(new JsonObject
                {
                    ["entry_id"] = id, ["title"] = row.Text("title"),
                    ["entry_title"] = row.Text("preferred_title") is { Length: > 0 } current ? current : row.Text("title"),
                    ["updated_at"] = row.Text("updated_at"),
                    ["scope"] = JsonList(scopesByEntry.TryGetValue(id, out var scope) ? scope : []),
                    ["aliases"] = JsonList(aliasesByEntry.TryGetValue(id, out var own) ? own : []),
                });
            }
            // An entry may have manual alternate titles without a manual main title.
            foreach (var (id, items) in aliasesByEntry)
            {
                if (seen.Contains(id)) continue;
                var title = c.Scalar("SELECT preferred_title FROM entries WHERE id=$p0", P(id)) as string;
                titles.Add(new JsonObject
                {
                    ["entry_id"] = id, ["title"] = "", ["entry_title"] = title ?? "Entry " + id.ToString(CultureInfo.InvariantCulture),
                    ["updated_at"] = "", ["scope"] = new JsonArray(), ["aliases"] = JsonList(items),
                });
            }
            var separateItems = separate.Select(row =>
            {
                var item = Json(row);
                item["source_name"] = names.TryGetValue(row.Text("source_id"), out var name) ? name : row.Text("source_id");
                return (JsonNode?)item;
            }).ToList();
            var entryTitles = c.Query("SELECT id,preferred_title FROM entries WHERE id IN (SELECT value FROM json_each($p0))", P(rejectedEntries)).ToDictionary(row => row.Integer("id"), row => row.Text("preferred_title"));
            return (Groups: GroupsPayload(c), Titles: titles, Separate: JsonList(separateItems),
                Duplicates: JsonList(duplicates.Select(row => (JsonNode?)Json(row))), Names: names, EntryTitles: entryTitles);
        });
        var rejectedItems = new JsonArray();
        foreach (var row in rejected)
        {
            var item = Json(row);
            var entry = row.TryGetValue("entry_id", out var value) && value is not null ? row.Integer("entry_id") : (long?)null;
            item["entry_title"] = entry is long id && catalog.EntryTitles.TryGetValue(id, out var title) ? title : "";
            if (row.Text("source_name").Length == 0)
                item["source_name"] = catalog.Names.TryGetValue(row.Text("source_id"), out var name) ? name : row.Text("source_id");
            rejectedItems.Add(item);
        }
        return new JsonObject
        {
            ["matches"] = catalog.Groups, ["titles"] = catalog.Titles, ["separate_entries"] = catalog.Separate,
            ["rejected_chapters"] = rejectedItems, ["duplicate_decisions"] = catalog.Duplicates,
            ["counts"] = new JsonObject
            {
                ["matches"] = catalog.Groups["manual_match_count"]?.GetValue<int>() ?? 0, ["titles"] = catalog.Titles.Count,
                ["separate_entries"] = catalog.Separate.Count, ["rejected_chapters"] = rejectedItems.Count, ["duplicate_decisions"] = catalog.Duplicates.Count,
            },
        };
    }

    /// <summary>manual_group_candidates: active records of entries whose title or source title matches the query, best first.</summary>
    public JsonObject ManualGroupCandidates(string groupId, string query, int limit = 60)
    {
        var text = (query ?? "").Trim();
        limit = Math.Clamp(limit, 1, 100);
        return Read(c =>
        {
            if (c.Scalar("SELECT 1 FROM manual_match_groups WHERE id=$p0", P(groupId)) is null) throw new KeyNotFoundException("Manual group not found");
            if (text.Length == 0) return new JsonObject { ["items"] = new JsonArray() };
            var existing = c.Query("SELECT source_id,source_manga_id FROM manual_match_bindings WHERE group_id=$p0", P(groupId))
                .Select(row => (row.Text("source_id"), row.Integer("source_manga_id"))).ToHashSet();
            var like = "%" + text + "%"; var normalized = "%" + TextMatcher.NormalizeTitle(text) + "%";
            var ids = c.QueryAs<long>("""
                SELECT id FROM entries WHERE preferred_title LIKE @like COLLATE NOCASE
                UNION SELECT entry_id FROM titles WHERE normalized LIKE @normalized
                UNION SELECT entry_id FROM source_matches WHERE active=1 AND source_title LIKE @like COLLATE NOCASE
                UNION SELECT entry_id FROM source_variants WHERE active=1 AND source_title LIKE @like COLLATE NOCASE
                LIMIT 400
                """, new { like, normalized }).Select(id => Canonical(c, id)).Distinct().ToArray();
            if (ids.Length == 0) return new JsonObject { ["items"] = new JsonArray() };
            var rows = c.Query("""
                SELECT sm.entry_id,sm.source_id,sm.source_manga_id,sm.source_title,COALESCE(sc.display_name,sc.name,sm.source_id) source_name,e.preferred_title
                FROM source_matches sm JOIN entries e ON e.id=sm.entry_id LEFT JOIN source_config sc ON sc.source_id=sm.source_id
                WHERE sm.active=1 AND sm.entry_id IN (SELECT value FROM json_each($p0))
                UNION ALL
                SELECT sv.entry_id,sv.source_id,sv.source_manga_id,sv.source_title,COALESCE(sc.display_name,sc.name,sv.source_id) source_name,e.preferred_title
                FROM source_variants sv JOIN entries e ON e.id=sv.entry_id LEFT JOIN source_config sc ON sc.source_id=sv.source_id
                WHERE sv.active=1 AND sv.entry_id IN (SELECT value FROM json_each($p0))
                """, P(System.Text.Json.JsonSerializer.Serialize(ids)));
            var items = rows.Where(row => !existing.Contains((row.Text("source_id"), row.Integer("source_manga_id"))))
                .Select(row => (Row: row, Score: Math.Max(TextMatcher.NameSimilarity(text, row.Text("preferred_title")), TextMatcher.NameSimilarity(text, row.Text("source_title")))))
                .OrderByDescending(item => item.Score).ThenBy(item => item.Row.Text("preferred_title").ToUpperInvariant(), StringComparer.Ordinal)
                .Take(limit)
                .Select(item => (JsonNode?)new JsonObject
                {
                    ["entry_id"] = Canonical(c, item.Row.Integer("entry_id")), ["preferred_title"] = item.Row.Text("preferred_title"),
                    ["source_id"] = item.Row.Text("source_id"), ["source_manga_id"] = item.Row.Integer("source_manga_id"),
                    ["source_name"] = item.Row.Text("source_name"), ["source_title"] = item.Row.Text("source_title"),
                    ["cover_url"] = "/api/v1/source-covers/" + item.Row.Integer("source_manga_id").ToString(CultureInfo.InvariantCulture),
                });
            return new JsonObject { ["items"] = JsonList(items) };
        });
    }

    private static long? GroupAnchor(DatabaseSession c, string groupId)
    {
        var rows = c.Query("SELECT source_id,source_manga_id,entry_id_hint FROM manual_match_bindings WHERE group_id=$p0 ORDER BY created_at", P(groupId));
        foreach (var row in rows)
            if (Owner(c, row.Text("source_id"), row.Integer("source_manga_id")) is long owner) return owner;
        foreach (var row in rows)
        {
            var hint = row.Integer("entry_id_hint");
            if (hint > 0 && EntryExists(c, hint)) return Canonical(c, hint);
        }
        return null;
    }

    /// <summary>add_manual_group_member: moves the record's entry into the group's entry (merging when needed) and binds it there.</summary>
    public JsonObject AddManualGroupMember(string groupId, string sourceId, long sourceMangaId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var (row, anchor) = Read(c =>
            {
                if (c.Scalar("SELECT 1 FROM manual_match_groups WHERE id=$p0", P(groupId)) is null) throw new KeyNotFoundException("Manual group not found");
                var record = RecordRow(c, sourceId, sourceMangaId) ?? throw new KeyNotFoundException("Source record not found");
                return (record, GroupAnchor(c, groupId));
            });
            var owner = Read(c => Canonical(c, row.Integer("entry_id")));
            var target = anchor ?? owner;
            if (target != owner) target = RequireOperations().MergeEntries(target, owner, "manual-group-add");
            var result = Write(c =>
            {
                var stamp = Stamp();
                foreach (var table in new[] { "source_matches", "source_variants" })
                    c.Execute($"UPDATE {table} SET match_type='manual',locked=1,last_checked=$p0 WHERE entry_id=$p1 AND source_id=$p2 AND source_manga_id=$p3", P(stamp, target, sourceId, sourceMangaId));
                PersistBinding(c, target, sourceId, ManualSql.Payload(row), row.Text("source_name"), groupId, ensureGroup: false);
                c.Execute("UPDATE manual_match_bindings SET entry_id_hint=$p0,group_id=$p1,updated_at=$p2 WHERE group_id=$p1", P(target, groupId, stamp));
                c.Execute("UPDATE manual_match_groups SET updated_at=$p0 WHERE id=$p1", P(stamp, groupId));
                Log("manual.group.add", actor, [target, owner], [RecordKey(sourceId, sourceMangaId)], "Added to manual group", after: new JsonObject { ["group_id"] = groupId });
                return GroupsPayload(c);
            });
            _operations?.ChooseMetadata(target);
            return result;
        }
    }

    /// <summary>remove_manual_group_member: the record stays attached but is no longer kept in the group by later rebuilds.</summary>
    public JsonObject RemoveManualGroupMember(string groupId, string sourceId, long sourceMangaId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return Write(c =>
        {
            var hint = c.Query("SELECT entry_id_hint FROM manual_match_bindings WHERE group_id=$p0 AND source_id=$p1 AND source_manga_id=$p2", P(groupId, sourceId, sourceMangaId));
            if (hint.Count == 0) throw new KeyNotFoundException("Manual group member not found");
            c.Execute("UPDATE manual_match_bindings SET group_id=NULL,grouping_suppressed=1,updated_at=$p0 WHERE group_id=$p1 AND source_id=$p2 AND source_manga_id=$p3",
                P(Stamp(), groupId, sourceId, sourceMangaId));
            DissolveIfSmall(c, groupId);
            Log("manual.group.remove", actor, [hint[0].Integer("entry_id_hint")], [RecordKey(sourceId, sourceMangaId)], "Removed from manual group",
                new JsonObject { ["group_id"] = groupId });
            return GroupsPayload(c);
        });
    }

    /// <summary>
    /// restore_persistent_manual_matches (after a normal reset): attaches every bound record again — group members to one
    /// entry, the others to their remembered entry or a new one — from the stored record, without any network request,
    /// and restores the "different works" reviews that separate-entry decisions imply.
    /// </summary>
    public JsonObject RestorePersistentManualMatches(DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (_gate)
        {
            var (groups, bindings) = Read(c => (c.Query("SELECT * FROM manual_match_groups ORDER BY created_at"), c.Query("SELECT * FROM manual_match_bindings ORDER BY created_at")));
            var restored = 0; var created = 0; var touched = new HashSet<long>();
            if (_operations is { } operations)
            {
                var grouped = bindings.Where(row => row.Text("group_id").Length > 0).GroupBy(row => row.Text("group_id"), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
                foreach (var group in groups)
                {
                    if (!grouped.TryGetValue(group.Text("id"), out var members)) continue;
                    var anchor = Read<long?>(c =>
                    {
                        foreach (var member in members)
                        {
                            if (Owner(c, member.Text("source_id"), member.Integer("source_manga_id")) is long owner) return owner;
                            var hint = member.Integer("entry_id_hint");
                            if (hint > 0 && EntryExists(c, hint)) return Canonical(c, hint);
                        }
                        return null;
                    });
                    foreach (var member in members) { anchor = Attach(operations, member, anchor, ref restored, ref created); touched.Add(anchor.Value); }
                    Write(c => c.Execute("UPDATE manual_match_groups SET updated_at=$p0 WHERE id=$p1", P(Stamp(), group.Text("id"))));
                }
                foreach (var member in bindings.Where(row => row.Text("group_id").Length == 0 || !grouped.ContainsKey(row.Text("group_id"))
                    || !groups.Any(group => group.Text("id") == row.Text("group_id"))))
                    touched.Add(Attach(operations, member, null, ref restored, ref created));
                foreach (var entry in touched) operations.ChooseMetadata(entry);
            }
            var reviews = Write(c =>
            {
                var count = 0;
                foreach (var row in c.Query("SELECT target_entry_id,separate_entry_id_hint FROM manual_separate_records WHERE separate_entry_id_hint IS NOT NULL"))
                {
                    var left = Canonical(c, row.Integer("target_entry_id")); var right = Canonical(c, row.Integer("separate_entry_id_hint"));
                    if (left == right || !EntryExists(c, left) || !EntryExists(c, right)) continue;
                    var (low, high) = Pair(left, right);
                    if (c.Scalar("SELECT decision FROM duplicate_reviews WHERE left_entry_id=$p0 AND right_entry_id=$p1", P(low, high)) as string == "not_duplicate") continue;
                    ReviewPair(c, left, right, "not_duplicate"); count++;
                }
                Log("manual.restore", actor, touched, bindings.Select(row => RecordKey(row.Text("source_id"), row.Integer("source_manga_id"))),
                    "Manual decisions restored after a database reset",
                    after: new JsonObject { ["restored"] = restored, ["entries_created"] = created, ["separate_reviews"] = count });
                return count;
            });
            _operations?.Invalidate([]);
            return new JsonObject
            {
                ["restored"] = restored, ["entries_created"] = created, ["groups"] = groups.Count, ["separate_reviews"] = reviews,
                ["pending"] = _operations is null ? bindings.Count : 0,
            };
        }
    }

    private long Attach(IManualCatalogueOperations operations, DatabaseRow row, long? target, ref int restored, ref int created)
    {
        var sourceId = row.Text("source_id"); var mangaId = row.Integer("source_manga_id");
        JsonObject payload;
        try { payload = JsonNode.Parse(row.Text("record_json", "{}")) as JsonObject ?? new(); }
        catch (System.Text.Json.JsonException) { payload = new(); }
        payload["id"] = mangaId;
        if (Text(payload["title"]).Trim().Length == 0)
            payload["title"] = row.Text("source_title") is { Length: > 0 } title ? title : "Manual source " + mangaId.ToString(CultureInfo.InvariantCulture);
        if (payload["initialized"] is null) payload["initialized"] = Truth(payload["description"]) || Truth(payload["thumbnailUrl"]);
        var (owner, hint, hintExists) = Read(c =>
        {
            var hintId = row.Integer("entry_id_hint");
            var canonicalHint = hintId > 0 ? Canonical(c, hintId) : 0;
            return (Owner(c, sourceId, mangaId), hintId, canonicalHint > 0 && EntryExists(c, canonicalHint) ? canonicalHint : 0);
        });
        long entry;
        if (target is long chosen)
            entry = owner is long moved && moved != chosen ? operations.MergeEntries(chosen, moved, "manual-persistence-restore") : chosen;
        else if (owner is long existing)
            entry = existing;
        else if (hintExists > 0)
            entry = hintExists;
        else
        {
            entry = operations.CreateEntry(sourceId, payload);
            created++;
            if (hint > 0 && hint != entry)
                Write(c => c.Execute("""
                    INSERT INTO entry_aliases(old_entry_id,canonical_entry_id,reason,merged_at) VALUES($p0,$p1,'manual-persistence-restore',$p2)
                    ON CONFLICT(old_entry_id) DO UPDATE SET canonical_entry_id=excluded.canonical_entry_id,reason=excluded.reason,merged_at=excluded.merged_at
                    """, P(hint, entry, Stamp())));
        }
        var primary = Read(c => c.Scalar("SELECT source_manga_id FROM source_matches WHERE entry_id=$p0 AND source_id=$p1 AND active=1", P(entry, sourceId)));
        if (primary is not null && Convert.ToInt64(primary, CultureInfo.InvariantCulture) != mangaId)
            operations.UpsertVariant(entry, sourceId, payload, 1.0, "manual", true);
        else
            operations.UpsertPrimary(entry, sourceId, payload, 1.0, "manual", true);
        operations.ApplySourceTitleOverride(entry, sourceId, mangaId);
        Write(c => c.Execute("UPDATE manual_match_bindings SET entry_id_hint=$p0,source_title=$p1,record_json=$p2,updated_at=$p3 WHERE source_id=$p4 AND source_manga_id=$p5",
            P(entry, Text(payload["title"]), payload.ToJsonString(), Stamp(), sourceId, mangaId)));
        restored++;
        return entry;
    }
}
