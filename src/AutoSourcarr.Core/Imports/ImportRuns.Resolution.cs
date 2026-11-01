using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Connections.Types;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Ingest;

namespace AutoSourcarr.Core.Imports;

/// <summary>The strict import gate's answer for one candidate entry (_import_candidate_evidence_base).</summary>
public sealed record ImportEvidence
{
    public long EntryId { get; init; }
    public bool Eligible { get; init; }
    public double Score { get; init; }
    public string Reason { get; init; } = "";
    public double TitleScore { get; init; }
    public double CompatibleTitleScore { get; init; }
    public double SummarySimilarity { get; init; }
    public double AuthorSimilarity { get; init; }
    public IReadOnlyList<string> SharedIdentities { get; init; } = [];
    public bool ExactReference { get; init; }
    public double RankScore { get; init; }
}

/// <summary>Where a backup row resolved to: an entry (automatic acceptance), a reference entry for review, and why.</summary>
public sealed record ImportResolution(long? EntryId, long? ReferenceEntryId, string Reason);

public sealed partial class ImportRuns
{
    private static readonly TimeSpan SourceCooldown = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Error)> _sourceFailures = new(StringComparer.Ordinal);

    // ---- evidence of the backup row --------------------------------------------------------------------------------

    private static JsonObject Bundle(JsonObject item) => item[MetadataKey] as JsonObject ?? new JsonObject();

    private static IEnumerable<string> Texts(JsonNode? node) => node is JsonArray array
        ? array.Select(value => value is JsonValue text && text.TryGetValue<string>(out var s) ? s : "").Where(value => value.Length > 0)
        : [];

    /// <summary>_import_evidence_titles: the backup title, an English title, description aliases and accepted metadata titles.</summary>
    internal static List<string> EvidenceTitles(JsonObject item, string englishTitle = "", bool withMetadata = true)
    {
        var values = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value, bool primary = false)
        {
            var text = ImportQueries.Collapse(value); var key = TextMatcher.NormalizeTitle(text);
            if (text.Length == 0 || key.Length == 0 || seen.Contains(key) || !TextMatcher.IsMatchEvidenceTitle(text, "", primary)) return;
            seen.Add(key); values.Add(text);
        }
        Add(Str(item, "title"), primary: true);
        Add(englishTitle);
        foreach (var alias in TextMatcher.ExtractAltTitles(Str(item, "description"))) Add(alias);
        if (withMetadata) foreach (var title in Texts(Bundle(item)["titles"])) Add(title);
        return values;
    }

    /// <summary>_import_evidence_descriptions.</summary>
    internal static List<string> EvidenceDescriptions(JsonObject item)
    {
        var values = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in new[] { Str(item, "description") }.Concat(Texts(Bundle(item)["descriptions"])))
        {
            var text = TextMatcher.CleanSummary(value); var key = TextMatcher.NormalizeDescription(text);
            if (text.Length > 0 && key.Length > 0 && seen.Add(key)) values.Add(text);
        }
        return values;
    }

    /// <summary>_import_evidence_identities: accepted provider identities (confidence ≥ 0.90) as "provider:id".</summary>
    internal static HashSet<string> EvidenceIdentities(JsonObject item)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        if (Bundle(item)["identities"] is not JsonArray rows) return output;
        foreach (var row in rows.OfType<JsonObject>())
        {
            var provider = Str(row, "provider").Trim().ToLowerInvariant(); var external = Str(row, "external_id").Trim();
            var confidence = row["confidence"] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
            if (provider.Length > 0 && external.Length > 0 && confidence >= .90) output.Add(provider + ":" + external);
        }
        return output;
    }

    private sealed record EntryFacts(long Id, List<string> Titles, IReadOnlyList<string> Descriptions, string Author, string ColorMode, string MediaMode,
        HashSet<string> Identities);

    private EntryFacts Facts(long entryId)
    {
        var descriptions = _ingest.EntryDescriptions(entryId);
        return Read(c =>
        {
            var id = CatalogueTitles.Canonical(c, entryId);
            var entry = c.Query("SELECT preferred_title,author,artist,color_mode,media_mode FROM entries WHERE id=$id", ("$id", id)).FirstOrDefault();
            var titles = new List<string>();
            if (entry is not null) titles.Add(entry.Text("preferred_title"));
            titles.AddRange(c.QueryAs<string>("SELECT title FROM titles WHERE entry_id=@id", new { id }));
            titles.AddRange(c.QueryAs<string>("SELECT source_title FROM source_matches WHERE entry_id=@id AND active=1 UNION ALL SELECT source_title FROM source_variants WHERE entry_id=@id AND active=1", new { id }));
            titles.AddRange(c.QueryAs<string>("SELECT title FROM identity_links WHERE entry_id=@id AND confidence>=0.90 AND title<>''", new { id }));
            var identities = c.Query("SELECT provider,external_id FROM identity_links WHERE entry_id=$id AND confidence>=0.90", ("$id", id))
                .Where(row => row.Text("provider").Length > 0 && row.Text("external_id").Length > 0)
                .Select(row => row.Text("provider").ToLowerInvariant() + ":" + row.Text("external_id")).ToHashSet(StringComparer.Ordinal);
            var author = entry is null ? "" : entry.Text("author").Trim().Length > 0 ? entry.Text("author").Trim() : entry.Text("artist").Trim();
            return new EntryFacts(id, titles.Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.Ordinal).ToList(), descriptions, author,
                entry?.Text("color_mode") ?? "unknown", entry?.Text("media_mode") ?? "unknown", identities);
        });
    }

    private static string ImportMedia(JsonObject item)
    {
        var title = TextMatcher.TitleMediaVariant(Str(item, "title"));
        if (title != "unknown") return title;
        var genres = Texts(item["genre"]).Select(genre => genre.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var webtoon = genres.Overlaps(["webtoon", "long strip"]);
        var manga = genres.Contains("manga");
        return webtoon == manga ? "unknown" : webtoon ? "webtoon" : "manga";
    }

    /// <summary>A matching rule of the backup's source that refuses this entry (_entry_matching_policy), or null.</summary>
    private string? PolicyBlock(JsonObject item, long entryId)
    {
        var sourceId = CurrentSourceId(item);
        var manga = new JsonObject
        {
            ["title"] = Str(item, "title"), ["description"] = Str(item, "description"), ["author"] = Str(item, "author"), ["artist"] = Str(item, "artist"),
            ["genre"] = item["genre"]?.DeepClone(), ["url"] = Str(item, "url"),
        };
        var decision = _ingest.WithRead(session => _ingest.SourceMatchEvidence(session, entryId, manga, sourceId));
        return decision.Blockers.FirstOrDefault(blocker => blocker.Kind == "policy") is { } blocked ? blocked.Detail : null;
    }

    /// <summary>
    /// _import_candidate_evidence: the strict automatic import gate over the original backup and accepted metadata evidence.
    /// Rejected works, colour/monochrome and manga/webtoon conflicts and matching rules refuse; a shared provider identity,
    /// a full title at the floor, or a compatible title with a matching summary admit.
    /// </summary>
    internal ImportEvidence CandidateEvidence(JsonObject item, long entryId, string englishTitle = "", long? referenceEntryId = null)
    {
        var facts = Facts(entryId);
        if (IsRejected(item, facts.Id)) return new ImportEvidence { EntryId = facts.Id, Reason = "previously rejected import match" };
        var importTitles = EvidenceTitles(item, englishTitle);
        var (strict, matchedImport, matchedEntry) = TextMatcher.BestAutomaticTitleScore(importTitles, facts.Titles);
        var loose = importTitles.SelectMany(left => facts.Titles.Select(right => TextMatcher.TitleSimilarity(left, right))).DefaultIfEmpty(0).Max();
        var descriptions = EvidenceDescriptions(item);
        var summary = descriptions.SelectMany(left => facts.Descriptions.Select(right => TextMatcher.DescriptionSimilarity(left, right))).DefaultIfEmpty(0).Max();
        var shared = EvidenceIdentities(item).Intersect(facts.Identities).Order(StringComparer.Ordinal).ToArray();

        var importColor = importTitles.Select(TextMatcher.TitleColorVariant).FirstOrDefault(value => value != "unknown") ?? "unknown";
        if (importColor is "colored" or "monochrome" && facts.ColorMode is "colored" or "monochrome" && importColor != facts.ColorMode)
            return new ImportEvidence { EntryId = facts.Id, Reason = "colored/monochrome edition conflict" };
        var importMedia = ImportMedia(item);
        if (importMedia is "manga" or "webtoon" && facts.MediaMode is "manga" or "webtoon" && importMedia != facts.MediaMode)
            return new ImportEvidence { EntryId = facts.Id, Reason = "manga/webtoon edition conflict" };
        if (PolicyBlock(item, facts.Id) is { } policy) return new ImportEvidence { EntryId = facts.Id, Reason = "matching rule: " + policy };

        var backupAuthor = Str(item, "author").Trim().Length > 0 ? Str(item, "author").Trim() : Str(item, "artist").Trim();
        var author = backupAuthor.Length > 0 && facts.Author.Length > 0 ? TextMatcher.NameSimilarity(backupAuthor, facts.Author) : 0;
        var authorConflict = backupAuthor.Length > 0 && facts.Author.Length > 0 && author < .30;
        var exactReference = referenceEntryId is { } reference && _ingest.CanonicalEntryId(reference) == facts.Id;
        var floor = Options().Floor;
        var result = new ImportEvidence
        {
            EntryId = facts.Id, Score = Math.Max(strict, summary), Reason = "insufficient independent evidence", TitleScore = strict,
            CompatibleTitleScore = loose, SummarySimilarity = summary, AuthorSimilarity = author, SharedIdentities = shared, ExactReference = exactReference,
        };
        if (authorConflict && shared.Length == 0 && strict < floor && summary < .90)
            return result with { Score = 0, Reason = "author conflict with insufficient work-level evidence" };
        if (shared.Length > 0) return result with { Eligible = true, Score = 1, Reason = "shared " + shared[0].Replace(":", " identity ", StringComparison.Ordinal) };
        if (strict >= floor) return result with { Eligible = true, Score = strict, Reason = "full/alternate title " + Percent(strict) + (matchedImport.Length > 0 ? " via " + matchedImport + " ↔ " + matchedEntry : "") };
        if (loose >= .94 && summary >= .80)
            return result with { Eligible = true, Score = Math.Max(loose, Math.Min(.999, .90 + summary * .099)), Reason = "compatible full title " + Percent(loose) + " + summary " + Percent(summary) };
        if (strict >= .80 && summary >= .90)
            return result with { Eligible = true, Score = Math.Max(strict, Math.Min(.999, .90 + summary * .099)), Reason = "title " + Percent(strict) + " + summary " + Percent(summary) };
        if (summary >= .94 && loose >= .75)
            return result with { Eligible = true, Score = Math.Max(summary, loose), Reason = "summary " + Percent(summary) + " + compatible title " + Percent(loose) };
        if (exactReference && loose >= .90 && summary >= .85)
            return result with { Eligible = true, Score = Math.Max(loose, summary), Reason = "backup-source reference + title/summary " + Percent(loose) + "/" + Percent(summary) };
        return result;
    }

    private static string Percent(double value) => Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) + "%";

    // ---- candidates ------------------------------------------------------------------------------------------------

    /// <summary>
    /// _backup_candidate_ranked: local review suggestions from titles, metadata identities, the source reference and the
    /// synopsis, scored for review (a lower floor for a typed search). A review score is never an acceptance.
    /// </summary>
    internal List<(double Score, long EntryId)> RankedCandidates(JsonObject item, string query = "", int limit = 12, string englishTitle = "",
        long? referenceEntryId = null, string referenceDescription = "")
    {
        var explicitQuery = (query ?? "").Trim();
        var bases = new List<string>();
        void AddBase(string value)
        {
            value = value.Trim();
            if (value.Length > 0 && bases.All(existing => TextMatcher.NormalizeTitle(existing) != TextMatcher.NormalizeTitle(value))) bases.Add(value);
        }
        AddBase(explicitQuery);
        foreach (var title in EvidenceTitles(item, englishTitle)) AddBase(title);
        if (explicitQuery.Length > 0) foreach (var fallback in ImportQueries.FallbackTitleQueries(explicitQuery)) AddBase(fallback);
        long? reference = referenceEntryId is { } referenceId && _ingest.EntryExists(referenceId) ? _ingest.CanonicalEntryId(referenceId) : null;
        if (reference is { } known) foreach (var title in Facts(known).Titles) AddBase(title);
        if (bases.Count == 0) return [];

        var ids = new List<long>();
        if (reference is { } first) ids.Add(first);
        var identities = EvidenceIdentities(item);
        if (identities.Count > 0)
            ids.AddRange(Read(c => c.Query("SELECT entry_id,provider,external_id FROM identity_links WHERE confidence>=0.90")
                .Where(row => identities.Contains(row.Text("provider").ToLowerInvariant() + ":" + row.Text("external_id")))
                .Select(row => row.Integer("entry_id")).ToArray()));
        var sourceId = CurrentSourceId(item);
        var fanout = Math.Max(24, limit * 4);
        foreach (var value in bases.Take(5))
        {
            ids.AddRange(ExactTitleCandidates(value, sourceId));
            ids.AddRange(_ingest.ExactSearchEntryIds(value, 100));
            ids.AddRange(_ingest.SearchEntryIds(value, fanout));
        }
        var descriptions = EvidenceDescriptions(item);
        var referenceClean = TextMatcher.CleanSummary(referenceDescription);
        if (referenceClean.Length > 0 && descriptions.All(text => TextMatcher.NormalizeDescription(text) != TextMatcher.NormalizeDescription(referenceClean)))
            descriptions.Add(referenceClean);
        foreach (var description in descriptions.Take(3)) ids.AddRange(DescriptionCandidates(description, Math.Max(30, limit * 5)));
        var unique = FilterRejected(item, ids).Take(120).ToList();
        if (unique.Count == 0) return [];

        var (titles, entryDescriptions, authors) = Read(c =>
        {
            var titleMap = unique.ToDictionary(id => id, _ => new List<string>());
            var descriptionMap = unique.ToDictionary(id => id, _ => new List<string>());
            var authorMap = new Dictionary<long, string>();
            var json = JsonSerializer.Serialize(unique);
            foreach (var row in c.Query("SELECT id,preferred_title,description,author,artist FROM entries WHERE id IN (SELECT value FROM json_each($ids))", ("$ids", json)))
            {
                var id = row.Integer("id");
                titleMap[id].Add(row.Text("preferred_title"));
                if (row.Text("description").Length > 0) descriptionMap[id].Add(row.Text("description"));
                authorMap[id] = row.Text("author").Length > 0 ? row.Text("author") : row.Text("artist");
            }
            foreach (var row in c.Query("SELECT entry_id,title FROM titles WHERE entry_id IN (SELECT value FROM json_each($ids))", ("$ids", json)))
                titleMap[row.Integer("entry_id")].Add(row.Text("title"));
            foreach (var row in c.Query("""
                SELECT entry_id,source_title title,description FROM source_matches WHERE active=1 AND entry_id IN (SELECT value FROM json_each($ids))
                UNION ALL SELECT entry_id,source_title,description FROM source_variants WHERE active=1 AND entry_id IN (SELECT value FROM json_each($ids))
                UNION ALL SELECT entry_id,title,description FROM identity_links WHERE confidence>=0.90 AND entry_id IN (SELECT value FROM json_each($ids))
                """, ("$ids", json)))
            {
                titleMap[row.Integer("entry_id")].Add(row.Text("title"));
                if (row.Text("description").Length > 0) descriptionMap[row.Integer("entry_id")].Add(row.Text("description"));
            }
            return (titleMap, descriptionMap, authorMap);
        });

        var backupAuthor = Str(item, "author").Trim().Length > 0 ? Str(item, "author").Trim() : Str(item, "artist").Trim();
        if (backupAuthor.Length == 0) backupAuthor = Texts(Bundle(item)["authors"]).FirstOrDefault() ?? "";
        var floor = explicitQuery.Length > 0 ? .48 : .64;
        var titleFloor = explicitQuery.Length > 0 ? .46 : .59;
        var scored = new List<(double Score, long EntryId)>();
        foreach (var id in unique)
        {
            var candidateTitles = titles[id].Where(title => title.Length > 0).ToArray();
            var title = bases.SelectMany(left => candidateTitles.Select(right => TextMatcher.TitleSimilarity(left, right))).DefaultIfEmpty(0).Max();
            var summary = descriptions.SelectMany(left => entryDescriptions[id].Select(right => TextMatcher.DescriptionSimilarity(left, right))).DefaultIfEmpty(0).Max();
            var score = title;
            if (identities.Count > 0 && Facts(id).Identities.Overlaps(identities)) score = 1;
            if (summary >= .90) score = Math.Max(score, .97);
            else if (summary >= .78) score = Math.Min(1, score + .06);
            if (backupAuthor.Length > 0 && authors.TryGetValue(id, out var author) && author.Length > 0 && TextMatcher.NameSimilarity(backupAuthor, author) >= .92)
                score = Math.Min(1, score + .03);
            if (reference == id) score = 1;
            if (reference == id || (score >= floor && (title >= titleFloor || summary >= .72))) scored.Add((score, id));
        }
        return scored.OrderByDescending(pair => pair.Score).ThenBy(pair => pair.EntryId).Take(Math.Max(1, limit)).ToList();
    }

    /// <summary>Entries whose description shares long words with this synopsis (_candidate_entries_for_manga's description fallback).</summary>
    private List<long> DescriptionCandidates(string description, int limit)
    {
        var words = TextMatcher.NormalizeDescription(description).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 7).Distinct(StringComparer.Ordinal).Take(4).ToArray();
        if (words.Length == 0) return [];
        return Read(c =>
        {
            var ids = new List<long>();
            foreach (var word in words)
            {
                var like = "%" + word + "%";
                ids.AddRange(c.Query("SELECT id FROM entries WHERE lower(description) LIKE $like LIMIT $limit", ("$like", like), ("$limit", limit)).Select(row => row.Integer("id")));
                ids.AddRange(c.Query("SELECT DISTINCT entry_id id FROM source_matches WHERE active=1 AND lower(description) LIKE $like LIMIT $limit", ("$like", like), ("$limit", limit))
                    .Select(row => row.Integer("id")));
            }
            return ids;
        });
    }

    /// <summary>_backup_exact_title_candidates: exact known titles; when several, the one entry that carries the backup's source wins.</summary>
    private IReadOnlyList<long> ExactTitleCandidates(string title, string sourceId = "")
    {
        var ids = _ingest.ExactSearchEntryIds(title, 100);
        if (ids.Count <= 1 || sourceId.Length == 0) return ids;
        var owned = _ingest.ExactSearchEntryIds(title, 100, [sourceId]);
        return owned.Count == 1 ? owned : ids;
    }

    /// <summary>_backup_entry_by_source_url: entries holding this exact source-relative URL on this source.</summary>
    internal List<long> EntriesBySourceUrl(string sourceId, string sourceUrl)
    {
        var wanted = ImportQueries.BackupUrlKey(sourceUrl);
        if (sourceId.Length == 0 || wanted.Length == 0) return [];
        return Read(c => c.Query("""
            SELECT entry_id,source_manga_url FROM source_matches WHERE active=1 AND source_id=$source
            UNION ALL SELECT entry_id,source_manga_url FROM source_variants WHERE active=1 AND source_id=$source
            """, ("$source", sourceId))
            .Where(row => ImportQueries.BackupUrlKey(row.Text("source_manga_url")) == wanted)
            .Select(row => CatalogueTitles.Canonical(c, row.Integer("entry_id"))).Distinct().ToList());
    }

    /// <summary>
    /// _import_auto_resolve_candidate: one compatible candidate, or a decisive leader (more shared identities, the exact
    /// source reference, a clearly better synopsis or creator, or a 0.03 score lead), or an equivalent cluster whose rows all
    /// share an identity or a full title + summary and agree on editions. Otherwise nothing ("multiple compatible candidates").
    /// </summary>
    internal (long? EntryId, string Reason) AutoResolveCandidate(JsonObject item, string englishTitle = "", long? referenceEntryId = null,
        int limit = 16, IReadOnlyList<long>? candidateIds = null)
    {
        var referenceDescription = referenceEntryId is { } reference && _ingest.EntrySummaries([reference]).FirstOrDefault() is { } summary
            ? Str(summary, "description") : "";
        var global = RankedCandidates(item, limit: Math.Max(limit, (candidateIds?.Count ?? 0) * 2), englishTitle: englishTitle,
            referenceEntryId: referenceEntryId, referenceDescription: referenceDescription);
        List<(double Score, long EntryId)> ranked;
        if (candidateIds is { Count: > 0 })
        {
            var rank = global.GroupBy(pair => pair.EntryId).ToDictionary(group => group.Key, group => group.First().Score);
            ranked = _ingest.CanonicalEntryIds(candidateIds.Where(id => id > 0)).Distinct().Take(Math.Max(1, limit))
                .Select(id => (rank.TryGetValue(id, out var score) ? score : 0, id)).ToList();
        }
        else ranked = global;
        var accepted = ranked.Select(pair => CandidateEvidence(item, pair.EntryId, englishTitle, referenceEntryId) with { RankScore = pair.Score })
            .Where(evidence => evidence.Eligible)
            .OrderByDescending(evidence => evidence.SharedIdentities.Count).ThenByDescending(evidence => evidence.ExactReference ? 1 : 0)
            .ThenByDescending(evidence => evidence.SummarySimilarity).ThenByDescending(evidence => evidence.AuthorSimilarity)
            .ThenByDescending(evidence => evidence.Score).ThenByDescending(evidence => evidence.RankScore).ThenBy(evidence => evidence.EntryId).ToList();
        if (accepted.Count == 0) return (null, "");
        var top = accepted[0];
        if (accepted.Count == 1) return (top.EntryId, top.Reason);
        var second = accepted[1];
        if (top.SharedIdentities.Count > second.SharedIdentities.Count) return (top.EntryId, top.Reason);
        if (top.ExactReference && !second.ExactReference) return (top.EntryId, top.Reason);
        if (top.SummarySimilarity >= .90 && top.SummarySimilarity >= second.SummarySimilarity + .06) return (top.EntryId, top.Reason);
        if (top.AuthorSimilarity >= .90 && top.AuthorSimilarity >= second.AuthorSimilarity + .15) return (top.EntryId, top.Reason);
        if (top.Score >= second.Score + .03) return (top.EntryId, top.Reason);
        return EquivalentChoice(accepted) is { } equivalent ? equivalent : (null, "multiple compatible candidates");
    }

    /// <summary>_import_equivalent_candidate_choice: every contender independently proven and edition-compatible with the others.</summary>
    private (long? EntryId, string Reason)? EquivalentChoice(IReadOnlyList<ImportEvidence> accepted)
    {
        var ids = accepted.Select(evidence => evidence.EntryId).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length < 2) return null;
        var editions = Read(c => ids.Select(id => c.Query("SELECT color_mode,media_mode FROM entries WHERE id=$id", ("$id", id)).FirstOrDefault()).ToArray());
        string Known(string value, params string[] allowed) => allowed.Contains(value) ? value : "";
        var colors = editions.Select(row => Known(row?.Text("color_mode") ?? "", "colored", "monochrome")).Where(value => value.Length > 0).Distinct().Count();
        var media = editions.Select(row => Known(row?.Text("media_mode") ?? "", "manga", "webtoon")).Where(value => value.Length > 0).Distinct().Count();
        if (colors > 1 || media > 1) return null;
        var common = accepted.Select(evidence => evidence.SharedIdentities.ToHashSet(StringComparer.Ordinal)).Aggregate((left, right) => { left.IntersectWith(right); return left; });
        if (common.Count > 0) return (ids[0], "equivalent catalog rows share " + common.Order(StringComparer.Ordinal).First().Replace(":", " identity ", StringComparison.Ordinal));
        var floor = Options().Floor;
        if (accepted.All(evidence => evidence.TitleScore >= floor && evidence.SummarySimilarity >= .90))
            return (ids[0], "equivalent catalog rows: exact/full title + summary >=90%");
        return null;
    }

    // ---- live sources ----------------------------------------------------------------------------------------------

    private bool InCooldown(string sourceId) => _sourceFailures.TryGetValue(sourceId, out var failure) && failure.Until > _clock.GetUtcNow();

    /// <summary>_import_source_failure: a compact per-source diagnostic on the row (timeouts are retryable, never "not found", IMP-8).</summary>
    private JsonObject SourceFailure(JsonObject item, string sourceId, Exception error, string query)
    {
        var name = ExpectedSourceName(item);
        var kind = error is TimeoutException or OperationCanceledException ? "timeout" : error is HttpRequestException ? "network" : "error";
        var diagnostic = new JsonObject
        {
            ["source_id"] = sourceId, ["source_name"] = name, ["kind"] = kind,
            ["message"] = name + (kind == "timeout" ? " timed out; Retry is available" : " could not be searched: " + error.Message),
            ["query"] = query, ["detail"] = error.Message.Length > 12000 ? error.Message[..12000] : error.Message, ["checked_at"] = Now(),
        };
        if (item[DiagnosticsKey] is not JsonObject diagnostics)
        {
            diagnostics = new JsonObject();
            item[DiagnosticsKey] = diagnostics;
        }
        diagnostics[sourceId] = diagnostic;
        while (diagnostics.Count > 24) diagnostics.Remove(diagnostics.First().Key);
        _sourceFailures[sourceId] = (_clock.GetUtcNow() + SourceCooldown, error.Message);
        return (JsonObject)diagnostic.DeepClone();
    }

    private void ClearSourceFailure(JsonObject item, string sourceId)
    {
        if (item[DiagnosticsKey] is JsonObject diagnostics) diagnostics.Remove(sourceId);
        _sourceFailures.TryRemove(sourceId, out _);
    }

    private async Task<JsonObject> WithDetailsAsync(JsonObject chosen, CancellationToken cancellationToken)
    {
        var mangaId = CatalogueTitles.MangaId(chosen["id"]);
        if (mangaId <= 0 || Sources is not { } sources) return chosen;
        try
        {
            if (await sources.DetailsAsync(mangaId, cancellationToken).ConfigureAwait(false) is { } full)
                foreach (var (key, value) in full) chosen[key] = value?.DeepClone();
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Search metadata is enough to keep the source identity; richer metadata can be repaired later.
        }
        chosen["id"] = mangaId;
        return chosen;
    }

    /// <summary>The source record of a backup row on its own source: one search per query until the exact URL or a unique exact title.</summary>
    private async Task<(JsonObject? Chosen, bool ExactUrl, string Evidence, JsonObject? Failure, List<string> Queries)> FindOnSourceAsync(
        JsonObject item, string sourceId, string preferred, CancellationToken cancellationToken)
    {
        var attempted = new List<string>();
        if (Sources is not { } sources) return (null, false, "live sources are not available in this host", null, attempted);
        var wanted = ImportQueries.BackupUrlKey(Str(item, "url"));
        var queries = ImportQueries.BackupSourceSearchQueries(Str(item, "title"), Str(item, "url"), preferred);
        for (var index = 0; index < queries.Count; index++)
        {
            var query = queries[index];
            IReadOnlyList<JsonObject> mangas;
            try { mangas = await sources.SearchAsync(sourceId, query, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                attempted.Add(query);
                return (null, false, "", SourceFailure(item, sourceId, error, query), attempted);
            }
            ClearSourceFailure(item, sourceId);
            attempted.Add(query);
            List<JsonObject> exactUrl = wanted.Length == 0 ? [] : mangas.Where(manga => ImportQueries.BackupUrlKey(CatalogueTitles.Text(manga["url"])) == wanted).ToList();
            if (exactUrl.Count == 1)
                return ((JsonObject)exactUrl[0].DeepClone(), true, "exact backup source URL" + (index > 0 ? " after fallback search '" + query + "'" : ""), null, attempted);
            var key = TextMatcher.NormalizeTitle(query);
            var exactTitle = mangas.Where(manga => TextMatcher.NormalizeTitle(CatalogueTitles.Text(manga["title"])) == key).ToList();
            if (exactTitle.Count == 1)
                return ((JsonObject)exactTitle[0].DeepClone(), false, index == 0 ? "unique exact backup title" : "unique exact fallback title '" + query + "'", null, attempted);
        }
        return (null, false, "", null, attempted);
    }

    /// <summary>
    /// _bind_backup_source_to_entry: keeps the backup's own source record on the chosen entry when that source is enabled
    /// (accepted metadata is applied first). A record owned by another entry is never merged here: that stays for duplicate
    /// review. Automatic binds respect separate-entry decisions and matching rules.
    /// </summary>
    internal async Task<JsonObject> BindBackupSourceAsync(long entryId, JsonObject item, bool manual, CancellationToken cancellationToken)
    {
        entryId = _ingest.CanonicalEntryId(entryId);
        if (!manual && PolicyBlock(item, entryId) is { } blocked) throw new ImportPolicyBlockedException(blocked);
        ApplyMetadataToEntry(entryId, item);
        var expected = ExpectedSourceName(item);
        var sourceId = CurrentSourceId(item);
        JsonObject Answer(bool bound, bool searched, string reason) => new() { ["entry_id"] = entryId, ["bound"] = bound, ["searched"] = searched, ["reason"] = reason };
        if (sourceId.Length == 0) return Answer(false, false, expected + " is not enabled");
        var attached = Read(c => c.Scalar("""
            SELECT 1 FROM source_matches WHERE entry_id=$id AND source_id=$source AND active=1
            UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND source_id=$source AND active=1 LIMIT 1
            """, ("$id", entryId), ("$source", sourceId)) is not null);
        if (attached) return Answer(true, false, "Already bound to " + expected);
        if (InCooldown(sourceId))
        {
            var deferred = Answer(false, false, expected + " is cooling down after a failure; Retry is available");
            deferred["deferred"] = true;
            return deferred;
        }
        if (Str(item, "title").Trim().Length == 0) return Answer(false, false, "No backup title available for " + expected);
        var (chosen, _, evidence, failure, queries) = await FindOnSourceAsync(item, sourceId, Str(item, "title").Trim(), cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            var deferred = Answer(false, true, Str(failure, "message"));
            deferred["deferred"] = true; deferred["source_error"] = failure;
            return deferred;
        }
        if (chosen is null)
        {
            var missing = Answer(false, true, "No exact " + expected + " source record found");
            missing["queries"] = new JsonArray(queries.Select(query => (JsonNode?)JsonValue.Create(query)).ToArray());
            return missing;
        }
        var mangaId = CatalogueTitles.MangaId(chosen["id"]);
        if (mangaId <= 0) return Answer(false, true, expected + " returned an invalid manga id");
        chosen = await WithDetailsAsync(chosen, cancellationToken).ConfigureAwait(false);
        if (!_ingest.EntryExists(entryId)) throw new ImportPolicyBlockedException("The selected entry is no longer available. Refresh the import review.");
        if (!manual && Manual?.IsSeparatedFrom(entryId, sourceId, mangaId) == true)
            throw new ImportPolicyBlockedException("This source record was manually marked as a different work.");
        if (_ingest.FindExistingBySource(sourceId, mangaId) is { } owner && _ingest.CanonicalEntryId(owner) != entryId)
            return Answer(false, true, expected + " record belongs to another entry; left for duplicate review");
        var primary = Read(c => c.Scalar("SELECT source_manga_id FROM source_matches WHERE entry_id=$id AND source_id=$source AND active=1",
            ("$id", entryId), ("$source", sourceId)));
        if (primary is long current && current != mangaId) _ingest.UpsertSourceVariant(entryId, sourceId, chosen, 1, "import", true);
        else _ingest.UpsertMatch(entryId, sourceId, chosen, 1, "import", true);
        var result = Answer(true, true, "Bound to " + expected + " by " + evidence);
        result["source_id"] = sourceId; result["source_manga_id"] = mangaId; result["entry_id"] = _ingest.CanonicalEntryId(entryId);
        return result;
    }

    /// <summary>
    /// _hydrate_backup_source_reference: the backup's own record on its enabled source, catalogued through match-on-ingest.
    /// An exact URL resolves; a unique exact title is only a reference for review.
    /// </summary>
    internal async Task<(long? EntryId, bool Exact, string Reason, bool Deferred)> SourceReferenceAsync(JsonObject item, CancellationToken cancellationToken)
    {
        var expected = ExpectedSourceName(item);
        var sourceId = CurrentSourceId(item);
        if (sourceId.Length == 0) return (null, false, expected + " is not enabled", false);
        if (Str(item, "title").Trim().Length == 0) return (null, false, "No backup title is available for " + expected, false);
        var local = EntriesBySourceUrl(sourceId, Str(item, "url"));
        if (local.Count == 1) return (local[0], true, "exact source URL", false);
        if (InCooldown(sourceId)) return (null, false, expected + " is cooling down after a failure; Retry is available", true);
        var (chosen, exact, evidence, failure, _) = await FindOnSourceAsync(item, sourceId, Str(item, "title").Trim(), cancellationToken).ConfigureAwait(false);
        if (failure is not null) return (null, false, Str(failure, "message"), true);
        if (chosen is null) return (null, false, expected + " entry not found", false);
        chosen = await WithDetailsAsync(chosen, cancellationToken).ConfigureAwait(false);
        try
        {
            var ingested = await IngestAsync(sourceId, chosen, cancellationToken).ConfigureAwait(false);
            return (ingested.EntryId is { } id ? (long?)_ingest.CanonicalEntryId(id) : null, exact,
                exact ? evidence.Replace("exact backup source URL", "live exact source URL", StringComparison.Ordinal) : "enabled source " + evidence + " reference", false);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return (null, false, "source record could not be catalogued: " + error.Message, false);
        }
    }

    /// <summary>_ingest_import_manga: match-on-ingest, one at a time (browse and import may find the same record).</summary>
    private async Task<IngestResult> IngestAsync(string sourceId, JsonObject manga, CancellationToken cancellationToken)
    {
        await _ingestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _ingest.IngestManga(sourceId, manga); }
        finally { _ingestLock.Release(); }
    }

    // ---- metadata identity -----------------------------------------------------------------------------------------

    /// <summary>_import_metadata_candidate_score: an external record against the original backup (0 = not accepted).</summary>
    internal (double Score, string Reason) MetadataCandidateScore(JsonObject item, MetadataCandidate candidate, string englishTitle = "")
    {
        var importTitles = EvidenceTitles(item, englishTitle, withMetadata: false);
        var candidateTitles = new[] { candidate.Title }.Concat(candidate.Titles.Select(row => row.Title)).Where(title => title.Trim().Length > 0).ToArray();
        var (strict, matchedCandidate, matchedImport) = TextMatcher.BestAutomaticTitleScore(candidateTitles, importTitles);
        var loose = candidateTitles.SelectMany(left => importTitles.Select(right => TextMatcher.TitleSimilarity(left, right))).DefaultIfEmpty(0).Max();
        var backupDescription = TextMatcher.CleanSummary(Str(item, "description"));
        var description = backupDescription.Length > 0 ? TextMatcher.DescriptionSimilarity(candidate.Description, backupDescription) : 0;
        var minimum = Math.Max(MatchingFloor, Options().MetadataMinScore);
        var accepted = strict >= minimum || (loose >= .94 && description >= .80) || (strict >= .80 && description >= .90) || (loose >= .80 && description >= .94);
        if (!accepted) return (0, "title " + Percent(strict) + "/" + Percent(loose) + " + summary " + Percent(description));
        var title = Math.Max(strict, description >= .80 ? loose : 0);
        var backupAuthor = Str(item, "author").Trim().Length > 0 ? Str(item, "author").Trim() : Str(item, "artist").Trim();
        var candidateAuthor = candidate.Authors.Concat(candidate.Artists).FirstOrDefault(name => name.Trim().Length > 0) ?? "";
        if (backupAuthor.Length > 0 && candidateAuthor.Length > 0)
        {
            var author = TextMatcher.NameSimilarity(backupAuthor, candidateAuthor);
            if (author < .30 && strict < minimum && description < .90) return (0, "author conflict with insufficient work-level evidence");
            if (author >= .90) title = Math.Min(1, Math.Max(title, description) + .01);
        }
        var score = Math.Max(title, description >= .90 ? Math.Min(.999, .90 + description * .099) : 0);
        var reason = "metadata title " + Percent(Math.Max(strict, loose)) + " + summary " + Percent(description)
            + (matchedCandidate.Length > 0 && matchedImport.Length > 0 ? " via " + matchedCandidate + " ↔ " + matchedImport : "");
        return (Math.Clamp(score, 0, 1), reason);
    }

    private const double MatchingFloor = .94;

    /// <summary>
    /// _resolve_import_metadata_identity: searches the enabled metadata catalogues with the backup's titles and keeps one
    /// accepted record per provider (a tie between different ids is ambiguous and kept out). The backup data is never
    /// replaced; the bundle is stored on the row as additional evidence. A provider failure keeps its earlier acceptance.
    /// </summary>
    internal async Task<JsonObject> ResolveMetadataIdentityAsync(JsonObject item, string englishTitle, bool force, CancellationToken cancellationToken)
    {
        var existing = Bundle(item);
        var hasIdentity = existing["records"] is JsonArray { Count: > 0 } || existing["identities"] is JsonArray { Count: > 0 };
        if (!force && existing["checked"] is JsonValue done && done.TryGetValue<bool>(out var isChecked) && isChecked)
        {
            var age = DateTimeOffset.TryParse(Str(existing, "checked_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? _clock.GetUtcNow() - at : (TimeSpan?)null;
            if (hasIdentity || (age is { } elapsed && elapsed < TimeSpan.FromMinutes(30))) return existing;
        }
        var options = Options();
        var bundle = new JsonObject
        {
            ["checked"] = false, ["checked_at"] = Now(), ["provider_errors"] = new JsonObject(), ["ambiguous_providers"] = new JsonArray(),
            ["titles"] = new JsonArray(), ["descriptions"] = new JsonArray(), ["authors"] = new JsonArray(), ["identities"] = new JsonArray(),
            ["matches"] = new JsonArray(), ["records"] = new JsonArray(), ["providers_checked"] = new JsonArray(),
        };
        if (!options.MetadataEnabled || Metadata is not { } metadata)
        {
            item[MetadataKey] = bundle;
            return bundle;
        }
        var providers = metadata.Providers;
        bundle["providers_checked"] = new JsonArray(providers.Select(provider => (JsonNode?)JsonValue.Create(provider)).ToArray());
        var queries = EvidenceTitles(item, englishTitle, withMetadata: false).Take(options.MetadataQueryLimit).ToArray();
        var accepted = new Dictionary<string, (MetadataCandidate Candidate, double Score, string Reason)>(StringComparer.Ordinal);
        var errors = (JsonObject)bundle["provider_errors"]!;
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = providers.ToHashSet(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            foreach (var provider in unresolved.ToArray())
            {
                IReadOnlyList<MetadataCandidate> rows;
                try { rows = await metadata.SearchAsync(provider, query, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    errors[provider] = error.Message.Length > 0 ? error.Message : error.GetType().Name;
                    continue;
                }
                errors.Remove(provider);
                var scored = rows.Select(candidate => (Candidate: candidate, Result: MetadataCandidateScore(item, candidate, englishTitle)))
                    .Where(row => row.Result.Score > 0).ToList();
                if (scored.Count == 0) continue;
                var best = scored.MaxBy(row => row.Result.Score);
                var tied = scored.Where(row => Math.Abs(row.Result.Score - best.Result.Score) < 1e-6).Select(row => row.Candidate.ExternalId).Distinct(StringComparer.Ordinal).Count();
                if (tied > 1) { ambiguous.Add(provider); continue; }
                accepted[provider] = (best.Candidate, best.Result.Score, best.Result.Reason);
                ambiguous.Remove(provider);
                unresolved.Remove(provider);
            }
            if (unresolved.Count == 0) break;
        }
        // A provider outage keeps that provider's earlier accepted record (never an ambiguous or negative one).
        var keptRecords = new List<JsonObject>();
        if (existing["records"] is JsonArray oldRecords)
            foreach (var record in oldRecords.OfType<JsonObject>())
                if (errors.ContainsKey(Str(record, "provider")) && !accepted.ContainsKey(Str(record, "provider"))) keptRecords.Add((JsonObject)record.DeepClone());

        var titles = (JsonArray)bundle["titles"]!; var descriptions = (JsonArray)bundle["descriptions"]!; var authors = (JsonArray)bundle["authors"]!;
        var identities = (JsonArray)bundle["identities"]!; var matches = (JsonArray)bundle["matches"]!; var records = (JsonArray)bundle["records"]!;
        var titleSeen = new HashSet<string>(StringComparer.Ordinal); var descriptionSeen = new HashSet<string>(StringComparer.Ordinal);
        var identitySeen = new HashSet<string>(StringComparer.Ordinal);
        void AddIdentity(string provider, string externalId, double confidence, string? crossref)
        {
            provider = provider.Trim().ToLowerInvariant(); externalId = externalId.Trim();
            if (provider.Length == 0 || externalId.Length == 0 || !identitySeen.Add(provider + ":" + externalId)) return;
            var row = new JsonObject { ["provider"] = provider, ["external_id"] = externalId, ["confidence"] = Math.Round(confidence, 4) };
            if (crossref is not null) row["crossref_from"] = crossref;
            identities.Add(row);
        }
        foreach (var (provider, (candidate, score, reason)) in accepted.OrderBy(pair => Array.IndexOf(providers.ToArray(), pair.Key)))
        {
            matches.Add(new JsonObject
            {
                ["provider"] = provider, ["external_id"] = candidate.ExternalId, ["confidence"] = Math.Round(score, 4), ["reason"] = reason,
                ["title"] = candidate.Title, ["url"] = candidate.Url,
            });
            records.Add(new JsonObject { ["provider"] = provider, ["confidence"] = Math.Round(score, 4), ["reason"] = reason, ["candidate"] = candidate.ToJson() });
            AddIdentity(provider, candidate.ExternalId, score, null);
            foreach (var (linked, linkedId) in candidate.Links) AddIdentity(linked, linkedId, score, provider);
            foreach (var title in new[] { candidate.Title }.Concat(candidate.Titles.Select(row => row.Title)))
            {
                var text = ImportQueries.Collapse(title); var key = TextMatcher.NormalizeTitle(text);
                if (text.Length > 0 && key.Length > 0 && TextMatcher.IsMatchEvidenceTitle(text) && titleSeen.Add(key)) titles.Add((JsonNode?)JsonValue.Create(text));
            }
            var description = TextMatcher.CleanSummary(candidate.Description);
            if (description.Length > 0 && descriptionSeen.Add(TextMatcher.NormalizeDescription(description))) descriptions.Add((JsonNode?)JsonValue.Create(description));
            foreach (var person in candidate.Authors.Concat(candidate.Artists).Select(ImportQueries.Collapse).Where(person => person.Length > 0).Distinct(StringComparer.Ordinal))
                if (!authors.Any(node => node?.GetValue<string>() == person)) authors.Add((JsonNode?)JsonValue.Create(person));
        }
        foreach (var record in keptRecords)
        {
            records.Add(record);
            if (record["candidate"] is JsonObject candidate)
                AddIdentity(Str(record, "provider"), Str(candidate, "external_id"), record["confidence"]?.GetValue<double>() ?? 0, null);
        }
        bundle["ambiguous_providers"] = new JsonArray(ambiguous.Order(StringComparer.Ordinal).Select(provider => (JsonNode?)JsonValue.Create(provider)).ToArray());
        bundle["checked"] = providers.Count > 0 && queries.Length > 0 && errors.Count == 0;
        item[MetadataKey] = bundle;
        return bundle;
    }

    /// <summary>_apply_import_metadata_to_entry: accepted provider records (≥ 0.90) become identity links of the resolved entry.</summary>
    internal int ApplyMetadataToEntry(long entryId, JsonObject item)
    {
        if (Identities is not { } sink || Bundle(item)["records"] is not JsonArray records) return 0;
        var applied = 0;
        foreach (var record in records.OfType<JsonObject>())
        {
            var confidence = record["confidence"] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
            if (confidence < .90 || record["candidate"] is not JsonObject candidate) continue;
            if (sink.Apply(entryId, (JsonObject)candidate.DeepClone(), confidence, "import-metadata:" + Str(record, "reason"))) applied++;
        }
        return applied;
    }

    // ---- resolution ------------------------------------------------------------------------------------------------

    /// <summary>
    /// resolve_backup_manga_entry_details: exact source URL, a unique exact known title that passes the gate, the strict
    /// gate over equal-title rows, then (optionally) metadata identity, then the backup's own source as a reference.
    /// Only a strict decision resolves; everything else returns a reference for review.
    /// </summary>
    internal async Task<(ImportResolution Resolution, bool Deferred)> ResolveDetailsAsync(JsonObject item, string englishTitle, bool useMetadata,
        CancellationToken cancellationToken)
    {
        var title = Str(item, "title").Trim();
        if (title.Length == 0) return (new ImportResolution(null, null, "backup entry has no title"), false);
        var sourceId = CurrentSourceId(item);
        var expected = ExpectedSourceName(item);
        var url = Str(item, "url").Trim();
        if (sourceId.Length > 0 && url.Length > 0)
        {
            var byUrl = FilterRejected(item, EntriesBySourceUrl(sourceId, url));
            if (byUrl.Count == 1) return (new ImportResolution(byUrl[0], byUrl[0], "exact source URL"), false);
            if (byUrl.Count > 1) return (new ImportResolution(null, null, "ambiguous source URL"), false);
        }
        var exactCandidates = new List<long>();
        foreach (var scoped in new[] { sourceId, "" })
        {
            var exact = FilterRejected(item, ExactTitleCandidates(title, scoped));
            if (exact.Count == 1 && CandidateEvidence(item, exact[0], englishTitle).Eligible)
                return (new ImportResolution(exact[0], exact[0], "exact known title"), false);
            exactCandidates.AddRange(exact.Where(id => !exactCandidates.Contains(id)).ToArray());
        }
        var (local, localReason) = AutoResolveCandidate(item, englishTitle, candidateIds: exactCandidates);
        if (local is { } localId) return (new ImportResolution(localId, localId, localReason.Length > 0 ? localReason : "compatible original metadata"), false);
        if (useMetadata)
        {
            await ResolveMetadataIdentityAsync(item, englishTitle, force: false, cancellationToken).ConfigureAwait(false);
            var (metadataId, metadataReason) = AutoResolveCandidate(item, englishTitle, candidateIds: exactCandidates);
            if (metadataId is { } found) return (new ImportResolution(found, found, metadataReason.Length > 0 ? metadataReason : "external metadata identity match"), false);
        }
        if (sourceId.Length == 0)
            return (new ImportResolution(null, null, "no corresponding AutoSourcarr entry found; expected source " + expected + " is not enabled"), false);
        var (referenceId, exactReference, referenceReason, deferred) = await SourceReferenceAsync(item, cancellationToken).ConfigureAwait(false);
        if (referenceId is not { } reference) return (new ImportResolution(null, null, referenceReason), deferred);
        if (IsRejected(item, reference)) return (new ImportResolution(null, null, expected + " found a match that you previously rejected"), false);
        if (exactReference) return (new ImportResolution(reference, reference, referenceReason), false);
        var (candidate, candidateReason) = AutoResolveCandidate(item, englishTitle, reference, candidateIds: exactCandidates);
        if (candidate is { } chosen) return (new ImportResolution(chosen, reference, candidateReason.Length > 0 ? candidateReason : "compatible source-reference metadata"), false);
        var evidence = CandidateEvidence(item, reference, englishTitle, reference);
        return (new ImportResolution(null, reference, "source record found, but combined original/metadata identity evidence is not strong enough to import automatically ("
            + (evidence.Reason.Length > 0 ? evidence.Reason : "insufficient evidence") + ")"), false);
    }

    /// <summary>import_item_suggestions: local ranked suggestions (a search without a query also refreshes the stored candidates).</summary>
    public JsonObject Suggestions(string importId, long itemId, string query = "")
    {
        var row = ReadItem(importId, itemId);
        var item = ItemPayload(row);
        long? reference = row["reference_entry_id"] is long referenceId ? referenceId : null;
        var ranked = RankedCandidates(item, query, 16, row.Text("english_title"), reference, row.Text("reference_description"));
        var scores = ranked.ToDictionary(pair => pair.EntryId, pair => pair.Score);
        var suggestions = _ingest.EntrySummaries(ranked.Select(pair => pair.EntryId)).Select(summary =>
        {
            var copy = (JsonObject)summary.DeepClone();
            var id = summary["canonical_id"]?.GetValue<long>() ?? 0;
            copy["import_score"] = Math.Round(scores.TryGetValue(id, out var score) ? score : 0, 4);
            return copy;
        }).ToList();
        if ((query ?? "").Trim().Length == 0)
            WriteDiscovery(importId, itemId, null, new JsonArray(ranked.Take(8).Select(pair => (JsonNode)pair.EntryId).ToArray()), null, ranked.Count > 0 ? "local" : "pending");
        return new JsonObject
        {
            ["query"] = (query ?? "").Trim().Length > 0 ? query!.Trim() : Str(item, "title"), ["english_title"] = row.Text("english_title"),
            ["suggestions"] = new JsonArray(suggestions.Select(suggestion => (JsonNode)suggestion).ToArray()),
        };
    }

    /// <summary>
    /// import_item_live_suggestions: the local catalogue first (stops only when the strict auto-resolver chose one work),
    /// then the backup's own source as a reference, then the enabled search sources in priority order with the backup and
    /// metadata titles; found records are catalogued through match-on-ingest and kept as concrete candidates. A Search
    /// is a decision: it claims a revision, so a later Match/Skip/Retry wins over this search's write.
    /// </summary>
    public async Task<JsonObject> LiveSuggestionsAsync(string importId, long itemId, string query, bool automatic, bool skipBackupReference = false,
        long? revision = null, CancellationToken cancellationToken = default)
    {
        var row = ReadItem(importId, itemId);
        if (revision is null && !automatic)
        {
            // A Search is the latest decision: a queued Match/Retry of this row stops, and the row is reviewable again.
            revision = Claim(importId, itemId);
            if (row.Text("state") is "resolving" or "retrying")
                UpdateState(importId, itemId, "unresolved", "Search replaced the pending action", null, expectedRevision: revision);
        }
        revision ??= row.Integer("decision_revision");
        var item = ItemPayload(row);
        var explicitQuery = (query ?? "").Trim().Length > 0 ? query!.Trim() : Str(item, "title");
        var englishTitle = row.Text("english_title");
        long? reference = row["reference_entry_id"] is long referenceId ? referenceId : null;
        var live = new List<long>();
        var searchedSources = new List<string>();

        JsonObject Persist(JsonObject result, bool final)
        {
            var suggestions = result["suggestions"] as JsonArray ?? new JsonArray();
            var present = suggestions.OfType<JsonObject>().Select(summary => summary["canonical_id"]?.GetValue<long>() ?? 0).ToHashSet();
            var missing = FilterRejected(item, live).Where(id => !present.Contains(id)).Take(32).ToArray();
            if (missing.Length > 0)
            {
                foreach (var extra in _ingest.EntrySummaries(missing))
                {
                    var copy = (JsonObject)extra.DeepClone();
                    var evidence = CandidateEvidence(item, copy["canonical_id"]?.GetValue<long>() ?? 0, englishTitle, reference);
                    copy["import_score"] = Math.Round(Math.Max(Math.Max(evidence.TitleScore, evidence.SummarySimilarity), evidence.SharedIdentities.Count > 0 ? 1 : 0), 4);
                    suggestions.Add(copy);
                }
                var sorted = suggestions.OfType<JsonObject>().Select(node => (JsonObject)node.DeepClone())
                    .OrderByDescending(node => node["import_score"]?.GetValue<double>() ?? 0).Take(32).ToArray();
                suggestions = new JsonArray(sorted.Select(node => (JsonNode)node).ToArray());
                result["suggestions"] = suggestions;
            }
            var ids = live.Concat(suggestions.OfType<JsonObject>().Select(summary => summary["canonical_id"]?.GetValue<long>() ?? 0)).Where(id => id > 0).Distinct().Take(24).ToArray();
            result["source_errors"] = item[DiagnosticsKey] is JsonObject diagnostics
                ? new JsonArray(diagnostics.Select(pair => pair.Value?.DeepClone()).ToArray()) : new JsonArray();
            var fresh = Read(c => c.Scalar("SELECT raw_json FROM import_items WHERE import_id=$run AND id=$id", ("$run", importId), ("$id", itemId)) as string);
            JsonObject freshRaw;
            try { freshRaw = (fresh is null ? null : JsonNode.Parse(fresh) as JsonObject) ?? (JsonObject)item.DeepClone(); }
            catch (JsonException) { freshRaw = (JsonObject)item.DeepClone(); }
            freshRaw[DiagnosticsKey] = item[DiagnosticsKey]?.DeepClone() ?? new JsonObject();
            if (item[MetadataKey] is JsonObject bundle) freshRaw[MetadataKey] = bundle.DeepClone();
            WriteDiscovery(importId, itemId, revision, new JsonArray(ids.Select(id => (JsonNode)id).ToArray()), final ? 1 : 0,
                final ? "done" : ids.Length > 0 ? "found" : "searching", freshRaw, reference);
            return result;
        }

        var local = Suggestions(importId, itemId, explicitQuery);
        var localIds = (local["suggestions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(summary => summary["canonical_id"]?.GetValue<long>() ?? 0).Where(id => id > 0).ToList();
        var topLocal = (local["suggestions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(summary => summary["import_score"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Max();
        if (topLocal >= .94)
        {
            var (auto, autoReason) = AutoResolveCandidate(item, englishTitle, reference, candidateIds: localIds);
            if (auto is { } chosen)
            {
                local["live_searched"] = false; local["live_sources"] = new JsonArray();
                local["auto_match_entry_id"] = chosen; local["auto_match_reason"] = autoReason.Length > 0 ? autoReason : "strict local auto-match";
                return Persist(local, true);
            }
            Persist((JsonObject)local.DeepClone(), false);
        }
        if (Superseded(importId, itemId, revision.Value)) return SupersededAnswer(importId, itemId);

        var backupSource = CurrentSourceId(item);
        if (backupSource.Length > 0 && !skipBackupReference && !InCooldown(backupSource))
        {
            var (referenceFound, _, _, _) = await SourceReferenceAsync(item, cancellationToken).ConfigureAwait(false);
            if (referenceFound is { } found) { reference = found; live.Add(found); }
            searchedSources.Add(backupSource);
        }
        if (Sources is { } sources)
        {
            var queries = ImportQueries.BackupSourceSearchQueries(Str(item, "title"), Str(item, "url"), explicitQuery).Take(2)
                .Concat(Texts(Bundle(item)["titles"]).Take(2)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var sourceId in SearchSources(Options().LiveSources))
            {
                if (InCooldown(sourceId)) continue;
                if (!searchedSources.Contains(sourceId)) searchedSources.Add(sourceId);
                foreach (var search in queries)
                {
                    if (Superseded(importId, itemId, revision.Value)) return SupersededAnswer(importId, itemId);
                    IReadOnlyList<JsonObject> results;
                    try { results = await sources.SearchAsync(sourceId, search, cancellationToken).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        SourceFailure(item, sourceId, error, search);
                        break;
                    }
                    ClearSourceFailure(item, sourceId);
                    foreach (var manga in results.Take(12))
                    {
                        var title = CatalogueTitles.Text(manga["title"]);
                        if (queries.Max(value => TextMatcher.TitleSimilarity(value, title)) < .60 || CatalogueTitles.MangaId(manga["id"]) <= 0) continue;
                        try
                        {
                            var ingested = await IngestAsync(sourceId, (JsonObject)manga.DeepClone(), cancellationToken).ConfigureAwait(false);
                            if (ingested.EntryId is { } entry) live.Add(_ingest.CanonicalEntryId(entry));
                        }
                        catch (ArgumentException) { }
                    }
                }
                if (automatic && live.Count > 0 && AutoResolveCandidate(item, englishTitle, reference, candidateIds: live.Distinct().ToArray()).EntryId is not null) break;
            }
        }
        var result = Suggestions(importId, itemId, explicitQuery);
        result["live_searched"] = searchedSources.Count > 0;
        result["live_sources"] = new JsonArray(searchedSources.Select(source => (JsonNode?)JsonValue.Create(source)).ToArray());
        result["reference_entry_id"] = reference;
        var (autoMatch, autoMatchReason) = AutoResolveCandidate(item, englishTitle, reference,
            candidateIds: live.Concat((result["suggestions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(summary => summary["canonical_id"]?.GetValue<long>() ?? 0))
                .Where(id => id > 0).Distinct().Take(64).ToArray());
        result["auto_match_entry_id"] = autoMatch;
        result["auto_match_reason"] = autoMatchReason;
        return Persist(result, true);
    }
}
