using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Tags;

public enum NativeTagTable { Matches, Variants }
public sealed record NativeTagRecord(NativeTagTable Table, long RowId, long CanonicalEntryId, string SourceId, long SourceMangaId, string Fingerprint);
public sealed class NativeTagConflictException(string message) : InvalidOperationException(message);

/// <summary>Factual tags from verified current native rows. Never creates a binding, review or layout task.</summary>
public sealed class NativeTagStore
{
    private const string Active = "SELECT entry_id,source_id,source_manga_id FROM source_matches WHERE active=1 UNION SELECT entry_id,source_id,source_manga_id FROM source_variants WHERE active=1";
    private readonly SqliteDatabase _database; private readonly object _sourceWriteGate; private readonly TimeProvider _clock;
    public NativeTagStore(SqliteDatabase database, object sourceWriteGate, TimeProvider? clock = null)
    { ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); _database = database; _sourceWriteGate = sourceWriteGate; _clock = clock ?? TimeProvider.System; }

    public NativeTagRecord Capture(NativeTagTable table, long rowId, CancellationToken cancellationToken = default) => WithGate(cancellationToken,
        () => _database.Read(DatabaseFile.Catalog, catalog => Capture(catalog, table, rowId, cancellationToken)));

    /// <summary>Call while holding the shared gate, inside the native writer's current catalogue transaction.</summary>
    public NativeTagRecord Capture(DatabaseSession catalog, NativeTagTable table, long rowId, CancellationToken cancellationToken = default)
    { RequireSession(catalog); return NativeTagOwnership.Read(catalog, table, rowId, cancellationToken).Identity; }

    public void Synchronize(NativeTagRecord expected, CancellationToken cancellationToken = default) => Write(cancellationToken,
        catalog => { Synchronize(catalog, expected, cancellationToken); return 0; });

    /// <summary>Synchronizes the exact stored frame; a failed native caller must propagate failure to roll its whole transaction back.</summary>
    public void Synchronize(DatabaseSession catalog, NativeTagRecord expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); RequireSession(catalog); cancellationToken.ThrowIfCancellationRequested();
        catalog.Savepoint(session =>
        {
            var current = NativeTagOwnership.Read(session, expected.Table, expected.RowId, cancellationToken);
            if (current.Identity != expected) throw new NativeTagConflictException("The native source row, manual binding or source profile changed before tag publication.");
            JsonObject payload;
            try { payload = JsonNode.Parse(current.Row.Text("metadata_json") is { Length: > 0 } text ? text : "{}") as JsonObject ?? throw new InvalidDataException("The stored native metadata frame is not an object."); }
            catch (JsonException error) { throw new InvalidDataException("The stored native metadata frame is not valid JSON.", error); }
            Persist(session, current.Identity, payload, Source(current.Source, reindex: false), cancellationToken); return 0;
        });
    }

    /// <summary>Released low-level projection. The outer catalogue API owns canonical-entry alias resolution.</summary>
    public JsonObject EntryMetadata(long entryId, CancellationToken cancellationToken = default) => WithGate(cancellationToken,
        () => _database.Read(DatabaseFile.Catalog, catalog => EntryMetadata(catalog, entryId, cancellationToken)));
    public JsonObject EntryMetadata(DatabaseSession catalog, long entryId, CancellationToken cancellationToken = default)
    {
        RequireSession(catalog); cancellationToken.ThrowIfCancellationRequested();
        return catalog.Savepoint(session =>
        {
            if (session.Scalar("SELECT 1 FROM entries WHERE id=$entry", ("$entry", entryId)) is null) throw new KeyNotFoundException("Entry not found");
            var tags = new JsonArray(session.Query("WITH active AS (" + Active + ") SELECT DISTINCT t.id,t.name,t.namespace FROM tags t JOIN source_record_tags st ON st.tag_id=t.id JOIN active a USING(source_id,source_manga_id) WHERE a.entry_id=$entry ORDER BY t.namespace,t.name", ("$entry", entryId)).Select(row => (JsonNode)NativeTagOwnership.Json(row)).ToArray());
            var gallery = new JsonArray();
            foreach (var row in session.Query("WITH active AS (" + Active + ") SELECT DISTINCT g.* FROM gallery_records g JOIN active a USING(source_id,source_manga_id) WHERE a.entry_id=$entry", ("$entry", entryId)))
            {
                cancellationToken.ThrowIfCancellationRequested(); JsonNode? value;
                try { value = JsonNode.Parse(row.Text("metadata_json")); } catch (JsonException) { continue; }
                if (value is not JsonObject metadata) throw new InvalidDataException("Retained native gallery metadata is not an object.");
                metadata["source_id"] = row.Text("source_id"); metadata["source_manga_id"] = row.Integer("source_manga_id"); gallery.Add(metadata);
            }
            cancellationToken.ThrowIfCancellationRequested(); return new JsonObject { ["tags"] = tags, ["gallery"] = gallery, ["metadata_basis"] = gallery.Count > 0 ? "source" : "catalogue" };
        });
    }

    /// <summary>Released pure first500 selection; normalization/parsing remain with the existing Core owners.</summary>
    public static IReadOnlyList<JsonObject> PayloadTags(JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var galleryValue = manga["_gallery_metadata"]; var gallery = NativeTagJson.Truth(galleryValue) ? galleryValue as JsonObject ?? throw new InvalidDataException("Retained gallery metadata is not an object.") : new JsonObject();
        var galleryTags = NativeTagJson.Sequence(gallery["tags"]).ToArray(); var native = (JsonObject)manga.DeepClone();
        if (native["genre"] is JsonValue scalar && scalar.TryGetValue<string>(out var genre) && genre.TrimStart().StartsWith('['))
        { try { native["genre"] = JsonNode.Parse(genre); } catch (JsonException) { } }
        var sourceTags = GalleryMetadata.SourceTags(native); var result = new List<JsonObject>(); var seen = new HashSet<(string, string)>();
        foreach (var value in galleryTags.Concat(sourceTags).Take(500))
        {
            var tag = GalleryTagEquivalence.Tag(value);
            if (tag is not null && seen.Add((tag["namespace"]!.GetValue<string>(), tag["normalized"]!.GetValue<string>()))) result.Add(tag);
        }
        return result;
    }

    internal T Write<T>(CancellationToken cancellationToken, Func<DatabaseSession, T> operation, bool requireOuterCommit = false)
    {
        if (requireOuterCommit && _database.HasCurrentThreadSession(DatabaseFile.Catalog)) throw new InvalidOperationException("Standalone native tag reindex requires its own committed catalogue transaction.");
        return WithGate(cancellationToken, () =>
        {
            if (requireOuterCommit && _database.HasCurrentThreadSession(DatabaseFile.Catalog)) throw new InvalidOperationException("Standalone native tag reindex requires its own committed catalogue transaction.");
            return _database.Write(DatabaseFile.Catalog, catalog => { cancellationToken.ThrowIfCancellationRequested(); var result = operation(catalog); cancellationToken.ThrowIfCancellationRequested(); return result; });
        });
    }

    internal void ReindexRow(DatabaseSession catalog, NativeTagTable table, long rowId, CancellationToken cancellationToken)
    {
        RequireSession(catalog); var current = NativeTagOwnership.Read(catalog, table, rowId, cancellationToken); JsonObject payload;
        try { payload = JsonNode.Parse(current.Row.Text("metadata_json") is { Length: > 0 } text ? text : "{}") as JsonObject ?? new(); }
        catch (JsonException) { payload = new(); }
        foreach (var field in new[] { "artist", "author", "genre", "description" }) if (!NativeTagJson.Truth(payload[field])) payload[field] = JsonValue.Create(current.Row.Text(field));
        if (!payload.ContainsKey("title")) payload["title"] = current.Row.Text("source_title");
        if (!payload.ContainsKey("initialized")) payload["initialized"] = current.Row.Flag("initialized");
        Persist(catalog, current.Identity, payload, Source(current.Source, reindex: true), cancellationToken);
    }

    private void Persist(DatabaseSession catalog, NativeTagRecord identity, JsonObject manga, JsonObject source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var enriched = GalleryMetadata.Enrich(manga, source); var tags = PayloadTags(enriched);
        var authoritative = NativeTagJson.Truth(manga["initialized"]) && new[] { "genre", "tags", "genres" }.Any(manga.ContainsKey);
        var existing = catalog.Query("SELECT t.namespace,t.normalized FROM source_record_tags st JOIN tags t ON t.id=st.tag_id WHERE st.source_id=$source AND st.source_manga_id=$manga", ("$source", identity.SourceId), ("$manga", identity.SourceMangaId)).Select(row => (row.Text("namespace"), row.Text("normalized"))).ToHashSet();
        var incoming = tags.Select(tag => (tag["namespace"]!.GetValue<string>(), tag["normalized"]!.GetValue<string>())).ToHashSet();
        var changed = authoritative ? !incoming.SetEquals(existing) : !incoming.IsSubsetOf(existing); cancellationToken.ThrowIfCancellationRequested();
        if (changed && authoritative) catalog.Execute("DELETE FROM source_record_tags WHERE source_id=$source AND source_manga_id=$manga", ("$source", identity.SourceId), ("$manga", identity.SourceMangaId));
        if (changed)
            foreach (var tag in tags)
            {
                cancellationToken.ThrowIfCancellationRequested(); var ns = tag["namespace"]!.GetValue<string>(); var normalized = tag["normalized"]!.GetValue<string>();
                catalog.Execute("INSERT OR IGNORE INTO tags(namespace,name,normalized) VALUES($namespace,$name,$normalized)", ("$namespace", ns), ("$name", tag["name"]!.GetValue<string>()), ("$normalized", normalized));
                var id = catalog.Scalar("SELECT id FROM tags WHERE namespace=$namespace AND normalized=$normalized", ("$namespace", ns), ("$normalized", normalized));
                catalog.Execute("INSERT OR IGNORE INTO source_record_tags VALUES($source,$manga,$tag)", ("$source", identity.SourceId), ("$manga", identity.SourceMangaId), ("$tag", id));
            }
        cancellationToken.ThrowIfCancellationRequested();
        if (NativeTagJson.Truth(enriched["_gallery_metadata"]))
        {
            var gallery = enriched["_gallery_metadata"] as JsonObject ?? throw new InvalidDataException("Retained gallery metadata is not an object.");
            if (!NativeTagJson.Truth(manga["initialized"])) gallery = MergeThin(catalog, identity, gallery);
            var text = NativeTagJson.Text(gallery); var timestamp = NativeTagJson.Timestamp(_clock); cancellationToken.ThrowIfCancellationRequested();
            catalog.Execute("""
                INSERT INTO gallery_records VALUES($source,$manga,$metadata,$time)
                ON CONFLICT(source_id,source_manga_id) DO UPDATE SET metadata_json=excluded.metadata_json,updated_at=excluded.updated_at
                WHERE gallery_records.metadata_json<>excluded.metadata_json
                """, ("$source", identity.SourceId), ("$manga", identity.SourceMangaId), ("$metadata", text), ("$time", timestamp));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static JsonObject MergeThin(DatabaseSession catalog, NativeTagRecord identity, JsonObject gallery)
    {
        var previous = catalog.Scalar("SELECT metadata_json FROM gallery_records WHERE source_id=$source AND source_manga_id=$manga", ("$source", identity.SourceId), ("$manga", identity.SourceMangaId)) as string;
        if (previous is null) return gallery; JsonNode? decoded;
        try { decoded = JsonNode.Parse(previous); } catch (JsonException) { return gallery; }
        if (decoded is not JsonObject old) return gallery;
        var overlay = (JsonObject)old.DeepClone(); foreach (var pair in gallery) if (NativeTagJson.OverlayValue(pair.Value)) overlay[pair.Key] = pair.Value?.DeepClone();
        var left = NativeTagJson.Truth(old["tags"]) ? old["tags"] : new JsonArray(); var right = NativeTagJson.Truth(overlay["tags"]) ? overlay["tags"] : new JsonArray(); IEnumerable<JsonNode?> combined;
        if (left is JsonArray a && right is JsonArray b) combined = a.Concat(b);
        else if (left is JsonValue l && l.TryGetValue<string>(out var ls) && right is JsonValue r && r.TryGetValue<string>(out var rs)) combined = NativeTagJson.Sequence(JsonValue.Create(ls + rs));
        else return overlay; // Released TypeError occurs after assigning the overlay.
        var values = new List<JsonNode?>(); var indexes = new Dictionary<(string, string), int>();
        foreach (var value in combined)
        {
            var tag = GalleryTagEquivalence.Tag(value); if (tag is null) continue; var key = (tag["namespace"]!.GetValue<string>(), tag["normalized"]!.GetValue<string>());
            if (indexes.TryGetValue(key, out var index)) values[index] = value?.DeepClone(); else { indexes.Add(key, values.Count); values.Add(value?.DeepClone()); }
        }
        overlay["tags"] = new JsonArray(values.ToArray()); return overlay;
    }

    private static JsonObject Source(DatabaseRow? source, bool reindex)
    {
        if (reindex) return new() { ["name"] = source is null ? null : JsonValue.Create(source.Text("name")), ["base_url"] = source is null ? null : JsonValue.Create(source.Text("base_url")), ["lang"] = source is null ? null : JsonValue.Create(source.Text("lang")) };
        if (source is null) return new();
        return new() { ["source_id"] = source.Text("source_id"), ["name"] = source.Text("name"), ["display_name"] = source.Text("display_name"), ["lang"] = source.Text("lang"), ["base_url"] = source.Text("base_url") };
    }
    private void RequireGate() { if (!Monitor.IsEntered(_sourceWriteGate)) throw new InvalidOperationException("Caller-owned native tag work requires the shared source-write gate."); }
    private void RequireSession(DatabaseSession catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog); RequireGate();
        if (!_database.IsCurrentThreadSession(DatabaseFile.Catalog, catalog)) throw new InvalidOperationException("Caller-owned native tag work requires this instance's current catalogue session.");
    }
    private T WithGate<T>(CancellationToken cancellationToken, Func<T> operation)
    {
        cancellationToken.ThrowIfCancellationRequested(); while (!Monitor.TryEnter(_sourceWriteGate, 50)) cancellationToken.ThrowIfCancellationRequested();
        try { cancellationToken.ThrowIfCancellationRequested(); return operation(); } finally { Monitor.Exit(_sourceWriteGate); }
    }
}
