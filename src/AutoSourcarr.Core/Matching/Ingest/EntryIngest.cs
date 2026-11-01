using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Records;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Catalogue.Titles;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Scoring;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Core.Matching.Ingest;

/// <summary>Where an ingested record went: the entry (null when refused), the score and the released reason text.</summary>
public sealed record IngestResult(long? EntryId, double Score, string Reason);

/// <summary>A local entry an unowned record may attach to, with the scorer's decision.</summary>
public sealed record LocalMatch(long EntryId, double Score, string Reason, ScoredMatchDecision Decision);

/// <summary>
/// Only stable, non-self-reinforcing evidence of an entry (_trusted_automatic_entry_evidence): its ingestion anchor
/// record, locked / manual / split / import records, manual and main-library titles and a manual main title. A previous
/// automatic attachment never teaches the entry a title that authorizes the next one.
/// </summary>
public sealed record TrustedEntryEvidence(long EntryId, IReadOnlyList<string> Titles, IReadOnlyList<string> Descriptions, string Author,
    IReadOnlyList<string> SourceIds, string ColorMode, string MediaMode, bool SharedUnverified, int PrimaryRecords)
{
    /// <summary>Every creator (author and artist) of the trusted records, so MAT-2 can see adaptations with other artists.</summary>
    public IReadOnlyList<string> Creators { get; init; } = [];
    /// <summary>Genres of the trusted records (one entry per record), for a matching rule's tags check.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];
    /// <summary>First-publication year of the trusted records (their stored payload), when known.</summary>
    public int? Year { get; init; }
}

/// <summary>One search query built from an entry's names (_expanded_search_names); kind says where it came from.</summary>
public sealed record SearchName(string Query, string Kind, string Language);

/// <summary>
/// Match-on-ingest (port-map "EntryIngest", 30 methods): every source record that reaches the catalogue either refreshes
/// its known owner, attaches to one strong local entry through <see cref="MatchScorer"/> (0.94 floor, missing-word
/// penalty, identity and edition checks, matching rules), or becomes a new entry. Two near-tied candidates never merge,
/// manual bindings, separate records and rejections are respected, and every automatic attachment is written to the
/// decision log. All writes share the source-write gate.
/// </summary>
public sealed class EntryIngest
{
    private static readonly HashSet<string> TrustedMatchTypes = new(StringComparer.Ordinal) { "manual", "split-duplicate", "import" };
    private static readonly string[] StatusLabels = ["Unknown", "Ongoing", "Completed", "Licensed", "Publishing finished", "Cancelled", "Hiatus"];
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDecisionLog _decisions;
    private readonly IManualDecisionsView _manual;
    private readonly IEntryIngestFollowUps _followUps;
    private readonly Func<double> _threshold;
    private readonly Func<bool> _identityOnIngest;
    private readonly TimeProvider _clock;

    public EntryIngest(SqliteDatabase database, object sourceWriteGate, IDecisionLog decisions, IManualDecisionsView? manual = null,
        IEntryIngestFollowUps? followUps = null, Func<double>? autoMatchThreshold = null, Func<bool>? identityOnIngest = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(decisions);
        _database = database; _gate = sourceWriteGate; _decisions = decisions;
        _manual = manual ?? new CatalogueManualDecisionsView(database);
        _followUps = followUps ?? NoEntryIngestFollowUps.Instance;
        _threshold = autoMatchThreshold ?? (() => MatchScorer.MinimumFloor);
        _identityOnIngest = identityOnIngest ?? (() => false);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Per-source matching rules (matching_policies); null = no rules.</summary>
    public MatchingPolicyStore? Policies { get; init; }
    /// <summary>Matching profiles: a rule set from a simple/advanced profile scores with that profile's options; null = the rule's checks only.</summary>
    public MatchingPresetStore? Presets { get; init; }
    /// <summary>Optional metadata cleaning of a source payload (GalleryEvidenceService.CleanNative); the source label is always stripped.</summary>
    public Func<string, JsonObject, JsonObject>? CleanPayload { get; init; }
    /// <summary>Optional cover cache probe for entry cards ("entry-{id}" keys).</summary>
    public Func<string, bool>? CoverCached { get; init; }
    /// <summary>Optional per-record chapter coverage for get_entry: key "source_id:source_manga_id".</summary>
    public Func<long, IReadOnlyDictionary<string, JsonObject>>? ChapterStats { get; init; }

    /// <summary>The automatic floor in use (never below 0.94).</summary>
    public double Floor => Math.Max(MatchScorer.MinimumFloor, Math.Clamp(_threshold(), 0, 1));

    // ---- identities ------------------------------------------------------------------------------------------------

    /// <summary>canonical_entry_id: follows merge aliases.</summary>
    public long CanonicalEntryId(long entryId) => Read(session => CatalogueTitles.Canonical(session, entryId));

    /// <summary>canonical_entry_ids.</summary>
    public IReadOnlyList<long> CanonicalEntryIds(IEnumerable<long> entryIds)
    {
        var ids = entryIds.ToArray();
        return Read(session => ids.Select(id => CatalogueTitles.Canonical(session, id)).ToArray());
    }

    /// <summary>entry_exists.</summary>
    public bool EntryExists(long entryId) => Read(session => Exists(session, CatalogueTitles.Canonical(session, entryId)));

    /// <summary>_find_existing_by_source: the entry that owns this exact record (primary or additional).</summary>
    public long? FindExistingBySource(string sourceId, long sourceMangaId) => Read(session => FindExisting(session, sourceId, sourceMangaId));

    /// <summary>_is_deleted_source_record.</summary>
    public bool IsDeletedSourceRecord(string sourceId, long sourceMangaId) => Read(session => Deleted(session, sourceId, sourceMangaId));

    /// <summary>_source_record_attached: the record is active on exactly this (canonical, not deleted) entry.</summary>
    public bool SourceRecordAttached(long entryId, string sourceId, long sourceMangaId) => Read(session =>
    {
        if (CatalogueTitles.Canonical(session, entryId) != entryId) return false;
        if (session.Scalar("SELECT 1 FROM deleted_entries_history WHERE old_entry_id=$id", ("$id", entryId)) is not null) return false;
        return session.Scalar("""
            SELECT 1 FROM source_matches WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1
            UNION ALL SELECT 1 FROM source_variants WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1 LIMIT 1
            """, ("$id", entryId), ("$source", sourceId), ("$manga", sourceMangaId)) is not null;
    });

    /// <summary>_match_id: the primary match row id of a source on an entry.</summary>
    public long MatchId(long entryId, string sourceId) => Read(session =>
        session.Scalar("SELECT id FROM source_matches WHERE entry_id=$id AND source_id=$source", ("$id", CatalogueTitles.Canonical(session, entryId)), ("$source", sourceId)) is long id
            ? id : throw new KeyNotFoundException("No match of source " + sourceId + " on entry " + entryId.ToString(CultureInfo.InvariantCulture) + "."));

    /// <summary>_entry_descriptions: entry, active records and confident identities (deduplicated, in that order).</summary>
    public IReadOnlyList<string> EntryDescriptions(long entryId) => Read(session =>
    {
        var id = CatalogueTitles.Canonical(session, entryId);
        var values = new List<string>();
        if (session.Scalar("SELECT description FROM entries WHERE id=$id", ("$id", id)) is string { Length: > 0 } own) values.Add(own);
        foreach (var sql in new[]
        {
            "SELECT description FROM source_matches WHERE entry_id=$id AND active=1 AND description<>''",
            "SELECT description FROM source_variants WHERE entry_id=$id AND active=1 AND description<>''",
            "SELECT description FROM identity_links WHERE entry_id=$id AND confidence>=0.90 AND description<>''",
        })
            values.AddRange(session.Query(sql, ("$id", id)).Select(row => row.Text("description")).Where(text => text.Length > 0));
        return values.Distinct(StringComparer.Ordinal).ToArray();
    });

    // ---- ingest ----------------------------------------------------------------------------------------------------

    /// <summary>ingest_manga: refresh the known owner, attach to one strong local entry, or create a new entry.</summary>
    public IngestResult IngestManga(string sourceId, JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(sourceId); ArgumentNullException.ThrowIfNull(manga);
        var followUps = new List<Action>();
        IngestResult result;
        lock (_gate) result = _database.Write(DatabaseFile.Catalog, session => Ingest(session, sourceId, manga, followUps));
        foreach (var action in followUps) action();
        return result;
    }

    private IngestResult Ingest(DatabaseSession session, string sourceId, JsonObject raw, List<Action> followUps)
    {
        var manga = Clean(session, sourceId, raw);
        var mangaId = CatalogueTitles.MangaId(manga["id"]);
        if (mangaId <= 0) throw new ArgumentException("A source record needs its numeric id.", nameof(raw));
        if (Deleted(session, sourceId, mangaId)) return new(null, 0, "manually-deleted");
        var stamp = Now();
        var sparse = Sparse(manga);

        if (FindExisting(session, sourceId, mangaId) is { } existing)
        {
            if (IsPrimary(session, existing, sourceId, mangaId)) UpsertMatch(session, existing, sourceId, manga, 1, "known", false, stamp, followUps);
            else UpsertVariant(session, existing, sourceId, manga, 1, "known", false, stamp, followUps);
            followUps.Add(() => _followUps.ChooseMetadata(existing));
            if (sparse) followUps.Add(() => _followUps.ScheduleHydration(sourceId, mangaId));
            if (_identityOnIngest() && !HasIdentity(session, existing)) followUps.Add(() => _followUps.ScheduleIdentity(existing));
            return new(existing, 1, "known-source-id");
        }

        // A record the user bound to an entry goes back to that entry, never to an automatic guess.
        if (_manual.ManualOwner(sourceId, mangaId) is { } owner && Exists(session, owner))
        {
            if (HasPrimary(session, owner, sourceId)) UpsertVariant(session, owner, sourceId, manga, 1, "manual", true, stamp, followUps);
            else UpsertMatch(session, owner, sourceId, manga, 1, "manual", true, stamp, followUps);
            followUps.Add(() => _followUps.ChooseMetadata(owner));
            return new(owner, 1, "manual-binding");
        }

        var candidates = LocalCandidates(session, manga, sourceId);
        if (candidates.Count == 0) return NewEntry(session, sourceId, manga, mangaId, 1, "auto", "new-entry", stamp, followUps);
        var best = candidates[0];
        if (candidates.Count > 1 && candidates[1].Score >= best.Score - .03)
            // Choosing one of two near-tied works would be an identity guess: keep the record apart for duplicate review.
            return NewEntry(session, sourceId, manga, mangaId, best.Score, "auto-ambiguous", "ambiguous-local-candidates:new-entry", stamp, followUps);

        var entryId = CatalogueTitles.Canonical(session, best.EntryId);
        if (_manual.IsSeparated(entryId, sourceId, mangaId))
        {
            var separate = NewEntry(session, sourceId, manga, mangaId, 1, "separate-anchor", "manual-separate:new-entry", stamp, followUps);
            if (separate.EntryId is { } newId) ReviewNotDuplicate(session, entryId, newId, stamp);
            return separate with { Score = 1 };
        }
        if (HasPrimary(session, entryId, sourceId)) UpsertVariant(session, entryId, sourceId, manga, best.Score, "auto", false, stamp, followUps);
        else UpsertMatch(session, entryId, sourceId, manga, best.Score, "auto", false, stamp, followUps);
        _decisions.Record(new("match.automatic", DecisionActor.Automatic, [entryId], [Key(sourceId, mangaId)], best.Reason, null, new JsonObject
        {
            ["source_id"] = sourceId, ["source_manga_id"] = mangaId, ["title"] = CatalogueTitles.Text(manga["title"]), ["score"] = best.Score,
            ["decision"] = best.Decision.ToJson(),
        }));
        followUps.Add(() => _followUps.ChooseMetadata(entryId));
        followUps.Add(() => _followUps.ScheduleHydration(sourceId, mangaId));
        if (_identityOnIngest() && !HasIdentity(session, entryId)) followUps.Add(() => _followUps.ScheduleIdentity(entryId));
        return new(entryId, best.Score, best.Reason);
    }

    private IngestResult NewEntry(DatabaseSession session, string sourceId, JsonObject manga, long mangaId, double score, string matchType, string reason,
        string stamp, List<Action> followUps)
    {
        var entryId = CreateEntry(session, manga, stamp);
        UpsertMatch(session, entryId, sourceId, manga, 1, matchType, false, stamp, followUps);
        followUps.Add(() => _followUps.ChooseMetadata(entryId));
        followUps.Add(() => _followUps.ScheduleHydration(sourceId, mangaId));
        if (_identityOnIngest()) followUps.Add(() => _followUps.ScheduleIdentity(entryId));
        return new(entryId, score, reason);
    }

    /// <summary>_create_entry: a new entry whose main title has no colour / media edition label.</summary>
    public long CreateEntry(JsonObject manga, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(manga);
        lock (_gate) return _database.Write(DatabaseFile.Catalog, session => CreateEntry(session, Clean(session, sourceId, manga), Now()));
    }

    private static long CreateEntry(DatabaseSession session, JsonObject manga, string stamp)
    {
        var raw = CatalogueTitles.Text(manga["title"]);
        var title = TextMatcher.TitleColorVariant(raw) != "unknown" ? TextMatcher.ColorBaseTitle(raw) : raw;
        if (TextMatcher.TitleMediaVariant(title) != "unknown") title = TextMatcher.MediaBaseTitle(title);
        if (title.Length == 0) title = raw;
        session.Execute("""
            INSERT INTO entries(preferred_title,description,thumbnail_url,status,author,artist,genre,created_at,updated_at)
            VALUES($title,$description,$thumbnail,$status,$author,$artist,$genre,$time,$time)
            """, ("$title", title), ("$description", TextMatcher.CleanSummary(CatalogueTitles.Text(manga["description"]))),
            ("$thumbnail", CatalogueTitles.Text(manga["thumbnailUrl"])), ("$status", NativeRecordStore.Status(manga["status"])),
            ("$author", CatalogueTitles.Text(manga["author"])), ("$artist", CatalogueTitles.Text(manga["artist"])),
            ("$genre", NativeRecordStore.GenreText(manga["genre"])), ("$time", stamp));
        var entryId = Convert.ToInt64(session.Scalar("SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        CatalogueIdentity.Ensure(session, entryId);
        return entryId;
    }

    // ---- candidates and evidence -----------------------------------------------------------------------------------

    /// <summary>_best_local_match: the strongest eligible local entry, or null.</summary>
    public LocalMatch? BestLocalMatch(JsonObject manga, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(manga);
        lock (_gate) return _database.Read(DatabaseFile.Catalog, session => LocalCandidates(session, Clean(session, sourceId, manga), sourceId).FirstOrDefault());
    }

    /// <summary>Every eligible local entry for a record, best first (score, then more primary records, then lower id).</summary>
    public IReadOnlyList<LocalMatch> LocalMatchCandidates(JsonObject manga, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(manga);
        lock (_gate) return _database.Read(DatabaseFile.Catalog, session => LocalCandidates(session, Clean(session, sourceId, manga), sourceId));
    }

    private List<LocalMatch> LocalCandidates(DatabaseSession session, JsonObject manga, string sourceId)
    {
        var title = CatalogueTitles.Text(manga["title"]);
        var description = CatalogueTitles.Text(manga["description"]);
        var mangaId = CatalogueTitles.MangaId(manga["id"]);
        var candidateTitles = new List<string>();
        if (TextMatcher.IsMatchEvidenceTitle(title, "", primary: true)) candidateTitles.Add(title);
        candidateTitles.AddRange(WholeTitleAliases.Extract(description, [title]).Where(alias => TextMatcher.IsMatchEvidenceTitle(alias)));
        var results = new List<(LocalMatch Match, int Primaries)>();
        foreach (var entryId in CandidateEntryIds(session, manga, 250))
        {
            if (_manual.IsRejected(entryId, sourceId, mangaId)) continue;
            var trusted = TrustedEvidence(session, entryId);
            var conflict = HasPrimary(session, entryId, sourceId);
            var options = OptionsFor(sourceId, trusted.SourceIds) ?? (MatchScoringOptions.Default with { Floor = Floor });
            if (conflict) options = options with { Floor = Math.Max(options.Floor, .97) };
            var decision = Evidence(candidateTitles, description, manga, trusted, options);
            if (PolicyBlocked(session, sourceId, manga, trusted) is { } blocked)
                decision = decision with { Blockers = [.. decision.Blockers, new MatchEvidence("policy", blocked)] };
            if (!decision.AutomaticMatch) continue;
            var reason = "trusted-anchor-title " + ScoredMatchDecision.Percent(decision.TitleScore) + ": " + decision.MatchedLeft + " ↔ " + decision.MatchedRight;
            if (conflict) reason = "additional-source-record:" + reason;
            results.Add((new LocalMatch(entryId, decision.Score, reason, decision), trusted.PrimaryRecords));
        }
        return results.OrderByDescending(item => item.Match.Score).ThenByDescending(item => item.Primaries).ThenBy(item => item.Match.EntryId)
            .Select(item => item.Match).ToList();
    }

    /// <summary>Cheap shortlist (_candidate_entries_for_manga): exact titles first, then word and description fallbacks.</summary>
    private static List<long> CandidateEntryIds(DatabaseSession session, JsonObject manga, int limit)
    {
        var title = CatalogueTitles.Text(manga["title"]).Trim();
        var normalized = TextMatcher.NormalizeTitle(title);
        var ids = new List<long>();
        if (normalized.Length > 0)
        {
            ids.AddRange(ExactCandidateIds(session, normalized, title, limit));
            if (ids.Count == 0)
            {
                var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length >= 3).Take(3).ToArray();
                foreach (var word in words)
                {
                    var like = "%" + word + "%";
                    ids.AddRange(Ids(session, "SELECT DISTINCT entry_id id FROM titles WHERE normalized LIKE $like LIMIT $limit", like, limit));
                    ids.AddRange(Ids(session, "SELECT id FROM entries WHERE lower(preferred_title) LIKE $like LIMIT $limit", like, limit));
                    ids.AddRange(Ids(session, "SELECT DISTINCT entry_id id FROM source_matches WHERE active=1 AND lower(source_title) LIKE $like LIMIT $limit", like, limit));
                    ids.AddRange(Ids(session, "SELECT DISTINCT entry_id id FROM source_variants WHERE active=1 AND lower(source_title) LIKE $like LIMIT $limit", like, limit));
                }
            }
        }
        if (ids.Distinct().Count() < Math.Min(12, limit))
        {
            var words = TextMatcher.NormalizeDescription(CatalogueTitles.Text(manga["description"])).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length >= 7).Distinct(StringComparer.Ordinal).Take(4).ToArray();
            foreach (var word in words)
            {
                var like = "%" + word + "%";
                ids.AddRange(Ids(session, "SELECT id FROM entries WHERE lower(description) LIKE $like LIMIT $limit", like, limit));
                ids.AddRange(Ids(session, "SELECT DISTINCT entry_id id FROM source_matches WHERE active=1 AND lower(description) LIKE $like LIMIT $limit", like, limit));
            }
        }
        return ids.Select(id => CatalogueTitles.Canonical(session, id)).Distinct().Where(id => Exists(session, id)).Take(limit).ToList();
    }

    private static IEnumerable<long> ExactCandidateIds(DatabaseSession session, string normalized, string rawTitle, int limit)
    {
        var ids = new List<long>();
        ids.AddRange(Ids(session, "SELECT DISTINCT entry_id id FROM titles WHERE normalized=$like LIMIT $limit", normalized, limit));
        ids.AddRange(Ids(session, "SELECT id FROM entries WHERE lower(preferred_title)=lower($like) LIMIT $limit", rawTitle, limit));
        ids.AddRange(Ids(session, "SELECT DISTINCT entry_id id FROM source_matches WHERE active=1 AND lower(source_title)=lower($like) LIMIT $limit", rawTitle, limit));
        ids.AddRange(Ids(session, "SELECT DISTINCT entry_id id FROM source_variants WHERE active=1 AND lower(source_title)=lower($like) LIMIT $limit", rawTitle, limit));
        return ids;
    }

    private static IEnumerable<long> Ids(DatabaseSession session, string sql, string value, int limit) =>
        session.Query(sql, ("$like", value), ("$limit", limit)).Select(row => row.Integer("id")).ToArray();

    /// <summary>_trusted_automatic_entry_evidence.</summary>
    public TrustedEntryEvidence TrustedAutomaticEntryEvidence(long entryId) => Read(session => TrustedEvidence(session, CatalogueTitles.Canonical(session, entryId)));

    private static TrustedEntryEvidence TrustedEvidence(DatabaseSession session, long entryId)
    {
        var entry = session.Query("SELECT preferred_title,manual_preferred_title,author,color_mode,media_mode FROM entries WHERE id=$id", ("$id", entryId)).FirstOrDefault();
        var titleRows = session.Query("SELECT title,title_type FROM titles WHERE entry_id=$id AND title_type IN ('manual','main-library') ORDER BY id", ("$id", entryId));
        var records = session.Query("""
            SELECT id record_id,0 is_variant,source_id,source_manga_id,source_title,description,author,artist,genre,metadata_json,match_type,locked FROM source_matches WHERE entry_id=$id AND active=1
            UNION ALL
            SELECT id record_id,1 is_variant,source_id,source_manga_id,source_title,description,author,artist,genre,metadata_json,match_type,locked FROM source_variants WHERE entry_id=$id AND active=1
            """, ("$id", entryId));
        var unverified = CatalogueIdentity.Unverified(session, entryId);
        var primaries = records.Where(row => !row.Flag("is_variant")).ToArray();
        // The ingestion anchor is an automatic choice: an official publisher source (default source ranking) anchors before
        // older records. Manual and locked records are trusted whatever the anchor, so a manual choice is never overridden.
        var official = records.Count > 1 ? SourceRanking.Load(session).Where(rank => rank.Official).Select(rank => rank.SourceId).ToHashSet(StringComparer.Ordinal) : [];
        var anchor = (primaries.Length > 0 ? primaries : records).OrderBy(row => official.Contains(row.Text("source_id")) ? 0 : 1).ThenBy(row => row.Integer("record_id")).FirstOrDefault();
        var trusted = records.Where(row => ReferenceEquals(row, anchor) || row.Flag("locked") || TrustedMatchTypes.Contains(row.Text("match_type").ToLowerInvariant())).ToArray();

        var titles = new List<string>(); var normalized = new HashSet<string>(StringComparer.Ordinal); var descriptions = new List<string>();
        void AddTitle(string value, string type)
        {
            value = value.Trim();
            if (!TextMatcher.IsMatchEvidenceTitle(value, type, primary: type is "source" or "source-variant" or "main-library" or "manual")) return;
            if (normalized.Add(TextMatcher.NormalizeTitle(value))) titles.Add(value);
        }
        if (!unverified) foreach (var row in titleRows) AddTitle(row.Text("title"), row.Text("title_type"));
        foreach (var row in trusted)
        {
            var own = row.Text("source_title");
            AddTitle(own, row.Flag("is_variant") ? "source-variant" : "source");
            var description = row.Text("description").Trim();
            if (description.Length > 0 && !descriptions.Contains(description)) descriptions.Add(description);
            foreach (var alias in WholeTitleAliases.Extract(description, [own])) AddTitle(alias, "description-alias");
        }
        var orphan = entry is not null && records.Count == 0 && !unverified;
        if (entry is not null && entry.Flag("manual_preferred_title") && !unverified) AddTitle(entry.Text("preferred_title"), "manual");
        var bootstrapAuthor = "";
        if (orphan)
        {
            if (titles.Count == 0) AddTitle(entry!.Text("preferred_title"), "main-library");
            // Metadata accepted for the exact orphan title may corroborate (summary / author), never authorize.
            foreach (var identity in session.Query("""
                SELECT title,description,author,metadata_json FROM identity_links
                WHERE entry_id=$id AND confidence>=0.94 AND match_type LIKE 'metadata:bootstrap-exact preferred title:%' ORDER BY confidence DESC,last_checked DESC
                """, ("$id", entryId)))
            {
                var identityTitles = new List<string> { identity.Text("title") };
                try
                {
                    if (JsonNode.Parse(identity.Text("metadata_json", "{}")) is JsonObject metadata && metadata["titles"] is JsonArray more)
                        identityTitles.AddRange(more.Select(item => item is JsonObject named ? CatalogueTitles.Text(named["title"]) : CatalogueTitles.Text(item)));
                }
                catch (JsonException) { }
                if (!identityTitles.Any(text => text.Length > 0 && normalized.Contains(TextMatcher.NormalizeTitle(text)))) continue;
                var description = identity.Text("description").Trim();
                if (description.Length > 0 && !descriptions.Contains(description)) descriptions.Add(description);
                if (bootstrapAuthor.Length == 0) bootstrapAuthor = identity.Text("author").Trim();
            }
        }
        var author = trusted.Select(row => row.Text("author").Trim()).FirstOrDefault(text => text.Length > 0) ?? "";
        if (author.Length == 0 && entry is not null && entry.Flag("manual_preferred_title")) author = entry.Text("author");
        else if (author.Length == 0 && orphan) author = bootstrapAuthor;
        var creators = trusted.SelectMany(row => new[] { row.Text("author").Trim(), row.Text("artist").Trim() }).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (creators.Count == 0 && author.Trim().Length > 0) creators.Add(author.Trim());
        return new(entryId, titles, descriptions, author, records.Select(row => row.Text("source_id")).Distinct(StringComparer.Ordinal).ToArray(),
            entry?.Text("color_mode", "unknown") ?? "unknown", entry?.Text("media_mode", "unknown") ?? "unknown", unverified, primaries.Length)
        {
            Creators = creators,
            Genres = trusted.Select(row => row.Text("genre")).Where(genre => genre.Trim().Length > 0).ToArray(),
            Year = trusted.Select(row => RecordYear(row.Text("metadata_json"))).FirstOrDefault(year => year is not null),
        };
    }

    /// <summary>
    /// _entry_matching_policy: the stricter scorer options the matching rules of the involved sources ask for, or null.
    /// A rule set from a simple/advanced profile brings that profile's options (identity evidence, year tolerance, cover
    /// distances, weights); the automatic floor in use is never lowered.
    /// </summary>
    private MatchScoringOptions? OptionsFor(string candidateSource, IReadOnlyList<string> entrySources)
    {
        if (Policies is null || Policies.Rules().Count == 0) return null;
        var options = MatchProfiles.Combine(Rules(candidateSource, entrySources).Select(item => item.Rule).OfType<JsonObject>()
            .Where(rule => rule["checks"] is JsonObject).Select(rule => Presets?.OptionsForRule(rule) ?? MatchProfiles.OptionsFor(rule, null)));
        return options is null ? null : options with { Floor = Math.Max(options.Floor, Floor) };
    }

    /// <summary>A disabled rule refuses automation; a tags check compares the record's genres/tags with the entry's trusted
    /// records through the tag synonyms (missing tags or an overlap below the threshold refuse).</summary>
    private string? PolicyBlocked(DatabaseSession session, string candidateSource, JsonObject manga, TrustedEntryEvidence trusted)
    {
        if (Policies is null || Policies.Rules().Count == 0) return null;
        double? needed = null; var tagOrigin = "";
        foreach (var (rule, origin) in Rules(candidateSource, trusted.SourceIds))
        {
            if (rule is null) continue;
            if (!MatchProfiles.Flag(rule["enabled"])) return origin + " is disabled";
            if (rule["checks"] is JsonObject checks && checks.ContainsKey("tags"))
            {
                var threshold = MatchProfiles.Number(checks["tags"]) ?? 1;
                if (needed is null || threshold > needed) { needed = threshold; tagOrigin = origin; }
            }
        }
        if (needed is not { } required) return null;
        var map = TagSynonyms.Load(session);
        var candidate = PolicyTags.Keys(manga["genre"] ?? manga["genres"], manga["tags"], map);
        var entry = trusted.Genres.Select(genre => (IReadOnlySet<string>)PolicyTags.Keys(JsonValue.Create(genre), null, map)).ToArray();
        if (PolicyTags.Overlap([candidate], entry) is not { } score) return tagOrigin + ": tags evidence missing";
        return score + 1e-12 >= required ? null : tagOrigin + ": tags " + ScoredMatchDecision.Percent(score) + " is below " + ScoredMatchDecision.Percent(required);
    }

    private IEnumerable<(JsonObject? Rule, string Origin)> Rules(string candidateSource, IReadOnlyList<string> entrySources)
    {
        foreach (var other in entrySources.DefaultIfEmpty(""))
        {
            var direction = other.Length > 0 && other == candidateSource ? "internal" : "external";
            foreach (var source in new[] { candidateSource, other }.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
            {
                var (rule, origin) = Policies!.Effective(source, direction);
                yield return (rule, direction + " matching · " + origin);
            }
        }
    }

    // ---- record writes ---------------------------------------------------------------------------------------------

    /// <summary>_upsert_match: writes the entry's primary record for this source; a locked row is only replaced by a manual match.</summary>
    public long UpsertMatch(long entryId, string sourceId, JsonObject manga, double confidence, string matchType = "auto", bool locked = false)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var followUps = new List<Action>(); long id;
        lock (_gate) id = _database.Write(DatabaseFile.Catalog, session =>
            UpsertMatch(session, CatalogueTitles.Canonical(session, entryId), sourceId, Clean(session, sourceId, manga), confidence, matchType, locked, Now(), followUps));
        foreach (var action in followUps) action();
        return id;
    }

    /// <summary>_upsert_source_variant: writes an additional record of this source (moving the exact record if it was elsewhere).</summary>
    public long UpsertSourceVariant(long entryId, string sourceId, JsonObject manga, double confidence = 1, string matchType = "auto", bool locked = false)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var followUps = new List<Action>(); long id;
        lock (_gate) id = _database.Write(DatabaseFile.Catalog, session =>
            UpsertVariant(session, CatalogueTitles.Canonical(session, entryId), sourceId, Clean(session, sourceId, manga), confidence, matchType, locked, Now(), followUps));
        foreach (var action in followUps) action();
        return id;
    }

    private long UpsertMatch(DatabaseSession session, long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked,
        string stamp, List<Action> followUps)
    {
        var mangaId = CatalogueTitles.MangaId(manga["id"]);
        var old = session.Query("SELECT id,source_manga_id,locked FROM source_matches WHERE entry_id=$id AND source_id=$source", ("$id", entryId), ("$source", sourceId)).FirstOrDefault();
        if (old is not null && old.Flag("locked") && matchType != "manual") return old.Integer("id");
        var moved = old is not null && old.Integer("source_manga_id") != mangaId;
        if (moved) CatalogueTitles.PurgeSourceRecordTitles(session, entryId, sourceId, [old!.Integer("source_manga_id")], dropPreferredOverride: true);
        var displaced = matchType == "manual" ? ClaimManualSourceRecord(session, entryId, sourceId, mangaId, stamp) : [];
        session.Execute("""
            INSERT INTO source_matches(entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,match_type,locked,active,initialized,
              thumbnail_url,description,author,artist,genre,status,real_url,metadata_json,last_checked)
            VALUES($entry,$source,$manga,$url,$title,$confidence,$type,$locked,1,$initialized,$thumbnail,$description,$author,$artist,$genre,$status,$real,$metadata,$time)
            ON CONFLICT(entry_id,source_id) DO UPDATE SET
              source_manga_id=excluded.source_manga_id,
              source_manga_url=CASE WHEN excluded.source_manga_url<>'' THEN excluded.source_manga_url WHEN source_matches.source_manga_id=excluded.source_manga_id THEN source_matches.source_manga_url ELSE '' END,
              source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE source_matches.source_title END,
              confidence=excluded.confidence,match_type=excluded.match_type,locked=excluded.locked,active=1,
              initialized=CASE WHEN source_matches.source_manga_id=excluded.source_manga_id THEN MAX(source_matches.initialized,excluded.initialized) ELSE excluded.initialized END,
              thumbnail_url=CASE WHEN excluded.thumbnail_url<>'' THEN excluded.thumbnail_url WHEN source_matches.source_manga_id=excluded.source_manga_id THEN source_matches.thumbnail_url ELSE '' END,
              description=CASE WHEN source_matches.source_manga_id<>excluded.source_manga_id THEN excluded.description
                WHEN length(excluded.description)>=length(source_matches.description) THEN excluded.description ELSE source_matches.description END,
              author=CASE WHEN excluded.author<>'' THEN excluded.author WHEN source_matches.source_manga_id=excluded.source_manga_id THEN source_matches.author ELSE '' END,
              artist=CASE WHEN excluded.artist<>'' THEN excluded.artist WHEN source_matches.source_manga_id=excluded.source_manga_id THEN source_matches.artist ELSE '' END,
              genre=CASE WHEN excluded.genre<>'' THEN excluded.genre WHEN source_matches.source_manga_id=excluded.source_manga_id THEN source_matches.genre ELSE '' END,
              status=CASE WHEN excluded.status<>0 THEN excluded.status WHEN source_matches.source_manga_id=excluded.source_manga_id THEN source_matches.status ELSE 0 END,
              real_url=CASE WHEN excluded.real_url<>'' THEN excluded.real_url WHEN source_matches.source_manga_id=excluded.source_manga_id THEN source_matches.real_url ELSE '' END,
              metadata_json=CASE WHEN source_matches.source_manga_id<>excluded.source_manga_id THEN excluded.metadata_json
                WHEN excluded.initialized=1 OR length(excluded.metadata_json)>length(source_matches.metadata_json) THEN excluded.metadata_json ELSE source_matches.metadata_json END,
              last_checked=excluded.last_checked
            """, RecordParameters(entryId, sourceId, mangaId, manga, confidence, matchType, locked, stamp));
        var row = session.Query("SELECT id,source_manga_url,real_url FROM source_matches WHERE entry_id=$id AND source_id=$source", ("$id", entryId), ("$source", sourceId)).First();
        CatalogueIdentity.BindNative(session, entryId, sourceId, mangaId, row.Text("source_manga_url"), row.Text("real_url") is { Length: > 0 } real ? real : null);
        // Promoting a former additional record of this exact manga removes its variant row (chapter candidates stay).
        session.Execute("DELETE FROM source_variants WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga", ("$id", entryId), ("$source", sourceId), ("$manga", mangaId));
        if (moved)
            session.Execute("INSERT INTO match_history(entry_id,source_id,old_source_manga_id,new_source_manga_id,change_type,changed_at) VALUES($id,$source,$old,$new,$type,$time)",
                ("$id", entryId), ("$source", sourceId), ("$old", old!.Integer("source_manga_id")), ("$new", mangaId), ("$type", matchType), ("$time", stamp));
        AfterRecordWrite(session, entryId, sourceId, mangaId, manga, displaced, stamp, followUps);
        return row.Integer("id");
    }

    private long UpsertVariant(DatabaseSession session, long entryId, string sourceId, JsonObject manga, double confidence, string matchType, bool locked,
        string stamp, List<Action> followUps)
    {
        var mangaId = CatalogueTitles.MangaId(manga["id"]);
        var old = session.Query("SELECT id,locked,match_type FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1",
            ("$source", sourceId), ("$manga", mangaId)).FirstOrDefault();
        if (old is not null && (old.Flag("locked") || old.Text("match_type").Equals("manual", StringComparison.OrdinalIgnoreCase)) && matchType != "manual")
            return old.Integer("id");
        var displaced = matchType == "manual" ? ClaimManualSourceRecord(session, entryId, sourceId, mangaId, stamp) : [];
        session.Execute("""
            INSERT INTO source_variants(entry_id,source_id,source_manga_id,source_manga_url,source_title,confidence,match_type,locked,active,initialized,
              thumbnail_url,description,author,artist,genre,status,real_url,metadata_json,last_checked)
            VALUES($entry,$source,$manga,$url,$title,$confidence,$type,$locked,1,$initialized,$thumbnail,$description,$author,$artist,$genre,$status,$real,$metadata,$time)
            ON CONFLICT(source_id,source_manga_id) DO UPDATE SET
              entry_id=excluded.entry_id,
              source_manga_url=CASE WHEN excluded.source_manga_url<>'' THEN excluded.source_manga_url ELSE source_variants.source_manga_url END,
              source_title=CASE WHEN excluded.source_title<>'' THEN excluded.source_title ELSE source_variants.source_title END,
              confidence=excluded.confidence,match_type=excluded.match_type,locked=excluded.locked,active=1,
              initialized=MAX(source_variants.initialized,excluded.initialized),
              thumbnail_url=CASE WHEN excluded.thumbnail_url<>'' THEN excluded.thumbnail_url ELSE source_variants.thumbnail_url END,
              description=CASE WHEN length(excluded.description)>=length(source_variants.description) THEN excluded.description ELSE source_variants.description END,
              author=CASE WHEN excluded.author<>'' THEN excluded.author ELSE source_variants.author END,
              artist=CASE WHEN excluded.artist<>'' THEN excluded.artist ELSE source_variants.artist END,
              genre=CASE WHEN excluded.genre<>'' THEN excluded.genre ELSE source_variants.genre END,
              status=CASE WHEN excluded.status<>0 THEN excluded.status ELSE source_variants.status END,
              real_url=CASE WHEN excluded.real_url<>'' THEN excluded.real_url ELSE source_variants.real_url END,
              metadata_json=CASE WHEN excluded.initialized=1 OR length(excluded.metadata_json)>length(source_variants.metadata_json) THEN excluded.metadata_json ELSE source_variants.metadata_json END,
              last_checked=excluded.last_checked
            """, RecordParameters(entryId, sourceId, mangaId, manga, confidence, matchType, locked, stamp));
        var row = session.Query("SELECT id,source_manga_url,real_url FROM source_variants WHERE source_id=$source AND source_manga_id=$manga", ("$source", sourceId), ("$manga", mangaId)).First();
        CatalogueIdentity.BindNative(session, entryId, sourceId, mangaId, row.Text("source_manga_url"), row.Text("real_url") is { Length: > 0 } real ? real : null);
        AfterRecordWrite(session, entryId, sourceId, mangaId, manga, displaced, stamp, followUps);
        return row.Integer("id");
    }

    private void AfterRecordWrite(DatabaseSession session, long entryId, string sourceId, long mangaId, JsonObject manga, HashSet<long> displaced, string stamp, List<Action> followUps)
    {
        foreach (var owner in displaced.Select(id => CatalogueTitles.Canonical(session, id)).Where(id => id != entryId).Distinct().Order())
        {
            CatalogueTitles.PurgeSourceRecordTitles(session, owner, sourceId, [mangaId], dropPreferredOverride: true);
            CatalogueTitles.SyncPreferredTitle(session, owner, stamp);
            followUps.Add(() => _followUps.Invalidate(owner));
        }
        CatalogueTitles.RefreshAttachedSourceTitles(session, entryId, sourceId, manga);
        CatalogueTitles.SyncPreferredTitle(session, entryId, stamp);
        var copy = (JsonObject)manga.DeepClone();
        followUps.Add(() => _followUps.RecordWritten(entryId, sourceId, mangaId, copy));
        followUps.Add(() => _followUps.Invalidate(entryId));
    }

    /// <summary>_claim_manual_source_record: a manual choice deactivates the exact record on every other entry.</summary>
    private static HashSet<long> ClaimManualSourceRecord(DatabaseSession session, long entryId, string sourceId, long mangaId, string stamp)
    {
        var displaced = session.Query("""
            SELECT entry_id FROM source_matches WHERE source_id=$source AND source_manga_id=$manga AND active=1 AND entry_id<>$id
            UNION SELECT entry_id FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1 AND entry_id<>$id
            """, ("$source", sourceId), ("$manga", mangaId), ("$id", entryId)).Select(row => row.Integer("entry_id")).ToHashSet();
        if (displaced.Count > 0)
            foreach (var table in new[] { "source_matches", "source_variants" })
                session.Execute("UPDATE " + table + " SET active=0,locked=0,match_type='manual-moved',last_checked=$time WHERE source_id=$source AND source_manga_id=$manga AND active=1 AND entry_id<>$id",
                    ("$time", stamp), ("$source", sourceId), ("$manga", mangaId), ("$id", entryId));
        return displaced;
    }

    private static (string, object?)[] RecordParameters(long entryId, string sourceId, long mangaId, JsonObject manga, double confidence, string matchType, bool locked, string stamp) =>
    [
        ("$entry", entryId), ("$source", sourceId), ("$manga", mangaId), ("$url", CatalogueTitles.Text(manga["url"])), ("$title", CatalogueTitles.Text(manga["title"])),
        ("$confidence", confidence), ("$type", matchType), ("$locked", locked ? 1 : 0), ("$initialized", Truth(manga["initialized"]) ? 1 : 0),
        ("$thumbnail", CatalogueTitles.Text(manga["thumbnailUrl"])), ("$description", TextMatcher.CleanSummary(CatalogueTitles.Text(manga["description"]))),
        ("$author", CatalogueTitles.Text(manga["author"])), ("$artist", CatalogueTitles.Text(manga["artist"])), ("$genre", NativeRecordStore.GenreText(manga["genre"])),
        ("$status", NativeRecordStore.Status(manga["status"])), ("$real", CatalogueTitles.Text(manga["realUrl"])), ("$metadata", manga.ToJsonString()), ("$time", stamp),
    ];

    /// <summary>_variant_payload: a stored record row as a source payload (row fields win over its metadata_json).</summary>
    public static JsonObject VariantPayload(DatabaseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        JsonObject payload;
        try { payload = JsonNode.Parse(row.Text("metadata_json", "{}")) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { payload = new JsonObject(); }
        string Pick(string column, string key) => row.Text(column) is { Length: > 0 } value ? value : CatalogueTitles.Text(payload[key]);
        var values = new JsonObject
        {
            ["id"] = row.Integer("source_manga_id"), ["url"] = Pick("source_manga_url", "url"), ["title"] = Pick("source_title", "title"),
            ["thumbnailUrl"] = Pick("thumbnail_url", "thumbnailUrl"), ["description"] = Pick("description", "description"),
            ["author"] = Pick("author", "author"), ["artist"] = Pick("artist", "artist"), ["genre"] = Pick("genre", "genre"),
            ["status"] = row.ContainsKey("status") && row["status"] is not null ? (JsonNode)row.Integer("status") : payload["status"]?.DeepClone() ?? (JsonNode)0,
            ["realUrl"] = Pick("real_url", "realUrl"),
        };
        foreach (var (key, value) in values) payload[key] = value?.DeepClone();
        return payload;
    }

    /// <summary>_apply_source_detail_if_current: hydrates a still-owned record with its current role / manual / lock state.</summary>
    public bool ApplySourceDetailIfCurrent(long entryId, string sourceId, long sourceMangaId, JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        if (manga["id"] is not null && CatalogueTitles.MangaId(manga["id"]) != sourceMangaId) return false;
        var followUps = new List<Action>(); bool applied;
        lock (_gate) applied = _database.Write(DatabaseFile.Catalog, session =>
        {
            var id = CatalogueTitles.Canonical(session, entryId);
            var current = session.Query("""
                SELECT confidence,match_type,locked,0 is_variant FROM source_matches WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1
                UNION ALL SELECT confidence,match_type,locked,1 is_variant FROM source_variants WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1 LIMIT 1
                """, ("$id", id), ("$source", sourceId), ("$manga", sourceMangaId)).FirstOrDefault();
            if (current is null) return false;
            var payload = (JsonObject)manga.DeepClone(); payload["id"] = sourceMangaId;
            payload = Clean(session, sourceId, payload);
            var confidence = current.Number("confidence", 1); if (confidence == 0) confidence = 1;
            var matchType = current.Text("match_type") is { Length: > 0 } type ? type : "auto";
            if (current.Flag("is_variant")) UpsertVariant(session, id, sourceId, payload, confidence, matchType, current.Flag("locked"), Now(), followUps);
            else UpsertMatch(session, id, sourceId, payload, confidence, matchType, current.Flag("locked"), Now(), followUps);
            return true;
        });
        foreach (var action in followUps) action();
        return applied;
    }

    /// <summary>
    /// _attach_automatic_record_if_current: after remote work, re-checks ownership, separation and the full local proof
    /// under the gate, then attaches (as additional record when the source already has a different primary).
    /// </summary>
    public bool AttachAutomaticRecordIfCurrent(long entryId, string sourceId, JsonObject manga, double score, string matchType, bool variantOnly = false)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var followUps = new List<Action>(); bool attached;
        lock (_gate) attached = _database.Write(DatabaseFile.Catalog, session =>
        {
            var id = CatalogueTitles.Canonical(session, entryId);
            var payload = Clean(session, sourceId, manga);
            var mangaId = CatalogueTitles.MangaId(payload["id"]);
            var owner = FindExisting(session, sourceId, mangaId);
            if ((owner is not null && owner != id) || _manual.IsSeparated(id, sourceId, mangaId) || _manual.IsRejected(id, sourceId, mangaId) || !Exists(session, id)) return false;
            var primary = session.Scalar("SELECT source_manga_id FROM source_matches WHERE entry_id=$id AND source_id=$source AND active=1", ("$id", id), ("$source", sourceId)) as long?;
            if (variantOnly && primary is null) return false;
            var finalScore = score; var reason = matchType;
            if (owner is null)
            {
                var evidence = SourceMatchEvidence(session, id, payload, sourceId);
                if (!evidence.AutomaticMatch) return false;
                finalScore = Math.Max(Floor, evidence.Score); reason = evidence.Reason;
                _decisions.Record(new("match.automatic", DecisionActor.Automatic, [id], [Key(sourceId, mangaId)], reason, null, new JsonObject
                {
                    ["source_id"] = sourceId, ["source_manga_id"] = mangaId, ["match_type"] = matchType, ["variant"] = primary is not null && primary != mangaId,
                    ["score"] = finalScore, ["decision"] = evidence.ToJson(),
                }));
            }
            if (primary is not null && primary != mangaId) UpsertVariant(session, id, sourceId, payload, finalScore, matchType, false, Now(), followUps);
            else UpsertMatch(session, id, sourceId, payload, finalScore, matchType, false, Now(), followUps);
            return true;
        });
        foreach (var action in followUps) action();
        return attached;
    }

    /// <summary>The strict evidence of one record against an entry's trusted evidence (SourceDiscovery uses it too).</summary>
    internal ScoredMatchDecision SourceMatchEvidence(DatabaseSession session, long entryId, JsonObject manga, string sourceId, TrustedEntryEvidence? trusted = null)
    {
        trusted ??= TrustedEvidence(session, entryId);
        var title = CatalogueTitles.Text(manga["title"]); var description = CatalogueTitles.Text(manga["description"]);
        var titles = new List<string>();
        if (TextMatcher.IsMatchEvidenceTitle(title, "", primary: true)) titles.Add(title);
        titles.AddRange(WholeTitleAliases.Extract(description, [title]).Where(alias => TextMatcher.IsMatchEvidenceTitle(alias)));
        var options = OptionsFor(sourceId, trusted.SourceIds) ?? (MatchScoringOptions.Default with { Floor = Floor });
        var decision = Evidence(titles, description, manga, trusted, options);
        return PolicyBlocked(session, sourceId, manga, trusted) is { } blocked ? decision with { Blockers = [.. decision.Blockers, new MatchEvidence("policy", blocked)] } : decision;
    }

    private static int? RecordYear(string metadata)
    {
        if (metadata.Length == 0) return null;
        try { return JsonNode.Parse(metadata) is JsonObject payload ? MatchCandidate.YearOf(payload["year"]) : null; }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> TrustedCreators(TrustedEntryEvidence trusted)
    {
        if (trusted.Creators.Count > 0) return trusted.Creators;
        return trusted.Author.Trim().Length > 0 ? new[] { trusted.Author } : Array.Empty<string>();
    }

    /// <summary>The scorer on one record against an entry's trusted evidence: every creator (author and artist) of both
    /// sides, so creator divergence (adaptations with other artists) blocks as in the automatic duplicate gate.</summary>
    private static ScoredMatchDecision Evidence(IReadOnlyList<string> titles, string description, JsonObject manga, TrustedEntryEvidence trusted, MatchScoringOptions options)
    {
        var decision = CatalogueTitles.StrictAutomaticTitleEvidence(titles, trusted.Titles, description, trusted.Descriptions, "", "", options,
            new MatchCandidate { Authors = Authors(manga), Year = MatchCandidate.YearOf(manga["year"]) },
            new MatchCandidate
            {
                Authors = TrustedCreators(trusted), Year = trusted.Year,
                ColorHint = Known(trusted.ColorMode, "colored", "monochrome"), MediaHint = Known(trusted.MediaMode, "manga", "webtoon", "novel"),
            });
        // The trusted year can refuse (a different year is a different work) but never alone admits a generic title.
        var generic = decision.MatchedLeft.Length == 0 || !MatchScorer.IsDistinctive(decision.MatchedLeft) || !MatchScorer.IsDistinctive(decision.MatchedRight);
        if (decision.AutomaticMatch && generic && !decision.Evidence.Any(item => item.Kind is "author" or "cover" or "summary"))
            decision = decision with { Blockers = [.. decision.Blockers, new MatchEvidence("identity-evidence", "a generic title needs agreeing creator, cover or summary evidence; a shared year alone is not enough")] };
        return decision;
    }

    internal T WithRead<T>(Func<DatabaseSession, T> operation) => Read(operation);

    // ---- search names ----------------------------------------------------------------------------------------------

    /// <summary>
    /// _expanded_search_names: the extra query, the main title, aliases and source titles (each normalized title once), plus
    /// an offline romanisation of non-Latin titles. Generated translations are not part of this port (phase 10 translation).
    /// </summary>
    public IReadOnlyList<SearchName> ExpandedSearchNames(long entryId, string extraQuery = "") => Read(session =>
    {
        var id = CatalogueTitles.Canonical(session, entryId);
        var names = new List<SearchName>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value, string kind, string language)
        {
            value = value.Trim(); var normalized = TextMatcher.NormalizeTitle(value);
            var key = kind == "transliteration" ? "transliteration:" + value.ToLowerInvariant() : normalized;
            if (normalized.Length == 0 || !seen.Add(key)) return;
            names.Add(new(value, kind, language));
        }
        if (extraQuery.Trim().Length > 0) Add(extraQuery, "manual", "");
        foreach (var row in CatalogueTitles.TitleRows(session, id))
        {
            if (row.TitleType.StartsWith("translated-", StringComparison.Ordinal)) continue;
            Add(row.Title, row.TitleType, row.Language);
            if (!TextMatcher.LooksEnglish(row.Title)) Add(row.Normalized, "transliteration", row.Language);
        }
        return names;
    });

    // ---- reads for the UI ------------------------------------------------------------------------------------------

    /// <summary>_entry_summaries: browse cards for many entries (requested order, aliases resolved, missing ones skipped).</summary>
    public IReadOnlyList<JsonObject> EntrySummaries(IEnumerable<long> entryIds)
    {
        var requested = entryIds.ToArray();
        return Read(session => Summaries(session, requested));
    }

    /// <summary>_entry_summary (KeyNotFoundException when the entry does not exist).</summary>
    public JsonObject EntrySummary(long entryId) => EntrySummaries([entryId]).FirstOrDefault()
        ?? throw new KeyNotFoundException("Entry " + entryId.ToString(CultureInfo.InvariantCulture) + " not found.");

    private List<JsonObject> Summaries(DatabaseSession session, IReadOnlyList<long> requested)
    {
        var output = new List<JsonObject>();
        var ranking = SourceRanking.Positions(SourceRanking.Load(session));
        foreach (var requestedId in requested)
        {
            var id = CatalogueTitles.Canonical(session, requestedId);
            var entry = session.Query("SELECT * FROM entries WHERE id=$id", ("$id", id)).FirstOrDefault();
            if (entry is null) continue;
            var families = new Dictionary<(string, string), (string Name, SortedSet<string> Languages, long Priority, int Rank)>();
            foreach (var row in session.Query("""
                SELECT sm.source_id,COALESCE(sc.name,sm.source_id) name,COALESCE(sc.base_url,'') base_url,COALESCE(sc.lang,'') lang,COALESCE(sc.priority,1000) priority
                FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.active=1 AND sm.entry_id=$id
                """, ("$id", id)))
            {
                var key = (row.Text("name").ToLowerInvariant(), row.Text("base_url").ToLowerInvariant());
                var rank = ranking.TryGetValue(row.Text("source_id"), out var position) ? position : int.MaxValue;
                if (!families.TryGetValue(key, out var family)) family = (row.Text("name"), new SortedSet<string>(StringComparer.Ordinal), row.Integer("priority", 1000), rank);
                if (row.Text("lang").Length > 0) family.Languages.Add(row.Text("lang"));
                families[key] = (family.Name, family.Languages, Math.Min(family.Priority, row.Integer("priority", 1000)), Math.Min(family.Rank, rank));
            }
            var records = Convert.ToInt64(session.Scalar("""
                SELECT (SELECT COUNT(*) FROM source_matches WHERE active=1 AND entry_id=$id)+(SELECT COUNT(*) FROM source_variants WHERE active=1 AND entry_id=$id)
                """, ("$id", id)), CultureInfo.InvariantCulture);
            var summary = new JsonObject();
            foreach (var (column, value) in entry) summary[column] = JsonValueOf(value);
            var missing = new JsonArray();
            if (entry.Text("thumbnail_url").Trim().Length == 0) missing.Add((JsonNode)"thumbnail");
            if (entry.Text("description").Trim().Length < 40) missing.Add((JsonNode)"description");
            if (entry.Text("author").Trim().Length == 0) missing.Add((JsonNode)"author");
            if (entry.Text("artist").Trim().Length == 0) missing.Add((JsonNode)"artist");
            if (entry.Text("genre").Trim().Length == 0) missing.Add((JsonNode)"genre");
            if (entry.Integer("status") == 0) missing.Add((JsonNode)"status");
            var status = entry.Integer("status");
            summary["match_count"] = families.Count;
            summary["entry_match_count"] = records;
            // Default ranking: official publisher sources first, then health, then quality (priority).
            summary["sources"] = new JsonArray(families.Values.OrderBy(family => family.Rank).ThenBy(family => family.Priority)
                .ThenBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
                .Select(family => (JsonNode)(family.Languages.Count > 0 ? family.Name + " (" + string.Join(", ", family.Languages) + ")" : family.Name)).ToArray());
            summary["status_label"] = status >= 0 && status < StatusLabels.Length ? StatusLabels[status] : "Unknown";
            summary["url"] = requestedId.ToString(CultureInfo.InvariantCulture);
            summary["canonical_id"] = id;
            summary["cover_url"] = "/api/v1/covers/" + requestedId.ToString(CultureInfo.InvariantCulture);
            summary["cover_cached"] = CoverCached?.Invoke("entry-" + id.ToString(CultureInfo.InvariantCulture)) ?? false;
            summary["metadata_complete"] = missing.Count == 0;
            summary["missing_metadata"] = missing;
            output.Add(summary);
        }
        return output;
    }

    /// <summary>_entry_reliability: (locked records, primary records, description length, stored titles).</summary>
    public (long Locked, long Matches, int DescriptionLength, long Titles) EntryReliability(long entryId) => Read(session =>
    {
        var id = CatalogueTitles.Canonical(session, entryId);
        var counts = session.Query("SELECT COALESCE(SUM(CASE WHEN locked=1 THEN 1 ELSE 0 END),0) locked_count,COUNT(*) match_count FROM source_matches WHERE entry_id=$id AND active=1", ("$id", id)).First();
        var description = session.Scalar("SELECT description FROM entries WHERE id=$id", ("$id", id)) as string ?? "";
        var titles = Convert.ToInt64(session.Scalar("SELECT COUNT(*) FROM titles WHERE entry_id=$id", ("$id", id)), CultureInfo.InvariantCulture);
        return (counts.Integer("locked_count"), counts.Integer("match_count"), description.Length, titles);
    });

    /// <summary>get_entry: the summary plus titles, records (with chapter coverage when available), identities and the extension description.</summary>
    public JsonObject GetEntry(long entryId)
    {
        var entry = Read(session =>
        {
            var id = CatalogueTitles.Canonical(session, entryId);
            var output = Summaries(session, [entryId]).FirstOrDefault() ?? throw new KeyNotFoundException("Entry " + entryId.ToString(CultureInfo.InvariantCulture) + " not found.");
            output["titles"] = Rows(session.Query("SELECT * FROM titles WHERE entry_id=$id ORDER BY title_type,title COLLATE NOCASE", ("$id", id)));
            output["matches"] = Rows(session.Query("""
                SELECT sm.*,sc.name source_name,sc.lang source_lang,sc.priority source_priority,sc.base_url base_url
                FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1 ORDER BY sc.priority,sm.source_title
                """, ("$id", id)));
            output["source_variants"] = Rows(session.Query("""
                SELECT sv.*,sc.name source_name,sc.lang source_lang,sc.priority source_priority,sc.base_url base_url
                FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1 ORDER BY sc.priority,sv.source_title
                """, ("$id", id)));
            output["identities"] = Rows(session.Query("""
                SELECT provider,provider_name,external_id,external_url,identity_scope,confidence,match_type,locked,title,description,thumbnail_url,status,author,artist,
                       genre,media_type,country,year,adult,last_checked
                FROM identity_links WHERE entry_id=$id
                ORDER BY CASE provider WHEN 'mangadex' THEN 10 WHEN 'anilist' THEN 20 WHEN 'mangaupdates' THEN 30 WHEN 'kitsu' THEN 40 WHEN 'myanimelist' THEN 50
                  WHEN 'comicvine' THEN 60 ELSE 999 END,provider
                """, ("$id", id)));
            output["canonical_id"] = id;
            return output;
        });
        var canonical = entry["canonical_id"]!.GetValue<long>();
        var stats = ChapterStats?.Invoke(canonical);
        foreach (var key in new[] { "matches", "source_variants" })
            foreach (var record in (entry[key] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var stat = stats?.GetValueOrDefault(CatalogueTitles.Text(record["source_id"]) + ":" + CatalogueTitles.Text(record["source_manga_id"]));
                record["chapter_count"] = stat?["chapter_count"]?.DeepClone() ?? 0;
                record["latest_chapter"] = stat?["latest_chapter"]?.DeepClone();
                record["missing_chapter_count"] = stat?["missing_chapter_count"]?.DeepClone() ?? 0;
                record["chapters_checked"] = stat?["chapters_checked"]?.DeepClone() ?? false;
                record["chapter_checked_at"] = stat?["chapter_checked_at"]?.DeepClone();
                record["chapter_check_error"] = stat?["chapter_check_error"]?.DeepClone() ?? "";
            }
        entry["description_for_extension"] = DescriptionForExtension(entry);
        return entry;
    }

    /// <summary>description_for_extension: description, alternative titles and source names for the Tachiyomi extension.</summary>
    public static string DescriptionForExtension(JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var preferred = TextMatcher.NormalizeTitle(CatalogueTitles.Text(entry["preferred_title"]));
        var aliases = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var title in (entry["titles"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (CatalogueTitles.Text(title["title_type"]).StartsWith("translated-", StringComparison.Ordinal)) continue;
            var normalized = CatalogueTitles.Text(title["normalized"]);
            if (normalized.Length > 0 && normalized != preferred && seen.Add(normalized)) aliases.Add(CatalogueTitles.Text(title["title"]));
        }
        var groups = new Dictionary<(string, string), (string Name, SortedSet<string> Languages, long Priority)>();
        foreach (var match in (entry["matches"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var name = CatalogueTitles.Text(match["source_name"]) is { Length: > 0 } known ? known : CatalogueTitles.Text(match["source_id"]);
            var key = (name.ToLowerInvariant(), CatalogueTitles.Text(match["base_url"]).ToLowerInvariant());
            var priority = long.TryParse(CatalogueTitles.Text(match["source_priority"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value != 0 ? value : 1000;
            if (!groups.TryGetValue(key, out var group)) group = (name, new SortedSet<string>(StringComparer.Ordinal), priority);
            if (CatalogueTitles.Text(match["source_lang"]) is { Length: > 0 } language) group.Languages.Add(language);
            groups[key] = (group.Name, group.Languages, Math.Min(group.Priority, priority));
        }
        var sources = groups.Values.OrderBy(group => group.Priority).ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Languages.Count > 0 ? group.Name + " (" + string.Join(", ", group.Languages) + ")" : group.Name).ToArray();
        var parts = new List<string> { CatalogueTitles.Text(entry["description"]).Trim() };
        if (aliases.Count > 0) parts.Add("Alternate titles\n" + string.Join('\n', aliases));
        if (sources.Length > 0) parts.Add("Sources\n" + string.Join('\n', sources));
        return string.Join("\n\n────────────\n\n", parts.Where(part => part.Length > 0));
    }

    /// <summary>
    /// refresh_entry: fetches every enabled record again (bounded by <paramref name="timeout"/>), applies details only to records
    /// that are still owned, then returns get_entry. Chapter storage is the reader's job (phase 10) and runs through <paramref name="chapters"/>.
    /// </summary>
    public async Task<JsonObject> RefreshEntryAsync(long entryId, Func<long, bool, bool, CancellationToken, Task<JsonObject?>> fetch,
        bool fetchDetails = true, bool fetchChapters = true, Action<long, string, long, JsonArray>? chapters = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var records = Read(session =>
        {
            var id = CatalogueTitles.Canonical(session, entryId);
            return session.Query("""
                SELECT sm.source_id,sm.source_manga_id,sc.priority FROM source_matches sm JOIN source_config sc ON sc.source_id=sm.source_id
                WHERE sm.entry_id=$id AND sm.active=1 AND sc.available=1 AND sc.enabled_language=1 AND sc.enabled_chapters=1
                UNION ALL
                SELECT sv.source_id,sv.source_manga_id,sc.priority FROM source_variants sv JOIN source_config sc ON sc.source_id=sv.source_id
                WHERE sv.entry_id=$id AND sv.active=1 AND sc.available=1 AND sc.enabled_language=1 AND sc.enabled_chapters=1
                ORDER BY priority
                """, ("$id", id)).Select(row => (Entry: id, Source: row.Text("source_id"), Manga: row.Integer("source_manga_id"))).ToArray();
        });
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        var results = await Task.WhenAll(records.Select(async record =>
        {
            try { return (record, Payload: await fetch(record.Manga, fetchDetails, fetchChapters, limit.Token).ConfigureAwait(false)); }
            catch (Exception error) when (error is HttpRequestException or TimeoutException or OperationCanceledException or IOException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                return (record, Payload: (JsonObject?)null);
            }
        })).ConfigureAwait(false);
        var anyDetails = false;
        foreach (var (record, payload) in results)
        {
            if (payload is null) continue;
            if (fetchDetails && payload["manga"] is JsonObject manga && manga.Count > 0)
            {
                if (!ApplySourceDetailIfCurrent(record.Entry, record.Source, record.Manga, manga)) continue;
                anyDetails = true;
            }
            if (fetchChapters && payload["chapters"] is JsonArray list) chapters?.Invoke(record.Entry, record.Source, record.Manga, list);
        }
        if (fetchDetails && anyDetails && records.Length > 0) _followUps.ChooseMetadata(records[0].Entry);
        return GetEntry(entryId);
    }

    /// <summary>entry_history: served chapter origins (origins database).</summary>
    public JsonObject EntryHistory(long entryId)
    {
        var id = CanonicalEntryId(entryId);
        return _database.Read(DatabaseFile.Origins, session =>
        {
            var chapters = session.Query("SELECT id,chapter_number,display_name FROM virtual_chapters WHERE entry_id=$id", ("$id", id));
            var byId = chapters.ToDictionary(row => row.Integer("id"), JsonRow);
            var history = new JsonArray();
            foreach (var batch in byId.Keys.Chunk(500))
                foreach (var row in session.Query("SELECT * FROM origin_history WHERE virtual_chapter_id IN (" + string.Join(',', batch.Select(value => value.ToString(CultureInfo.InvariantCulture))) + ") ORDER BY served_at DESC"))
                {
                    var item = JsonRow(row);
                    item["chapter"] = byId.TryGetValue(row.Integer("virtual_chapter_id"), out var chapter) ? chapter.DeepClone() : null;
                    history.Add(item);
                }
            return new JsonObject { ["chapters"] = new JsonArray(byId.Values.Select(value => (JsonNode)value).ToArray()), ["history"] = history };
        });
    }

    // ---- search ----------------------------------------------------------------------------------------------------

    /// <summary>_exact_search_entry_ids: exact known titles (aliases, then main titles), optionally only entries with records of these sources.</summary>
    public IReadOnlyList<long> ExactSearchEntryIds(string search, int limit = 200, IReadOnlyCollection<string>? sourceIds = null) => Read(session =>
    {
        var normalized = TextMatcher.NormalizeTitle((search ?? "").Trim());
        if (normalized.Length == 0) return Array.Empty<long>();
        var ids = new List<long>();
        ids.AddRange(session.Query("SELECT DISTINCT entry_id FROM titles WHERE normalized=$n LIMIT $limit", ("$n", normalized), ("$limit", limit)).Select(row => row.Integer("entry_id")));
        // entry_preferred_titles keeps a normalized copy of every main title (NULL until normalized once).
        foreach (var row in session.Query("""
            SELECT e.id,p.raw_title,p.normalized FROM entry_preferred_titles p JOIN entries e ON e.id=p.entry_id
            WHERE p.normalized=$n OR p.normalized IS NULL ORDER BY e.updated_at DESC,e.id DESC
            """, ("$n", normalized)))
        {
            var value = row["normalized"] is string stored ? stored : TextMatcher.NormalizeTitle(row.Text("raw_title"));
            if (value != normalized) continue;
            ids.Add(row.Integer("id"));
            if (ids.Count >= limit) break;
        }
        var canonical = ids.Select(id => CatalogueTitles.Canonical(session, id)).Distinct().ToList();
        if (sourceIds is { Count: > 0 }) canonical = Scoped(session, canonical, sourceIds);
        return canonical.Take(limit).ToArray();
    });

    /// <summary>_source_scoped_entry_ids: keeps the order, drops entries without an active record of these sources.</summary>
    public IReadOnlyList<long> SourceScopedEntryIds(IReadOnlyList<long> entryIds, IReadOnlyCollection<string>? sourceIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0 || sourceIds is not { Count: > 0 }) return entryIds;
        return Read(session => Scoped(session, entryIds.ToList(), sourceIds));
    }

    private static List<long> Scoped(DatabaseSession session, List<long> entryIds, IReadOnlyCollection<string> sourceIds)
    {
        var wanted = sourceIds.ToHashSet(StringComparer.Ordinal);
        var linked = new HashSet<long>();
        foreach (var id in entryIds.Distinct())
            if (session.Query("SELECT source_id FROM source_matches WHERE entry_id=$id AND active=1 UNION SELECT source_id FROM source_variants WHERE entry_id=$id AND active=1", ("$id", id))
                .Any(row => wanted.Contains(row.Text("source_id")))) linked.Add(id);
        return entryIds.Where(linked.Contains).ToList();
    }

    /// <summary>_search_entry_ids: aliases, main titles and active record titles containing the query (normalized / case-folded).</summary>
    public IReadOnlyList<long> SearchEntryIds(string search, int limit = 200, IReadOnlyList<long>? restrictIds = null, IReadOnlyCollection<string>? sourceIds = null) => Read(session =>
    {
        var query = (search ?? "").Trim(); var normalized = TextMatcher.NormalizeTitle(query);
        if (normalized.Length == 0) return Array.Empty<long>();
        var raw = "%" + query.ToLowerInvariant() + "%"; var norm = "%" + normalized + "%";
        var ids = new List<long>();
        ids.AddRange(session.Query("SELECT DISTINCT entry_id id FROM titles WHERE normalized LIKE $n", ("$n", norm)).Select(row => row.Integer("id")));
        ids.AddRange(session.Query("SELECT id FROM entries WHERE lower(preferred_title) LIKE $r", ("$r", raw)).Select(row => row.Integer("id")));
        ids.AddRange(session.Query("SELECT DISTINCT entry_id id FROM source_matches WHERE active=1 AND lower(source_title) LIKE $r", ("$r", raw)).Select(row => row.Integer("id")));
        ids.AddRange(session.Query("SELECT DISTINCT entry_id id FROM source_variants WHERE active=1 AND lower(source_title) LIKE $r", ("$r", raw)).Select(row => row.Integer("id")));
        var found = ids.Select(id => CatalogueTitles.Canonical(session, id)).Distinct().ToList();
        if (sourceIds is { Count: > 0 }) found = Scoped(session, found, sourceIds);
        if (restrictIds is { Count: > 0 })
        {
            var set = found.ToHashSet();
            return restrictIds.Distinct().Where(set.Contains).Take(limit).ToArray();
        }
        return found.Take(limit).ToArray();
    });

    /// <summary>entry_list: newest first, or search results.</summary>
    public IReadOnlyList<JsonObject> EntryList(string search = "", int limit = 200)
    {
        var ids = (search ?? "").Trim().Length > 0 ? SearchEntryIds(search!, limit)
            : Read(session => session.Query("SELECT id FROM entries ORDER BY updated_at DESC LIMIT $limit", ("$limit", limit)).Select(row => row.Integer("id")).ToArray());
        return EntrySummaries(ids);
    }

    /// <summary>entries_by_ids: summaries in the given (library) order, optionally filtered by a search.</summary>
    public IReadOnlyList<JsonObject> EntriesByIds(IEnumerable<long> entryIds, string search = "", int limit = 500)
    {
        var wanted = CanonicalEntryIds(entryIds).Distinct().ToList();
        if (wanted.Count == 0) return [];
        if ((search ?? "").Trim().Length > 0) wanted = SearchEntryIds(search!, Math.Max(limit, wanted.Count), wanted).ToList();
        return EntrySummaries(wanted.Take(limit));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private JsonObject Clean(DatabaseSession session, string sourceId, JsonObject manga)
    {
        var payload = (JsonObject)manga.DeepClone();
        var raw = CatalogueTitles.Text(payload["title"]).Trim();
        var cleaned = TextMatcher.StripKnownSourceSuffix(raw, CatalogueTitles.SourceTitleNames(session, sourceId));
        if (cleaned.Length > 0 && cleaned != raw)
        {
            if (!payload.ContainsKey("_autosourcarr_raw_title")) payload["_autosourcarr_raw_title"] = raw;
            payload["title"] = cleaned;
        }
        if (sourceId.Length > 0) payload["sourceId"] = sourceId;
        return CleanPayload is null ? payload : CleanPayload(sourceId, payload);
    }

    private static long? FindExisting(DatabaseSession session, string sourceId, long mangaId)
    {
        var owner = session.Scalar("SELECT entry_id FROM source_matches WHERE source_id=$source AND source_manga_id=$manga AND active=1", ("$source", sourceId), ("$manga", mangaId))
            ?? session.Scalar("SELECT entry_id FROM source_variants WHERE source_id=$source AND source_manga_id=$manga AND active=1", ("$source", sourceId), ("$manga", mangaId));
        return owner is null ? null : CatalogueTitles.Canonical(session, Convert.ToInt64(owner, CultureInfo.InvariantCulture));
    }

    private static bool Deleted(DatabaseSession session, string sourceId, long mangaId) =>
        session.Scalar("SELECT 1 FROM deleted_source_records WHERE source_id=$source AND source_manga_id=$manga", ("$source", sourceId), ("$manga", mangaId)) is not null;

    private static bool Exists(DatabaseSession session, long entryId) => session.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is not null;

    private static bool IsPrimary(DatabaseSession session, long entryId, string sourceId, long mangaId) =>
        session.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$id AND source_id=$source AND source_manga_id=$manga AND active=1",
            ("$id", entryId), ("$source", sourceId), ("$manga", mangaId)) is not null;

    private static bool HasPrimary(DatabaseSession session, long entryId, string sourceId) =>
        session.Scalar("SELECT 1 FROM source_matches WHERE entry_id=$id AND source_id=$source AND active=1", ("$id", entryId), ("$source", sourceId)) is not null;

    private static bool HasIdentity(DatabaseSession session, long entryId) => session.Scalar("SELECT 1 FROM identity_links WHERE entry_id=$id LIMIT 1", ("$id", entryId)) is not null;

    private static void ReviewNotDuplicate(DatabaseSession session, long left, long right, string stamp)
    {
        var (a, b) = left < right ? (left, right) : (right, left);
        session.Execute("""
            INSERT INTO duplicate_reviews(left_entry_id,right_entry_id,decision,reviewed_at) VALUES($left,$right,'not_duplicate',$time)
            ON CONFLICT(left_entry_id,right_entry_id) DO UPDATE SET decision=excluded.decision,reviewed_at=excluded.reviewed_at
            """, ("$left", a), ("$right", b), ("$time", stamp));
    }

    /// <summary>_manga_metadata_is_sparse.</summary>
    public static bool Sparse(JsonObject manga)
    {
        ArgumentNullException.ThrowIfNull(manga);
        var genres = manga["genre"] ?? manga["genres"];
        return !Truth(manga["initialized"]) || CatalogueTitles.Text(manga["thumbnailUrl"]).Trim().Length == 0 || CatalogueTitles.Text(manga["description"]).Trim().Length < 40
            || CatalogueTitles.Text(manga["author"]).Trim().Length == 0 || CatalogueTitles.Text(manga["artist"]).Trim().Length == 0
            || !Truth(genres) || NativeRecordStore.Status(manga["status"]) == 0;
    }

    private static bool Truth(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
        JsonValue value => value.ToJsonString() is not ("0" or "0.0"),
        _ => true,
    };

    private static string[] Authors(JsonObject manga) =>
        new[] { CatalogueTitles.Text(manga["author"]), CatalogueTitles.Text(manga["artist"]) }.Where(name => name.Trim().Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static string Known(string value, params string[] allowed) => allowed.Contains(value, StringComparer.Ordinal) ? value : "";

    private static string Key(string sourceId, long mangaId) => sourceId + ":" + mangaId.ToString(CultureInfo.InvariantCulture);

    private static JsonArray Rows(IEnumerable<DatabaseRow> rows) => new(rows.Select(row => (JsonNode)JsonRow(row)).ToArray());

    private static JsonObject JsonRow(DatabaseRow row)
    {
        var item = new JsonObject();
        foreach (var (column, value) in row) item[column] = JsonValueOf(value);
        return item;
    }

    private static JsonNode? JsonValueOf(object? value) => value switch
    {
        null => null,
        long number => number,
        int number => number,
        double number => number,
        string text => text,
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private string Now() => SourceConfigurationJson.Timestamp(_clock);

    private T Read<T>(Func<DatabaseSession, T> operation)
    {
        lock (_gate) return _database.Read(DatabaseFile.Catalog, operation);
    }
}
