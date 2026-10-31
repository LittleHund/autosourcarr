using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Gallery;

public static class GalleryTagEquivalence
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal) { ["sci fi"]="science fiction",["sci-fi"]="science fiction",["scifi"]="science fiction",["full color"]="full colour",["full-color"]="full colour",["full-colour"]="full colour",["colour"]="color",["greyscale"]="grayscale",["one-shot"]="one shot",["oneshot"]="one shot" };
    private static readonly Dictionary<string, string> Namespaces = new(StringComparer.Ordinal) { ["artists"]="artist",["authors"]="author",["groups"]="circle",["group"]="circle",["languages"]="language",["characters"]="character",["parodies"]="parody",["genres"]="tag",["genre"]="tag",["tags"]="tag",["misc"]="tag",["general"]="tag" };
    public static string Normalize(string value) => Regex.Replace(UnicodeCaseFold.Fold(value.Normalize(NormalizationForm.FormKC)).Replace('_', ' '), @"[\s\x1c-\x1f]+", " ").Trim();
    public static JsonObject? Tag(JsonNode? value)
    {
        string name, ns;
        if (value is JsonObject obj) { name = UnicodeCaseFold.Truncate(GalleryJson.First(obj["name"], obj["tag"]).Trim(), 200); ns = UnicodeCaseFold.Truncate(Normalize(GalleryJson.First(obj["namespace"], JsonValue.Create("tag"))), 50); }
        else
        {
            name = UnicodeCaseFold.Truncate(GalleryJson.String(value).Trim(), 200); ns = "tag"; var index = name.IndexOf(':');
            if (index >= 0 && Regex.IsMatch(name[..index], "^[a-zA-Z _-]{1,30}$") && name[(index + 1)..].Trim().Length > 0) { ns = Normalize(name[..index]); name = name[(index + 1)..].Trim(); }
        }
        ns = Namespaces.GetValueOrDefault(ns, ns);
        return name.Length == 0 || name.Any(character => character < 32) ? null : new() { ["namespace"] = ns, ["name"] = name, ["normalized"] = Normalize(name) };
    }
    public static IReadOnlySet<string> CanonicalKeys(IEnumerable<JsonNode?> tags, IReadOnlyDictionary<string, string>? mapping = null)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in tags)
        {
            var tag = Tag(value); if (tag is null) continue; var key = Key(tag); var automatic = Automatic(key);
            result.Add(mapping is not null && mapping.TryGetValue(key, out var found) ? found : mapping is not null && mapping.TryGetValue(automatic, out found) ? found : automatic);
        }
        return result;
    }
    public static IReadOnlyDictionary<string, string> Mapping(IReadOnlyList<JsonObject> rows, IReadOnlyList<JsonObject> reviews, bool strict = false)
    {
        var keys = rows.ToDictionary(row => GalleryJson.Integer(row["id"]), Key);
        var all = keys.Values.Concat(keys.Values.Select(Automatic)).ToHashSet(StringComparer.Ordinal);
        var parent = all.ToDictionary(key => key, key => key, StringComparer.Ordinal);
        var members = all.ToDictionary(key => key, key => new HashSet<string>([key], StringComparer.Ordinal), StringComparer.Ordinal);
        var blocked = reviews.Where(row => GalleryJson.String(row["decision"]) == "different").Select(row => (keys[GalleryJson.Integer(row["left_id"])], keys[GalleryJson.Integer(row["right_id"])])).ToArray();
        string Root(string key) { while (parent[key] != key) { parent[key] = parent[parent[key]]; key = parent[key]; } return key; }
        void Union(string left, string right, bool manual = false)
        {
            left = Root(left); right = Root(right); if (left == right) return;
            var conflict = blocked.Any(pair => members[left].Contains(pair.Item1) && members[right].Contains(pair.Item2) || members[left].Contains(pair.Item2) && members[right].Contains(pair.Item1));
            if (conflict) { if (strict && manual) throw new ArgumentException("This would contradict a Separate decision. Clear the conflicting review first."); return; }
            if (UnicodeCodePointComparer.Instance.Compare(left, right) > 0) (left, right) = (right, left);
            parent[right] = left; members[left].UnionWith(members[right]); members.Remove(right);
        }
        foreach (var row in reviews) if (GalleryJson.String(row["decision"]) == "same") Union(keys[GalleryJson.Integer(row["left_id"])], keys[GalleryJson.Integer(row["right_id"])], true);
        foreach (var key in all.Order(UnicodeCodePointComparer.Instance)) Union(key, Automatic(key));
        return all.ToDictionary(key => key, Root, StringComparer.Ordinal);
    }
    private static string Key(JsonObject tag) => GalleryJson.String(tag["namespace"]) + ":" + Normalize(GalleryJson.String(tag["name"]));
    private static string Automatic(string key)
    {
        var index = key.IndexOf(':'); var ns = key[..index]; var name = key[(index + 1)..];
        return ns + ":" + (ns is "tag" or "category" or "type" ? Aliases.GetValueOrDefault(name, name) : name);
    }
}
