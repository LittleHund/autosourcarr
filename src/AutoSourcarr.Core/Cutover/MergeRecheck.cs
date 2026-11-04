using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Maintenance;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Duplicates;

namespace AutoSourcarr.Core.Cutover;

/// <summary>One earlier automatic merge the recheck says would not be made again under the current rules.</summary>
public sealed record MergeRecheckItem(long Duplicate, long Canonical, string Title, string Reason)
{
    public JsonObject ToJson() => new() { ["duplicate_entry_id"] = Duplicate, ["canonical_entry_id"] = Canonical, ["title"] = Title, ["reason"] = Reason };
}

public sealed class MergeRecheckResult
{
    public bool DecisionLogPresent { get; init; }
    public long DecisionLogRows { get; init; }
    public int Merges { get; set; }
    public int ManualMerges { get; set; }
    public int AutomaticMerges { get; set; }
    public int Hold { get; set; }
    public int WouldNotHold { get; set; }
    public int NotRechecked { get; set; }
    public List<MergeRecheckItem> Failing { get; } = [];
    /// <summary>Merges whose stored snapshot cannot be read: not rechecked, and listed (a new merge among them fails the verdict).</summary>
    public List<MergeRecheckItem> Unreadable { get; } = [];
    /// <summary>Merges that fail the recheck or cannot be rechecked: a merge the migration made among them fails the verdict.</summary>
    public IEnumerable<MergeRecheckItem> Suspect => Failing.Concat(Unreadable);

    public JsonObject ToJson() => new()
    {
        ["decision_log_present"] = DecisionLogPresent, ["decision_log_rows"] = DecisionLogRows, ["merges"] = Merges,
        ["manual_merges"] = ManualMerges, ["automatic_merges"] = AutomaticMerges, ["hold"] = Hold, ["would_not_hold"] = WouldNotHold,
        ["not_rechecked"] = NotRechecked, ["unreadable_snapshots"] = new JsonArray(Unreadable.Select(item => (JsonNode)item.ToJson()).ToArray()), ["would_not_hold_items"] = new JsonArray(Failing.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["applied"] = false,
    };
}

/// <summary>
/// IMP-9 dry run on the migrated catalogue (read-only, nothing is applied): the merge log is present, and every earlier
/// merge (entry_aliases) is rechecked against the current rules. Manual merges are your decisions and always hold. An
/// automatic merge holds when its stored merge snapshot (automatic_merge_events) has at least one record pair that the
/// current repair rules call directly compatible (never across colour/mono or manga/webtoon editions) and no
/// "different works" review sits between the two entries. Merges without a stored snapshot are counted as not rechecked.
/// </summary>
public static class MergeRecheck
{
    public static MergeRecheckResult Run(LegacyDatabase catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var hasLog = catalog.HasTable("decision_log");
        var result = new MergeRecheckResult { DecisionLogPresent = hasLog, DecisionLogRows = hasLog ? catalog.Count("decision_log") : 0 };
        if (!catalog.HasTable("entry_aliases")) return result;
        var ids = new EntryIds(catalog);
        var events = new Dictionary<long, Dictionary<string, string?>>();
        if (catalog.HasTable("automatic_merge_events"))
            foreach (var row in catalog.Query("SELECT duplicate_entry_id,target_snapshot_json,duplicate_snapshot_json FROM automatic_merge_events ORDER BY id"))
                if (EntryIds.Id(row["duplicate_entry_id"]) is { } duplicate) events[duplicate] = row;
        var different = new HashSet<(long, long)>();
        if (catalog.HasTable("duplicate_reviews"))
            foreach (var row in catalog.Query("SELECT left_entry_id,right_entry_id FROM duplicate_reviews WHERE decision='not_duplicate'"))
                if (EntryIds.Id(row["left_entry_id"]) is { } left && EntryIds.Id(row["right_entry_id"]) is { } right)
                    different.Add((Math.Min(left, right), Math.Max(left, right)));
        var fingerprints = catalog.HasTable("thumbnail_fingerprints")
            ? catalog.Query("SELECT subject_key,fingerprint FROM thumbnail_fingerprints").GroupBy(row => row["subject_key"] ?? "", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First()["fingerprint"] ?? "", StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in catalog.Query("SELECT old_entry_id,canonical_entry_id,reason FROM entry_aliases ORDER BY merged_at"))
        {
            if (EntryIds.Id(row["old_entry_id"]) is not { } duplicate || EntryIds.Id(row["canonical_entry_id"]) is not { } target) continue;
            result.Merges++;
            if (!DuplicateMatching.AutomaticMergeReason(row["reason"] ?? "")) { result.ManualMerges++; continue; }
            result.AutomaticMerges++;
            var canonical = ids.Resolve(target);
            if (different.Contains((Math.Min(duplicate, target), Math.Max(duplicate, target))))
            {
                result.WouldNotHold++;
                result.Failing.Add(new(duplicate, canonical, ids.Title(canonical), "reviewed as different works"));
                continue;
            }
            if (!events.TryGetValue(duplicate, out var merge))
            {
                result.NotRechecked++;
                result.Unreadable.Add(new(duplicate, canonical, ids.Title(canonical), "no stored merge snapshot"));
                continue;
            }
            var left = Records(merge["duplicate_snapshot_json"], fingerprints);
            var right = Records(merge["target_snapshot_json"], fingerprints);
            if (left is null || right is null)
            {
                result.NotRechecked++;
                result.Unreadable.Add(new(duplicate, canonical, ids.Title(canonical), "the stored merge snapshot cannot be read"));
                continue;
            }
            if (left.Count == 0 || right.Count == 0)
            {
                result.NotRechecked++;
                result.Unreadable.Add(new(duplicate, canonical, ids.Title(canonical), "the stored merge snapshot holds no records"));
                continue;
            }
            if (left.Any(a => right.Any(b => CatalogueMaintenance.RecordsDirectlyCompatible(a, b)))) result.Hold++;
            else
            {
                result.WouldNotHold++;
                result.Failing.Add(new(duplicate, canonical, ids.Title(canonical), "no record pair passes the current edition and evidence checks"));
            }
        }
        return result;
    }

    /// <summary>The records of one stored merge snapshot, or null when the snapshot is not valid JSON.</summary>
    private static List<RecordRepairSnapshot>? Records(string? json, IReadOnlyDictionary<string, string> fingerprints)
    {
        var list = new List<RecordRepairSnapshot>();
        JsonNode? node;
        try { node = JsonNode.Parse(json ?? ""); }
        catch (System.Text.Json.JsonException) { return null; }
        foreach (var name in new[] { "matches", "variants", "records" })
            if (node?[name] is JsonArray rows)
                foreach (var item in rows.OfType<JsonObject>())
                {
                    string Text(string key) => item[key] is JsonValue value ? value.ToString() : "";
                    var title = Text("source_title").Trim();
                    var source = Text("source_id");
                    var manga = long.TryParse(Text("source_manga_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
                    var fingerprint = fingerprints.TryGetValue("source:" + source + ":" + manga.ToString(CultureInfo.InvariantCulture), out var found) ? found : "";
                    list.Add(new RecordRepairSnapshot(source, manga, title, TextMatcher.NormalizeTitle(title), Text("description"), Text("author"), fingerprint,
                        TextMatcher.TitleColorVariant(title), CatalogueMaintenance.MediaMode(title, Text("genre")), Text("locked") is "1" or "true"));
                }
        return list;
    }
}
