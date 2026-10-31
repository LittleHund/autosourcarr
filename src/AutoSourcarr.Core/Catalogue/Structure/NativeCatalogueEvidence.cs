using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Text;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Structure;

/// <summary>Released native duplicate snapshot data, not an eligible-match decision. Display/shared metadata is excluded.</summary>
public sealed class NativeCatalogueEvidence(CatalogueMetadata metadata, GalleryEvidenceService gallery, Func<int> minimumSummaryCharacters, Func<bool> exactThumbnailMatching)
{
    public JsonObject Snapshot(DatabaseSession c, long entryId)
    {
        entryId = PortableIdentityResolver.Canonical(c, entryId); var entry = c.Query("SELECT * FROM entries WHERE id=$p0", P(entryId)).SingleOrDefault() ?? throw new KeyNotFoundException("Native evidence entry is missing");
        var identities = c.Query("SELECT provider,external_id,confidence,match_type,locked FROM identity_links WHERE entry_id=$p0 AND confidence>=0.94 ORDER BY provider,external_id", P(entryId));
        var records = new List<DatabaseRow>(); foreach (var table in new[] { "source_matches", "source_variants" }) records.AddRange(c.Query($"SELECT * FROM {table} WHERE entry_id=$p0 AND active=1", P(entryId)));
        var titles = CatalogueMetadata.NativeTitleRows(c, entryId).Where(row => Text(row, "title_type") is "source" or "source-variant" or "main-library" or "manual")
            .Where(row => TextMatcher.IsMatchEvidenceTitle(Text(row, "title"), Text(row, "title_type"))).Select(row => UnicodeText.Trim(Text(row, "title"))).Where(title => title.Length != 0).Distinct(StringComparer.Ordinal).Order(UnicodeCodePointComparer.Instance).ToArray();
        var descriptions = records.Select(row => UnicodeText.Trim(row.Text("description"))).Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Where(value => Length(TextMatcher.NormalizeDescription(value)) >= Math.Max(1, minimumSummaryCharacters())).Take(12).ToArray();
        var color = metadata.RecomputeColor(c, entryId); var media = metadata.RecomputeMedia(c, entryId);
        var details = identities.Where(row => row.Text("provider").Length != 0 && row.Text("external_id").Length != 0).Select(row => new JsonObject { ["provider"] = row.Text("provider"), ["external_id"] = row.Text("external_id"), ["confidence"] = row.Number("confidence"), ["match_type"] = row.Text("match_type"), ["locked"] = row.Flag("locked") }).ToArray();
        JsonObject Select(JsonObject payload, IReadOnlyList<string> keys, bool includeMissing)
        { var value = new JsonObject(); foreach (var key in keys) if (payload.ContainsKey(key) || includeMissing) value[key] = payload[key]?.DeepClone(); return value; }
        var nativePayloads = gallery.TrustedNativePayloads(entryId).Select(payload => Select(payload, ["_gallery_metadata", "sourceId", "title", "author", "artist", "genre", "tags", "description"], false));
        var policyPayloads = records.Select(row => gallery.CleanNative(row.Text("source_id"), GalleryPayload(row))).Select(payload => Select(payload, ["sourceId", "title", "description", "author", "artist", "genre", "tags", "_gallery_metadata"], true));
        var thumbs = new SortedSet<string>(UnicodeCodePointComparer.Instance);
        if (exactThumbnailMatching())
        {
            var subjects = records.Select(row => "source:" + row.Text("source_id") + ":" + row.Integer("source_manga_id").ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            foreach (var url in records.Select(row => row.Text("thumbnail_url")).Append(entry.Text("thumbnail_url")).Where(url => url.Length != 0)) subjects.Add("url:" + Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, false).GetBytes(UnicodeText.Trim(url)))));
            foreach (var subject in subjects) if (c.Scalar("SELECT fingerprint FROM thumbnail_fingerprints WHERE subject_key=$p0", P(subject)) is string fingerprint && fingerprint.Length != 0) thumbs.Add(fingerprint);
        }
        static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return new JsonObject
        {
            ["entry_id"] = entryId, ["preferred_title"] = entry.Text("preferred_title"), ["shared_unverified"] = CatalogueIdentity.Unverified(c, entryId), ["author"] = entry.Text("author"),
            ["titles"] = Strings(titles), ["normalized_titles"] = Strings(titles.Select(TextMatcher.NormalizeTitle).Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Order(UnicodeCodePointComparer.Instance)), ["descriptions"] = Strings(descriptions),
            ["gallery_payloads"] = new JsonArray(nativePayloads.Select(value => (JsonNode?)value).ToArray()), ["source_ids"] = Strings(records.Select(row => row.Text("source_id")).Distinct(StringComparer.Ordinal).Order(UnicodeCodePointComparer.Instance)),
            ["policy_payloads"] = new JsonArray(policyPayloads.Select(value => (JsonNode?)value).ToArray()), ["identities"] = new JsonArray(details.Select(value => (JsonNode?)new JsonArray(value["provider"]!.DeepClone(), value["external_id"]!.DeepClone())).ToArray()),
            ["identity_details"] = new JsonArray(details.Select(value => (JsonNode?)value).ToArray()), ["thumbnail_fingerprints"] = Strings(thumbs), ["color_mode"] = color["color_mode"]!.DeepClone(), ["media_mode"] = media,
            ["color_confidence"] = color["confidence"]!.DeepClone()
        };
    }
}
