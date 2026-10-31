using System.Globalization;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources;

public sealed partial class SuwayomiClient
{
    public async Task<JsonObject> FetchSourceMangaAsync(string sourceId, string mode, int page = 1, string queryText = "", CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.FetchSourceMangaAsync(sourceId, mode, page, queryText, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject> FetchSourceMangaCoreAsync(SourceTransportSession session, string sourceId, string mode, int page, string queryText, CancellationToken cancellationToken)
    {
        mode = mode.ToUpperInvariant();
        if (sourceId.Length == 0 || mode is not ("SEARCH" or "LATEST" or "POPULAR") || page < 1)
            throw new ArgumentException("Source, mode and positive page are required.");
        var query = $"mutation AutoSourcarrBrowse($input: FetchSourceMangaInput!) {{ fetchSourceManga(input: $input) {{ hasNextPage mangas {{ {MangaFields} }} }} }}";
        var variables = new JsonObject { ["input"] = new JsonObject { ["source"] = sourceId, ["type"] = mode, ["page"] = page, ["query"] = queryText } };
        JsonObject data;
        try { data = await RunSourceWorkAsync(session, token => GraphQlCoreAsync(session, query, variables, token), Operation("browse", JsonValue.Create(sourceId), query, variables, sourceId), null, cancellationToken).ConfigureAwait(false); }
        catch (SuwayomiException error) when (mode == "SEARCH" && (error.Message.Contains("http error 404", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("http 404", StringComparison.OrdinalIgnoreCase)))
        {
            session.ThrowIfRetired();
            return new() { ["hasNextPage"] = false, ["mangas"] = new JsonArray(), ["searchMiss"] = true,
                ["searchWarning"] = "Source returned HTTP 404 for this search query; treated as no results." };
        }
        if (data["fetchSourceManga"] is not JsonObject payload || !SourceResponseContract.Boolean(payload["hasNextPage"], out _)
            || payload["mangas"] is not JsonArray mangas)
            throw new SuwayomiException("Incomplete source feed response");
        if (mangas.Any(row => row is not JsonObject || !SourceResponseContract.PositiveId(row["id"], out _)
            || SourceResponseContract.Identity(row["sourceId"]) != sourceId)) throw new SuwayomiException("Invalid source feed identity");
        session.ThrowIfRetired(); Attention?.Learn(payload);
        return (JsonObject)payload.DeepClone();
    }

    public async Task<JsonObject> FetchMangaAndChaptersAsync(long mangaId, bool fetchManga = true, bool fetchChapters = true, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.FetchMangaAndChaptersAsync(mangaId, fetchManga, fetchChapters, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject> FetchMangaAndChaptersCoreAsync(SourceTransportSession session, long mangaId, bool fetchManga, bool fetchChapters, CancellationToken cancellationToken)
    {
        if (mangaId <= 0) throw new ArgumentOutOfRangeException(nameof(mangaId));
        var query = $"mutation AutoSourcarrManga($input: FetchMangaAndChaptersInput!) {{ fetchMangaAndChapters(input: $input) {{ manga {{ {MangaFields} }} chapters {{ {ChapterFields} }} }} }}";
        var variables = new JsonObject { ["input"] = new JsonObject { ["id"] = mangaId, ["fetchManga"] = fetchManga, ["fetchChapters"] = fetchChapters } };
        var data = await RunSourceWorkAsync(session, token => GraphQlCoreAsync(session, query, variables, token), Operation("manga", JsonValue.Create(mangaId), query, variables), null, cancellationToken).ConfigureAwait(false);
        if (data["fetchMangaAndChapters"] is not JsonObject payload || payload["manga"] is not JsonObject manga
            || !SourceResponseContract.TryLong(manga["id"], out var returnedId) || returnedId != mangaId)
            throw new SuwayomiException("Incomplete manga identity response");
        if (fetchChapters && (payload["chapters"] is not JsonArray chapters || chapters.Any(row => row is not JsonObject
            || !SourceResponseContract.PositiveId(row["id"], out _) || !SourceResponseContract.TryLong(row["mangaId"], out var parent) || parent != mangaId)))
            throw new SuwayomiException("Incomplete chapter response");
        session.ThrowIfRetired(); Attention?.Learn(payload);
        return (JsonObject)payload.DeepClone();
    }

    public async Task<List<string>> FetchChapterPagesAsync(long chapterId, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.FetchChapterPagesAsync(chapterId, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<List<string>> FetchChapterPagesCoreAsync(SourceTransportSession session, long chapterId, CancellationToken cancellationToken)
    {
        if (chapterId <= 0) throw new ArgumentOutOfRangeException(nameof(chapterId));
        const string query = "mutation AutoSourcarrPages($input: FetchChapterPagesInput!) { fetchChapterPages(input: $input) { pages } }";
        var variables = new JsonObject { ["input"] = new JsonObject { ["chapterId"] = chapterId } };
        var data = await RunSourceWorkAsync(session, token => GraphQlCoreAsync(session, query, variables, token), Operation("pages", JsonValue.Create(chapterId), query, variables), null, cancellationToken).ConfigureAwait(false);
        if (data["fetchChapterPages"] is not JsonObject payload || payload["pages"] is not JsonArray pages
            || pages.Any(url => SourceResponseContract.Text(url).Length == 0)) throw new SuwayomiException("Incomplete page response");
        return pages.Select(url => url!.GetValue<string>()).ToList();
    }

    public async Task<JsonObject> RetryVerificationAsync(JsonObject operation, JsonObject context, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.RetryVerificationAsync(operation, context, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<JsonObject> RetryVerificationCoreAsync(SourceTransportSession session, JsonObject operation, JsonObject context, CancellationToken cancellationToken)
    {
        var query = SourceResponseContract.Text(operation["query"]);
        if (query.Length == 0 || operation["variables"] is not JsonObject variables) throw new SuwayomiException("Verification operation is incomplete");
        var data = await RunSourceWorkAsync(session, token => GraphQlCoreAsync(session, query, variables, token), operation, context, cancellationToken).ConfigureAwait(false);
        bool valid;
        switch (SourceResponseContract.Text(operation["kind"]))
        {
            case "pages":
                valid = (data["fetchChapterPages"] as JsonObject)?["pages"] is JsonArray pages && pages.Count > 0
                    && pages.All(url => SourceResponseContract.Text(url).Length > 0);
                break;
            case "manga":
                var manga = (data["fetchMangaAndChapters"] as JsonObject)?["manga"] as JsonObject;
                var source = SourceResponseContract.Identity(context["source_id"]);
                valid = manga is not null && SourceResponseContract.PositiveId(manga["id"], out var id)
                    && SourceResponseContract.TryLong(operation["id"], out var wanted) && id == wanted
                    && (string.IsNullOrEmpty(source) || SourceResponseContract.Identity(manga["sourceId"]) == source);
                break;
            default:
                valid = (data["fetchSourceManga"] as JsonObject)?["mangas"] is JsonArray;
                break;
        }
        if (!valid) throw new SuwayomiException("Verification retry returned an incomplete response; assistance remains open");
        return data;
    }

    public async Task<List<JsonObject>> LibraryMangasAsync(int first = 500, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.LibraryMangasAsync(first, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<List<JsonObject>> LibraryMangasCoreAsync(SourceTransportSession session, int first, CancellationToken cancellationToken)
    {
        if (first < 1 || first > 10000) throw new ArgumentOutOfRangeException(nameof(first));
        Exception? lastError = null;
        foreach (var fields in new[] { MangaFields + " downloadCount chapters { totalCount }",
            "id sourceId url title thumbnailUrl inLibrary downloadCount chapters { totalCount }", "id sourceId url title thumbnailUrl inLibrary" })
        {
            var query = $"query AutoSourcarrLibrary($first: Int!, $after: Cursor) {{ mangas(condition: {{inLibrary: true}}, first: $first, after: $after) {{ nodes {{ {fields} }} pageInfo {{ hasNextPage endCursor }} }} }}";
            List<JsonObject> items = [];
            HashSet<long> seenIds = [];
            HashSet<string> seenCursors = new(StringComparer.Ordinal);
            string? after = null;
            try
            {
                for (var page = 0; page < 100; page++)
                {
                    var data = await GraphQlCoreAsync(session, query, new() { ["first"] = first, ["after"] = after }, cancellationToken).ConfigureAwait(false);
                    if (data["mangas"] is not JsonObject connection || connection["nodes"] is not JsonArray)
                        throw new SuwayomiException("Incomplete main library connection");
                    var nodes = SourceResponseContract.ConnectionNodes(connection);
                    foreach (var row in nodes)
                    {
                        if (!SourceResponseContract.PositiveId(row["id"], out var id) || !seenIds.Add(id)
                            || string.IsNullOrEmpty(SourceResponseContract.Identity(row["sourceId"])) || SourceResponseContract.Text(row["url"]).Length == 0
                            || !SourceResponseContract.Boolean(row["inLibrary"], out var inLibrary) || !inLibrary)
                            throw new SuwayomiException("Incomplete or repeated main library identity");
                    }
                    items.AddRange(nodes);
                    if (connection["pageInfo"] is not JsonObject info || !SourceResponseContract.Boolean(info["hasNextPage"], out var hasNext))
                        throw new SuwayomiException("Incomplete main library pageInfo");
                    if (!hasNext) return items;
                    after = SourceResponseContract.Text(info["endCursor"]);
                    if (after.Length == 0 || !seenCursors.Add(after))
                        throw new SuwayomiException("Main library pagination is incomplete or its cursor repeated; refusing an incomplete snapshot");
                }
                throw new SuwayomiException("Main library pagination exceeded the 100-page safety limit");
            }
            catch (Exception error) when (error is not (OperationCanceledException or SourceWorkDeferredException)) { session.ThrowIfRetired(); lastError = error; }
        }
        throw new SuwayomiException("Unable to query main library: " + _privacy.Text(lastError?.Message ?? "Incomplete response"));
    }

    public async Task<JsonObject> SetMangaInLibraryAsync(long mangaId, bool inLibrary = true, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        var data = await session.GraphQlAsync($"mutation AutoSourcarrSetLibrary($input: UpdateMangaInput!) {{ updateManga(input: $input) {{ manga {{ {MangaFields} }} }} }}",
            new() { ["input"] = new JsonObject { ["id"] = mangaId, ["patch"] = new JsonObject { ["inLibrary"] = inLibrary } } }, cancellationToken).ConfigureAwait(false);
        session.ThrowIfRetired();
        return (data["updateManga"] as JsonObject)?["manga"] is JsonObject manga ? (JsonObject)manga.DeepClone() : new();
    }

    public async Task<JsonObject> SourceMangaDetailsAsync(long mangaId, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        var data = await session.FetchMangaAndChaptersAsync(mangaId, true, false, cancellationToken).ConfigureAwait(false);
        session.ThrowIfRetired(); return (JsonObject)data["manga"]!.DeepClone();
    }

    public async Task CheckChapterAttentionAsync(long chapterId, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        await session.CheckChapterAttentionAsync(chapterId, cancellationToken).ConfigureAwait(false);
    }
    internal async Task CheckChapterAttentionCoreAsync(SourceTransportSession session, long chapterId, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken, session.ScopeCancellationToken);
        if (Attention is not { } attention) return;
        var context = await attention.ContextAsync(BaseUrl, new() { ["kind"] = "pages", ["id"] = chapterId }, linked.Token).ConfigureAwait(false);
        await attention.CheckAsync(context, linked.Token).ConfigureAwait(false);
        session.ThrowIfRetired();
    }

    public async Task<int> ChapterIndexForIdAsync(long mangaId, long chapterId, CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession(cancellationToken);
        return await session.ChapterIndexForIdAsync(mangaId, chapterId, cancellationToken).ConfigureAwait(false);
    }
    internal async Task<int> ChapterIndexForIdCoreAsync(SourceTransportSession session, long mangaId, long chapterId, CancellationToken cancellationToken)
    {
        var payload = await RestJsonCoreAsync(session, $"/api/v1/manga/{mangaId.ToString(CultureInfo.InvariantCulture)}/chapters", cancellationToken).ConfigureAwait(false);
        if ((payload is JsonObject obj ? obj["chapters"] : payload) is not JsonArray chapters)
            throw new SuwayomiException("Unexpected chapter-list response from source host");
        for (var index = 0; index < chapters.Count; index++)
        {
            if (chapters[index] is not JsonObject chapter || SourceResponseContract.Identity(chapter["id"]) != chapterId.ToString(CultureInfo.InvariantCulture)) continue;
            foreach (var key in new[] { "index", "chapterIndex" })
                if (SourceResponseContract.Identity(chapter[key]) is { } raw && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ownIndex)) return ownIndex;
            return index;
        }
        throw new SuwayomiException($"Chapter id {chapterId} not found in manga {mangaId}");
    }

    public Task<OwnedUpstreamResponse> OpenPageImageStreamAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken = default)
        => OpenBinaryStreamAsync(FormattableString.Invariant($"/api/v1/manga/{mangaId}/chapter/{chapterIndex}/page/{pageIndex}"), cancellationToken: cancellationToken);
    public Task<(byte[] Bytes, string ContentType)> FetchPageImageAsync(long mangaId, int chapterIndex, int pageIndex, CancellationToken cancellationToken = default)
        => FetchBinaryAsync(FormattableString.Invariant($"/api/v1/manga/{mangaId}/chapter/{chapterIndex}/page/{pageIndex}"), cancellationToken: cancellationToken);
    public Task<(byte[] Bytes, string ContentType)> FetchMangaThumbnailAsync(long mangaId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => FetchBinaryAsync(FormattableString.Invariant($"/api/v1/manga/{mangaId}/thumbnail"), timeout, cancellationToken: cancellationToken);

    private static JsonObject Operation(string kind, JsonValue id, string query, JsonObject variables, string? sourceId = null)
    {
        var value = new JsonObject { ["kind"] = kind, ["id"] = id, ["query"] = query, ["variables"] = variables.DeepClone() };
        if (sourceId is not null) value["source_id"] = sourceId;
        return value;
    }
}
