using System.Globalization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Tags;

/// <param name="Changes">Actual committed SQLite total_changes delta, including factual links/gallery rows; not a completed whole-index version.</param>
public sealed record NativeTagBatch(long Cursor, long Count, long Changes);

/// <summary>Explicit saved-native-row batches. No scheduling, fetching, version or layout acknowledgement.</summary>
public sealed class NativeTagReindex(NativeTagStore store)
{
    public const string AttemptedChangesKey = "AutoSourcarr.NativeTagReindex.AttemptedChanges";
    public NativeTagBatch Run(string table, long after, long size = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var recordTable = table switch { "source_matches" => NativeTagTable.Matches, "source_variants" => NativeTagTable.Variants, _ => throw new ArgumentException("Invalid source record table", nameof(table)) };
        return store.Write(cancellationToken, catalog =>
        {
            var initial = Changes(catalog); var cursor = after; var count = 0L;
            try
            {
                // A negative legacy LIMIT means all rows. Preserve it with
                // bounded SQL pages inside the same logical batch transaction.
                while (size < 0 || count < size)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var limit = size < 0 ? 100 : Math.Min(100, size - count);
                    var rows = catalog.Query("SELECT id FROM " + table + " WHERE id>$after AND active=1 ORDER BY id LIMIT $limit", ("$after", cursor), ("$limit", limit));
                    if (rows.Count == 0) break;
                    foreach (var row in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var id = row.Integer("id"); store.ReindexRow(catalog, recordTable, id, cancellationToken); cursor = id; count = checked(count + 1);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested(); return new NativeTagBatch(cursor, count, Changes(catalog) - initial);
            }
            catch (Exception error)
            {
                // Diagnostic attempted writes are retained on the actual error;
                // no batch cursor or successful receipt escapes this rollback.
                try { error.Data[AttemptedChangesKey] = Changes(catalog) - initial; }
                catch (Exception unavailable) { error.Data[AttemptedChangesKey + ".Unavailable"] = unavailable.GetType().Name; }
                throw;
            }
        }, requireOuterCommit: true);
    }
    private static long Changes(DatabaseSession catalog) => Convert.ToInt64(catalog.Scalar("SELECT total_changes()"), CultureInfo.InvariantCulture);
}
