using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Catalogue.Tags;

/// <summary>One synonym: tags named <see cref="From"/> in <see cref="Namespace"/> count as <see cref="To"/>. Origin is "built-in" or "user".</summary>
public sealed record TagSynonym(string Namespace, string From, string To, string Origin);

public sealed class TagSynonymException(string message) : ArgumentException(message);

/// <summary>
/// The tag synonym map (MAT-3 evidence and gallery tag comparison): a small built-in vocabulary (the released 0.4.x
/// aliases plus unambiguous format words), user edits kept in the catalogue <c>settings</c> row <see cref="SettingsKey"/>,
/// and the reviewed same/different tag pairs (<c>tag_reviews</c>). Source tags stay stored exactly as the source sent
/// them; the map is applied whenever tags are compared, merged or used as edition evidence. A synonym never merges
/// entries on its own.
/// </summary>
public sealed class TagSynonyms(SqliteDatabase database)
{
    public const string SettingsKey = "tag_synonyms";
    public const int MaximumUserSynonyms = 2000;
    private static readonly string[] AliasNamespaces = ["tag", "category", "type"];
    private static readonly (string From, string To)[] BuiltInPairs =
    [
        // Released 0.4.x vocabulary (tag_store._ALIASES / GalleryTagEquivalence) — unchanged.
        ("sci fi", "science fiction"), ("sci-fi", "science fiction"), ("scifi", "science fiction"),
        ("full color", "full colour"), ("full-color", "full colour"), ("full-colour", "full colour"),
        ("colour", "color"), ("greyscale", "grayscale"), ("one-shot", "one shot"), ("oneshot", "one shot"),
        // Format words used as edition evidence (phase 8, MAT-3). Each names exactly one format.
        ("web comic", "webtoon"), ("webcomic", "webtoon"), ("web-comic", "webtoon"), ("long strip", "webtoon"), ("long-strip", "webtoon"),
        ("vertical scroll", "webtoon"), ("coloured", "colored"), ("full colored", "full colour"), ("full coloured", "full colour"),
        ("black & white", "monochrome"), ("black and white", "monochrome"), ("black-and-white", "monochrome"),
    ];

    public static IReadOnlyList<TagSynonym> BuiltIn { get; } = AliasNamespaces
        .SelectMany(ns => BuiltInPairs.Select(pair => new TagSynonym(ns, GalleryTagEquivalence.Normalize(pair.From), GalleryTagEquivalence.Normalize(pair.To), "built-in")))
        .ToArray();

    /// <summary>The effective map (one catalogue read).</summary>
    public TagSynonymMap Load() => database.Read(DatabaseFile.Catalog, c => Load(c));

    /// <summary>The effective map inside the caller's catalogue session.</summary>
    public static TagSynonymMap Load(DatabaseSession catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var user = ReadUser(catalog);
        var tags = new Dictionary<long, JsonObject>(); var reviews = new List<JsonObject>();
        foreach (var row in catalog.Query("SELECT r.left_id,r.right_id,r.decision,l.namespace lns,l.name lname,t.namespace rns,t.name rname FROM tag_reviews r JOIN tags l ON l.id=r.left_id JOIN tags t ON t.id=r.right_id ORDER BY r.left_id,r.right_id"))
        {
            var left = row.Integer("left_id"); var right = row.Integer("right_id");
            tags[left] = new() { ["id"] = left, ["namespace"] = row.Text("lns"), ["name"] = row.Text("lname") };
            tags[right] = new() { ["id"] = right, ["namespace"] = row.Text("rns"), ["name"] = row.Text("rname") };
            reviews.Add(new() { ["left_id"] = left, ["right_id"] = right, ["decision"] = row.Text("decision") });
        }
        var reviewed = reviews.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : GalleryTagEquivalence.Mapping(tags.Values.ToArray(), reviews);
        return new TagSynonymMap(user, reviewed);
    }

    /// <summary>Replaces the user synonyms. <paramref name="expectedRevision"/> (when given) must match the current one.</summary>
    public TagSynonymMap Save(IReadOnlyList<TagSynonym> synonyms, string? expectedRevision, IDecisionLog decisions, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(synonyms); ArgumentNullException.ThrowIfNull(decisions); ArgumentNullException.ThrowIfNull(actor);
        var cleaned = Validate(synonyms);
        return database.Write(DatabaseFile.Catalog, c =>
        {
            var before = ReadUser(c);
            if (expectedRevision is { Length: > 0 } && expectedRevision != Revision(before))
                throw new InvalidOperationException("The synonyms changed since they were loaded. Reload and try again.");
            var json = Json(cleaned);
            c.Execute("INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$key", SettingsKey), ("$value", json.ToJsonString()));
            decisions.Record(new DecisionEvent("tags.synonyms", actor, [], [], "Tag synonyms edited",
                new JsonObject { ["synonyms"] = Json(before) }, new JsonObject { ["synonyms"] = json.DeepClone() }));
            return Load(c);
        });
    }

    /// <summary>Normalizes, de-duplicates and checks user synonyms (no self-loops except a deliberate built-in override, no cycles).</summary>
    public static IReadOnlyList<TagSynonym> Validate(IReadOnlyList<TagSynonym> synonyms)
    {
        ArgumentNullException.ThrowIfNull(synonyms);
        if (synonyms.Count > MaximumUserSynonyms) throw new TagSynonymException("At most " + MaximumUserSynonyms + " synonyms can be saved.");
        var result = new Dictionary<(string, string), TagSynonym>();
        foreach (var value in synonyms)
        {
            var from = GalleryTagEquivalence.Tag(new JsonObject { ["namespace"] = value.Namespace.Length == 0 ? "tag" : value.Namespace, ["name"] = value.From });
            var to = GalleryTagEquivalence.Tag(new JsonObject { ["namespace"] = value.Namespace.Length == 0 ? "tag" : value.Namespace, ["name"] = value.To });
            if (from is null || to is null) throw new TagSynonymException("Every synonym needs a tag name and the tag it means.");
            var ns = from["namespace"]!.GetValue<string>();
            if (ns is "artist" or "author" or "circle" or "character" or "parody" or "language")
                throw new TagSynonymException("Creator, character, parody and language names are never treated as synonyms.");
            var key = (ns, from["normalized"]!.GetValue<string>());
            var target = to["normalized"]!.GetValue<string>();
            // A self-mapping is allowed only to switch a built-in synonym off.
            if (key.Item2 == target && !BuiltIn.Any(item => item.Namespace == ns && item.From == target))
                throw new TagSynonymException("'" + value.From + "' cannot be a synonym of itself.");
            if (!result.TryAdd(key, new TagSynonym(ns, key.Item2, target, "user")))
                throw new TagSynonymException("'" + value.From + "' is listed twice.");
        }
        var map = new TagSynonymMap(result.Values.ToArray(), new Dictionary<string, string>(StringComparer.Ordinal));
        foreach (var item in result.Values) _ = map.Canonical(item.Namespace, item.From); // throws on a cycle
        return result.Values.OrderBy(item => item.Namespace, StringComparer.Ordinal).ThenBy(item => item.From, StringComparer.Ordinal).ToArray();
    }

    public static string Revision(IReadOnlyList<TagSynonym> user) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Text(Json(user))))).ToLowerInvariant()[..16];

    internal static JsonArray Json(IEnumerable<TagSynonym> values) => new(values.Select(item => (JsonNode)new JsonObject
    { ["namespace"] = item.Namespace, ["from"] = item.From, ["to"] = item.To }).ToArray());

    private static List<TagSynonym> ReadUser(DatabaseSession c)
    {
        var result = new List<TagSynonym>();
        if (c.Scalar("SELECT value FROM settings WHERE key=$key", ("$key", SettingsKey)) is not string raw) return result;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(raw); } catch (JsonException) { return result; }
        if (parsed is not JsonArray array) return result;
        foreach (var item in array.OfType<JsonObject>())
        {
            var ns = item["namespace"] is JsonValue a && a.TryGetValue<string>(out var nsText) ? nsText : "tag";
            var from = item["from"] is JsonValue b && b.TryGetValue<string>(out var fromText) ? fromText : "";
            var to = item["to"] is JsonValue d && d.TryGetValue<string>(out var toText) ? toText : "";
            if (from.Length != 0 && to.Length != 0) result.Add(new TagSynonym(ns, from, to, "user"));
        }
        return result;
    }
}

/// <summary>An immutable effective synonym map. Keys are "namespace:normalized name".</summary>
public sealed class TagSynonymMap
{
    private readonly Dictionary<string, string> _direct = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _reviewed;

    internal TagSynonymMap(IReadOnlyList<TagSynonym> user, IReadOnlyDictionary<string, string> reviewed)
    {
        User = user; _reviewed = reviewed;
        foreach (var item in TagSynonyms.BuiltIn) _direct[item.Namespace + ":" + item.From] = item.Namespace + ":" + item.To;
        // User entries override built-in ones with the same name (a self-mapping switches a built-in off).
        foreach (var item in user) _direct[item.Namespace + ":" + item.From] = item.Namespace + ":" + item.To;
        Revision = TagSynonyms.Revision(user);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in _direct.Keys.Concat(_reviewed.Keys).Concat(_reviewed.Values)) mapping[key] = CanonicalKey(key);
        Mapping = mapping;
    }

    public static TagSynonymMap Empty { get; } = new([], new Dictionary<string, string>(StringComparer.Ordinal));
    public IReadOnlyList<TagSynonym> User { get; }
    public string Revision { get; }
    /// <summary>Key → canonical key for every key the map knows; the form <see cref="GalleryTagEquivalence.CanonicalKeys"/> takes.</summary>
    public IReadOnlyDictionary<string, string> Mapping { get; }

    /// <summary>Built-in entries that no user entry overrides, followed by the user entries.</summary>
    public IReadOnlyList<TagSynonym> Effective => TagSynonyms.BuiltIn
        .Where(item => !User.Any(user => user.Namespace == item.Namespace && user.From == item.From)).Concat(User).ToArray();

    /// <summary>The canonical "namespace:name" of one tag name.</summary>
    public string Canonical(string ns, string name) =>
        CanonicalKey(new JsonObject { ["namespace"] = ns, ["name"] = name }) ?? CanonicalKey(ns + ":" + GalleryTagEquivalence.Normalize(name));

    /// <summary>The canonical key of a source tag value (string "ns:name" or {namespace,name}); null when it is not a tag.</summary>
    public string? CanonicalKey(JsonNode? tag)
    {
        var parsed = GalleryTagEquivalence.Tag(tag);
        return parsed is null ? null : CanonicalKey(parsed["namespace"]!.GetValue<string>() + ":" + parsed["normalized"]!.GetValue<string>());
    }

    public IReadOnlySet<string> CanonicalKeys(IEnumerable<JsonNode?> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags) if (CanonicalKey(tag) is { } key) result.Add(key);
        return result;
    }

    /// <summary>Adds "canonical" to each indexed tag ({namespace,name,normalized}) without changing the stored source fact.</summary>
    public IReadOnlyList<JsonObject> Annotate(IEnumerable<JsonObject> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return tags.Select(tag =>
        {
            var copy = (JsonObject)tag.DeepClone();
            copy["canonical"] = CanonicalKey(copy["namespace"]?.GetValue<string>() + ":" + copy["normalized"]?.GetValue<string>());
            return copy;
        }).ToArray();
    }

    private string CanonicalKey(string key)
    {
        var current = key; var seen = new HashSet<string>(StringComparer.Ordinal) { current };
        for (var step = 0; _direct.TryGetValue(current, out var next) && next != current; step++)
        {
            if (!seen.Add(next) || step > 16) throw new TagSynonymException("Synonyms form a loop at '" + key + "'.");
            current = next;
        }
        if (_reviewed.TryGetValue(current, out var reviewed)) return reviewed;
        return _reviewed.TryGetValue(key, out var original) ? original : current;
    }
}
