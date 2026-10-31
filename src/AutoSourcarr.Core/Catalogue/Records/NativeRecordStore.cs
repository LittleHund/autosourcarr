using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Gallery;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Catalogue.Tags;

namespace AutoSourcarr.Core.Catalogue.Records;

public sealed record NativeRecordPrefix(bool Accepted, long? EntryId, long? RowId, NativeTagTable? Table,
    bool CachedStored, bool TitlesRefreshed, bool AuthoritativeStored, long AttemptedChanges);
/// <summary>A caller-owned phase result, without a claim that its outer transaction committed.</summary>
public sealed record NativeRecordWrite(NativeTagRecord Record, bool Stored, long AttemptedChanges);

/// <summary>
/// The released existing-owner row/title/detail prefix. Never attaches, promotes, moves or chooses catalogue metadata.
/// Each standalone write phase commits separately; a later exception retains earlier commits and is propagated.
/// Full refresh composition still requires the real metadata, gallery layout, cache and task owners.
/// </summary>
public sealed class NativeRecordStore
{
    public const string AttemptedChangesKey = "AutoSourcarr.NativeRecord.AttemptedChanges";
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly TimeProvider _clock;
    private readonly NativeTagStore _tags;
    private readonly GalleryEvidenceService _gallery;

    public NativeRecordStore(SqliteDatabase database, object sourceWriteGate, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate);
        _database = database; _gate = sourceWriteGate; _clock = clock ?? TimeProvider.System;
        _tags = new(database, sourceWriteGate, _clock); _gallery = new(database, sourceWriteGate);
    }

    public NativeTagRecord? Capture(string sourceId, long sourceMangaId, CancellationToken cancellationToken = default) => WithGate(cancellationToken,
        () => _database.Read(DatabaseFile.Catalog, catalog => Current(catalog, sourceId, sourceMangaId, cancellationToken)));

    /// <summary>Expected-entry refresh prefix. Re-reads the current role/manual flags, follows canonical aliases and rejects a moved pair.</summary>
    public NativeRecordPrefix RefreshIfCurrent(long entryId, string sourceId, long sourceMangaId, JsonObject manga, CancellationToken cancellationToken = default)
        => Prefix(entryId, sourceId, sourceMangaId, manga, background: false, cancellationToken);

    /// <summary>Background storage prefix, following the pair's current owner. Input must be the actual detail for that exact source manga.</summary>
    public NativeRecordPrefix StoreBackgroundPrefix(string sourceId, long sourceMangaId, JsonObject manga, CancellationToken cancellationToken = default)
        => Prefix(null, sourceId, sourceMangaId, manga, background: true, cancellationToken);

    private NativeRecordPrefix Prefix(long? entryId, string sourceId, long sourceMangaId, JsonObject manga, bool background, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manga); ArgumentNullException.ThrowIfNull(sourceId); RequireStandalone(); long attempted = 0;
        try
        {
            return WithGate(cancellationToken, () =>
            {
                RequireStandalone();
                NativeRecordPrefix Missing() => new(false, null, null, null, false, false, false, attempted);
                if (background && manga.Count == 0) return Missing();
                var payload = (JsonObject)manga.DeepClone();
                var expectedEntry = entryId is long target ? _database.Read(DatabaseFile.Catalog, c => Canonical(c, target, cancellationToken)) : (long?)null;
                if (!background)
                {
                    if (NativeTagJson.Truth(payload["id"]) && Integer(payload["id"]) != sourceMangaId) return Missing();
                    payload["id"] = sourceMangaId;
                }
                else
                {
                    if (!payload.ContainsKey("id")) payload["id"] = sourceMangaId;
                    if (Integer(payload["id"]) != sourceMangaId)
                        throw new ArgumentException("Background native detail must name the exact fetched source manga; new attachment is outside this prefix.", nameof(manga));
                }
                var owner = _database.Read(DatabaseFile.Catalog, c => Current(c, sourceId, sourceMangaId, cancellationToken));
                if (owner is null || (expectedEntry is long wanted && wanted != owner.CanonicalEntryId)) return Missing();
                // The original owned-pair branch returns before new-attachment gallery policy checks.
                // Combining disabled is not authority to reject this actual native record's refresh.
                var cleaned = _gallery.CleanNative(sourceId, payload); cancellationToken.ThrowIfCancellationRequested();
                var cached = Phase(c => Cached(c, owner, cleaned, cancellationToken), cancellationToken, ref attempted);
                var refreshed = false;
                if (cached.Stored)
                {
                    TitlePhases(cached.Record, cleaned, cancellationToken, ref attempted); refreshed = true;
                }
                if (background) Phase(c => Authoritative(c, cached.Record, _gallery.CleanNative(sourceId, payload), cancellationToken), cancellationToken, ref attempted);
                return new(true, owner.CanonicalEntryId, owner.RowId, owner.Table, cached.Stored, refreshed, background, attempted);
            });
        }
        catch (Exception error) { error.Data[AttemptedChangesKey] = attempted; throw; }
    }

    /// <summary>Original source-title replacement phases, restricted to an existing exact native owner.</summary>
    public long RefreshTitles(long entryId, string sourceId, JsonObject manga, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manga); ArgumentNullException.ThrowIfNull(sourceId); RequireStandalone(); long attempted = 0;
        try
        {
            return WithGate(cancellationToken, () =>
            {
                RequireStandalone(); var mid = NativeTagJson.Truth(manga["id"]) ? Integer(manga["id"]) : 0; if (mid <= 0) return 0L;
                var expected = _database.Read(DatabaseFile.Catalog, c => Canonical(c, entryId, cancellationToken));
                var owner = _database.Read(DatabaseFile.Catalog, c => Current(c, sourceId, mid, cancellationToken));
                if (owner is null || owner.CanonicalEntryId != expected) throw new NativeTagConflictException("The source-title record is no longer attached to this entry.");
                TitlePhases(owner, (JsonObject)manga.DeepClone(), cancellationToken, ref attempted); return attempted;
            });
        }
        catch (Exception error) { error.Data[AttemptedChangesKey] = attempted; throw; }
    }

    /// <summary>Actual caller-owned catalogue phase, including native lineage and tags. Requires the shared gate and fresh captured fingerprint.</summary>
    public NativeRecordWrite WriteCached(DatabaseSession catalog, NativeTagRecord expected, JsonObject manga, CancellationToken cancellationToken = default)
    {
        RequireSession(catalog); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(manga);
        return catalog.Savepoint(c => Cached(c, expected, _gallery.CleanNative(expected.SourceId, ExactFrame(expected, manga)), cancellationToken));
    }
    /// <summary>Actual authoritative-detail phase. Fresh nonblank fields may replace longer cached values; this is not layout reconciliation.</summary>
    public NativeRecordWrite WriteAuthoritative(DatabaseSession catalog, NativeTagRecord expected, JsonObject manga, CancellationToken cancellationToken = default)
    {
        RequireSession(catalog); ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(manga);
        return catalog.Savepoint(c => Authoritative(c, expected, _gallery.CleanNative(expected.SourceId, ExactFrame(expected, manga)), cancellationToken));
    }
    private static JsonObject ExactFrame(NativeTagRecord expected, JsonObject manga)
    {
        var payload = (JsonObject)manga.DeepClone(); if (NativeTagJson.Truth(payload["id"]) && Integer(payload["id"]) != expected.SourceMangaId) throw new NativeTagConflictException("Native detail names another source manga.");
        payload["id"] = expected.SourceMangaId; return payload;
    }

    private NativeRecordWrite Cached(DatabaseSession c, NativeTagRecord expected, JsonObject payload, CancellationToken cancellationToken)
    {
        var before = Changes(c); var old = Validate(c, expected, cancellationToken); var stamp = NativeTagJson.Timestamp(_clock); cancellationToken.ThrowIfCancellationRequested();
        var matchType = old.Text("match_type"); if (matchType.Length == 0) matchType = "auto";
        if (matchType != "manual" && (old.Flag("locked") || expected.Table == NativeTagTable.Variants && old.Text("match_type").ToLowerInvariant() == "manual"))
            return new(expected, false, 0);
        var table = NativeTagOwnership.Table(expected.Table);
        // Same-manga only. A retained secondary physical row needs the real promotion/cleanup owner.
        if (expected.Table == NativeTagTable.Matches && c.Scalar("SELECT 1 FROM source_variants WHERE source_id=$source AND source_manga_id=$manga", ("$source", expected.SourceId), ("$manga", expected.SourceMangaId)) is not null)
            throw new NativeTagConflictException("A retained secondary role requires explicit native promotion before refreshing this primary.");
        var ownerUpdate = expected.Table == NativeTagTable.Matches ? "source_manga_id=excluded.source_manga_id," : "entry_id=excluded.entry_id,";
        var conflict = expected.Table == NativeTagTable.Matches ? "entry_id,source_id" : "source_id,source_manga_id";
        c.Execute($"""
            INSERT INTO {table}(entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,match_type,locked,active,initialized,
              thumbnail_url,description,author,artist,genre,status,real_url,metadata_json,last_checked)
            VALUES($entry,$source,$manga,$url,$title,$confidence,$type,$locked,1,$initialized,$cover,$description,$author,$artist,$genre,$status,$real,$metadata,$stamp)
            ON CONFLICT({conflict}) DO UPDATE SET {ownerUpdate}
              source_manga_url=CASE WHEN excluded.source_manga_url<>'' THEN excluded.source_manga_url ELSE {table}.source_manga_url END,
              source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE {table}.source_title END,
              confidence=excluded.confidence,match_type=excluded.match_type,locked=excluded.locked,active=1,
              initialized=MAX({table}.initialized,excluded.initialized),
              thumbnail_url=CASE WHEN excluded.thumbnail_url<>'' THEN excluded.thumbnail_url ELSE {table}.thumbnail_url END,
              description=CASE WHEN length(excluded.description)>=length({table}.description) THEN excluded.description ELSE {table}.description END,
              author=CASE WHEN excluded.author<>'' THEN excluded.author ELSE {table}.author END,
              artist=CASE WHEN excluded.artist<>'' THEN excluded.artist ELSE {table}.artist END,
              genre=CASE WHEN excluded.genre<>'' THEN excluded.genre ELSE {table}.genre END,
              status=CASE WHEN excluded.status<>0 THEN excluded.status ELSE {table}.status END,
              real_url=CASE WHEN excluded.real_url<>'' THEN excluded.real_url ELSE {table}.real_url END,
              metadata_json=CASE WHEN excluded.initialized=1 OR length(excluded.metadata_json)>length({table}.metadata_json) THEN excluded.metadata_json ELSE {table}.metadata_json END,
              last_checked=excluded.last_checked
            """, ("$entry", expected.CanonicalEntryId), ("$source", expected.SourceId), ("$manga", expected.SourceMangaId),
            ("$url", CachedValue(payload["url"])), ("$title", CachedValue(payload["title"])), ("$confidence", old.Number("confidence") is var confidence && confidence != 0 ? confidence : 1d),
            ("$type", matchType), ("$locked", old.Flag("locked") ? 1 : 0), ("$initialized", NativeTagJson.Truth(payload["initialized"]) ? 1 : 0),
            ("$cover", CachedValue(payload["thumbnailUrl"])), ("$description", CachedSummary(payload["description"])),
            ("$author", CachedValue(payload["author"])), ("$artist", CachedValue(payload["artist"])), ("$genre", GenreText(payload["genre"])), ("$status", Status(payload["status"])),
            ("$real", CachedValue(payload["realUrl"])), ("$metadata", NativeTagJson.Text(payload)), ("$stamp", stamp));
        var row = c.Query("SELECT * FROM " + table + " WHERE id=$id", ("$id", expected.RowId)).Single();
        var native = RecordPayload(row); cancellationToken.ThrowIfCancellationRequested();
        CatalogueIdentity.BindNative(c, expected.CanonicalEntryId, expected.SourceId, Integer(native["id"]), SourceText(native["url"]), SourceText(native["realUrl"]));
        var current = _tags.Capture(c, expected.Table, expected.RowId, cancellationToken); _tags.Synchronize(c, current, cancellationToken);
        if (expected.Table == NativeTagTable.Matches) c.Execute("DELETE FROM source_variants WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga", ("$entry", expected.CanonicalEntryId), ("$source", expected.SourceId), ("$manga", expected.SourceMangaId));
        cancellationToken.ThrowIfCancellationRequested(); return new(current, true, Changes(c) - before);
    }

    private NativeRecordWrite Authoritative(DatabaseSession c, NativeTagRecord expected, JsonObject payload, CancellationToken cancellationToken)
    {
        var before = Changes(c); Validate(c, expected, cancellationToken); var stamp = NativeTagJson.Timestamp(_clock); cancellationToken.ThrowIfCancellationRequested(); var table = NativeTagOwnership.Table(expected.Table);
        c.Execute($"""
            UPDATE {table} SET initialized=1,
              source_manga_url=CASE WHEN $url<>'' THEN $url ELSE source_manga_url END,
              source_title=CASE WHEN $title<>'' THEN $title ELSE source_title END,
              thumbnail_url=CASE WHEN $cover<>'' THEN $cover ELSE thumbnail_url END,
              description=CASE WHEN $description<>'' THEN $description ELSE description END,
              author=CASE WHEN $author<>'' THEN $author ELSE author END,
              artist=CASE WHEN $artist<>'' THEN $artist ELSE artist END,
              genre=CASE WHEN $genre<>'' THEN $genre ELSE genre END,
              status=CASE WHEN $status<>0 THEN $status ELSE status END,
              real_url=CASE WHEN $real<>'' THEN $real ELSE real_url END,
              metadata_json=$metadata,last_checked=$stamp
            WHERE entry_id=$entry AND source_id=$source AND source_manga_id=$manga AND active=1
            """, ("$url", SourceText(payload["url"])), ("$title", SourceText(payload["title"])), ("$cover", SourceText(payload["thumbnailUrl"])),
            ("$description", SourceText(payload["description"])), ("$author", SourceText(payload["author"])), ("$artist", SourceText(payload["artist"])), ("$genre", GenreText(payload["genre"])),
            ("$status", Status(payload["status"])), ("$real", SourceText(payload["realUrl"])), ("$metadata", NativeTagJson.Text(payload)), ("$stamp", stamp),
            ("$entry", expected.CanonicalEntryId), ("$source", expected.SourceId), ("$manga", expected.SourceMangaId));
        var current = _tags.Capture(c, expected.Table, expected.RowId, cancellationToken); _tags.Synchronize(c, current, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); return new(current, true, Changes(c) - before);
    }

    private void TitlePhases(NativeTagRecord owner, JsonObject manga, CancellationToken cancellationToken, ref long attempted)
    {
        var description = NativeRecordTitles.HasDescription(manga);
        Phase(c => { Validate(c, owner, cancellationToken); NativeRecordTitles.Clear(c, owner, description); return 0; }, cancellationToken, ref attempted);
        var language = _database.Read(DatabaseFile.Catalog, c => (c.Scalar("SELECT lang FROM source_config WHERE source_id=$source", ("$source", owner.SourceId)) as string ?? "").ToLowerInvariant());
        foreach (var alias in NativeRecordTitles.Aliases(manga, language, description))
            Phase(c => { Validate(c, owner, cancellationToken); NativeRecordTitles.Add(c, owner, alias); return 0; }, cancellationToken, ref attempted);
    }
    private T Phase<T>(Func<DatabaseSession, T> operation, CancellationToken cancellationToken, ref long attempted)
    {
        long changes = 0;
        try { return _database.Write(DatabaseFile.Catalog, c => { var before = Changes(c); try { cancellationToken.ThrowIfCancellationRequested(); var result = operation(c); cancellationToken.ThrowIfCancellationRequested(); return result; } finally { changes = Changes(c) - before; } }); }
        finally { attempted += changes; }
    }
    private DatabaseRow Validate(DatabaseSession catalog, NativeTagRecord expected, CancellationToken cancellationToken)
    {
        RequireSession(catalog); var current = Current(catalog, expected.SourceId, expected.SourceMangaId, cancellationToken);
        if (current != expected) throw new NativeTagConflictException("The exact native row, current role, source profile or entry ownership changed before publication.");
        return catalog.Query("SELECT * FROM " + NativeTagOwnership.Table(expected.Table) + " WHERE id=$id", ("$id", expected.RowId)).Single();
    }
    private NativeTagRecord? Current(DatabaseSession catalog, string sourceId, long mangaId, CancellationToken cancellationToken)
    {
        RequireSession(catalog); cancellationToken.ThrowIfCancellationRequested();
        var rows = catalog.Query("SELECT id,0 is_variant FROM source_matches WHERE source_id=$source AND source_manga_id=$manga AND active=1 UNION ALL SELECT id,1 is_variant FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1", ("$source", sourceId), ("$manga", mangaId));
        if (rows.Count == 0) return null;
        if (rows.Count != 1) throw new NativeTagConflictException("More than one physical native role requires explicit promotion or structural reconciliation.");
        var role = rows[0]; var result = _tags.Capture(catalog, role.Flag("is_variant") ? NativeTagTable.Variants : NativeTagTable.Matches, role.Integer("id"), cancellationToken);
        var physical = catalog.Scalar("SELECT entry_id FROM " + NativeTagOwnership.Table(result.Table) + " WHERE id=$id", ("$id", result.RowId));
        if (physical is not long entry || entry != result.CanonicalEntryId) throw new NativeTagConflictException("The physical native row requires alias reconciliation before it can be refreshed.");
        return result;
    }
    private static long Canonical(DatabaseSession catalog, long entry, CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>(); while (seen.Add(entry))
        {
            cancellationToken.ThrowIfCancellationRequested(); var next = catalog.Scalar("SELECT canonical_entry_id FROM entry_aliases WHERE old_entry_id=$entry", ("$entry", entry));
            if (next is null) return entry; if (next is not long id) throw new NativeTagConflictException("Entry aliases require exact SQLite integer identities."); entry = id;
        }
        throw new NativeTagConflictException("The entry alias chain is cyclic.");
    }
    private void RequireStandalone() { if (_database.HasCurrentThreadSession(DatabaseFile.Catalog)) throw new InvalidOperationException("Standalone native record phases require their own separately committed catalogue transactions."); }
    private void RequireSession(DatabaseSession catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog); if (!Monitor.IsEntered(_gate)) throw new InvalidOperationException("Native record phases require the shared source-write gate.");
        if (!_database.IsCurrentThreadSession(DatabaseFile.Catalog, catalog)) throw new InvalidOperationException("Native record phases require this instance's actual current catalogue session.");
    }
    private T WithGate<T>(CancellationToken cancellationToken, Func<T> operation)
    {
        cancellationToken.ThrowIfCancellationRequested(); while (!Monitor.TryEnter(_gate, 50)) cancellationToken.ThrowIfCancellationRequested();
        try { cancellationToken.ThrowIfCancellationRequested(); return operation(); } finally { Monitor.Exit(_gate); }
    }
    private static long Changes(DatabaseSession catalog) => Convert.ToInt64(catalog.Scalar("SELECT total_changes()"), CultureInfo.InvariantCulture);

    // Pure released source-value coercions are grouped here with their only
    // persistence consumer. They are not native matching or canonical tag rules.
    public static long Status(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            var raw = UnicodeText.Trim(text); if (raw.Length == 0) return 0;
            var digits = raw.TrimStart('-').EnumerateRunes().ToArray();
            if (digits.Length > 0 && digits.All(rune => CharUnicodeInfo.GetDigitValue(rune.ToString(), 0) >= 0)) return Integer(JsonValue.Create(raw));
            return raw.ToUpperInvariant() switch { "ONGOING" => 1, "COMPLETED" => 2, "LICENSED" => 3, "PUBLISHING_FINISHED" => 4, "CANCELLED" => 5, "ON_HIATUS" => 6, _ => 0 };
        }
        return value is JsonValue ? Integer(value) : 0;
    }
    public static string GenreText(JsonNode? value) => value is null ? "" : value is JsonArray array
        ? string.Join(", ", array.Select(SourceEngineEnvironment.Scalar).Select(UnicodeText.Trim).Where(text => text.Length > 0)) : SourceEngineEnvironment.Scalar(value);
    // Shared representation retains its 1024-character integer-literal guard.
    // Oversized numeric text data is unqualified; native Int64 IDs use Integer.
    internal static string SourceText(JsonNode? value) => NativeTagJson.Truth(value) ? SourceEngineEnvironment.Scalar(value) : "";
    // Cached source SQL binds the original primitive; authoritative detail uses
    // SourceText above. SQLite TEXT affinity owns bool/numeric spelling here.
    private static object CachedValue(JsonNode? value)
    {
        if (!NativeTagJson.Truth(value)) return "";
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text)) return text;
            if (scalar.TryGetValue<bool>(out var flag)) return flag ? 1L : 0L;
            if (scalar.GetValueKind() == JsonValueKind.Number)
            {
                var raw = scalar.ToJsonString();
                if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) return checked((long)BigInteger.Parse(raw, CultureInfo.InvariantCulture));
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
        throw new InvalidDataException("A cached native text field contains a source value SQLite cannot bind.");
    }
    private static string CachedSummary(JsonNode? value)
    {
        if (!NativeTagJson.Truth(value)) return "";
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return TextMatcher.CleanSummary(text);
        throw new InvalidDataException("A cached native synopsis must contain source text before summary parsing.");
    }
    private static long Integer(JsonNode? value)
    {
        if (value is null) return 0;
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag)) return flag ? 1 : 0;
        if (value is JsonValue number && number.GetValueKind() == JsonValueKind.Number)
        {
            var raw = number.ToJsonString(); if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) return checked((long)BigInteger.Parse(raw, CultureInfo.InvariantCulture));
            return checked((long)Math.Truncate(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)));
        }
        if (value is JsonValue str && str.TryGetValue<string>(out var text))
        {
            var raw = UnicodeText.Trim(text); var result = new StringBuilder(); var index = 0; if (raw.Length > 0 && raw[0] is '+' or '-') { result.Append(raw[0]); index++; }
            var digits = 0; var previous = false;
            foreach (var rune in raw[index..].EnumerateRunes())
            {
                if (rune.Value == '_' && previous) { previous = false; continue; }
                if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber) throw new ArgumentException("Source integer is not a valid decimal integer.");
                result.Append((char)('0' + CharUnicodeInfo.GetDecimalDigitValue(rune.ToString(), 0))); digits++; previous = true;
            }
            if (digits == 0 || !previous) throw new ArgumentException("Source integer is not a valid decimal integer.");
            return checked((long)BigInteger.Parse(result.ToString(), CultureInfo.InvariantCulture));
        }
        return 0; // Released status coercion catches int(container)'s TypeError.
    }
    private static JsonObject RecordPayload(DatabaseRow row)
    {
        JsonNode? decoded; try { decoded = JsonNode.Parse(row.Text("metadata_json") is { Length: > 0 } text ? text : "{}"); } catch (JsonException) { decoded = new JsonObject(); }
        if (decoded is not JsonObject payload) throw new InvalidDataException("Retained native record metadata is not an object; lineage publication was refused.");
        payload["id"] = row.Integer("source_manga_id");
        foreach (var (target, source) in new[] { ("url", "source_manga_url"), ("title", "source_title"), ("thumbnailUrl", "thumbnail_url"), ("description", "description"), ("author", "author"), ("artist", "artist") })
            payload[target] = row.Text(source).Length > 0 ? JsonValue.Create(row.Text(source)) : NativeTagJson.Truth(payload[target]) ? payload[target]!.DeepClone() : JsonValue.Create("");
        payload["genre"] = row.Text("genre").Length > 0 ? JsonValue.Create(row.Text("genre")) : NativeTagJson.Truth(payload["genre"]) ? payload["genre"]!.DeepClone() : new JsonArray();
        payload["status"] = row.Integer("status") != 0 ? JsonValue.Create(row.Integer("status")) : NativeTagJson.Truth(payload["status"]) ? payload["status"]!.DeepClone() : JsonValue.Create(0);
        payload["realUrl"] = row.Text("real_url").Length > 0 ? JsonValue.Create(row.Text("real_url")) : NativeTagJson.Truth(payload["realUrl"]) ? payload["realUrl"]!.DeepClone() : JsonValue.Create("");
        payload["initialized"] = row.Flag("initialized"); return payload;
    }
}
