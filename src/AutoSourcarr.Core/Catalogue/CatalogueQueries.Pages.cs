using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue;

/// <summary>One page of catalogue cards and the cursor of the next page (null on the last page).</summary>
public sealed record CatalogueEntryPage(IReadOnlyList<JsonObject> Items, string? NextCursor);

public sealed partial class CatalogueQueries
{
    /// <summary>The first page reads only the newest rows of idx_entries_updated_id (no count, no full scan).</summary>
    internal const string FirstPageSql = "SELECT id AS Id, updated_at AS UpdatedAt FROM entries ORDER BY updated_at DESC, id DESC";
    /// <summary>Later pages continue strictly after the last card by the same index key (keyset, no OFFSET).</summary>
    internal const string NextPageSql = "SELECT id AS Id, updated_at AS UpdatedAt FROM entries WHERE (updated_at, id) < (@at, @id) ORDER BY updated_at DESC, id DESC";

    /// <summary>
    /// PERF-5 page-first catalogue list: newest first, <paramref name="limit"/> cards per page through
    /// <see cref="DatabaseSession.QueryPage{T}"/>; <paramref name="cursor"/> is the previous page's next cursor.
    /// A cursor this method did not issue is refused with <see cref="ArgumentException"/>.
    /// </summary>
    public CatalogueEntryPage ListPage(string? cursor, int limit)
    {
        var after = DecodeCursor(cursor);
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session =>
        {
            var page = after is { } key
                ? session.QueryPage<EntryKey>(NextPageSql, new { at = key.UpdatedAt, id = key.Id }, limit)
                : session.QueryPage<EntryKey>(FirstPageSql, null, limit);
            var ids = page.Items.Select(item => item.Id).ToArray();
            var cards = ids.Length == 0 ? [] : Summaries(session, ids);
            var next = page.HasMore && page.Items.Count > 0 ? EncodeCursor(page.Items[^1]) : null;
            return new CatalogueEntryPage(cards, next);
        });
    }

    internal sealed class EntryKey
    {
        public long Id { get; set; }
        public string? UpdatedAt { get; set; }
    }

    private static string EncodeCursor(EntryKey key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes((key.UpdatedAt ?? "") + "\n" + key.Id.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static EntryKey? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        if (cursor.Length > 512) throw new ArgumentException("The page cursor is not valid; start again from the first page.");
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var split = text.LastIndexOf('\n');
            if (split < 0 || !long.TryParse(text[(split + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                throw new FormatException();
            return new EntryKey { Id = id, UpdatedAt = text[..split] };
        }
        catch (FormatException) { throw new ArgumentException("The page cursor is not valid; start again from the first page."); }
    }
}
