using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Tags;

internal static class NativeTagOwnership
{
    internal sealed record State(DatabaseRow Row, DatabaseRow? Source, NativeTagRecord Identity);
    public static string Table(NativeTagTable table) => table switch
    {
        NativeTagTable.Matches => "source_matches", NativeTagTable.Variants => "source_variants", _ => throw new ArgumentOutOfRangeException(nameof(table))
    };

    public static State Read(DatabaseSession catalog, NativeTagTable table, long rowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var name = Table(table);
        var row = catalog.Query("SELECT * FROM " + name + " WHERE id=$id", ("$id", rowId)).SingleOrDefault();
        if (row is null || Integer(row, "active") != 1) throw new NativeTagConflictException("The native source record is absent or inactive.");
        var entry = Integer(row, "entry_id"); var manga = Integer(row, "source_manga_id");
        if (row["source_id"] is not string sourceId) throw new NativeTagConflictException("The native source identity is not stored as exact text.");
        var owners = catalog.Query("""
            SELECT 0 record_table,id,entry_id,match_type,locked FROM source_matches WHERE source_id=$source AND source_manga_id=$manga AND active=1
            UNION ALL SELECT 1 record_table,id,entry_id,match_type,locked FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1
            ORDER BY record_table,id
            """, ("$source", sourceId), ("$manga", manga));
        var ownerProof = new JsonArray(); var canonicalOwners = new HashSet<long>();
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested(); var path = new JsonArray(); var canonical = Canonical(catalog, Integer(owner, "entry_id"), path, cancellationToken);
            canonicalOwners.Add(canonical); var proof = Json(owner); proof["canonical_entry_id"] = canonical; proof["aliases"] = path; ownerProof.Add(proof);
        }
        var recordPath = new JsonArray(); var canonicalEntry = Canonical(catalog, entry, recordPath, cancellationToken);
        if (canonicalOwners.Count != 1 || !canonicalOwners.Contains(canonicalEntry))
            throw new NativeTagConflictException("The exact native source record has ambiguous current entry ownership.");
        var source = catalog.Query("SELECT * FROM source_config WHERE source_id=$source", ("$source", sourceId)).SingleOrDefault();
        var snapshot = new JsonObject { ["table"] = name, ["record"] = Json(row), ["source"] = source is null ? null : Json(source), ["owners"] = ownerProof, ["aliases"] = recordPath };
        var fingerprint = Convert.ToHexString(SHA256.HashData(CanonicalJson.Bytes(snapshot))).ToLowerInvariant(); cancellationToken.ThrowIfCancellationRequested();
        return new(row, source, new(table, rowId, canonicalEntry, sourceId, manga, fingerprint));
    }

    private static long Canonical(DatabaseSession catalog, long entry, JsonArray path, CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        while (seen.Add(entry))
        {
            cancellationToken.ThrowIfCancellationRequested(); var alias = catalog.Query("SELECT * FROM entry_aliases WHERE old_entry_id=$entry", ("$entry", entry)).SingleOrDefault();
            if (alias is null)
            {
                if (catalog.Scalar("SELECT 1 FROM entries WHERE id=$entry", ("$entry", entry)) is null) throw new NativeTagConflictException("The native source owner has no current catalogue entry.");
                return entry;
            }
            path.Add(Json(alias)); entry = Integer(alias, "canonical_entry_id");
        }
        throw new NativeTagConflictException("The native source owner has a cyclic entry alias chain.");
    }
    public static JsonObject Json(DatabaseRow row) => JsonSerializer.SerializeToNode(row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))!.AsObject();
    private static long Integer(DatabaseRow row, string field) => row[field] is long value ? value : throw new NativeTagConflictException("The native source identity is not stored as an exact SQLite integer.");
}
