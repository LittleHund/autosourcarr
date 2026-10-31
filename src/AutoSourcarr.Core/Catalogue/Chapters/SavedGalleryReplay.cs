using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Catalogue.Structure.StructureSql;

namespace AutoSourcarr.Core.Catalogue.Chapters;

/// <summary>Offline restoration of actual saved split candidates. The structural journal owner performs acknowledgement.</summary>
public sealed class SavedGalleryReplay : ICatalogueGalleryReplay
{
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly NativeChapterWriter _writer;
    private readonly string _work;
    public SavedGalleryReplay(SqliteDatabase database, object sourceWriteGate, NativeChapterWriter writer, string privateWorkDirectory)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!writer.Owns(database, sourceWriteGate)) throw new ArgumentException("Gallery replay must share its real native writer's database and source gate", nameof(writer));
        _database = database; _gate = sourceWriteGate; _writer = writer; _work = privateWorkDirectory;
    }
    public void Restore(long splitId, IReadOnlyList<NativeSourceRecord> records, IReadOnlyDictionary<NativeSourceRecord, long> currentOwners) => Restore(splitId, records, currentOwners, default);
    public void Restore(long splitId, IReadOnlyList<NativeSourceRecord> records, IReadOnlyDictionary<NativeSourceRecord, long> currentOwners, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = _database.Read(DatabaseFile.Catalog, c => c.Scalar("SELECT 1 FROM gallery_layout_candidates WHERE split_id=$p0", P(splitId)) is not null); if (!exists) return;
            using var files = new OwnedReplayFiles(_work);
            var ids = _database.Read(DatabaseFile.Catalog, c =>
            {
                var pending = c.Scalar("SELECT records_json FROM pending_entry_splits WHERE id=$p0", P(splitId)) as string ?? throw new InvalidDataException("Saved gallery intent has no owning split journal");
                var declared = new HashSet<NativeSourceRecord>(); var pairs = JsonNode.Parse(pending) as JsonArray ?? throw new InvalidDataException("Saved split records are invalid");
                foreach (var pair in pairs)
                {
                    if (pair is not JsonArray { Count: 2 } values || values[0]?.GetValueKind() != System.Text.Json.JsonValueKind.String || values[1] is not JsonValue scalar || !scalar.TryGetValue<long>(out var manga)) throw new InvalidDataException("Saved split source identity is invalid");
                    declared.Add(new(values[0]!.GetValue<string>(), manga));
                }
                if (!declared.SetEquals(records)) throw new InvalidOperationException("Gallery replay records differ from the owning split journal");
                return c.WithReadBlob("gallery_layout_candidates", "candidate_ids_json", splitId, stream => ReplayIdIndex.Read(stream, files, cancellationToken));
            });
            // The complete JSON was validated and its blob closed before any origins write.
            var selected = records.ToHashSet(); var owners = new Dictionary<NativeSourceRecord, long>(); var contexts = new List<NativeChapterWriter.Context>();
            foreach (var pair in records.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actual = _database.Read(DatabaseFile.Catalog, c =>
                {
                    var values = c.Query("SELECT entry_id FROM source_matches WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1 UNION ALL SELECT entry_id FROM source_variants WHERE source_id=$p0 AND source_manga_id=$p1 AND active=1", P(pair.SourceId, pair.MangaId)).Select(row => PortableIdentityResolver.Canonical(c, row.Integer("entry_id"))).Distinct().ToArray();
                    if (values.Length > 1) throw new InvalidOperationException("Saved gallery native ownership is ambiguous"); return values.Length == 0 ? (long?)null : values[0];
                });
                if (!actual.HasValue) { if (currentOwners.ContainsKey(pair)) throw new InvalidOperationException("Saved gallery current owner was detached"); continue; }
                if (!currentOwners.TryGetValue(pair, out var expected) || expected != actual.Value) throw new InvalidOperationException("Saved gallery source ownership changed before replay");
                owners.Add(pair, actual.Value); contexts.Add(_writer.Prepare(actual.Value, pair.SourceId, pair.MangaId));
            }
            foreach (var context in contexts) _writer.Verify(context);
            // The released restoration commits exclusive chapter moves first, then ingests
            // each frozen source record. Any later failure leaves the existing split intent
            // pending; retry uses actual current ownership and the retained candidate IDs.
            var input = files.Create(); var batches = _database.Write(DatabaseFile.Origins, o =>
            {
                var result = new List<(NativeChapterWriter.Context Context, FrozenChapterBatch Batch)>();
                foreach (var context in contexts)
                {
                    cancellationToken.ThrowIfCancellationRequested(); input.Position = input.Length; var frozen = new FrozenChapterBatch(input);
                    long cursor = 0; var first = true;
                    while (true)
                    {
                        var page = o.Query("""
                            SELECT cc.*,vc.chapter_key FROM chapter_candidates cc JOIN virtual_chapters vc ON vc.id=cc.virtual_chapter_id
                            WHERE cc.source_id=$p0 AND cc.source_manga_id=$p1 AND ($p2=1 OR cc.id>$p3) ORDER BY cc.id LIMIT 256
                            """, P(context.SourceId, context.MangaId, first ? 1 : 0, cursor)); if (page.Count == 0) break; first = false;
                        foreach (var row in page)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); cursor = row.Integer("id"); if (!ids.Contains(cursor)) continue;
                            if (o.Scalar("SELECT 1 FROM chapter_candidate_rejections WHERE source_id=$p0 AND source_chapter_id=$p1", P(context.SourceId, row.Integer("source_chapter_id"))) is not null) continue;
                            MoveExclusive(o, row, context.EntryId, selected, owners, cancellationToken);
                            var chapter = new NativeChapter(row.Integer("source_chapter_id"), row.Text("chapter_key").StartsWith("g:", StringComparison.Ordinal) ? -1 : row.Number("chapter_number"), row.Text("name"), row.Text("source_chapter_url"), row.Integer("source_order"), row.Number("volume_number"), row.Integer("upload_date"), row.Text("scanlator"), row.Integer("page_count"));
                            NativeChapterWriter.Validate(chapter); frozen.Append(new(chapter, NativeChapterWriter.Old(row)));
                        }
                    }
                    if (frozen.Count != 0) result.Add((context, frozen));
                }
                return result;
            });
            // One logical complete record per origins transaction. Internal SQL pages never
            // deactivate earlier pages. Frozen source input survives changes to shared old keys.
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _database.Write(DatabaseFile.Origins, o => _writer.Apply(o, batch.Context, batch.Batch, files, updateScan: false, collectEffects: false, cancellationToken));
            }
        }
    }
    private static void MoveExclusive(DatabaseSession o, DatabaseRow row, long owner, IReadOnlySet<NativeSourceRecord> selected, IReadOnlyDictionary<NativeSourceRecord, long> owners, CancellationToken token)
    {
        var vid = row.Integer("virtual_chapter_id"); long cursor = 0; var first = true; var any = false;
        while (true)
        {
            var page = o.Query("SELECT id,source_id,source_manga_id FROM chapter_candidates WHERE virtual_chapter_id=$p0 AND ($p1=1 OR id>$p2) ORDER BY id LIMIT 256", P(vid, first ? 1 : 0, cursor)); if (page.Count == 0) break; first = false;
            foreach (var occupant in page)
            {
                token.ThrowIfCancellationRequested(); cursor = occupant.Integer("id"); any = true; var key = new NativeSourceRecord(occupant.Text("source_id"), occupant.Integer("source_manga_id"));
                if (!selected.Contains(key) || !owners.TryGetValue(key, out var current) || current != owner) return;
            }
        }
        if (any && o.Scalar("SELECT 1 FROM virtual_chapters WHERE entry_id=$p0 AND chapter_key=$p1 AND id<>$p2", P(owner, row.Text("chapter_key"), vid)) is null)
            o.Execute("UPDATE virtual_chapters SET entry_id=$p0 WHERE id=$p1", P(owner, vid));
    }
}
