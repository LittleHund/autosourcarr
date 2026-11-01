using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;

namespace AutoSourcarr.Core.Library;

/// <summary>
/// The library's view of the catalogue: series metadata from entries (with locked display metadata), the chapter
/// list the reader serves (ReaderRepository.ListChapters: one serving source per chapter, the source's enabled and
/// priority rules applied), source coverage from CatalogueQueries.RecordChapterStats, and a chapter refresh supplied
/// by the host (the phase-10 chapter sync; without it monitoring observes the stored chapter list).
/// </summary>
public sealed class CatalogueLibrary(SqliteDatabase database, ReaderRepository reader, CatalogueQueries? queries = null,
    Func<long, CancellationToken, Task>? refresh = null, DisplayMetadata? display = null) : ILibraryCatalogue
{
    private static readonly char[] GenreSeparators = [',', ';'];

    public long CanonicalEntryId(long entryId) => reader.CanonicalEntryId(entryId);

    public LibrarySeriesInfo? Series(long entryId)
    {
        var row = database.Read(DatabaseFile.Catalog, c => c.Query(
            "SELECT id,preferred_title,description,thumbnail_url,status,author,artist,genre FROM entries WHERE id=$id", ("$id", entryId)).FirstOrDefault());
        if (row is null) return null;
        var entry = LibraryProjection.Json(row);
        if (display is not null) entry = display.Overlay(entry);
        var status = entry["status"] is JsonValue value
            ? value.TryGetValue<long>(out var number) ? number : value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0
            : 0;
        var genres = Text(entry, "genre").Split(GenreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new LibrarySeriesInfo(entryId, Text(entry, "preferred_title"), Text(entry, "author"), Text(entry, "artist"), Text(entry, "description"),
            genres, (int)Math.Clamp(status, 0, 6));
    }

    public IReadOnlyList<LibraryChapterInfo> Chapters(long entryId)
    {
        var rows = reader.ListChapters(entryId);
        var languages = database.Read(DatabaseFile.Catalog, c => c.Query("SELECT source_id,lang FROM source_config")
            .ToDictionary(row => row.Text("source_id"), row => row.Text("lang"), StringComparer.Ordinal));
        return rows.Select(row =>
        {
            var source = ReaderValue.Text(row, "serving_source_id");
            var language = languages.GetValueOrDefault(source, "");
            return new LibraryChapterInfo(ReaderValue.Integer(row, "id"), ReaderValue.Integer(row, "entry_id", entryId),
                ReaderValue.Number(row, "chapter_number", -1), ReaderValue.Number(row, "volume_number", -1), ReaderValue.Text(row, "display_name"),
                source, ReaderValue.Text(row, "serving_source_name"), "", ReaderValue.Integer(row, "upload_date"),
                language is "all" or "other" ? "" : language);
        }).OrderBy(chapter => LibraryNames.Known(chapter.Number) ? 0 : 1).ThenBy(chapter => chapter.Number).ThenBy(chapter => chapter.ChapterId).ToArray();
    }

    public LibraryCoverage Coverage(long entryId)
    {
        if (queries is null) return new(false, 0, "");
        var values = queries.RecordChapterStats([entryId]).Values.ToArray();
        var allChecked = values.Length > 0 && values.All(row => row["chapters_checked"] is JsonValue flag && flag.TryGetValue<bool>(out var done) && done
            && Text(row, "chapter_check_error").Length == 0);
        var gaps = values.Where(row => row["chapters_checked"] is JsonValue flag && flag.TryGetValue<bool>(out var done) && done)
            .Sum(row => row["missing_chapter_count"] is JsonValue count && count.TryGetValue<long>(out var missing) ? missing : 0);
        var times = values.Select(row => row["chapter_checked_at"] switch
        {
            JsonValue stamp when stamp.TryGetValue<string>(out var text) => text,
            JsonValue stamp => stamp.ToJsonString(),
            _ => "",
        }).ToArray();
        var checkedAt = times.Length > 0 && times.All(time => time.Length > 0) ? times.Min(StringComparer.Ordinal) ?? "" : "";
        return new(allChecked, gaps, checkedAt);
    }

    public Task RefreshAsync(long entryId, CancellationToken cancellationToken) => refresh?.Invoke(entryId, cancellationToken) ?? Task.CompletedTask;

    private static string Text(JsonObject row, string key) => row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
