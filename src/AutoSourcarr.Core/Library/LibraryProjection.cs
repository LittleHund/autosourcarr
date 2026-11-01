using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Library;

/// <summary>Filters of the library page (0.4.5 GET /api/admin/library query parameters).</summary>
public sealed record LibraryQuery
{
    public string Search { get; init; } = "";
    public string Source { get; init; } = "";
    public string Scope { get; init; } = "all";
    public string Sort { get; init; } = "updated";
    public string Publication { get; init; } = "any";
    public string TitleState { get; init; } = "any";
    public string MatchStatus { get; init; } = "any";
    public string SourceCount { get; init; } = "";
    public string RecordCount { get; init; } = "";
    public string Cursor { get; init; } = "";
    public int Limit { get; init; } = 60;
}

/// <summary>
/// Port of library_projection.py: the globally filtered library with stable keyset pages. In 0.4.5 the membership
/// came from main Suwayomi's library; here it is AutoSourcarr's own library (library_series), and the per-work
/// library stats are the downloaded CBZ chapters.
/// </summary>
public static class LibraryProjection
{
    private static readonly string[] Sorts = ["title", "updated", "matches"];
    private static readonly string[] Scopes = ["all", "catalog", "missing_downloads", "source_gaps", "unmatched", "verification", "monitored"];

    public static JsonObject Page(SqliteDatabase database, LibraryQuery query, IReadOnlyList<long> members, IReadOnlyCollection<long> duplicates,
        JsonObject subscription, DisplayMetadata? display = null)
    {
        using var metric = Diagnostics.RuntimeMetrics.Shared.Phase("projection");
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(query);
        if (!Sorts.Contains(query.Sort)) throw new LibraryConflictException("Unknown library sort");
        if (!Scopes.Contains(query.Scope)) throw new LibraryConflictException("Unknown library view");
        var limit = Math.Clamp(query.Limit, 1, 120);
        var search = Cut(query.Search, 200); var source = Cut(query.Source, 200);
        var ids = members.Where(id => id > 0).Distinct().ToArray();
        var sources = database.Read(DatabaseFile.Catalog, c => c.Query(
            "SELECT source_id,name,base_url,lang,group_id,priority,available,enabled_language,enabled_search,enabled_chapters FROM source_config ORDER BY name,lang"));
        var groups = ShownGroups(sources);
        var groupsJson = new JsonArray(groups.Select(group => (JsonNode?)new JsonArray(group.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())).ToArray()).ToJsonString();
        var queryKey = new JsonObject
        {
            ["search"] = search, ["source"] = source, ["scope"] = query.Scope, ["sort"] = query.Sort, ["publication"] = query.Publication,
            ["title_state"] = query.TitleState, ["match_status"] = query.MatchStatus, ["source_count"] = query.SourceCount, ["record_count"] = query.RecordCount,
        };
        var stamp = Fingerprint(queryKey.ToJsonString() + "|" + string.Join(',', ids.Order()) + "|" + groupsJson);
        var parameters = new List<(string, object?)>
        {
            ("$ids", "[" + string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]"),
            ("$limit", limit + 1), ("$groups", groupsJson), ("$duplicates", "[" + string.Join(',', duplicates.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]"),
        };
        var conditions = new List<string>();
        if (query.Scope != "catalog") conditions.Add("e.id IN (SELECT value FROM json_each($ids))");
        if (search.Length > 0)
        {
            parameters.Add(("$term", "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
            conditions.Add("(e.preferred_title LIKE $term ESCAPE '\\' OR EXISTS(SELECT 1 FROM titles t WHERE t.entry_id=e.id AND t.title LIKE $term ESCAPE '\\'))");
        }
        if (source.Length > 0)
        {
            parameters.Add(("$source", source));
            conditions.Add("EXISTS(SELECT 1 FROM records r WHERE r.entry_id=e.id AND r.source_id=$source)");
        }
        foreach (var (value, column, name) in new[] { (query.SourceCount, "source_count", "$exact_source_count"), (query.RecordCount, "match_count", "$exact_record_count") })
        {
            if (value.Length == 0) continue;
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact) || exact is < 0 or > 1_000_000_000)
                throw new LibraryConflictException("Invalid exact match count");
            parameters.Add((name, exact));
            conditions.Add("COALESCE(m." + column + ",0)=" + name);
        }
        if (query.TitleState is "manual" or "automatic") conditions.Add("e.manual_preferred_title=" + (query.TitleState == "manual" ? "1" : "0"));
        // The Suwayomi/Tachiyomi status integers; "finished" covers Completed (2) and Publishing finished (4).
        var statuses = query.Publication switch
        {
            "ongoing" => "[1]", "finished" => "[2,4]", "licensed" => "[3]", "hiatus" => "[6]", "cancelled" => "[5]", "unknown" => "[0]", _ => null,
        };
        if (statuses is not null) { parameters.Add(("$status", statuses)); conditions.Add("e.status IN (SELECT value FROM json_each($status))"); }
        switch (query.Scope)
        {
            case "unmatched": conditions.Add("COALESCE(m.match_count,0)=0"); break;
            case "monitored": conditions.Add("lt.monitored=1"); break;
            case "source_gaps": conditions.Add("json_extract(lt.observation_json,'$.coverage_state')='checked' AND json_extract(lt.observation_json,'$.source_gaps')>0"); break;
            case "missing_downloads": conditions.Add("json_extract(lt.observation_json,'$.download_state')='checked' AND json_extract(lt.observation_json,'$.missing_downloads')>0"); break;
            case "verification":
                conditions.Add("(lt.entry_id IS NULL OR COALESCE(json_extract(lt.observation_json,'$.download_state'),'unknown')<>'checked' OR COALESCE(json_extract(lt.observation_json,'$.coverage_state'),'unknown')<>'checked')");
                break;
        }
        switch (query.MatchStatus)
        {
            case "manual": conditions.Add("m.manual_count>0"); break;
            case "multiple_records": conditions.Add("m.match_count>m.source_count"); break;
            case "duplicates": conditions.Add("e.id IN (SELECT value FROM json_each($duplicates))"); break;
            case "gaps" or "full":
                const string missing = "EXISTS(SELECT 1 FROM json_each($groups) g WHERE NOT EXISTS(SELECT 1 FROM records r WHERE r.entry_id=e.id AND r.source_id IN(SELECT value FROM json_each(g.value))))";
                conditions.Add(query.MatchStatus == "full" ? "json_array_length($groups)>0 AND NOT " + missing : missing);
                break;
        }
        var where = conditions.Count == 0 ? "1" : string.Join(" AND ", conditions);
        const string records = """
            WITH records AS (
              SELECT entry_id,source_id,locked FROM source_matches WHERE active=1
              UNION ALL SELECT entry_id,source_id,locked FROM source_variants WHERE active=1
            ), matched AS (SELECT entry_id,COUNT(*) match_count,COUNT(DISTINCT source_id) source_count,
               SUM(locked) manual_count FROM records GROUP BY entry_id)
            """;
        var filtered = """
            , filtered AS (
              SELECT e.id,e.preferred_title,substr(e.description,1,420) description,e.thumbnail_url,e.status,e.updated_at,e.manual_preferred_title,
               COALESCE(m.match_count,0) match_count,COALESCE(m.source_count,0) source_count,
               COALESCE(lt.monitored,0) monitored,COALESCE(lt.revision,0) revision,
               COALESCE(lt.observation_json,'{}') observation_json,lower(e.preferred_title) title_key
              FROM entries e LEFT JOIN matched m ON m.entry_id=e.id
              LEFT JOIN library_tracking lt ON lt.entry_id=e.id WHERE
            """ + " " + where + ") ";
        var (key, direction, comparison) = query.Sort switch
        {
            "title" => ("title_key", "ASC", ">"),
            "matches" => ("source_count", "DESC", "<"),
            _ => ("updated_at", "DESC", "<"),
        };
        var after = "";
        if (query.Cursor.Length > 0)
        {
            JsonObject? cursor;
            try { cursor = JsonNode.Parse(Encoding.UTF8.GetString(Base64Url(query.Cursor))) as JsonObject; }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException) { cursor = null; }
            if (cursor is null || LibraryStore.Text(cursor, "stamp") != stamp || cursor["id"] is not JsonValue idValue || !idValue.TryGetValue<long>(out var afterId))
                throw new LibraryConflictException("Library changed; start again from the first page");
            object? afterKey = null;
            if (cursor["key"] is JsonValue keyValue)
            {
                if (keyValue.TryGetValue<string>(out var keyText)) afterKey = keyText;
                else if (keyValue.TryGetValue<long>(out var keyNumber)) afterKey = keyNumber;
            }
            parameters.Add(("$after_key", afterKey)); parameters.Add(("$after_id", afterId));
            after = "WHERE (" + key + ",id) " + comparison + " ($after_key,$after_id)";
        }
        var (total, rows) = database.Read(DatabaseFile.Catalog, c =>
        {
            var all = parameters.ToArray();
            var count = Convert.ToInt64(c.Scalar(records + filtered + "SELECT COUNT(*) FROM filtered", all) ?? 0L, CultureInfo.InvariantCulture);
            var page = c.Query(records + filtered + "SELECT * FROM filtered " + after + " ORDER BY " + key + " " + direction + ",id " + direction + " LIMIT $limit", all);
            return (count, page);
        });
        var more = rows.Count > limit;
        var shown = rows.Take(limit).Select(Json).ToList();
        var next = "";
        if (more && shown.Count > 0)
        {
            var last = shown[^1];
            next = Base64Url(Encoding.UTF8.GetBytes(new JsonObject { ["stamp"] = stamp, ["key"] = last[key]?.DeepClone(), ["id"] = last["id"]!.DeepClone() }.ToJsonString()));
        }
        var pageIds = "[" + string.Join(',', shown.Select(row => row["id"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture))) + "]";
        var (matches, clocks, complete, downloaded) = database.Read(DatabaseFile.Catalog, c => (
            c.Query("""
                SELECT entry_id,source_id,source_title,locked FROM source_matches WHERE active=1 AND entry_id IN(SELECT value FROM json_each($ids))
                UNION ALL SELECT entry_id,source_id,source_title,locked FROM source_variants WHERE active=1 AND entry_id IN(SELECT value FROM json_each($ids))
                """, ("$ids", pageIds)),
            c.Query("SELECT * FROM due_work WHERE kind='library' AND work_key IN(SELECT CAST(value AS TEXT) FROM json_each($ids))", ("$ids", pageIds)),
            c.Query("SELECT entry_id,observed_at FROM library_complete_observations WHERE entry_id IN(SELECT value FROM json_each($ids))", ("$ids", pageIds)),
            c.Query("SELECT entry_id,COUNT(*) downloaded FROM library_downloads WHERE entry_id IN(SELECT value FROM json_each($ids)) GROUP BY entry_id", ("$ids", pageIds))));
        var clockById = clocks.ToDictionary(row => row.Text("work_key"), Json, StringComparer.Ordinal);
        var completeById = complete.ToDictionary(row => row.Integer("entry_id"), row => row.Text("observed_at"));
        var downloadedById = downloaded.ToDictionary(row => row.Integer("entry_id"), row => row.Integer("downloaded"));
        var labels = new Dictionary<long, string> { [0] = "Unknown", [1] = "Ongoing", [2] = "Completed", [3] = "Licensed", [4] = "Publishing finished", [5] = "Cancelled", [6] = "Hiatus" };
        foreach (var row in shown)
        {
            var id = row["id"]!.GetValue<long>();
            var observation = LibraryStore.ParseObject(row["observation_json"]?.GetValue<string>() ?? "{}");
            row.Remove("observation_json"); row.Remove("title_key");
            row["status_label"] = labels.GetValueOrDefault(row["status"] is JsonValue status && status.TryGetValue<long>(out var code) ? code : 0, "Unknown");
            row["entry_match_count"] = row["match_count"]?.DeepClone();
            row["match_count"] = row["source_count"]?.DeepClone();
            row["monitored"] = row["monitored"] is JsonValue flag && flag.TryGetValue<long>(out var on) && on != 0;
            row["library_stats"] = new JsonObject
            {
                ["entry_id"] = id,
                ["chapter_count"] = observation["chapter_count"]?.DeepClone(),
                ["downloaded_chapter_count"] = downloadedById.GetValueOrDefault(id),
            };
            row["observation"] = observation;
            row["due"] = clockById.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out var clock) ? clock : new JsonObject();
            row["last_complete_observed_at"] = completeById.GetValueOrDefault(id, "");
        }
        IReadOnlyList<JsonObject> entries = display is null ? shown : display.OverlayMany(shown);
        return new JsonObject
        {
            ["entries"] = new JsonArray(entries.Select(row => (JsonNode?)row).ToArray()),
            ["total"] = total, ["next_cursor"] = next, ["limit"] = limit,
            ["sources"] = new JsonArray(sources.Select(row => (JsonNode?)Json(row)).ToArray()),
            ["matches"] = new JsonArray(matches.Select(row => (JsonNode?)Json(row)).ToArray()),
            ["variants"] = new JsonArray(),
            ["subscription"] = subscription.DeepClone(),
            ["scope"] = query.Scope,
        };
    }

    /// <summary>Source families / custom groups as shown in the source controls (library_projection.shown_groups).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> ShownGroups(IReadOnlyList<DatabaseRow> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var families = new Dictionary<(string Name, string Url), List<DatabaseRow>>();
        foreach (var source in sources)
        {
            if (!(source.Flag("available") && source.Flag("enabled_language") && (source.Flag("enabled_search") || source.Flag("enabled_chapters")))) continue;
            var name = source.Text("name"); if (name.Length == 0) name = source.Text("source_id");
            var key = (name.Trim().ToLowerInvariant(), source.Text("base_url").Trim().ToLowerInvariant());
            if (!families.TryGetValue(key, out var list)) families[key] = list = [];
            list.Add(source);
        }
        var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, variants) in families)
        {
            var assigned = variants.Select(row => row["group_id"] is null ? null : row.Text("group_id")).Distinct().ToArray();
            var group = assigned.Length == 1 && assigned[0] is { } custom ? "custom|" + custom : "family|" + key.Name + "|" + key.Url;
            if (!groups.TryGetValue(group, out var members)) groups[group] = members = [];
            members.AddRange(variants.Select(row => row.Text("source_id")));
        }
        return groups.Values.Select(values => (IReadOnlyList<string>)values.Order(StringComparer.Ordinal).ToArray()).ToArray();
    }

    internal static JsonObject Json(DatabaseRow row)
    {
        var result = new JsonObject();
        foreach (var (name, value) in row)
            result[name] = value switch
            {
                null => null,
                long number => JsonValue.Create(number),
                int number => JsonValue.Create(number),
                double number => JsonValue.Create(number),
                string text => JsonValue.Create(text),
                byte[] => null,
                _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
            };
        return result;
    }

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];

    private static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_');

    private static byte[] Base64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
    }
}
