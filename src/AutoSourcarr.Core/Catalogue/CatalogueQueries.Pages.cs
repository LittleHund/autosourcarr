using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue;

/// <summary>One page of catalogue cards and the cursor of the next page (null on the last page).</summary>
public sealed record CatalogueEntryPage(IReadOnlyList<JsonObject> Items, string? NextCursor);

public sealed partial class CatalogueQueries
{
    /// <summary>
    /// The first page: the newest-added entries by their immutable id (the rowid order, no index or sort needed).
    /// The order is "newest added first", not "recently updated first" (the 0.4.5 List order): an updated_at key moves
    /// while a person pages, so an entry updated after page 1 would jump ahead of the cursor and never be shown.
    /// </summary>
    internal const string FirstPageSql = "SELECT id FROM entries ORDER BY id DESC";
    /// <summary>Later pages continue strictly below the last id (keyset seek on the primary key, no OFFSET).</summary>
    internal const string NextPageSql = "SELECT id FROM entries WHERE id<@before ORDER BY id DESC";

    /// <summary>
    /// PERF-5 page-first catalogue list: newest added first, <paramref name="limit"/> cards per page through
    /// <see cref="DatabaseSession.QueryPage{T}"/>; <paramref name="cursor"/> is the previous page's next cursor (the
    /// last id shown). Every entry that exists when its page is read is shown exactly once, however often it changes.
    /// A cursor this method did not issue is refused with <see cref="ArgumentException"/>.
    /// </summary>
    public CatalogueEntryPage ListPage(string? cursor, int limit)
    {
        var before = DecodeCursor(cursor);
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, session =>
        {
            var page = before is { } id
                ? session.QueryPage<long>(NextPageSql, new { before = id }, limit)
                : session.QueryPage<long>(FirstPageSql, null, limit);
            var cards = page.Items.Count == 0 ? [] : Summaries(session, page.Items);
            var next = page.HasMore && page.Items.Count > 0 ? page.Items[^1].ToString(CultureInfo.InvariantCulture) : null;
            return new CatalogueEntryPage(cards, next);
        });
    }

    private static long? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        return cursor.Length <= 20 && long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id
            : throw new ArgumentException("The page cursor is not valid; start again from the first page.");
    }
}
