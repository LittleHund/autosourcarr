using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Imaging;

namespace AutoSourcarr.Core.Sources;

public sealed class InconclusiveSourceCheckException(string message) : Exception(message);

public static class SourceHealthFailures
{
    public static string Classify(Exception error)
    {
        var kind = SourceFailures.Kind(error);
        if (error is HumanAssistanceRequiredException || kind == "captcha") return "verification";
        if (error is SourceWorkDeferredException || kind == "deferred") return "deferred";
        if (kind == "rate_limit") return "rate_limited";
        if (error is InconclusiveSourceCheckException or TimeoutException or HttpRequestException or ImageAnalysisLimitException
            || kind is "timeout" or "access_denied" or "origin_unavailable") return "inconclusive";
        if (error.Message.Contains("unsupportedoperationexception", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("not implemented", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("buffered-analysis limit", StringComparison.Ordinal)) return "inconclusive";
        return "failed";
    }
}

public sealed class SourceHealthProbe(SuwayomiClient client, BoundedWorkScheduler io, TimeSpan? pace = null)
{
    private readonly TimeSpan _pace = pace ?? TimeSpan.FromSeconds(3);

    public async Task CheckAsync(JsonObject source, JsonArray checks, CancellationToken cancellationToken = default)
    {
        await using var session = client.OpenSession(cancellationToken);
        await CheckAsync(session, source, checks, cancellationToken).ConfigureAwait(false);
    }
    internal async Task CheckAsync(SourceTransportSession session, JsonObject source, JsonArray checks, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        cancellationToken = lifetime.Token;
        var sid = SourceResponseContract.Identity(source["source_id"]) ?? throw new ArgumentException("Source identity is missing", nameof(source));
        async Task<T> Step<T>(string stage, Func<CancellationToken, Task<T>> call)
        {
            if (checks.Count > 0) await Task.Delay(_pace, cancellationToken).ConfigureAwait(false);
            var observation = new JsonObject { ["stage"] = stage, ["state"] = "checking" }; checks.Add(observation);
            using var budget = SourceWorkContext.WithBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(33));
            T value;
            try { value = await call(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested) { session.ThrowIfRetired(); throw new SourceBudgetExceededException(); }
            session.ThrowIfRetired();
            observation["state"] = "passed"; return value;
        }
        var feed = await Step("catalog", token => session.FetchSourceMangaAsync(sid, "POPULAR", cancellationToken: token)).ConfigureAwait(false);
        var works = (JsonArray)feed["mangas"]!;
        if (works.Count == 0) throw new InconclusiveSourceCheckException("Catalog returned no titles to sample");
        if (Enabled(source["supports_latest"]))
        {
            var latest = await Step("latest catalog", token => session.FetchSourceMangaAsync(sid, "LATEST", cancellationToken: token)).ConfigureAwait(false);
            if (((JsonArray)latest["mangas"]!).Count == 0) throw new InconclusiveSourceCheckException("Supported latest catalog returned no titles to verify");
        }
        var work = works[0]!.AsObject(); var title = SourceResponseContract.Text(work["title"]).Trim();
        if (!SourceResponseContract.PositiveId(work["id"], out var mid) || SourceResponseContract.Identity(work["sourceId"]) != sid || title.Length == 0)
            throw new InvalidDataException("Catalog sample identity is invalid");
        var search = await Step("search", token => session.FetchSourceMangaAsync(sid, "SEARCH", queryText: title, cancellationToken: token)).ConfigureAwait(false);
        if (!((JsonArray)search["mangas"]!).OfType<JsonObject>().Any(row => SourceResponseContract.TryLong(row["id"], out var id) && id == mid))
            throw new InconclusiveSourceCheckException("Search did not retrieve the sampled title; check source preferences or search support");
        var data = await Step("metadata/chapters", token => session.FetchMangaAndChaptersAsync(mid, cancellationToken: token)).ConfigureAwait(false);
        var manga = data["manga"]!.AsObject(); var chapters = data["chapters"]!.AsArray();
        if (SourceResponseContract.Identity(manga["sourceId"]) != sid || SourceResponseContract.Text(manga["title"]).Length == 0
            || SourceResponseContract.Text(manga["url"]).Length == 0 || chapters.Count == 0)
            throw new InvalidDataException("Sample metadata, source identity or chapter list is incomplete");
        var chapter = chapters[0]!.AsObject();
        if (SourceResponseContract.Text(chapter["name"]).Length == 0 || SourceResponseContract.Text(chapter["url"]).Length == 0 || !SourceResponseContract.PositiveId(chapter["id"], out var cid))
            throw new InvalidDataException("Sample chapter identity or metadata is incomplete");
        if (!new[] { "description", "author", "artist", "genre" }.Any(key => manga[key] is JsonArray array ? array.Count > 0 : SourceResponseContract.Text(manga[key]).Length > 0))
            throw new InconclusiveSourceCheckException("The sampled title has no descriptive metadata to verify");
        async Task<bool> Image(string path, CancellationToken token)
        {
            var content = await session.FetchSourceBinaryAsync(path, token).ConfigureAwait(false);
            await io.RunAsync(() => { RasterImages.VerifyHealthSample(content.Bytes); return 0; }, token).ConfigureAwait(false);
            return true;
        }
        await Step("cover", token => Image(FormattableString.Invariant($"/api/v1/manga/{mid}/thumbnail"), token)).ConfigureAwait(false);
        var pages = await Step("page list", token => session.FetchChapterPagesAsync(cid, token)).ConfigureAwait(false);
        if (pages.Count == 0 || !Regex.IsMatch(pages[0], FormattableString.Invariant($"^/api/v1/manga/{mid}/chapter/[0-9]+/page/[0-9]+$"), RegexOptions.CultureInvariant))
            throw new InvalidDataException("Sample page list is empty or contains an unexpected reader route");
        await Step("page image", token => Image(pages[0], token)).ConfigureAwait(false);
    }
    private static bool Enabled(JsonNode? value) => SourceResponseContract.Boolean(value, out var enabled) ? enabled : SourceResponseContract.TryLong(value, out var number) && number != 0;
}
