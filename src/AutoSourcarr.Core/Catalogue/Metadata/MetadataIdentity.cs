using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Imports;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Duplicates;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Catalogue.Metadata;

/// <summary>
/// Metadata identity (port-map "Catalogue/Metadata → IdentityResolver", moved from phase 8): looks an entry up at the
/// metadata providers (AniList, MangaUpdates, MangaDex connection clients), accepts a provider record only on strict
/// native evidence (_identity_candidate_score: native source/library/manual titles, never another provider's), and keeps
/// it in identity_links with its titles and cross-links. Provider answers are cached in metadata_query_cache with the
/// released key, so a sweep repeats no network work; every lookup is bounded (concurrency, per-call deadline, entries per
/// sweep) and paced by the clients' per-provider rate limiters. As <see cref="IDuplicateHydration"/> it gives the automatic
/// duplicate sweep the identities it compares (same provider id = same work; different ids = blocked, see
/// <see cref="MetadataIdentityGate"/>).
/// </summary>
public sealed class MetadataIdentityResolver : IDuplicateHydration, IImportIdentitySink, IAsyncDisposable
{
    private const int MaximumPending = 256;
    private static readonly IReadOnlyDictionary<string, (string Label, string Scope)> Specs = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["mangadex"] = ("MangaDex", "edition"), ["anilist"] = ("AniList", "work"), ["mangaupdates"] = ("MangaUpdates", "edition"),
        ["kitsu"] = ("Kitsu", "work"), ["myanimelist"] = ("MyAnimeList (Jikan)", "work"), ["comicvine"] = ("Comic Vine", "edition"),
    };

    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly Func<AutoSourcarrSettings> _settings;
    private readonly Func<IReadOnlyList<IMetadataProviderClient>> _providers;
    private readonly Func<DatabaseSession, long, JsonObject>? _snapshot;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _pendingGate = new();
    private readonly Dictionary<long, Task> _pending = [];

    public MetadataIdentityResolver(SqliteDatabase database, object sourceWriteGate, Func<AutoSourcarrSettings> settings,
        Func<IReadOnlyList<IMetadataProviderClient>> providers, Func<DatabaseSession, long, JsonObject>? snapshot = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate);
        ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(providers);
        _database = database; _gate = sourceWriteGate; _settings = settings; _providers = providers; _snapshot = snapshot; _clock = clock ?? TimeProvider.System;
        var capacity = Math.Clamp(settings().MetadataIdentityConcurrency, 1, 8);
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    /// <summary>After an entry gained or changed identities (choose metadata, drop the duplicate snapshot).</summary>
    public Action<long>? IdentitiesChanged { get; init; }
    /// <summary>add_title (CatalogueTitles.AddTitle): entry, title, language, title type, source key ("metadata:provider").</summary>
    public Action<long, string, string, string, string>? AddTitle { get; init; }
    /// <summary>Thumbnail fingerprinting of the Imaging area for <see cref="FingerprintThumbnailsAsync"/>; null does none.</summary>
    public Func<int, CancellationToken, Task<int>>? Fingerprints { get; init; }
    /// <summary>Background failures of <see cref="Schedule"/> (logged by the host).</summary>
    public Action<long, Exception>? Failed { get; init; }

    // ---- IDuplicateHydration -----------------------------------------------------------------------------------------

    /// <summary>
    /// _hydrate_duplicate_identity_candidates: resolves the shortlisted entries that have no identity yet, at most
    /// max(24, 2 × entries per feed) (≤ 200) per sweep; answers how many gained an identity.
    /// </summary>
    public async Task<int> ResolveIdentitiesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var settings = _settings();
        if (!settings.MetadataIdentityEnabled || entryIds.Count == 0) return 0;
        var cap = Math.Clamp(Math.Max(24, settings.MetadataIdentityEntriesPerFeed * 2), 1, 200);
        var wanted = Read(session => entryIds.Select(id => CatalogueTitles.Canonical(session, id)).Distinct()
            .Where(id => session.Scalar("SELECT 1 FROM identity_links WHERE entry_id=$id LIMIT 1", ("$id", id)) is null
                && session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", id)) is not null)
            .Take(cap).ToArray());
        var gained = 0;
        foreach (var id in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ResolveAsync(id, force: false, cancellationToken).ConfigureAwait(false);
            if (result["matched"] is JsonValue matched && matched.TryGetValue<int>(out var count) && count > 0) gained++;
        }
        return gained;
    }

    public Task<int> FingerprintThumbnailsAsync(int limit, CancellationToken cancellationToken) =>
        Fingerprints?.Invoke(limit, cancellationToken) ?? Task.FromResult(0);

    // ---- background on ingest ----------------------------------------------------------------------------------------

    /// <summary>schedule_entry_identity: one bounded background resolution per entry (deduplicated, at most 256 waiting).</summary>
    public bool Schedule(long entryId, bool force = false)
    {
        if (!_settings().MetadataIdentityEnabled || _stopping.IsCancellationRequested) return false;
        lock (_pendingGate)
        {
            if (_pending.ContainsKey(entryId) || _pending.Count >= MaximumPending) return false;
            var task = Task.Run(async () =>
            {
                try { await ResolveAsync(entryId, force, _stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                catch (Exception error) when (error is not OutOfMemoryException) { Failed?.Invoke(entryId, error); }
                finally { lock (_pendingGate) _pending.Remove(entryId); }
            });
            if (!task.IsCompleted) _pending[entryId] = task;
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_pendingGate) { if (_stopping.IsCancellationRequested) return; _stopping.Cancel(); running = _pending.Values.ToArray(); }
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stopping.Dispose(); _slots.Dispose();
    }

    // ---- resolution --------------------------------------------------------------------------------------------------

    /// <summary>resolve_entry_identity: searches every enabled provider with the best native title first, then the others.</summary>
    public async Task<JsonObject> ResolveAsync(long entryId, bool force, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var id = Read(session => CatalogueTitles.Canonical(session, entryId));
        if (!settings.MetadataIdentityEnabled) return new JsonObject { ["entry_id"] = id, ["enabled"] = false, ["links"] = Links(id) };
        var (queries, native, bootstrap) = Read(session => (QueryTitles(session, id, settings.MetadataIdentityQueryLimit), Native(session, id), BootstrapTitle(session, id)));
        if (native["gallery_payloads"] is JsonArray gallery && gallery.Count != 0)
            return new JsonObject { ["entry_id"] = id, ["enabled"] = true, ["matched"] = 0, ["metadata_basis"] = "source", ["links"] = Links(id),
                ["reason"] = "Gallery entries use their source metadata; provider identities are not inferred for them." };
        if (bootstrap.Length != 0)
        {
            native = new JsonObject { ["titles"] = new JsonArray(JsonValue.Create(bootstrap)), ["author"] = "", ["descriptions"] = new JsonArray() };
            queries = [bootstrap, .. queries.Where(query => query != bootstrap)];
        }
        if (queries.Count == 0) return new JsonObject { ["entry_id"] = id, ["enabled"] = true, ["matched"] = 0, ["links"] = Links(id) };

        var providers = _providers().GroupBy(client => client.Provider, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var unresolved = providers.Select(client => client.Provider).ToHashSet(StringComparer.Ordinal);
        var accepted = new Dictionary<string, (MetadataCandidate Candidate, double Score, string Reason)>(StringComparer.Ordinal);
        var errors = new JsonObject(); var ambiguous = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            if (unresolved.Count == 0) break;
            var batch = providers.Where(client => unresolved.Contains(client.Provider)).ToArray();
            var answers = await Task.WhenAll(batch.Select(client => SearchAsync(client, query, force, cancellationToken))).ConfigureAwait(false);
            foreach (var (provider, rows, error) in answers)
            {
                if (error is not null) { errors[provider] = error; continue; }
                errors.Remove(provider);
                var scored = rows.Select(row => (Candidate: row, Result: CandidateScore(native, row, bootstrap, settings.MetadataIdentityMinScore, settings.MetadataIdentityDescriptionThreshold)))
                    .Where(item => item.Result.Score > 0).ToArray();
                if (scored.Length == 0) continue;
                (MetadataCandidate Candidate, (double Score, string Reason) Result) best;
                if (bootstrap.Length != 0)
                {
                    // A title-only entry proves nothing when one provider names two works with that exact title.
                    var exact = scored.Where(item => item.Result.Reason.StartsWith("bootstrap-exact", StringComparison.Ordinal)).ToArray();
                    if (exact.Select(item => item.Candidate.ExternalId).Distinct(StringComparer.Ordinal).Count() > 1) { ambiguous.Add(provider); continue; }
                    if (exact.Length == 0) continue;
                    best = exact.MaxBy(item => item.Result.Score);
                }
                else best = scored.MaxBy(item => item.Result.Score);
                accepted[provider] = (best.Candidate, best.Result.Score, best.Result.Reason);
                unresolved.Remove(provider);
            }
        }
        if (accepted.Count != 0)
        {
            var titles = new List<(string Title, string Language, string Kind, string Source)>();
            Write(session =>
            {
                foreach (var (_, (candidate, score, reason)) in accepted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (Upsert(session, id, candidate, score, "metadata:" + reason, Stamp()))
                        titles.AddRange(TitleRows(candidate));
                    foreach (var (provider, external) in candidate.Links.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        if (!Specs.ContainsKey(provider) || external.Trim().Length == 0) continue;
                        Upsert(session, id, new MetadataCandidate
                        {
                            Provider = provider, ExternalId = external.Trim(),
                            Raw = new JsonObject { ["crossref_from"] = candidate.Provider, ["crossref_id"] = candidate.ExternalId },
                        }, score, "crossref:" + candidate.Provider, Stamp());
                    }
                }
                return 0;
            });
            if (AddTitle is { } add) foreach (var (title, language, kind, source) in titles) add(id, title, language, kind, source);
            IdentitiesChanged?.Invoke(id);
        }
        return new JsonObject
        {
            ["entry_id"] = id, ["enabled"] = true, ["matched"] = accepted.Count,
            ["providers_checked"] = new JsonArray(providers.Select(client => (JsonNode)JsonValue.Create(client.Provider)).ToArray()),
            ["provider_errors"] = errors, ["ambiguous_bootstrap_providers"] = new JsonArray(ambiguous.Select(name => (JsonNode)JsonValue.Create(name)).ToArray()),
            ["bootstrap_title"] = bootstrap, ["links"] = Links(id),
        };
    }

    /// <summary>
    /// _identity_candidate_score (non-gallery): a provider record is accepted on an automatic title score of at least
    /// max(0.94, min score), or 0.88 plus an agreeing summary; a known author that disagrees refuses it. A title-only entry
    /// (<paramref name="bootstrapTitle"/>) accepts only an exact title.
    /// </summary>
    public static (double Score, string Reason) CandidateScore(JsonObject native, MetadataCandidate candidate, string bootstrapTitle, double minimumScore, double descriptionThreshold)
    {
        ArgumentNullException.ThrowIfNull(native); ArgumentNullException.ThrowIfNull(candidate);
        var candidateTitles = new List<string>();
        if (candidate.Title.Length != 0) candidateTitles.Add(candidate.Title);
        candidateTitles.AddRange(candidate.Titles.Select(row => row.Title).Where(title => title.Trim().Length != 0));
        List<string> entryTitles = bootstrapTitle.Length != 0 ? [bootstrapTitle] : Strings(native["titles"]).ToList();
        var (titleScore, candidateTitle, entryTitle) = TextMatcher.BestAutomaticTitleScore(candidateTitles, entryTitles);
        var description = TextMatcher.BestDescriptionScore(candidate.Description, Strings(native["descriptions"]));
        var threshold = Math.Max(0.94, Math.Clamp(minimumScore, 0, 1));
        var descriptionMinimum = Math.Max(0.70, Math.Min(1, descriptionThreshold));
        var score = titleScore;
        var reason = "title " + Percent(titleScore) + ": " + candidateTitle + " ↔ " + entryTitle;
        var accepted = false;
        if (bootstrapTitle.Length != 0)
        {
            var exact = candidateTitle.Length != 0 && entryTitle.Length != 0 && TextMatcher.NormalizeTitle(candidateTitle) == TextMatcher.NormalizeTitle(entryTitle)
                && TextMatcher.NormalizeTitle(entryTitle) == TextMatcher.NormalizeTitle(bootstrapTitle);
            if (exact) { accepted = true; score = 1; reason = "bootstrap-exact preferred title: " + candidateTitle + " ↔ " + entryTitle; }
        }
        else if (titleScore >= threshold) accepted = true;
        else if (titleScore >= 0.88 && description >= descriptionMinimum)
        {
            accepted = true; score = Math.Max(titleScore, Math.Min(0.995, 0.90 + description * 0.095));
            reason = "title " + Percent(titleScore) + " + summary " + Percent(description);
        }
        if (!accepted) return (0, reason);
        var entryAuthor = Str(native["author"]);
        var candidateAuthor = string.Join(", ", candidate.Authors);
        if (entryAuthor.Length != 0 && candidateAuthor.Length != 0)
        {
            var authorScore = TextMatcher.NameSimilarity(entryAuthor, candidateAuthor);
            if (authorScore < 0.30) return (0, reason + " - author conflict");
            if (authorScore >= 0.90) { score = Math.Min(1, score + 0.01); reason += " + author"; }
        }
        return (Math.Clamp(score, 0, 1), reason);
    }

    // ---- imported metadata (IImportIdentitySink) ----------------------------------------------------------------------

    /// <summary>
    /// _apply_import_metadata_to_entry: a provider record the import pipeline accepted for a backup row (MetadataCandidate
    /// JSON, confidence at least 0.90) becomes an identity link of the resolved entry, with its cross-links and titles, under
    /// the same upsert rules as a lookup (a locked link is never replaced; another id only with a higher confidence).
    /// Answers whether the provider's own link was written.
    /// </summary>
    public bool Apply(long entryId, JsonObject candidate, double confidence, string reason)
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(reason);
        if (FromJson(candidate) is not { } record) return false;
        var score = Math.Clamp(confidence, 0, 1);
        var titles = new List<(string Title, string Language, string Kind, string Source)>();
        var (id, written) = Write(session =>
        {
            var canonical = CatalogueTitles.Canonical(session, entryId);
            if (session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", canonical)) is null) return (Id: canonical, Written: false);
            var stamp = Stamp();
            var main = Upsert(session, canonical, record, score, reason.Length != 0 ? reason : "import-metadata", stamp);
            if (main) titles.AddRange(TitleRows(record));
            foreach (var (provider, external) in record.Links.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!Specs.ContainsKey(provider) || external.Trim().Length == 0) continue;
                Upsert(session, canonical, new MetadataCandidate
                {
                    Provider = provider, ExternalId = external.Trim(),
                    Raw = new JsonObject { ["crossref_from"] = record.Provider, ["crossref_id"] = record.ExternalId },
                }, score, "crossref:" + record.Provider, stamp);
            }
            return (Id: canonical, Written: main);
        });
        if (!written) return false;
        if (AddTitle is { } add) foreach (var (title, language, kind, source) in titles) add(id, title, language, kind, source);
        IdentitiesChanged?.Invoke(id);
        return true;
    }

    // ---- provider search with the released cache ---------------------------------------------------------------------

    private async Task<(string Provider, IReadOnlyList<MetadataCandidate> Rows, string? Error)> SearchAsync(IMetadataProviderClient client, string query,
        bool force, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var key = QueryKey(query, settings.MetadataIdentityIncludeAdult, settings.MetadataIdentityResultsPerProvider);
        if (key.Length == 0) return (client.Provider, Array.Empty<MetadataCandidate>(), null);
        if (!force && CacheRead(client.Provider, key) is { } cached) return (client.Provider, cached, null);
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.MetadataIdentityOverallTimeout)));
            IReadOnlyList<MetadataCandidate> rows;
            try { rows = await client.SearchAsync(query, Math.Max(1, settings.MetadataIdentityResultsPerProvider), deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (client.Provider, Array.Empty<MetadataCandidate>(), "timed out"); }
            catch (Exception error) when (error is MetadataProviderException or HttpRequestException or JsonException or InvalidDataException)
            {
                // A failure is not an empty answer: keep the previous cache untouched (released behaviour).
                return (client.Provider, Array.Empty<MetadataCandidate>(), error.Message.Length != 0 ? error.Message : error.GetType().Name);
            }
            if (!settings.MetadataIdentityIncludeAdult) rows = rows.Where(row => !row.Adult).ToArray();
            CacheWrite(client.Provider, key, query, rows);
            return (client.Provider, rows, null);
        }
        finally { _slots.Release(); }
    }

    /// <summary>_metadata_query_key: "v85:" + sha256 of json.dumps([NFKC-casefolded query, include adult, results per provider]).</summary>
    public static string QueryKey(string query, bool includeAdult, int resultsPerProvider)
    {
        var text = string.Join(' ', UnicodeCaseFold.Fold((query ?? "").Normalize(NormalizationForm.FormKC)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return "";
        var policy = "[" + PythonString(text) + ", " + (includeAdult ? "true" : "false") + ", " + Math.Max(1, resultsPerProvider).ToString(CultureInfo.InvariantCulture) + "]";
        return "v85:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(policy)));
    }

    private IReadOnlyList<MetadataCandidate>? CacheRead(string provider, string key)
    {
        var row = Read(session => session.Query("SELECT results_json,updated_at FROM metadata_query_cache WHERE provider=$provider AND query_key=$key",
            ("$provider", provider), ("$key", key)).SingleOrDefault());
        if (row is null || !DateTimeOffset.TryParse(row.Text("updated_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated)) return null;
        JsonArray? results;
        try { results = JsonNode.Parse(row.Text("results_json", "[]")) as JsonArray; }
        catch (JsonException) { return null; }
        if (results is null) return null;
        // Positive hits keep the long TTL; an empty answer is retried after at most 30 minutes.
        var ttl = Math.Max(60, _settings().MetadataIdentityCacheTtlSeconds);
        if (results.Count == 0) ttl = Math.Min(ttl, 1800);
        if ((_clock.GetUtcNow() - updated).TotalSeconds > ttl) return null;
        return results.OfType<JsonObject>().Select(FromJson).Where(candidate => candidate is not null).Select(candidate => candidate!).ToArray();
    }

    private void CacheWrite(string provider, string key, string query, IReadOnlyList<MetadataCandidate> rows)
    {
        var json = new JsonArray(rows.Select(row => (JsonNode)ToCacheJson(row)).ToArray()).ToJsonString();
        Write(session => session.Execute("""
            INSERT INTO metadata_query_cache(provider,query_key,query_text,results_json,updated_at) VALUES($provider,$key,$query,$results,$stamp)
            ON CONFLICT(provider,query_key) DO UPDATE SET query_text=excluded.query_text,results_json=excluded.results_json,updated_at=excluded.updated_at
            """, ("$provider", provider), ("$key", key), ("$query", query), ("$results", json), ("$stamp", Stamp())));
    }

    /// <summary>The released cache row shape (author/artist as text, genres as a list) plus the C# lists.</summary>
    internal static JsonObject ToCacheJson(MetadataCandidate candidate)
    {
        var json = candidate.ToJson();
        json["author"] = string.Join(", ", candidate.Authors); json["artist"] = string.Join(", ", candidate.Artists);
        return json;
    }

    internal static MetadataCandidate? FromJson(JsonObject json)
    {
        var provider = Str(json["provider"]); var external = Str(json["external_id"]);
        if (provider.Length == 0 || external.Length == 0) return null;
        static IReadOnlyList<string> Names(JsonNode? list, JsonNode? text) =>
            list is JsonArray ? Strings(list).ToArray() : Str(text).Split(',').Select(part => part.Trim()).Where(part => part.Length != 0).ToArray();
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json["links"] is JsonObject map) foreach (var (key, value) in map) if (Str(value) is { Length: > 0 } link) links[key] = link;
        return new MetadataCandidate
        {
            Provider = provider, ExternalId = external, Url = Str(json["url"]), Title = Str(json["title"]),
            Titles = json["titles"] is JsonArray titles ? titles.OfType<JsonObject>().Select(row => new MetadataTitle(Str(row["title"]), Str(row["language"]), Str(row["type"])))
                .Where(row => row.Title.Length != 0).ToArray() : [],
            Description = Str(json["description"]), CoverUrl = Str(json["cover_url"]), Status = Str(json["status"]) is { Length: > 0 } status ? status : "unknown",
            Authors = Names(json["authors"], json["author"]), Artists = Names(json["artists"], json["artist"]), Genres = Strings(json["genres"]).ToArray(),
            MediaType = Str(json["media_type"]), Country = Str(json["country"]), Year = MetadataText.Year(json["year"]),
            Adult = json["adult"] is JsonValue adult && adult.TryGetValue<bool>(out var flag) && flag, Links = links,
        };
    }

    // ---- identity_links ----------------------------------------------------------------------------------------------

    /// <summary>
    /// _upsert_identity_link: a locked link is never replaced by an automatic one, and a different provider id replaces an
    /// established link only with a higher confidence. Answers whether the row was written.
    /// </summary>
    internal static bool Upsert(DatabaseSession session, long entryId, MetadataCandidate candidate, double confidence, string matchType, string stamp, bool locked = false)
    {
        var provider = candidate.Provider.Trim().ToLowerInvariant(); var external = candidate.ExternalId.Trim();
        if (provider.Length == 0 || external.Length == 0) return false;
        var existing = session.Query("SELECT external_id,confidence,locked FROM identity_links WHERE entry_id=$id AND provider=$provider",
            ("$id", entryId), ("$provider", provider)).SingleOrDefault();
        if (existing is not null && existing.Flag("locked") && !locked) return false;
        if (existing is not null && existing.Text("external_id") != external && existing.Number("confidence") >= confidence) return false;
        var (label, scope) = Specs.TryGetValue(provider, out var spec) ? spec : (provider, "work");
        var raw = ToCacheJson(candidate);
        if (candidate.Raw is not null) raw["raw"] = candidate.Raw.DeepClone();
        session.Execute("""
            INSERT INTO identity_links(entry_id,provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked,
                title,description,thumbnail_url,status,author,artist,genre,media_type,country,year,adult,metadata_json,last_checked)
            VALUES($id,$provider,$name,$external,$url,$scope,$confidence,$type,$locked,$title,$description,$cover,$status,$author,$artist,$genre,$media,$country,$year,$adult,$json,$stamp)
            ON CONFLICT(entry_id,provider) DO UPDATE SET
              provider_name=excluded.provider_name, external_id=excluded.external_id,
              external_url=CASE WHEN excluded.external_url<>'' THEN excluded.external_url ELSE identity_links.external_url END,
              identity_scope=excluded.identity_scope, confidence=MAX(identity_links.confidence,excluded.confidence),
              match_type=CASE WHEN identity_links.locked=1 THEN identity_links.match_type ELSE excluded.match_type END,
              locked=MAX(identity_links.locked,excluded.locked),
              title=CASE WHEN excluded.title<>'' THEN excluded.title ELSE identity_links.title END,
              description=CASE WHEN length(excluded.description)>=length(identity_links.description) THEN excluded.description ELSE identity_links.description END,
              thumbnail_url=CASE WHEN excluded.thumbnail_url<>'' THEN excluded.thumbnail_url ELSE identity_links.thumbnail_url END,
              status=CASE WHEN excluded.status<>0 THEN excluded.status ELSE identity_links.status END,
              author=CASE WHEN excluded.author<>'' THEN excluded.author ELSE identity_links.author END,
              artist=CASE WHEN excluded.artist<>'' THEN excluded.artist ELSE identity_links.artist END,
              genre=CASE WHEN excluded.genre<>'' THEN excluded.genre ELSE identity_links.genre END,
              media_type=CASE WHEN excluded.media_type<>'' THEN excluded.media_type ELSE identity_links.media_type END,
              country=CASE WHEN excluded.country<>'' THEN excluded.country ELSE identity_links.country END,
              year=COALESCE(excluded.year,identity_links.year), adult=MAX(identity_links.adult,excluded.adult),
              metadata_json=CASE WHEN length(excluded.metadata_json)>length(identity_links.metadata_json) THEN excluded.metadata_json ELSE identity_links.metadata_json END,
              last_checked=excluded.last_checked
            """, ("$id", entryId), ("$provider", provider), ("$name", label), ("$external", external), ("$url", candidate.Url), ("$scope", scope),
            ("$confidence", confidence), ("$type", matchType), ("$locked", locked ? 1 : 0), ("$title", candidate.Title),
            ("$description", TextMatcher.CleanSummary(candidate.Description)), ("$cover", candidate.CoverUrl), ("$status", MetadataFields.ProviderStatus(candidate.Status)),
            ("$author", string.Join(", ", candidate.Authors)), ("$artist", string.Join(", ", candidate.Artists)), ("$genre", string.Join(", ", candidate.Genres)),
            ("$media", candidate.MediaType), ("$country", candidate.Country), ("$year", candidate.Year), ("$adult", candidate.Adult ? 1 : 0),
            ("$json", raw.ToJsonString()), ("$stamp", stamp));
        return true;
    }

    private static IEnumerable<(string, string, string, string)> TitleRows(MetadataCandidate candidate)
    {
        var source = "metadata:" + candidate.Provider;
        for (var index = 0; index < candidate.Titles.Count; index++)
        {
            var row = candidate.Titles[index];
            if (row.Title.Trim().Length == 0) continue;
            yield return (row.Title.Trim(), row.Language, index == 0 || row.Kind == "primary" ? "identity-primary" : "identity-alias", source);
        }
        if (candidate.Title.Trim().Length != 0) yield return (candidate.Title.Trim(), "", "identity-primary", source);
    }

    /// <summary>_identity_links: the entry's provider identities, MangaDex, AniList, MangaUpdates, ... first.</summary>
    public JsonArray Links(long entryId) => Read(session => new JsonArray(session.Query("""
        SELECT provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked,title,last_checked FROM identity_links WHERE entry_id=$id
        ORDER BY CASE provider WHEN 'mangadex' THEN 10 WHEN 'anilist' THEN 20 WHEN 'mangaupdates' THEN 30 WHEN 'kitsu' THEN 40
                 WHEN 'myanimelist' THEN 50 WHEN 'comicvine' THEN 60 ELSE 999 END, provider
        """, ("$id", entryId)).Select(row => (JsonNode)new JsonObject
    {
        ["provider"] = row.Text("provider"), ["provider_name"] = row.Text("provider_name"), ["external_id"] = row.Text("external_id"),
        ["external_url"] = row.Text("external_url"), ["identity_scope"] = row.Text("identity_scope"), ["confidence"] = row.Number("confidence"),
        ["match_type"] = row.Text("match_type"), ["locked"] = row.Flag("locked"), ["title"] = row.Text("title"), ["last_checked"] = row.Text("last_checked"),
    }).ToArray()));

    // ---- native evidence ---------------------------------------------------------------------------------------------

    /// <summary>_identity_query_titles: main title, native titles (not translations), then source titles; each once.</summary>
    internal static IReadOnlyList<string> QueryTitles(DatabaseSession session, long entryId, int limit)
    {
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value)
        {
            var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); var norm = TextMatcher.NormalizeTitle(text);
            if (text.Length == 0 || norm.Length == 0 || seen.Contains(norm) || !TextMatcher.IsMatchEvidenceTitle(text, "", primary: result.Count == 0)) return;
            seen.Add(norm); result.Add(text);
        }
        if (session.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) is string preferred) Add(preferred);
        foreach (var row in session.Query("""
            SELECT title,title_type FROM titles WHERE entry_id=$id ORDER BY CASE title_type WHEN 'main-library' THEN 0 WHEN 'manual' THEN 1
              WHEN 'identity-primary' THEN 2 WHEN 'matched-alias' THEN 3 ELSE 9 END, id
            """, ("$id", entryId)))
            if (!row.Text("title_type").StartsWith("translated-", StringComparison.Ordinal)) Add(row.Text("title"));
        foreach (var row in session.Query("SELECT source_title FROM source_matches WHERE entry_id=$id AND active=1 UNION ALL SELECT source_title FROM source_variants WHERE entry_id=$id AND active=1", ("$id", entryId)))
            Add(row.Text("source_title"));
        return result.Take(Math.Max(1, limit)).ToArray();
    }

    /// <summary>_first_source_bootstrap_title: a source-less entry's main-library / manual (or main) title.</summary>
    internal static string BootstrapTitle(DatabaseSession session, long entryId)
    {
        if (session.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$id AND active=1 UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND active=1 LIMIT 1", ("$id", entryId)) is not null)
            return "";
        var native = session.Query("SELECT title FROM titles WHERE entry_id=$id AND title_type IN ('main-library','manual') ORDER BY CASE title_type WHEN 'main-library' THEN 0 ELSE 1 END,id", ("$id", entryId))
            .Select(row => row.Text("title")).ToList();
        if (native.Count == 0 && session.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) is string preferred) native.Add(preferred);
        var first = native.FirstOrDefault(text => TextMatcher.IsMatchEvidenceTitle(text, "", primary: true));
        return first is null ? "" : string.Join(' ', first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The duplicate-gate snapshot (NativeCatalogueEvidence) or, without one, the native titles, summaries and author.</summary>
    private JsonObject Native(DatabaseSession session, long entryId)
    {
        if (_snapshot is { } snapshot) return snapshot(session, entryId);
        var titles = session.Query("""
            SELECT source_title title FROM source_matches WHERE entry_id=$id AND active=1
            UNION ALL SELECT source_title FROM source_variants WHERE entry_id=$id AND active=1
            UNION ALL SELECT title FROM titles WHERE entry_id=$id AND title_type IN ('main-library','manual')
            """, ("$id", entryId)).Select(row => row.Text("title")).Where(title => TextMatcher.IsMatchEvidenceTitle(title)).Distinct(StringComparer.Ordinal);
        var descriptions = session.Query("SELECT description FROM source_matches WHERE entry_id=$id AND active=1 AND description<>'' UNION ALL SELECT description FROM source_variants WHERE entry_id=$id AND active=1 AND description<>''",
            ("$id", entryId)).Select(row => row.Text("description"));
        return new JsonObject
        {
            ["titles"] = new JsonArray(titles.Select(title => (JsonNode)JsonValue.Create(title)).ToArray()),
            ["descriptions"] = new JsonArray(descriptions.Select(text => (JsonNode)JsonValue.Create(text)).ToArray()),
            ["author"] = session.Scalar("SELECT author FROM entries WHERE id=$id", ("$id", entryId)) as string ?? "",
        };
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static string PythonString(string value)
    {
        // json.dumps(ensure_ascii=False): only quotes, backslashes and control characters are escaped.
        var output = new StringBuilder("\"");
        foreach (var character in value)
            output.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        return output.Append('"').ToString();
    }

    private static string Percent(double value) => Math.Round(value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    private static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text
        : node is JsonValue number && number.GetValueKind() == JsonValueKind.Number ? number.ToJsonString() : "";
    private static IEnumerable<string> Strings(JsonNode? node) => node is JsonArray array ? array.Select(Str).Where(text => text.Length != 0) : [];
    private T Read<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Read(DatabaseFile.Catalog, operation); }
    private T Write<T>(Func<DatabaseSession, T> operation) { lock (_gate) return _database.Write(DatabaseFile.Catalog, operation); }
    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);
}

/// <summary>
/// Metadata identity as matching evidence (moved from phase 8): put in front of the automatic duplicate gate. Two entries
/// that both carry a confident identity (≥ 0.94, NativeCatalogueEvidence "identity_details") of the same provider but no
/// common id there are different works, whatever the titles say; a shared id stays the inner gate's strong evidence
/// (TextAutomaticMatchGate: trusted shared identity, or shared identity + distinctive exact title). It never allows a pair
/// the inner gate refused.
/// </summary>
public sealed class MetadataIdentityGate(IAutomaticMatchGate inner) : IAutomaticMatchGate
{
    public AutomaticMatchDecision Decide(JsonObject left, JsonObject right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var decision = inner.Decide(left, right);
        if (!decision.Eligible) return decision;
        var conflict = Conflicts(left, right).FirstOrDefault();
        if (conflict.Provider is null) return decision;
        var evidence = (JsonObject)decision.Evidence.DeepClone();
        var reason = "different " + conflict.Provider + " identities (" + conflict.Left + " / " + conflict.Right + ")";
        evidence["identity_conflict"] = new JsonObject { ["provider"] = conflict.Provider, ["left"] = conflict.Left, ["right"] = conflict.Right };
        return AutomaticMatchDecision.No(reason, evidence);
    }

    /// <summary>Providers both snapshots name with disjoint ids (the smallest id of each side is reported).</summary>
    public static IEnumerable<(string? Provider, string Left, string Right)> Conflicts(JsonObject left, JsonObject right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var a = Ids(left); var b = Ids(right);
        foreach (var provider in a.Keys.Intersect(b.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (!a[provider].Overlaps(b[provider]))
                yield return (provider, a[provider].Min(StringComparer.Ordinal) ?? "", b[provider].Min(StringComparer.Ordinal) ?? "");
    }

    private static Dictionary<string, HashSet<string>> Ids(JsonObject snapshot)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string provider, string id)
        {
            provider = provider.Trim().ToLowerInvariant(); id = id.Trim();
            if (provider.Length == 0 || id.Length == 0) return;
            if (!result.TryGetValue(provider, out var set)) result[provider] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(id);
        }
        if (snapshot["identity_details"] is JsonArray details)
            foreach (var row in details.OfType<JsonObject>()) Add(Text(row["provider"]), Text(row["external_id"]));
        else if (snapshot["identities"] is JsonArray pairs)
            foreach (var pair in pairs.OfType<JsonArray>()) if (pair.Count == 2) Add(Text(pair[0]), Text(pair[1]));
        return result;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";
}
