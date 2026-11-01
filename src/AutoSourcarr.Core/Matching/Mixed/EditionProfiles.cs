using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Catalogue.Tags;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Matching.Mixed;

/// <summary>Color detection settings (color_detection_* in the app settings).</summary>
public sealed record EditionOptions(bool Enabled = true, double MinimumConfidence = .78, double ColoredPixelFraction = .06, int SamplePages = 4, double TimeoutSeconds = 8);

/// <summary>The edition decisions of one attached source record.</summary>
public sealed record EditionRecordDecision(EditionRecordFacts Facts, string Kind, long RowId, bool Locked, string MatchType, string Author, string Lang,
    EditionDecision Color, EditionDecision Media);

/// <summary>An entry's edition profile: the combined color and media decisions plus every record's own decision.</summary>
public sealed record EditionProfile(long EntryId, string PreferredTitle, EditionDecision Color, EditionDecision Media, IReadOnlyList<EditionRecordDecision> Records)
{
    public JsonObject Json() => new()
    {
        ["entry_id"] = EntryId, ["color_mode"] = Color.Mode, ["color_confidence"] = Color.Confidence, ["media_mode"] = Media.Mode, ["media_confidence"] = Media.Confidence,
        ["evidence"] = EditionProfiles.SignalsJson(Color.Evidence.Concat(Media.Evidence)),
    };
}

/// <summary>
/// Matching → MixedEntries / ColorProfile (phase 8): entry and source-record color/media profiles decided from more than
/// the title (MAT-3), the conflicts and automatic gates built on them (<see cref="IEditionGate"/>), the saved page
/// analysis (source_color_profiles), and the color-label repairs. Reads stay local; only
/// <see cref="AnalyzeSourceColorAsync"/> reads chapter pages, through <see cref="IPageImageSource"/>.
/// </summary>
public sealed partial class EditionProfiles : IEditionGate
{
    private readonly SqliteDatabase _database;
    private readonly Func<EditionOptions> _options;
    private readonly IPageImageSource? _pages;
    private readonly TimeProvider _clock;

    public EditionProfiles(SqliteDatabase database, Func<EditionOptions> options, IPageImageSource? pages = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(options);
        _database = database; _options = options; _pages = pages; _clock = clock ?? TimeProvider.System;
    }

    /// <summary>mixed_color_confidence: the confidence a color or media decision needs to count.</summary>
    public double MinimumConfidence => Math.Clamp(_options().MinimumConfidence, 0, 1);
    internal string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    // ---- per-record facts -------------------------------------------------------------------------------------------

    /// <summary>Loads the active records of the given entries (at most <paramref name="perEntryLimit"/> each, primary
    /// records first) with their tags, source and saved page analysis, and decides each record's editions.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<EditionRecordDecision>> RecordDecisions(DatabaseSession catalog, IReadOnlyCollection<long> entryIds, TagSynonymMap synonyms, int perEntryLimit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(entryIds); ArgumentNullException.ThrowIfNull(synonyms);
        var result = new Dictionary<long, IReadOnlyList<EditionRecordDecision>>();
        if (entryIds.Count == 0) return result;
        var (names, parameters) = InList(entryIds);
        var limit = ("$limit", (object?)Math.Max(1, perEntryLimit));
        var rows = catalog.Query("""
            WITH r AS (
              SELECT 'primary' kind,id,entry_id,source_id,source_manga_id,source_title,genre,description,author,match_type,locked,metadata_json FROM source_matches WHERE active=1 AND entry_id IN (NAMES)
              UNION ALL
              SELECT 'additional' kind,id,entry_id,source_id,source_manga_id,source_title,genre,description,author,match_type,locked,metadata_json FROM source_variants WHERE active=1 AND entry_id IN (NAMES)
            ), ranked AS (SELECT r.*,ROW_NUMBER() OVER (PARTITION BY r.entry_id ORDER BY r.kind DESC,r.id) rn FROM r)
            SELECT ranked.*,COALESCE(NULLIF(sc.display_name,''),sc.name,ranked.source_id) source_name,COALESCE(sc.lang,'') source_lang,COALESCE(sc.base_url,'') base_url,
                   p.color_mode p_mode,p.confidence p_confidence,p.evidence p_evidence,p.sampled_pages p_pages
            FROM ranked LEFT JOIN source_config sc ON sc.source_id=ranked.source_id
            LEFT JOIN source_color_profiles p ON p.source_id=ranked.source_id AND p.source_manga_id=ranked.source_manga_id
            WHERE ranked.rn<=$limit ORDER BY ranked.entry_id,ranked.rn
            """.Replace("NAMES", names, StringComparison.Ordinal), [.. parameters, limit]);
        var tags = new Dictionary<(string, long), List<string>>();
        foreach (var row in catalog.Query("""
            SELECT a.source_id,a.source_manga_id,t.namespace,t.name FROM (
              SELECT source_id,source_manga_id FROM source_matches WHERE active=1 AND entry_id IN (NAMES)
              UNION SELECT source_id,source_manga_id FROM source_variants WHERE active=1 AND entry_id IN (NAMES)) a
            JOIN source_record_tags st ON st.source_id=a.source_id AND st.source_manga_id=a.source_manga_id
            JOIN tags t ON t.id=st.tag_id ORDER BY a.source_id,a.source_manga_id,t.namespace,t.name
            """.Replace("NAMES", names, StringComparison.Ordinal), parameters))
        {
            var key = (row.Text("source_id"), row.Integer("source_manga_id"));
            if (!tags.TryGetValue(key, out var list)) tags[key] = list = [];
            if (list.Count < 200) list.Add(row.Text("namespace") + ":" + row.Text("name"));
        }
        foreach (var group in rows.GroupBy(row => row.Integer("entry_id")))
            result[group.Key] = group.Select(row => Decide(row, tags.TryGetValue((row.Text("source_id"), row.Integer("source_manga_id")), out var list) ? (IReadOnlyList<string>)list : Array.Empty<string>(), synonyms)).ToArray();
        return result;
    }

    private EditionRecordDecision Decide(DatabaseRow row, IReadOnlyList<string> tags, TagSynonymMap synonyms)
    {
        JsonObject? payload = null;
        string Field(string column, string name)
        {
            var value = row.Text(column); if (value.Length != 0) return value;
            payload ??= ParseObject(row.Text("metadata_json", "{}"));
            return payload[name] is JsonValue item && item.TryGetValue<string>(out var text) ? text : "";
        }
        var pages = row["p_mode"] is null ? null : new EditionPageProfile(row.Text("p_mode"), row.Number("p_confidence"), row.Text("p_evidence"), (int)row.Integer("p_pages"));
        var facts = new EditionRecordFacts(row.Text("source_id"), row.Integer("source_manga_id"), Field("source_title", "title"), Field("genre", "genre"),
            Field("description", "description"), tags, row.Text("source_name"), row.Text("base_url"), pages);
        return new(facts, row.Text("kind"), row.Integer("id"), row.Flag("locked"), row.Text("match_type"), Field("author", "author"), row.Text("source_lang"),
            EditionSignals.Decide(EditionSignals.Color(facts, synonyms)), EditionSignals.Decide(EditionSignals.Media(facts, synonyms)));
    }

    private static JsonObject ParseObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject(); } catch (JsonException) { return new JsonObject(); }
    }

    // ---- entry profiles ---------------------------------------------------------------------------------------------

    /// <summary>_entry_color_profile / _entry_media_mode: decided from current evidence, nothing written.</summary>
    public EditionProfile Profile(DatabaseSession catalog, long entryId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        entryId = PortableIdentityResolver.Canonical(catalog, entryId);
        var title = catalog.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) as string ?? throw new KeyNotFoundException("Entry not found");
        var records = RecordDecisions(catalog, [entryId], TagSynonyms.Load(catalog));
        return Build(entryId, title, records.TryGetValue(entryId, out var list) ? list : Array.Empty<EditionRecordDecision>());
    }

    public EditionProfile Profile(long entryId) => _database.Read(DatabaseFile.Catalog, c => Profile(c, entryId));

    internal EditionProfile Build(long entryId, string preferredTitle, IReadOnlyList<EditionRecordDecision> records)
    {
        var minimum = MinimumConfidence;
        var colors = records.Select(record => record.Color).ToList(); var media = records.Select(record => record.Media).ToList();
        var titleColor = TextMatcher.TitleColorVariant(preferredTitle);
        if (titleColor != "unknown") colors.Add(new(titleColor, .995, [new("color", titleColor, .995, "preferred-title")]));
        var titleMedia = TextMatcher.TitleMediaVariant(preferredTitle);
        if (titleMedia != "unknown") media.Add(new(titleMedia, .995, [new("media", titleMedia, .995, "preferred-title")]));
        return new(entryId, preferredTitle, EditionSignals.Combine(colors, minimum), EditionSignals.Combine(media, minimum), records);
    }

    /// <summary>_recompute_entry_color_profile + _recompute_entry_media_mode: stores the decisions on the entry (only
    /// when they changed) and saves non-title, non-page record evidence as an "inferred:" source profile so the
    /// catalogue metadata owner (CatalogueMetadata.RecomputeColor) sees the same edition.</summary>
    public EditionProfile Recompute(DatabaseSession catalog, long entryId)
    {
        var profile = Profile(catalog, entryId);
        if (catalog.CanWrite) Store(catalog, profile);
        return profile;
    }

    /// <summary>Stores an already decided profile (see <see cref="Recompute(DatabaseSession, long)"/>) in the caller's write session.</summary>
    public void Store(DatabaseSession catalog, EditionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(profile);
        catalog.Execute("UPDATE entries SET color_mode=$color,color_confidence=$confidence,media_mode=$media WHERE id=$id AND (color_mode,color_confidence,media_mode) IS NOT ($color,$confidence,$media)",
            ("$color", profile.Color.Mode), ("$confidence", profile.Color.Confidence), ("$media", profile.Media.Mode), ("$id", profile.EntryId));
        foreach (var record in profile.Records)
        {
            if (!record.Color.Strong(MinimumConfidence) || record.Color.Evidence.Any(signal => signal.Evidence is "title-marker" or "chapter-pages")) continue;
            var kinds = string.Join(',', record.Color.Evidence.Where(signal => signal.Mode == record.Color.Mode).Select(signal => signal.Evidence).Distinct(StringComparer.Ordinal));
            catalog.Execute("""
                INSERT INTO source_color_profiles(source_id,source_manga_id,color_mode,confidence,sampled_pages,evidence,colored_fraction,analyzed_at)
                VALUES($source,$manga,$mode,$confidence,0,$evidence,0,$stamp)
                ON CONFLICT(source_id,source_manga_id) DO UPDATE SET color_mode=excluded.color_mode,confidence=excluded.confidence,evidence=excluded.evidence,analyzed_at=excluded.analyzed_at
                WHERE source_color_profiles.evidence LIKE 'inferred:%' OR (source_color_profiles.color_mode='unknown' AND source_color_profiles.confidence<$minimum AND source_color_profiles.evidence NOT LIKE 'chapter-pages%')
                """, ("$source", record.Facts.SourceId), ("$manga", record.Facts.MangaId), ("$mode", record.Color.Mode), ("$confidence", record.Color.Confidence),
                ("$evidence", "inferred:" + kinds), ("$stamp", Stamp()), ("$minimum", MinimumConfidence));
        }
    }

    public EditionProfile Recompute(long entryId) => _database.Write(DatabaseFile.Catalog, c => Recompute(c, entryId));

    // ---- conflicts and gates (IEditionGate) -------------------------------------------------------------------------

    public EditionVerdict CompareEntries(long leftEntryId, long rightEntryId) => _database.Read(DatabaseFile.Catalog, c => CompareEntries(c, leftEntryId, rightEntryId));

    /// <summary>_entries_have_color_conflict + _entries_have_media_conflict, with evidence.</summary>
    public EditionVerdict CompareEntries(DatabaseSession catalog, long leftEntryId, long rightEntryId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var left = PortableIdentityResolver.Canonical(catalog, leftEntryId); var right = PortableIdentityResolver.Canonical(catalog, rightEntryId);
        if (left == right) return new(true, "same-entry", []);
        EditionProfile a, b;
        // A stale/deleted id is never mergeable (released rule; alias repair heals recoverable chains).
        try { a = Profile(catalog, left); b = Profile(catalog, right); }
        catch (KeyNotFoundException) { return new(false, "edition-conflict:missing-entry", []); }
        var evidence = a.Color.Evidence.Concat(a.Media.Evidence).Concat(b.Color.Evidence).Concat(b.Media.Evidence).ToArray();
        var color = Conflict("color", a.Color.Mode, b.Color.Mode, "colored", "monochrome");
        if (color is not null) return new(false, color, evidence);
        // An explicit colored edition label must not merge into an unlabeled same-base entry before its pages are known.
        var lc = TextMatcher.TitleColorVariant(a.PreferredTitle); var rc = TextMatcher.TitleColorVariant(b.PreferredTitle);
        var lb = TextMatcher.NormalizedColorBase(a.PreferredTitle); var rb = TextMatcher.NormalizedColorBase(b.PreferredTitle);
        if (lb.Length != 0 && lb == rb && (lc == "colored") != (rc == "colored")) return new(false, "color-edition-conflict:colored-label", evidence);
        var media = Conflict("media", a.Media.Mode, b.Media.Mode, "manga", "webtoon");
        if (media is not null) return new(false, media, evidence);
        var lm = TextMatcher.TitleMediaVariant(a.PreferredTitle); var rm = TextMatcher.TitleMediaVariant(b.PreferredTitle);
        var lmb = TextMatcher.NormalizedMediaBase(a.PreferredTitle); var rmb = TextMatcher.NormalizedMediaBase(b.PreferredTitle);
        if (lmb.Length != 0 && lmb == rmb && lm is "manga" or "webtoon" && rm is "manga" or "webtoon" && lm != rm) return new(false, "media-edition-conflict:" + lm + "!=" + rm, evidence);
        return new(true, "edition-compatible:" + a.Color.Mode + "/" + b.Color.Mode + "," + a.Media.Mode + "/" + b.Media.Mode, evidence);
    }

    public EditionVerdict CompareCandidates(EditionCandidate left, EditionCandidate right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        return _database.Read(DatabaseFile.Catalog, c =>
        {
            var map = TagSynonyms.Load(c); var minimum = MinimumConfidence;
            var a = left.Facts(SourceProfile(c, left.SourceId, left.MangaId)); var b = right.Facts(SourceProfile(c, right.SourceId, right.MangaId));
            var ac = EditionSignals.Decide(EditionSignals.Color(a, map)); var bc = EditionSignals.Decide(EditionSignals.Color(b, map));
            var am = EditionSignals.Decide(EditionSignals.Media(a, map)); var bm = EditionSignals.Decide(EditionSignals.Media(b, map));
            var evidence = ac.Evidence.Concat(am.Evidence).Concat(bc.Evidence).Concat(bm.Evidence).ToArray();
            var color = Conflict("color", ac.Strong(minimum) ? ac.Mode : "unknown", bc.Strong(minimum) ? bc.Mode : "unknown", "colored", "monochrome");
            if (color is not null) return new EditionVerdict(false, color, evidence);
            var media = Conflict("media", am.Strong(minimum) ? am.Mode : "unknown", bm.Strong(minimum) ? bm.Mode : "unknown", "manga", "webtoon");
            return media is not null ? new EditionVerdict(false, media, evidence) : new EditionVerdict(true, "edition-compatible", evidence);
        });
    }

    /// <summary>_automatic_media_gate + the saved-evidence part of _automatic_color_gate.</summary>
    public EditionVerdict GateCandidate(DatabaseSession catalog, long entryId, EditionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(candidate);
        var target = Profile(catalog, entryId); var map = TagSynonyms.Load(catalog); var minimum = MinimumConfidence;
        var facts = candidate.Facts(SourceProfile(catalog, candidate.SourceId, candidate.MangaId));
        var media = EditionSignals.Decide(EditionSignals.Media(facts, map)); var color = EditionSignals.Decide(EditionSignals.Color(facts, map));
        var evidence = target.Color.Evidence.Concat(target.Media.Evidence).Concat(color.Evidence).Concat(media.Evidence).ToArray();
        var candidateMedia = media.Strong(minimum) ? media.Mode : "unknown";
        if (candidateMedia is "manga" or "webtoon" && target.Media.Mode is "manga" or "webtoon" && candidateMedia != target.Media.Mode)
            return new(false, "media-edition-conflict:" + target.Media.Mode + "!=" + candidateMedia, evidence);
        if (target.Media.Mode == "mixed") return new(false, "media-edition-conflict:mixed", evidence);
        if (!_options().Enabled) return new(true, "color-detection-disabled", evidence);
        if (target.Color.Mode == "mixed") return new(false, "color-edition-conflict:mixed", evidence);
        var targetStrong = target.Color.Strong(minimum); var candidateStrong = color.Strong(minimum);
        var tmode = target.Color.Mode; var cmode = candidateStrong ? color.Mode : "unknown";
        // No signal on either side: never delay an ordinary match with page downloads (released rule).
        if (!targetStrong && !candidateStrong) return new(true, "color-unknown/deferred", evidence);
        if (targetStrong && candidateStrong && tmode != cmode) return new(false, "color-edition-conflict:" + tmode + "!=" + cmode, evidence);
        if (targetStrong != candidateStrong) return new(false, "color-edition-unresolved:" + tmode + "/" + cmode, evidence);
        return new(true, "color-compatible:" + tmode + "/" + cmode, evidence);
    }

    public async Task<EditionVerdict> GateCandidateAsync(long entryId, EditionCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var verdict = _database.Read(DatabaseFile.Catalog, c => GateCandidate(c, entryId, candidate));
        if (!verdict.Reason.StartsWith("color-edition-unresolved:", StringComparison.Ordinal)) return verdict;
        // Lazy page inspection: sample only the side without a color edition yet.
        var target = _database.Read(DatabaseFile.Catalog, c => Profile(c, entryId));
        if (target.Color.Strong(MinimumConfidence)) await AnalyzeSourceColorAsync(candidate.SourceId, candidate.MangaId, candidate.Title, false, cancellationToken).ConfigureAwait(false);
        else await EnsureEntryColorProfileAsync(entryId, cancellationToken).ConfigureAwait(false);
        return _database.Read(DatabaseFile.Catalog, c => GateCandidate(c, entryId, candidate));
    }

    private static string? Conflict(string axis, string left, string right, string first, string second)
    {
        if (left == "mixed" || right == "mixed") return axis + "-edition-conflict:mixed";
        if ((left == first || left == second) && (right == first || right == second) && left != right) return axis + "-edition-conflict:" + left + "!=" + right;
        return null;
    }

    // ---- saved page analysis (source_color_profiles) ----------------------------------------------------------------

    public static EditionPageProfile? SourceProfile(DatabaseSession catalog, string sourceId, long mangaId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var row = catalog.Query("SELECT color_mode,confidence,evidence,sampled_pages FROM source_color_profiles WHERE source_id=$source AND source_manga_id=$manga",
            ("$source", sourceId), ("$manga", mangaId)).FirstOrDefault();
        return row is null ? null : new(row.Text("color_mode", "unknown"), row.Number("confidence"), row.Text("evidence"), (int)row.Integer("sampled_pages"));
    }

    /// <summary>save_source_color_profile.</summary>
    public JsonObject SaveSourceProfile(string sourceId, long mangaId, string mode, double confidence, int sampledPages, string evidence, double coloredFraction = 0)
    {
        mode = mode is "colored" or "monochrome" ? mode : "unknown"; var stamp = Stamp();
        _database.Write(DatabaseFile.Catalog, c => c.Execute("""
            INSERT INTO source_color_profiles(source_id,source_manga_id,color_mode,confidence,sampled_pages,evidence,colored_fraction,analyzed_at)
            VALUES($source,$manga,$mode,$confidence,$pages,$evidence,$fraction,$stamp)
            ON CONFLICT(source_id,source_manga_id) DO UPDATE SET color_mode=excluded.color_mode,confidence=excluded.confidence,sampled_pages=excluded.sampled_pages,
              evidence=excluded.evidence,colored_fraction=excluded.colored_fraction,analyzed_at=excluded.analyzed_at
            """, ("$source", sourceId), ("$manga", mangaId), ("$mode", mode), ("$confidence", confidence), ("$pages", sampledPages), ("$evidence", evidence),
            ("$fraction", coloredFraction), ("$stamp", stamp)));
        return new JsonObject
        {
            ["source_id"] = sourceId, ["source_manga_id"] = mangaId, ["color_mode"] = mode, ["confidence"] = confidence, ["sampled_pages"] = sampledPages,
            ["evidence"] = evidence, ["colored_fraction"] = coloredFraction, ["analyzed_at"] = stamp,
        };
    }

    /// <summary>analyze_source_color: explicit title marker first, then a saved page analysis, then interior pages of
    /// one chapter. The page layout (long strip or paged) is saved with it as media evidence.</summary>
    public async Task<JsonObject> AnalyzeSourceColorAsync(string sourceId, long mangaId, string title, bool force, CancellationToken cancellationToken)
    {
        var options = _options();
        if (!options.Enabled) return SaveSourceProfile(sourceId, mangaId, "unknown", 0, 0, "disabled");
        var marker = TextMatcher.TitleColorVariant(title);
        if (marker != "unknown") return SaveSourceProfile(sourceId, mangaId, marker, .995, 0, "explicit-title-marker");
        var cached = _database.Read(DatabaseFile.Catalog, c => SourceProfile(c, sourceId, mangaId));
        if (cached is not null && !force && cached.Confidence >= MinimumConfidence && !cached.Evidence.StartsWith("inferred:", StringComparison.Ordinal))
            return new JsonObject { ["source_id"] = sourceId, ["source_manga_id"] = mangaId, ["color_mode"] = cached.ColorMode, ["confidence"] = cached.Confidence, ["sampled_pages"] = cached.SampledPages, ["evidence"] = cached.Evidence };
        if (_pages is null) return new JsonObject { ["source_id"] = sourceId, ["source_manga_id"] = mangaId, ["color_mode"] = "unknown", ["confidence"] = 0.0, ["evidence"] = "no-page-reader" };
        IReadOnlyList<PageRaster> pages;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds * 3)));
            try { pages = await _pages.SamplePagesAsync(sourceId, mangaId, Math.Clamp(options.SamplePages, 2, 6), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return SaveSourceProfile(sourceId, mangaId, "unknown", 0, 0, "page-timeout"); }
            catch (Exception error) when (error is not OperationCanceledException) { return SaveSourceProfile(sourceId, mangaId, "unknown", 0, 0, "page-list-error: " + error.GetType().Name); }
        }
        if (pages.Count == 0) return SaveSourceProfile(sourceId, mangaId, "unknown", 0, 0, "no-pages");
        var samples = pages.Select(PageColorAnalysis.Inspect).ToArray();
        var (mode, confidence, fraction) = PageColorAnalysis.Classify(samples, options.ColoredPixelFraction);
        var layout = PageColorAnalysis.LongStrip(samples) ? "long-strip" : "paged";
        return SaveSourceProfile(sourceId, mangaId, mode, confidence, samples.Length, "chapter-pages:sampled;" + layout, fraction);
    }

    /// <summary>_ensure_entry_color_profile: samples up to three records (by source priority) until one is decided.</summary>
    public async Task<EditionProfile> EnsureEntryColorProfileAsync(long entryId, CancellationToken cancellationToken)
    {
        var profile = _database.Read(DatabaseFile.Catalog, c => Profile(c, entryId));
        if (profile.Color.Strong(MinimumConfidence) && profile.Color.Mode is "colored" or "monochrome") return profile;
        var rows = _database.Read(DatabaseFile.Catalog, c => c.Query("""
            SELECT r.source_id,r.source_manga_id,r.source_title,COALESCE(sc.priority,1000) priority FROM (
              SELECT source_id,source_manga_id,source_title FROM source_matches WHERE entry_id=$id AND active=1
              UNION ALL SELECT source_id,source_manga_id,source_title FROM source_variants WHERE entry_id=$id AND active=1) r
            LEFT JOIN source_config sc ON sc.source_id=r.source_id ORDER BY priority LIMIT 3
            """, ("$id", profile.EntryId)));
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await AnalyzeSourceColorAsync(row.Text("source_id"), row.Integer("source_manga_id"), row.Text("source_title"), false, cancellationToken).ConfigureAwait(false);
            if (result["color_mode"]?.GetValue<string>() is "colored" or "monochrome" && (result["confidence"]?.GetValue<double>() ?? 0) >= MinimumConfidence) break;
        }
        return _database.Write(DatabaseFile.Catalog, c => Recompute(c, profile.EntryId));
    }

    // ---- display labels and legacy repairs --------------------------------------------------------------------------

    /// <summary>_sync_colored_title_label: color identity stays internal; the unified display title is the clean base title.</summary>
    public static bool SyncColoredTitleLabel(DatabaseSession catalog, long entryId, string stamp)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) is not string raw) return false;
        var title = UnicodeText.Trim(raw);
        if (title.Length == 0 || TextMatcher.TitleColorVariant(title) == "unknown") return false;
        var cleaned = UnicodeText.Trim(TextMatcher.ColorBaseTitle(title) is { Length: > 0 } value ? value : title);
        return cleaned.Length != 0 && cleaned != title && catalog.Execute("UPDATE entries SET preferred_title=$title,updated_at=$stamp WHERE id=$id AND preferred_title=$old",
            ("$title", cleaned), ("$stamp", stamp), ("$id", entryId), ("$old", raw)) != 0;
    }

    /// <summary>_repair_colored_display_titles, in keyset pages of 500 entries.</summary>
    public int RepairColoredDisplayTitles(CancellationToken cancellationToken = default)
    {
        var changed = 0; var after = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _database.Write(DatabaseFile.Catalog, c =>
            {
                var ids = c.QueryPage<long>("SELECT id FROM entries WHERE id>@after ORDER BY id", new { after }, 500);
                foreach (var id in ids.Items) if (SyncColoredTitleLabel(c, id, Stamp())) changed++;
                return ids;
            });
            if (page.Items.Count == 0 || !page.HasMore) return changed;
            after = page.Items[^1];
        }
    }

    /// <summary>_legacy_ambiguous_color_base: the unsafe v0.2.30–v0.2.35 bare-word stripping (only to find its damage).</summary>
    public static string LegacyAmbiguousColorBase(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var raw = LegacyColorWords().Replace(value.Trim(), " ");
        raw = MultipleSpaces().Replace(raw, " ").Trim(" -–—:[]()（）【】".ToCharArray());
        return TextMatcher.NormalizeTitle(raw);
    }

    /// <summary>_repair_legacy_color_marker_profiles: invalidates title-derived profiles whose title is not a strict
    /// edition marker, restores a title only when all records agree, and recomputes the affected entries. Page-derived
    /// profiles are never discarded.</summary>
    public JsonObject RepairLegacyColorMarkerProfiles()
    {
        var invalidated = 0; var restored = 0; var affected = new SortedSet<long>();
        _database.Write(DatabaseFile.Catalog, c =>
        {
            foreach (var row in c.Query("""
                SELECT p.source_id,p.source_manga_id,sm.entry_id,sm.source_title FROM source_color_profiles p JOIN source_matches sm ON sm.source_id=p.source_id AND sm.source_manga_id=p.source_manga_id
                WHERE sm.active=1 AND p.evidence IN ('explicit-title-marker','source-title')
                UNION ALL
                SELECT p.source_id,p.source_manga_id,sv.entry_id,sv.source_title FROM source_color_profiles p JOIN source_variants sv ON sv.source_id=p.source_id AND sv.source_manga_id=p.source_manga_id
                WHERE sv.active=1 AND p.evidence IN ('explicit-title-marker','source-title')
                """))
            {
                var title = row.Text("source_title").Trim();
                if (title.Length == 0 || !LegacyColorWords().IsMatch(title) || TextMatcher.TitleColorVariant(title) != "unknown") continue;
                c.Execute("UPDATE source_color_profiles SET color_mode='unknown',confidence=0,sampled_pages=0,evidence='invalidated-legacy-title-marker',colored_fraction=0,analyzed_at=$stamp WHERE source_id=$source AND source_manga_id=$manga",
                    ("$stamp", Stamp()), ("$source", row.Text("source_id")), ("$manga", row.Integer("source_manga_id")));
                affected.Add(row.Integer("entry_id")); invalidated++;
            }
            foreach (var entryId in affected)
            {
                var entry = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", entryId)).FirstOrDefault();
                if (entry is null || entry.Flag("manual_preferred_title")) continue;
                var ordinary = c.Query("SELECT source_title FROM source_matches WHERE entry_id=$id AND active=1 UNION ALL SELECT source_title FROM source_variants WHERE entry_id=$id AND active=1", ("$id", entryId))
                    .Select(item => item.Text("source_title").Trim()).Where(text => text.Length != 0 && TextMatcher.TitleColorVariant(text) == "unknown")
                    .GroupBy(TextMatcher.NormalizeTitle, StringComparer.Ordinal).Where(group => group.Key.Length != 0).ToArray();
                if (ordinary.Length != 1) continue;
                var title = ordinary[0].Last();
                if (TextMatcher.NormalizeTitle(entry.Text("preferred_title")) == TextMatcher.NormalizeTitle(title)) continue;
                c.Execute("UPDATE entries SET preferred_title=$title,updated_at=$stamp WHERE id=$id", ("$title", title), ("$stamp", Stamp()), ("$id", entryId)); restored++;
            }
            foreach (var entryId in affected)
                if (c.Scalar("SELECT 1 FROM entries WHERE id=$id", ("$id", entryId)) is not null) Recompute(c, entryId);
            return 0;
        });
        return new JsonObject { ["invalidated_profiles"] = invalidated, ["restored_titles"] = restored, ["affected_entries"] = affected.Count };
    }

    internal static JsonArray SignalsJson(IEnumerable<EditionSignal> signals) => new(signals.Select(signal => (JsonNode)new JsonObject
    { ["axis"] = signal.Axis, ["mode"] = signal.Mode, ["confidence"] = signal.Confidence, ["evidence"] = signal.Evidence }).ToArray());

    internal static (string Names, (string, object?)[] Parameters) InList(IEnumerable<long> ids)
    {
        var values = ids.Distinct().ToArray();
        var parameters = values.Select((id, index) => ("$e" + index.ToString(CultureInfo.InvariantCulture), (object?)id)).ToArray();
        return (string.Join(',', parameters.Select(pair => pair.Item1)), parameters);
    }

    [GeneratedRegex(@"(?i)\bcolou?red\b|\bmonochrome\b|\bblack\s*(?:and|&)\s*white\b")] private static partial Regex LegacyColorWords();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex MultipleSpaces();
}
