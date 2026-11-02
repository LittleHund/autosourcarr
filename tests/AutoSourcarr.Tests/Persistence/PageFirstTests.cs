using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Library;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

/// <summary>PERF-5: the big list reads are page-first keyset reads that use an index for both order and continuation.</summary>
public static class PageFirstTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("page-first lists (PERF-5): catalogue, Matrix and download history pages use an index for order and continuation, never a sort or full scan", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            foreach (var sql in new[]
            {
                CatalogueQueries.FirstPageSql, CatalogueQueries.NextPageSql.Replace("@before", "10", StringComparison.Ordinal),
                LibraryStore.DownloadFirstPageSql, LibraryStore.DownloadNextPageSql.Replace("@before", "10", StringComparison.Ordinal),
                "SELECT * FROM decision_log WHERE id<10 ORDER BY id DESC",
            })
            {
                var plan = string.Join(" | ", database.Read(DatabaseFile.Catalog, c => c.Query("EXPLAIN QUERY PLAN " + sql + " LIMIT 51")).Select(row => row.Text("detail")));
                // An in-order walk (index or rowid) stops after LIMIT rows; only an in-memory sort would read every row.
                Check.True(!plan.Contains("TEMP B-TREE", StringComparison.Ordinal), "sorted in memory (reads every row): " + sql + " -> " + plan);
                if (sql.Contains("WHERE id<", StringComparison.Ordinal))
                    Check.True(plan.Contains("SEARCH", StringComparison.Ordinal), "the next page seeks, it does not skip rows: " + sql + " -> " + plan);
            }
        });

        suite.Add("page-first lists (PERF-5): catalogue pages of 50 continue by cursor without repeats, also when an entry changes between pages; a foreign cursor is refused", () =>
        {
            using var temp = new Sandbox();
            using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            new SchemaMigrator(database).Initialize();
            database.Write(DatabaseFile.Catalog, c =>
            {
                for (var i = 1; i <= 120; i++)
                    c.Execute("INSERT INTO entries(id,preferred_title,status,created_at,updated_at) VALUES($id,$title,0,'2026-01-01',$at)",
                        ("$id", i), ("$title", "Work " + i), ("$at", "2026-01-" + (1 + i % 3).ToString("00", System.Globalization.CultureInfo.InvariantCulture)));
                return 0;
            });
            var queries = new CatalogueQueries(database, new object(), _ => false);
            var seen = new List<long>(); string? cursor = null; var pages = 0;
            do
            {
                var page = queries.ListPage(cursor, 50); pages++;
                Check.True(page.Items.Count <= 50, "page size");
                seen.AddRange(page.Items.Select(item => item["id"]!.GetValue<long>()));
                cursor = page.NextCursor;
            } while (cursor is not null && pages < 10);
            Check.Equal(3, pages);
            Check.Equal(120, seen.Count);
            Check.Equal(120, seen.Distinct().Count());
            Check.Throws<ArgumentException>(() => queries.ListPage("not-a-cursor", 50));

            // Audit A4: an entry updated while a person pages (after page 1, before page 2) is still shown exactly once.
            var first = queries.ListPage(null, 50);
            var onPageTwo = 60L;
            Check.True(!first.Items.Any(item => item["id"]!.GetValue<long>() == onPageTwo), "entry 60 belongs to page 2");
            database.Write(DatabaseFile.Catalog, c => c.Execute("UPDATE entries SET updated_at='2099-01-01T00:00:00Z' WHERE id=$id", ("$id", onPageTwo)));
            var rest = new List<long>(); cursor = first.NextCursor;
            while (cursor is not null)
            {
                var page = queries.ListPage(cursor, 50);
                rest.AddRange(page.Items.Select(item => item["id"]!.GetValue<long>()));
                cursor = page.NextCursor;
            }
            Check.Equal(1, rest.Count(id => id == onPageTwo));
            Check.Equal(120, first.Items.Count + rest.Count);
        });
    }
}
