using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Core.Repair.Migration;

/// <summary>Outcome of one content check: the step it reached (search, popular, details, pages, image) and what it saw.</summary>
public sealed record ContentCheckResult(bool Ok, string Step, string SourceId, string SourceName, int Titles, string Title, int Chapters,
    int Pages, int ImageBytes, string ImageType, string Error, double Seconds)
{
    public JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["ok"] = Ok, ["source_id"] = SourceId, ["source"] = SourceName, ["titles"] = Titles, ["title"] = Title,
            ["chapters"] = Chapters, ["pages"] = Pages, ["image_bytes"] = ImageBytes, ["image_type"] = ImageType,
            ["seconds"] = Math.Round(Seconds, 1),
        };
        if (!Ok) { result["failed_at"] = Step; result["error"] = Error; }
        return result;
    }
}

/// <summary>The source calls a content check makes (the SuwayomiClient API in production, a fake in tests).</summary>
public interface IContentCheckSource
{
    Task<JsonObject> BrowseAsync(string sourceId, string mode, string query, CancellationToken cancellationToken);
    Task<JsonObject> DetailsAsync(long mangaId, CancellationToken cancellationToken);
    Task<List<string>> PagesAsync(long chapterId, CancellationToken cancellationToken);
    Task<(byte[] Bytes, string ContentType)> FirstImageAsync(long mangaId, long chapterId, CancellationToken cancellationToken);
}

/// <summary>The content check through the existing SuwayomiClient calls (FetchSourceMangaAsync, FetchMangaAndChaptersAsync,
/// FetchChapterPagesAsync, ChapterIndexForIdAsync + FetchPageImageAsync), as in SourceHostIntegrationTests.</summary>
public sealed class SuwayomiContentCheckSource(SuwayomiClient client) : IContentCheckSource
{
    public Task<JsonObject> BrowseAsync(string sourceId, string mode, string query, CancellationToken cancellationToken) =>
        client.FetchSourceMangaAsync(sourceId, mode, 1, query, cancellationToken);
    public Task<JsonObject> DetailsAsync(long mangaId, CancellationToken cancellationToken) =>
        client.FetchMangaAndChaptersAsync(mangaId, true, true, cancellationToken);
    public Task<List<string>> PagesAsync(long chapterId, CancellationToken cancellationToken) =>
        client.FetchChapterPagesAsync(chapterId, cancellationToken);
    public async Task<(byte[] Bytes, string ContentType)> FirstImageAsync(long mangaId, long chapterId, CancellationToken cancellationToken)
    {
        var index = await client.ChapterIndexForIdAsync(mangaId, chapterId, cancellationToken).ConfigureAwait(false);
        return await client.FetchPageImageAsync(mangaId, index, 0, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Phase 6e content check (same steps as scripts/source-host-check.py check()): search, or the popular list when the
/// search finds nothing or no query is given; then details and chapters of the first titles; the pages of the newest,
/// oldest or middle chapter; and the first page image, which must be larger than 512 bytes.
/// </summary>
public static class ContentCheck
{
    public const int MinimumImageBytes = 512;

    public static async Task<ContentCheckResult> RunAsync(IContentCheckSource source, string sourceId, string sourceName, string? query = null,
        int titles = 5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var watch = Stopwatch.StartNew();
        var step = "search"; var found = 0; var title = ""; var chapterCount = 0; var pageCount = 0; var imageBytes = 0; var imageType = "";
        ContentCheckResult Done(bool ok, string error) =>
            new(ok, step, sourceId, sourceName, found, title, chapterCount, pageCount, imageBytes, imageType, error, watch.Elapsed.TotalSeconds);
        try
        {
            JsonArray mangas = [];
            if (!string.IsNullOrWhiteSpace(query))
                mangas = (await source.BrowseAsync(sourceId, "SEARCH", query, cancellationToken).ConfigureAwait(false))["mangas"] as JsonArray ?? [];
            if (mangas.Count == 0)
            {
                step = "popular";
                mangas = (await source.BrowseAsync(sourceId, "POPULAR", "", cancellationToken).ConfigureAwait(false))["mangas"] as JsonArray ?? [];
            }
            if (mangas.Count == 0) return Done(false, "no titles found");
            found = mangas.Count;
            List<string> pages = []; long pagesManga = 0, pagesChapter = 0; var seenChapters = false;
            foreach (var candidate in mangas.OfType<JsonObject>().Take(Math.Clamp(titles, 1, 30)))
            {
                step = "details";
                if (!SourceResponseContract.PositiveId(candidate["id"], out var mangaId)) continue;
                var detail = await source.DetailsAsync(mangaId, cancellationToken).ConfigureAwait(false);
                var chapters = (detail["chapters"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                if (chapters.Count == 0) continue;
                seenChapters = true; chapterCount = chapters.Count;
                title = SourceResponseContract.Text((detail["manga"] as JsonObject)?["title"]);
                step = "pages";
                var picks = new[] { chapters[^1], chapters[0], chapters[chapters.Count / 2] }.Distinct().Take(Math.Min(3, chapters.Count));
                foreach (var chapter in picks)
                {
                    if (!SourceResponseContract.PositiveId(chapter["id"], out var chapterId)) continue;
                    pages = await source.PagesAsync(chapterId, cancellationToken).ConfigureAwait(false);
                    if (pages.Count > 0) { pagesManga = mangaId; pagesChapter = chapterId; break; }
                }
                if (pages.Count > 0) break;
            }
            if (!seenChapters) return Done(false, "no chapters in the first titles");
            if (pages.Count == 0) return Done(false, "no chapter with pages in the first titles");
            pageCount = pages.Count;
            step = "image";
            var (bytes, contentType) = await source.FirstImageAsync(pagesManga, pagesChapter, cancellationToken).ConfigureAwait(false);
            imageBytes = bytes.Length; imageType = contentType;
            if (bytes.Length <= MinimumImageBytes)
                return Done(false, "first image is only " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes (" + contentType + ")");
            return Done(true, "");
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = error.Message.Length <= 3000 ? error.Message : error.Message[..3000];
            return Done(false, error.GetType().Name + ": " + message);
        }
    }
}
