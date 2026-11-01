using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Metadata;

/// <summary>A refused metadata request: 400 bad value, 404 unknown entry or field.</summary>
public sealed class MetadataChoiceException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Per-field official-first metadata with overrides (phase 9). For each field (title, description, authors, artists,
/// genres, status, year, cover) the value comes from the highest-ranked place that has one: official publisher sources
/// first (SourceRanking), then the metadata providers (AniList, MangaUpdates, MangaDex, then other providers), then the
/// other sources by health and the priority the user set. A user override always wins; it is stored where 0.4.5 keeps
/// display ownership (display_metadata; the title in preferred_title_overrides) and every change is recorded in the
/// decision log with its actor. Matching keeps reading the native source evidence: overrides are display-only.
/// </summary>
public sealed class MetadataChooser : IMetadataFieldPolicy
{
    /// <summary>Provider identities below this confidence are not metadata (the released ChooseMetadata cut-off).</summary>
    public const double ProviderConfidence = 0.88;
    private const int MaximumAlternatives = 12;
    private const int MaximumValueLength = 12000;

    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDecisionLog _decisions;
    private readonly CatalogueTitles? _titles;
    private readonly TimeProvider _clock;

    public MetadataChooser(SqliteDatabase database, object sourceWriteGate, IDecisionLog decisions, CatalogueTitles? titles = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(decisions);
        _database = database; _gate = sourceWriteGate; _decisions = decisions; _titles = titles; _clock = clock ?? TimeProvider.System;
    }

    /// <summary>After an override changed (cover cache, duplicate snapshot, search caches).</summary>
    public Action<long>? Changed { get; init; }

    /// <summary>Provider order after the official sources: AniList, MangaUpdates, MangaDex, then the others by name.</summary>
    public static int ProviderRank(string provider) => provider switch
    {
        "anilist" => 0, "mangaupdates" => 1, "mangadex" => 2, "kitsu" => 3, "myanimelist" => 4, "comicvine" => 5, _ => 9,
    };

    // ---- reading -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// GET /api/v1/entries/{id}/metadata: every field with its chosen value, where it came from, whether it is overridden
    /// and the alternatives in rank order. <paramref name="includePrivate"/> adds who set an override and when (admins only).
    /// </summary>
    public JsonObject Read(long entryId, bool includePrivate)
    {
        lock (_gate) return _database.Read(DatabaseFile.Catalog, session =>
        {
            var id = CatalogueTitles.Canonical(session, entryId);
            var entry = session.Query("SELECT id,preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", id)).SingleOrDefault()
                ?? throw new MetadataChoiceException(404, "Entry not found");
            var candidates = Candidates(session, id);
            var owned = DisplayMetadata.OwnedFields(session, id);
            var fields = new JsonArray();
            foreach (var field in MetadataFields.All)
            {
                var ranked = Ranked(candidates, field);
                var chosen = ranked.FirstOrDefault();
                var item = new JsonObject
                {
                    ["field"] = field, ["value"] = chosen?.Value.DeepClone(), ["source"] = chosen?.Origin.ToJson() ?? None(),
                    ["overridden"] = false,
                };
                JsonObject? overrideView = null;
                if (field == MetadataFields.Title)
                {
                    if (entry.Flag("manual_preferred_title"))
                    {
                        item["value"] = entry.Text("preferred_title"); item["overridden"] = true; item["source"] = Override();
                        overrideView = new JsonObject { ["mode"] = "locked" };
                        if (includePrivate && session.Scalar("SELECT updated_at FROM preferred_title_overrides WHERE entry_id=$id", ("$id", id)) is string at) overrideView["set_at"] = at;
                    }
                    else if (entry.Text("preferred_title").Length != 0)
                    {
                        // The main title is the bound-title consensus of CatalogueTitles; show it, with the ranked alternatives.
                        item["value"] = entry.Text("preferred_title");
                        item["source"] = ranked.FirstOrDefault(row => TextMatcher.NormalizeTitle(row.Value.GetValue<string>()) == TextMatcher.NormalizeTitle(entry.Text("preferred_title")))?.Origin.ToJson()
                            ?? new JsonObject { ["kind"] = "consensus", ["name"] = "Bound source titles" };
                    }
                }
                else if (owned[MetadataFields.OwnershipKeys[field]] is JsonObject choice)
                {
                    var mode = Text(choice["mode"]);
                    if (mode == "locked")
                    {
                        item["value"] = Locked(field, choice["value"]); item["overridden"] = true; item["source"] = Override();
                        overrideView = new JsonObject { ["mode"] = "locked" };
                    }
                    else if (mode == "source")
                    {
                        var sid = Text(choice["source_id"]); var mid = Long(choice["source_manga_id"]);
                        var picked = ranked.FirstOrDefault(row => row.Origin.Kind == "source" && row.Origin.Id == sid && row.Origin.MangaId == mid);
                        overrideView = new JsonObject { ["mode"] = "source", ["source_id"] = sid, ["source_manga_id"] = mid, ["available"] = picked is not null };
                        if (picked is not null) { item["value"] = picked.Value.DeepClone(); item["overridden"] = true; item["source"] = picked.Origin.ToJson(); }
                    }
                    if (overrideView is not null && includePrivate)
                    {
                        if (Text(choice["set_by"]) is { Length: > 0 } by) overrideView["set_by"] = by;
                        if (Text(choice["set_at"]) is { Length: > 0 } at) overrideView["set_at"] = at;
                    }
                }
                if (overrideView is not null) item["override"] = overrideView;
                item["alternatives"] = new JsonArray(ranked.Take(MaximumAlternatives)
                    .Select(row => (JsonNode)new JsonObject { ["value"] = row.Value.DeepClone(), ["source"] = row.Origin.ToJson() }).ToArray());
                fields.Add(item);
            }
            return new JsonObject
            {
                ["entry_id"] = id, ["fields"] = fields,
                ["ranking"] = "official sources, then metadata providers (AniList, MangaUpdates, MangaDex), then other sources by health and priority; overrides always win",
            };
        });
    }

    // ---- overrides ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// PUT /api/v1/entries/{id}/metadata/{field}: <c>{value}</c> locks a value; <c>{source_id, source_manga_id}</c> (description,
    /// cover, authors, artists) follows one attached record. Recorded as "metadata.override" with the actor.
    /// </summary>
    public JsonObject Override(long entryId, string field, JsonObject body, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(actor);
        field = Field(field);
        if (field == MetadataFields.Title)
        {
            var title = body["value"] is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
            if (title is null) throw new MetadataChoiceException(400, "A title override needs a text value.");
            var titles = _titles ?? throw new MetadataChoiceException(400, "Title overrides are not available in this host.");
            long canonical;
            try { canonical = titles.SetPreferredTitle(entryId, title, actor); }
            catch (CatalogueTitleException error) { throw new MetadataChoiceException(error.Status == 404 ? 404 : 400, error.Message); }
            Changed?.Invoke(canonical);
            return Read(canonical, includePrivate: true);
        }
        var stamp = Stamp();
        var id = Write(session =>
        {
            var canonical = Existing(session, entryId);
            JsonObject choice;
            var sourceId = body["source_id"] is JsonValue sidValue && sidValue.TryGetValue<string>(out var sid) ? sid.Trim() : "";
            if (sourceId.Length != 0)
            {
                if (!MetadataFields.AllowsSourceOwnership(field)) throw new MetadataChoiceException(400, "Only description, cover, authors and artists can follow a source record.");
                var mangaId = Long(body["source_manga_id"]);
                if (mangaId <= 0 || session.Scalar("""
                        SELECT 1 FROM source_matches WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1
                        UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1 LIMIT 1
                        """, ("$id", canonical), ("$source", sourceId), ("$manga", mangaId)) is null)
                    throw new MetadataChoiceException(400, "Select a source record attached to this entry.");
                choice = new JsonObject { ["mode"] = "source", ["source_id"] = sourceId, ["source_manga_id"] = mangaId };
            }
            else
            {
                choice = new JsonObject { ["mode"] = "locked", ["value"] = LockedValue(session, canonical, field, body["value"]) };
            }
            choice["set_by"] = actor.Kind + ":" + actor.Name; choice["set_at"] = stamp;
            var key = MetadataFields.OwnershipKeys[field];
            var before = DisplayMetadata.SetOwnedField(session, canonical, key, choice, "before " + field + " override", stamp);
            _decisions.Record(new DecisionEvent("metadata.override", actor, [canonical], [], field + " overridden",
                new JsonObject { ["field"] = field, ["override"] = before }, new JsonObject { ["field"] = field, ["override"] = choice.DeepClone() }));
            return canonical;
        });
        Changed?.Invoke(id);
        return Read(id, includePrivate: true);
    }

    /// <summary>DELETE /api/v1/entries/{id}/metadata/{field}: the field returns to the ranked choice ("metadata.override.clear").</summary>
    public JsonObject ClearOverride(long entryId, string field, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        field = Field(field);
        var stamp = Stamp();
        var id = Write(session =>
        {
            var canonical = Existing(session, entryId);
            if (field == MetadataFields.Title)
            {
                var before = session.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", canonical)).Single();
                if (!before.Flag("manual_preferred_title")) return canonical;
                // Only the main title: manual aliases stay (ClearManualTitles also forgets them).
                session.Execute("DELETE FROM preferred_title_overrides WHERE entry_id=$id", ("$id", canonical));
                session.Execute("DELETE FROM manual_title_scopes WHERE entry_id=$id", ("$id", canonical));
                session.Execute("DELETE FROM preferred_title_override_sources");
                session.Execute("UPDATE entries SET manual_preferred_title=0,updated_at=$stamp WHERE id=$id", ("$stamp", stamp), ("$id", canonical));
                var after = CatalogueTitles.SyncPreferredTitle(session, canonical, stamp);
                _decisions.Record(new DecisionEvent("metadata.override.clear", actor, [canonical], [], "title override cleared",
                    new JsonObject { ["field"] = field, ["preferred_title"] = before.Text("preferred_title"), ["manual"] = true },
                    new JsonObject { ["field"] = field, ["preferred_title"] = after, ["manual"] = false }));
                return canonical;
            }
            var key = MetadataFields.OwnershipKeys[field];
            if (DisplayMetadata.OwnedFields(session, canonical)[key] is null) return canonical;
            var previous = DisplayMetadata.SetOwnedField(session, canonical, key, null, "before " + field + " override cleared", stamp);
            _decisions.Record(new DecisionEvent("metadata.override.clear", actor, [canonical], [], field + " override cleared",
                new JsonObject { ["field"] = field, ["override"] = previous }, new JsonObject { ["field"] = field, ["override"] = null }));
            return canonical;
        });
        Changed?.Invoke(id);
        return Read(id, includePrivate: true);
    }

    // ---- the native columns ------------------------------------------------------------------------------------------

    /// <summary>
    /// IMetadataFieldPolicy: writes the ranked choice of description, cover, author, artist, genre and status into the entry
    /// row inside the caller's write. Fields with no candidate keep what CatalogueMetadata wrote; overrides are not written
    /// (DisplayMetadata overlays them), so matching keeps reading native evidence.
    /// </summary>
    public void Apply(DatabaseSession session, long entryId, string timestamp)
    {
        ArgumentNullException.ThrowIfNull(session);
        var candidates = Candidates(session, entryId);
        if (candidates.Count == 0) return;
        string Chosen(string field) => Ranked(candidates, field).FirstOrDefault() is { } best ? Plain(best.Value) : "";
        var status = Ranked(candidates, MetadataFields.Status).FirstOrDefault()?.Value is JsonValue code && code.TryGetValue<long>(out var number) ? number : 0;
        session.Execute("""
            UPDATE entries SET description=CASE WHEN $description<>'' THEN $description ELSE description END,
                   thumbnail_url=CASE WHEN $cover<>'' THEN $cover ELSE thumbnail_url END,
                   author=CASE WHEN $author<>'' THEN $author ELSE author END,
                   artist=CASE WHEN $artist<>'' THEN $artist ELSE artist END,
                   genre=CASE WHEN $genre<>'' THEN $genre ELSE genre END,
                   status=CASE WHEN $status<>0 THEN $status ELSE status END
             WHERE id=$id
            """, ("$description", Chosen(MetadataFields.Description)), ("$cover", Chosen(MetadataFields.Cover)), ("$author", Chosen(MetadataFields.Authors)),
            ("$artist", Chosen(MetadataFields.Artists)), ("$genre", Chosen(MetadataFields.Genres)), ("$status", status), ("$id", entryId));
    }

    // ---- candidates --------------------------------------------------------------------------------------------------

    /// <summary>Where a value comes from: an attached source record or a provider identity.</summary>
    internal sealed record Origin(string Kind, string Id, long MangaId, string Name, bool Official, int Group, int Position, bool Variant)
    {
        public JsonObject ToJson()
        {
            var json = new JsonObject { ["kind"] = Kind, ["name"] = Name };
            if (Kind == "source") { json["source_id"] = Id; json["source_manga_id"] = MangaId; json["official"] = Official; json["additional"] = Variant; }
            else { json["provider"] = Id; json["external_id"] = ExternalId; }
            return json;
        }
        public string ExternalId { get; init; } = "";
    }

    internal sealed record Candidate(Origin Origin, IReadOnlyDictionary<string, JsonNode> Values);
    private sealed record RankedValue(Origin Origin, JsonNode Value);

    /// <summary>The attached records and confident provider identities of an entry, best first.</summary>
    internal static IReadOnlyList<Candidate> Candidates(DatabaseSession session, long entryId)
    {
        var ranking = SourceRanking.Load(session);
        var positions = SourceRanking.Positions(ranking);
        var official = ranking.Where(rank => rank.Official).Select(rank => rank.SourceId).ToHashSet(StringComparer.Ordinal);
        var result = new List<Candidate>();
        foreach (var row in session.Query("""
            SELECT sm.source_id,sm.source_manga_id,sm.source_title,sm.description,sm.author,sm.artist,sm.genre,sm.status,sm.thumbnail_url,sm.metadata_json,
                   0 is_variant,COALESCE(NULLIF(sc.display_name,''),sc.name,sm.source_id) source_name
              FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id
             WHERE sm.entry_id=$id AND sm.active=1 AND COALESCE(sc.enabled_language,1)=1
            UNION ALL
            SELECT sv.source_id,sv.source_manga_id,sv.source_title,sv.description,sv.author,sv.artist,sv.genre,sv.status,sv.thumbnail_url,sv.metadata_json,
                   1 is_variant,COALESCE(NULLIF(sc.display_name,''),sc.name,sv.source_id) source_name
              FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id
             WHERE sv.entry_id=$id AND sv.active=1 AND COALESCE(sc.enabled_language,1)=1
            """, ("$id", entryId)))
        {
            var sourceId = row.Text("source_id");
            var isOfficial = official.Contains(sourceId);
            var origin = new Origin("source", sourceId, row.Integer("source_manga_id"), row.Text("source_name"), isOfficial, isOfficial ? 0 : 2,
                positions.TryGetValue(sourceId, out var position) ? position : int.MaxValue, row.Flag("is_variant"));
            result.Add(new Candidate(origin, Values(row.Text("source_title"), row.Text("description"), row.Text("author"), row.Text("artist"), row.Text("genre"),
                row.Integer("status"), RecordYear(row.Text("metadata_json")), row.Text("thumbnail_url"))));
        }
        foreach (var row in session.Query("""
            SELECT provider,provider_name,external_id,title,description,author,artist,genre,status,thumbnail_url,year
              FROM identity_links WHERE entry_id=$id AND confidence>=$minimum
            """, ("$id", entryId), ("$minimum", ProviderConfidence)))
        {
            var provider = row.Text("provider");
            var origin = new Origin("provider", provider, 0, row.Text("provider_name") is { Length: > 0 } name ? name : provider, false, 1, ProviderRank(provider), false)
            { ExternalId = row.Text("external_id") };
            int? year = row["year"] is null ? null : (int)Math.Clamp(row.Integer("year"), 0, 9999);
            result.Add(new Candidate(origin, Values(row.Text("title"), row.Text("description"), row.Text("author"), row.Text("artist"), row.Text("genre"),
                row.Integer("status"), year is > 0 ? year : null, row.Text("thumbnail_url"))));
        }
        return result.OrderBy(item => item.Origin.Group).ThenBy(item => item.Origin.Position).ThenBy(item => item.Origin.Variant)
            .ThenBy(item => item.Origin.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Origin.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Origin.MangaId).ToArray();
    }

    private static Dictionary<string, JsonNode> Values(string title, string description, string author, string artist, string genre, long status, int? year, string cover)
    {
        var values = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        void Add(string field, string value) { value = value.Trim(); if (value.Length != 0) values[field] = JsonValue.Create(value); }
        Add(MetadataFields.Title, title);
        Add(MetadataFields.Description, TextMatcher.CleanSummary(description));
        Add(MetadataFields.Authors, MetadataFields.Join(author.Split(',')));
        Add(MetadataFields.Artists, MetadataFields.Join(artist.Split(',')));
        Add(MetadataFields.Genres, MetadataFields.Join(genre.Replace('|', ',').Split(',')));
        if (status is > 0 and <= 6) values[MetadataFields.Status] = JsonValue.Create(status);
        if (year is { } known) values[MetadataFields.Year] = JsonValue.Create(known);
        Add(MetadataFields.Cover, cover);
        return values;
    }

    private static List<RankedValue> Ranked(IReadOnlyList<Candidate> candidates, string field) =>
        candidates.Where(item => item.Values.ContainsKey(field)).Select(item => new RankedValue(item.Origin, item.Values[field])).ToList();

    private static int? RecordYear(string metadata)
    {
        if (metadata.Length == 0) return null;
        try { return JsonNode.Parse(metadata) is JsonObject payload ? MatchCandidate.YearOf(payload["year"]) : null; }
        catch (JsonException) { return null; }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static JsonNode LockedValue(DatabaseSession session, long entryId, string field, JsonNode? value)
    {
        switch (field)
        {
            case MetadataFields.Status:
                return JsonValue.Create(MetadataFields.StatusCode(value) ?? throw new MetadataChoiceException(400, "Status must be unknown, ongoing, completed, licensed, publishing finished, cancelled or hiatus (or 0-6)."));
            case MetadataFields.Year:
                if (value is JsonValue number && number.GetValueKind() == JsonValueKind.Number && number.TryGetValue<int>(out var year) && year is >= 1800 and <= 2200)
                    return JsonValue.Create(year);
                throw new MetadataChoiceException(400, "Year must be a whole number between 1800 and 2200.");
            case MetadataFields.Authors or MetadataFields.Artists or MetadataFields.Genres:
                var names = MetadataFields.JoinedNames(value);
                if (value is null || names.Length > MaximumValueLength) throw new MetadataChoiceException(400, "Send a text or a list of names (an empty text clears the field).");
                return JsonValue.Create(names);
            default:
                if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) throw new MetadataChoiceException(400, "Send a text value.");
                text = text.Trim();
                if (text.Length > MaximumValueLength) throw new MetadataChoiceException(400, "The value is too long.");
                if (field == MetadataFields.Cover && text.Length != 0 && session.Scalar("""
                        SELECT 1 FROM source_matches WHERE entry_id=$entry AND active=1 AND thumbnail_url=$value
                        UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$entry AND active=1 AND thumbnail_url=$value LIMIT 1
                        """, ("$entry", entryId), ("$value", text)) is null)
                    throw new MetadataChoiceException(400, "Choose a cover from a source record attached to this entry.");
                return JsonValue.Create(text);
        }
    }

    private static JsonNode? Locked(string field, JsonNode? value) => field switch
    {
        MetadataFields.Status => MetadataFields.StatusCode(value) is { } code ? JsonValue.Create(code) : null,
        _ => value?.DeepClone(),
    };

    private static string Field(string field)
    {
        var name = (field ?? "").Trim().ToLowerInvariant();
        name = name switch { "author" => MetadataFields.Authors, "artist" => MetadataFields.Artists, "genre" => MetadataFields.Genres, "artwork" or "thumbnail" => MetadataFields.Cover, _ => name };
        return MetadataFields.All.Contains(name) ? name : throw new MetadataChoiceException(404, "Unknown metadata field: use " + string.Join(", ", MetadataFields.All) + ".");
    }

    private static long Existing(DatabaseSession session, long entryId)
    {
        var id = CatalogueTitles.Canonical(session, entryId);
        return session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is null ? throw new MetadataChoiceException(404, "Entry not found") : id;
    }

    private static JsonObject None() => new() { ["kind"] = "none", ["name"] = "" };
    private static JsonObject Override() => new() { ["kind"] = "override", ["name"] = "Your override" };
    private static string Plain(JsonNode node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    /// <summary>A whole number from a request value: parsed JSON, an in-memory int/long (JsonValue.Create(int) does not
    /// convert through TryGetValue&lt;long&gt;) or a numeric string; anything else is 0.</summary>
    private static long Long(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var small)) return small;
        return value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private T Write<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Write(DatabaseFile.Catalog, operation); }

    private string Stamp()
    {
        var value = _clock.GetUtcNow();
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
    }
}
