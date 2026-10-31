using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Gallery;

public static class GalleryPolicy
{
    public static JsonObject Default => new() { ["profile"] = "auto", ["enabled"] = true, ["criteria"] = new JsonArray(), ["title_minimum"] = .94, ["tag_similarity"] = .5, ["tag_overlap"] = 2 };
    public static IReadOnlySet<string> Criteria { get; } = new HashSet<string>(["artist", "circle", "parody", "language", "category", "tags"], StringComparer.Ordinal);
    public static JsonObject Validate(JsonNode? value)
    {
        if (value is not JsonObject obj || !obj.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(Default.Select(pair => pair.Key))) throw new ArgumentException("Use the complete gallery policy");
        if (GalleryJson.String(obj["profile"]) is not ("auto" or "gallery") || obj["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out _)) throw new ArgumentException("Choose the gallery profile and combining setting");
        if (obj["criteria"] is not JsonArray fields || fields.Count > Criteria.Count || fields.Any(field => field is not JsonValue item || !item.TryGetValue<string>(out var name) || !Criteria.Contains(name)) || fields.Select(field => field!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != fields.Count) throw new ArgumentException("Choose supported gallery matching criteria");
        foreach (var (name, minimum) in new[] { ("title_minimum", .94), ("tag_similarity", .01) })
        {
            if (obj[name] is not JsonValue number || number.TryGetValue<bool>(out _) || !double.TryParse(number.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var raw) || !double.IsFinite(raw) || raw < minimum || raw > 1) throw new ArgumentException(name + " is outside its allowed range");
        }
        if (obj["tag_overlap"] is not JsonValue overlap || overlap.TryGetValue<bool>(out _) || !long.TryParse(overlap.ToJsonString(), out var count) || count is < 1 or > 50) throw new ArgumentException("Choose 1 to 50 shared informative tags");
        var result = (JsonObject)obj.DeepClone(); result["criteria"] = new JsonArray(fields.Select(field => field!.GetValue<string>()).Order(UnicodeCodePointComparer.Instance).Select(field => JsonValue.Create(field)).ToArray()); return result;
    }
    public static JsonObject SourceProfile(JsonObject? source, JsonObject policy)
    {
        var result = source is null ? new JsonObject() : (JsonObject)source.DeepClone(); if (GalleryJson.String(policy["profile"]) == "gallery") result["metadata_profile"] = "gallery"; return result;
    }
    public static JsonObject? PairGate(JsonObject left, JsonObject right, Func<string, JsonObject> effective, Func<IEnumerable<JsonNode?>, IReadOnlySet<string>> canonicalize, double threshold = .94, bool manual = false)
    {
        foreach (var sid in new[] { GalleryJson.String(left["source_id"]), GalleryJson.String(right["source_id"]) }.Where(sid => sid.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var policy = effective(sid);
            if (!GalleryJson.Truth(policy["enabled"])) return Failure("Gallery combining is disabled for this source");
            if (manual) continue;
            var score = TextMatcher.BestAutomaticTitleScore([GalleryJson.First(left["work_title"], left["title"])], [GalleryJson.First(right["work_title"], right["title"])]).Score;
            if (score < Math.Max(.94, Math.Max(threshold, GalleryJson.Number(policy["title_minimum"])))) return Failure("Gallery native work title is below the selected minimum");
            foreach (var field in policy["criteria"]!.AsArray())
            {
                var criterion = field!.GetValue<string>(); var a = Keys(left, criterion, canonicalize); var b = Keys(right, criterion, canonicalize);
                if (a.Count == 0 || b.Count == 0) return Failure("Gallery " + criterion + " evidence is missing");
                var shared = a.Intersect(b, StringComparer.Ordinal).Count();
                if (criterion == "tags") { if (shared < GalleryJson.Integer(policy["tag_overlap"]) || (double)shared / a.Union(b, StringComparer.Ordinal).Count() < GalleryJson.Number(policy["tag_similarity"])) return Failure("Gallery informative tags do not meet the selected overlap"); }
                else if (shared == 0) return Failure("Gallery " + criterion + " does not match");
            }
        }
        return null;
    }
    public static void GroupingGate(IEnumerable<JsonObject> payloads, Func<string, JsonObject> effective)
    {
        var seen = new HashSet<(string, string)>(); var galleries = new List<JsonObject>();
        foreach (var payload in payloads)
        {
            if (!seen.Add((GalleryJson.String(payload["sourceId"]), GalleryJson.String(payload["id"])))) continue;
            if (payload["_gallery_metadata"] is JsonObject { Count: > 0 } metadata) galleries.Add(metadata);
        }
        if (seen.Count > 1 && galleries.Any(data => !GalleryJson.Truth(effective(GalleryJson.String(data["source_id"]))["enabled"]))) throw new ArgumentException("Gallery combining is disabled for this source; enable it before combining records");
    }
    private static IReadOnlySet<string> Keys(JsonObject data, string criterion, Func<IEnumerable<JsonNode?>, IReadOnlySet<string>> canonicalize)
    {
        var tags = (data["tags"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
        if (criterion == "tags") return canonicalize(tags.Where(tag => GalleryJson.String(tag["namespace"]) is not ("language" or "category" or "type" or "uploader") && !GalleryMetadata.GenericTags.Contains(UnicodeCaseFold.Fold(GalleryJson.String(tag["name"])))));
        var selected = tags.Where(tag => criterion == "artist" ? GalleryJson.String(tag["namespace"]) is "artist" or "author" : GalleryJson.String(tag["namespace"]) == criterion).Select(tag => { var value = (JsonObject)tag.DeepClone(); value["namespace"] = criterion; return value; }).ToList();
        if (selected.Count == 0 && GalleryJson.Truth(data[criterion])) selected.Add(new() { ["namespace"] = criterion, ["name"] = data[criterion]!.DeepClone() });
        return canonicalize(selected);
    }
    private static JsonObject Failure(string reason) => new() { ["applies"] = true, ["eligible"] = false, ["score"] = 0.0, ["title_score"] = 0.0, ["reason"] = reason };
}
