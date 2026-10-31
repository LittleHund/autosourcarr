using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Sharing;

public static partial class PortableIdentity
{
    private static string Field(JsonNode? node, string key) => node?[key] is JsonValue value ? value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : "" : "";
    public static JsonObject Describe(JsonObject entry)
    {
        var references = new SortedDictionary<string, JsonObject>(UnicodeCodePointComparer.Instance);
        foreach (var row in entry["identities"] as JsonArray ?? [])
        {
            var provider = Field(row, "provider"); if (!Providers.TryGetValue(provider, out var scope) || Field(row, "identity_scope") != scope) continue;
            if (row?["external_id"] is not JsonValue externalValue || externalValue.GetValueKind() != JsonValueKind.String) continue;
            try
            {
                var external = ExternalId(provider, externalValue.GetValue<string>()); var key = $"asc1:{scope}:{provider}:{external}";
                references[key] = new() { ["id"] = key, ["scope"] = scope, ["provider"] = provider, ["external_id"] = external, ["association"] = Field(row, "locked") == "1" ? "locked" : "inferred" };
            }
            catch (ArgumentException) { }
        }
        foreach (var row in (entry["matches"] as JsonArray ?? []).Concat(entry["source_variants"] as JsonArray ?? []))
        {
            var gallery = GalleryReference(Field(row, "source_manga_url"), Field(row, "real_url"), Field(row, "base_url"));
            if (gallery is not null)
            { var parts = gallery.Split(':'); references[gallery] = new() { ["id"] = gallery, ["scope"] = "gallery", ["provider"] = parts[2], ["external_id"] = parts[3], ["association"] = "source URL" }; continue; }
            try { var key = RecordId(Field(row, "source_id"), Field(row, "source_manga_url")); references[key] = new() { ["id"] = key, ["scope"] = "record", ["provider"] = "tachiyomi", ["association"] = "attached source" }; }
            catch (ArgumentException) { }
        }
        return new() { ["standard"] = "ASC Media ID v1", ["local_entry_id"] = (entry.ContainsKey("canonical_id") ? entry["canonical_id"] : entry["id"])?.DeepClone(), ["references"] = new JsonArray(references.Values.Select(value => (JsonNode?)value).ToArray()),
            ["note"] = "The same public anchor gives the same ID on every instance. Work IDs do not prove edition or chapter equivalence; inferred catalogue associations need review. Different catalogues need a corroborated cross-reference. Local numeric reader IDs stay unchanged." };
    }
}
