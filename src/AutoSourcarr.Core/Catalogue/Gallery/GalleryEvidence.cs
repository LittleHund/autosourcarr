using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Gallery;

public static class GalleryEvidence
{
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    public static JsonObject Pair(JsonObject? left, JsonObject? right, Func<IEnumerable<JsonNode?>, IReadOnlySet<string>> canonicalize, double threshold = .94)
    {
        left ??= new(); right ??= new();
        var result = new JsonObject { ["applies"] = left.Count > 0 || right.Count > 0, ["eligible"] = false, ["score"] = 0.0, ["title_score"] = 0.0, ["tag_overlap"] = new JsonArray(), ["reason"] = "gallery metadata needs source detail hydration" };
        if (left.Count == 0 || right.Count == 0) return result;
        if (GalleryJson.Truth(left["animated"]) != GalleryJson.Truth(right["animated"])) { result["reason"] = "animated and non-animated galleries are separate entries"; return result; }
        if (GalleryJson.String(left["kind"]) == "collection" || GalleryJson.String(right["kind"]) == "collection") { result["reason"] = "artist collections need manual work-level review"; return result; }
        var lt = GalleryJson.String(left["title"]); var rt = GalleryJson.String(right["title"]);
        var score = TextMatcher.BestAutomaticTitleScore([lt], [rt]).Score; result["title_score"] = score;
        foreach (var (name, function) in new (string, Func<string?, string>)[] { ("color edition", TextMatcher.TitleColorVariant), ("media edition", TextMatcher.TitleMediaVariant) })
        {
            var a = function(GalleryJson.First(left["raw_title"], JsonValue.Create(lt))); var b = function(GalleryJson.First(right["raw_title"], JsonValue.Create(rt)));
            if (a != "unknown" && b != "unknown" && a != b) { result["reason"] = "gallery " + name + " conflict"; return result; }
        }
        string[] Serial(string value) => Regex.Matches(value, @"\b(?:part|vol(?:ume)?\.?|chapter|ch\.?)\s*(\d+(?:\.\d+)?)", IgnoreCase).Select(match => match.Groups[1].Value).ToArray();
        var ls = Serial(GalleryJson.First(left["raw_title"], JsonValue.Create(lt))); var rs = Serial(GalleryJson.First(right["raw_title"], JsonValue.Create(rt)));
        if (!ls.SequenceEqual(rs, StringComparer.Ordinal) && (ls.Length > 0 || rs.Length > 0)) { result["reason"] = "gallery part/volume mismatch"; return result; }
        var ltags = Tags(left); var rtags = Tags(right);
        foreach (var ns in new[] { "artist", "circle", "parody" })
        {
            var a = canonicalize(ltags.Where(tag => GalleryJson.String(tag["namespace"]) == ns)); var b = canonicalize(rtags.Where(tag => GalleryJson.String(tag["namespace"]) == ns));
            if (a.Count > 0 && b.Count > 0 && !a.Overlaps(b)) { result["reason"] = "gallery " + ns + " conflict"; return result; }
        }
        var lk = canonicalize(Informative(ltags)); var rk = canonicalize(Informative(rtags)); var overlap = lk.Intersect(rk, StringComparer.Ordinal).Order(UnicodeCodePointComparer.Instance).ToArray();
        result["tag_overlap"] = new JsonArray(overlap.Select(value => JsonValue.Create(value)).ToArray());
        var ratio = (double)overlap.Length / Math.Max(1, Math.Min(lk.Count, rk.Count)); result["tag_similarity"] = ratio;
        var creators = canonicalize(ltags.Where(tag => GalleryJson.String(tag["namespace"]) is "artist" or "circle")).Overlaps(canonicalize(rtags.Where(tag => GalleryJson.String(tag["namespace"]) is "artist" or "circle")));
        var normalized = TextMatcher.NormalizeTitle(lt); var distinctive = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 || normalized.EnumerateRunes().Count() >= 18;
        var enough = overlap.Length >= 2 && ratio >= .5 && creators || overlap.Length >= 3 && ratio >= .75 && distinctive;
        var eligible = score >= Math.Max(.94, threshold) && enough; result["eligible"] = eligible; result["score"] = eligible ? score : 0;
        result["reason"] = eligible ? "gallery title " + Math.Round(score * 100, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture) + "% + " + overlap.Length.ToString(CultureInfo.InvariantCulture) + " shared source tags" : "gallery match needs a strong work title and informative shared tags";
        return result;
    }

    public static JsonObject Family(JsonObject? left, JsonObject? right, Func<IEnumerable<JsonNode?>, IReadOnlySet<string>> canonicalize, double threshold = .94)
    {
        left ??= new(); right ??= new();
        var result = new JsonObject { ["applies"] = left.Count > 0 || right.Count > 0, ["eligible"] = false, ["score"] = 0.0, ["title_score"] = 0.0, ["reason"] = "gallery family needs a source-native creator and work title" };
        if (left.Count == 0 || right.Count == 0 || GalleryJson.Truth(left["animated"]) != GalleryJson.Truth(right["animated"])) return result;
        var lt = GalleryJson.First(left["work_title"], left["title"]); var rt = GalleryJson.First(right["work_title"], right["title"]);
        var score = TextMatcher.BestAutomaticTitleScore([lt], [rt]).Score; result["title_score"] = score; if (score < Math.Max(.94, threshold)) return result;
        foreach (var function in new Func<string?, string>[] { TextMatcher.TitleColorVariant, TextMatcher.TitleMediaVariant })
        { var a = function(GalleryJson.First(left["raw_title"], JsonValue.Create(lt))); var b = function(GalleryJson.First(right["raw_title"], JsonValue.Create(rt))); if (a != "unknown" && b != "unknown" && a != b) return result; }
        var creator = false;
        foreach (var ns in new[] { "artist", "circle", "parody" })
        {
            var a = canonicalize(Tags(left).Where(tag => GalleryJson.String(tag["namespace"]) == ns)); var b = canonicalize(Tags(right).Where(tag => GalleryJson.String(tag["namespace"]) == ns));
            if (a.Count > 0 && b.Count > 0 && !a.Overlaps(b)) return result; if (ns is "artist" or "circle" && a.Overlaps(b)) creator = true;
        }
        var sameSource = GalleryJson.Truth(left["source_id"]) && GalleryJson.String(left["source_id"]) == GalleryJson.String(right["source_id"]);
        var collection = GalleryJson.String(left["kind"]) == "collection" || GalleryJson.String(right["kind"]) == "collection";
        if (collection && (!sameSource || GalleryJson.String(left["kind"]) != GalleryJson.String(right["kind"]))) return result;
        if (sameSource && creator && TextMatcher.NormalizeTitle(lt) == TextMatcher.NormalizeTitle(rt)) { result["eligible"] = true; result["score"] = score; result["reason"] = "same-source gallery family: exact work title and creator"; result["chapter_family"] = true; return result; }
        if (collection || left["chapter_number"] is null && right["chapter_number"] is null) return result;
        var l = (JsonObject)left.DeepClone(); var r = (JsonObject)right.DeepClone(); l["title"] = lt; l["raw_title"] = lt; l["kind"] = "work"; r["title"] = rt; r["raw_title"] = rt; r["kind"] = "work";
        var evidence = Pair(l, r, canonicalize, threshold); evidence["chapter_family"] = GalleryJson.Truth(evidence["eligible"]); return evidence;
    }

    public static JsonObject CleanNativePayload(string sourceId, JsonObject manga, JsonObject? source, JsonObject policy)
    {
        var payload = (JsonObject)manga.DeepClone(); var raw = GalleryJson.String(payload["title"]).Trim();
        var names = new[] { GalleryJson.String(source?["display_name"]), GalleryJson.String(source?["name"]) }.Where(value => value.Length > 0).Select(value => value.Trim()).Distinct(StringComparer.Ordinal);
        var cleaned = TextMatcher.StripKnownSourceSuffix(raw, names);
        if (cleaned.Length > 0 && cleaned != raw) { if (!payload.ContainsKey("_autosourcarr_raw_title")) payload["_autosourcarr_raw_title"] = raw; payload["title"] = cleaned; }
        if (sourceId.Length > 0) payload["sourceId"] = sourceId;
        var result = GalleryMetadata.Enrich(payload, GalleryPolicy.SourceProfile(source, policy));
        if (result["_gallery_metadata"] is JsonObject metadata && !GalleryJson.Truth(policy["enabled"])) result["title"] = GalleryJson.First(metadata["raw_title"], result["title"]);
        return result;
    }

    public static JsonObject? Compose(JsonObject candidate, IReadOnlyList<JsonObject> trusted, Func<string, JsonObject> source, Func<string, JsonObject> policy,
        Func<IEnumerable<JsonNode?>, IReadOnlySet<string>> canonicalize, double threshold = .94)
    {
        if (candidate["_gallery_metadata"] is not JsonObject) { var sid = GalleryJson.String(candidate["sourceId"]); candidate = CleanNativePayload(sid, candidate, source(sid), policy(sid)); }
        var cm = candidate["_gallery_metadata"] as JsonObject ?? new();
        var anchors = trusted.Select(payload => payload["_gallery_metadata"] as JsonObject ?? new()).ToArray();
        if (cm.Count == 0 && anchors.All(item => item.Count == 0)) return null;
        if (cm.Count == 0) cm = GalleryMetadata.Enrich(candidate, new() { ["metadata_profile"] = "gallery" })["_gallery_metadata"]!.AsObject();
        for (var index = 0; index < anchors.Length; index++) if (anchors[index].Count == 0) anchors[index] = GalleryMetadata.Enrich(trusted[index], new() { ["metadata_profile"] = "gallery" })["_gallery_metadata"]!.AsObject();
        var pairs = new List<JsonObject>();
        foreach (var anchor in anchors.Length == 0 ? new JsonObject[] { new() } : anchors)
        {
            var refusal = GalleryPolicy.PairGate(cm, anchor, policy, canonicalize, threshold); if (refusal is not null) { pairs.Add(refusal); continue; }
            var family = Family(cm, anchor, canonicalize, threshold); pairs.Add(GalleryJson.Truth(family["eligible"]) ? family : Pair(cm, anchor, canonicalize, threshold));
        }
        return pairs.FirstOrDefault(item => !GalleryJson.Truth(item["eligible"])) ?? pairs.MaxBy(item => GalleryJson.Number(item["score"]));
    }
    private static IEnumerable<JsonObject> Tags(JsonObject metadata) => (metadata["tags"] as JsonArray ?? new()).OfType<JsonObject>();
    private static IEnumerable<JsonObject> Informative(IEnumerable<JsonObject> tags) => tags.Where(tag => GalleryJson.String(tag["namespace"]) is not ("language" or "category" or "type" or "uploader") && !GalleryMetadata.GenericTags.Contains(UnicodeCaseFold.Fold(GalleryJson.Text(tag["name"]))));
}
