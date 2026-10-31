using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Catalogue;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Unicode;
using AutoSourcarr.Core.Catalogue.Sharing;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue;

/// <summary>Rebuilds native metadata inside the caller's catalogue transaction and shared writer gate.
/// Display ownership is deliberately absent from this evidence-producing module.</summary>
public sealed partial class CatalogueMetadata(Func<double> minimumColorConfidence)
{
    public void Recompute(DatabaseSession c, long entryId, string timestamp)
    {
        RecomputeColor(c, entryId); ChooseMetadata(c, entryId, timestamp);
    }

    public void ChooseMetadata(DatabaseSession c, long entryId, string timestamp)
    {
        entryId = PortableIdentityResolver.Canonical(c, entryId);
        var entry = c.Query("SELECT * FROM entries WHERE id=$id", ("$id", entryId)).SingleOrDefault(); if (entry is null) return;
        var records = c.Query("""
            SELECT sm.source_title,sm.thumbnail_url,sm.description,sm.author,sm.artist,sm.genre,sm.status,
                   sc.lang,COALESCE(sc.priority,1000) priority,0 is_variant,0 is_identity,'' provider
            FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id
            WHERE sm.entry_id=$id AND sm.active=1 AND COALESCE(sc.enabled_language,1)=1
            UNION ALL
            SELECT sv.source_title,sv.thumbnail_url,sv.description,sv.author,sv.artist,sv.genre,sv.status,
                   sc.lang,COALESCE(sc.priority,1000) priority,1 is_variant,0 is_identity,'' provider
            FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id
            WHERE sv.entry_id=$id AND sv.active=1 AND COALESCE(sc.enabled_language,1)=1
            UNION ALL
            SELECT il.title source_title,il.thumbnail_url,il.description,il.author,il.artist,il.genre,il.status,'' lang,
                   CASE il.provider WHEN 'mangadex' THEN -990 WHEN 'anilist' THEN -980 WHEN 'mangaupdates' THEN -970
                   WHEN 'kitsu' THEN -960 WHEN 'myanimelist' THEN -950 WHEN 'comicvine' THEN -940 ELSE -900 END priority,
                   0 is_variant,1 is_identity,il.provider
            FROM identity_links il WHERE il.entry_id=$id AND il.confidence>=0.88
            ORDER BY priority,is_identity DESC,is_variant,source_title
            """, ("$id", entryId));
        if (records.Count == 0)
        {
            c.Execute("UPDATE entries SET description='',thumbnail_url='',status=0,author='',artist='',genre='',updated_at=$stamp WHERE id=$id", ("$stamp", timestamp), ("$id", entryId)); return;
        }
        var preferred = entry.Text("preferred_title");
        if (!entry.Flag("manual_preferred_title"))
        {
            var bound = CatalogueTitleConsensus.MostBoundTitle(records.Select(Vote), preferred);
            if (bound.Length != 0) preferred = bound;
            else
            {
                var titles = c.Query("""
                    SELECT t.title,t.language,t.title_type,t.source_id,COALESCE(sc.lang,'') source_lang
                    FROM titles t LEFT JOIN source_config sc ON sc.source_id=t.source_id WHERE t.entry_id=$id ORDER BY t.id
                    """, ("$id", entryId)).Select(row => new FallbackTitle(row.Text("title"), row.Text("language"), row.Text("title_type"), row.Text("source_lang"))).ToList();
                titles.AddRange(records.Select(row => new FallbackTitle(row.Text("source_title"), row.Text("lang"),
                    row.Flag("is_identity") ? "identity-primary" : row.Flag("is_variant") ? "source-variant" : "source", row.Text("lang"))));
                var english = titles.Where(t => UnicodeText.Trim(t.Value).Length != 0 && !t.Kind.StartsWith("translated-", StringComparison.Ordinal))
                    .OrderBy(t => t.Tier).ThenBy(t => t.TypeRank).ThenBy(t => UnicodeText.Trim(t.Value).EnumerateRunes().Count())
                    .ThenBy(t => UnicodeCaseFold.Fold(UnicodeText.Trim(t.Value)), UnicodeCodePointComparer.Instance).FirstOrDefault(t => t.Tier < 8);
                preferred = english?.Value ?? records.Select(row => row.Text("source_title")).FirstOrDefault(title => title.Length != 0) ?? preferred;
            }
            if (TextMatcher.TitleColorVariant(preferred) != "unknown") preferred = Nonempty(TextMatcher.ColorBaseTitle(preferred), preferred);
            if (TextMatcher.TitleMediaVariant(preferred) != "unknown") preferred = Nonempty(TextMatcher.MediaBaseTitle(preferred), preferred);
        }
        var ordered = records.OrderByDescending(row => row.Flag("is_identity")).ThenBy(Priority).ThenBy(row => row.Flag("is_variant")).ToArray();
        var description = records.Where(row => UnicodeText.Trim(row.Text("description")).Length != 0)
            .Select(row => (Row: row, Value: TextMatcher.CleanSummary(row.Text("description")))).Where(item => item.Value.Length != 0)
            .OrderByDescending(item => item.Row.Flag("is_identity")).ThenByDescending(item => item.Value.EnumerateRunes().Count())
            .ThenBy(item => Priority(item.Row)).ThenBy(item => item.Row.Flag("is_variant")).FirstOrDefault().Value ?? "";
        string CanonicalValue(string field) => ordered.FirstOrDefault(row => UnicodeText.Trim(row.Text(field)).Length != 0)?.Text(field) ?? "";
        var genres = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ordered) foreach (var raw in row.Text("genre").Replace('|', ',').Split(','))
        { var part = UnicodeText.Trim(raw); if (part.Length != 0 && seen.Add(UnicodeCaseFold.Fold(part))) genres.Add(part); }
        var status = ordered.Select(row => Status(row["status"])).FirstOrDefault(value => value != 0);
        c.Execute("""
            UPDATE entries SET preferred_title=CASE WHEN manual_preferred_title=$manual AND preferred_title=$old THEN $preferred ELSE preferred_title END,
                   description=$description,thumbnail_url=$cover,status=$status,author=$author,artist=$artist,genre=$genre,updated_at=$stamp WHERE id=$id
            """, ("$manual", entry.Integer("manual_preferred_title")), ("$old", entry.Text("preferred_title")), ("$preferred", preferred),
            ("$description", description), ("$cover", CanonicalValue("thumbnail_url")), ("$status", status), ("$author", CanonicalValue("author")),
            ("$artist", CanonicalValue("artist")), ("$genre", string.Join(", ", genres)), ("$stamp", timestamp), ("$id", entryId));
        SyncColoredTitle(c, entryId, timestamp); RecomputeMedia(c, entryId); SyncRelatedMediaTitles(c, entryId, timestamp);
    }

    public string SyncPreferredTitle(DatabaseSession c, long entryId, string timestamp)
    {
        entryId = PortableIdentityResolver.Canonical(c, entryId);
        var entry = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", entryId)).SingleOrDefault();
        if (entry is null || entry.Flag("manual_preferred_title")) return entry?.Text("preferred_title") ?? "";
        var records = c.Query("""
            SELECT sm.source_title,COALESCE(sc.lang,'') lang,COALESCE(sc.priority,1000) priority,0 is_variant,0 is_identity
            FROM source_matches sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1
            UNION ALL
            SELECT sv.source_title,COALESCE(sc.lang,'') lang,COALESCE(sc.priority,1000) priority,1 is_variant,0 is_identity
            FROM source_variants sv LEFT JOIN source_config sc ON sc.source_id=sv.source_id WHERE sv.entry_id=$id AND sv.active=1
            """, ("$id", entryId));
        var wanted = CatalogueTitleConsensus.MostBoundTitle(records.Select(Vote), entry.Text("preferred_title"));
        if (wanted.Length != 0 && TextMatcher.NormalizeTitle(wanted) != TextMatcher.NormalizeTitle(entry.Text("preferred_title")))
        {
            if (c.Execute("UPDATE entries SET preferred_title=$title,updated_at=$stamp WHERE id=$id AND manual_preferred_title=0 AND preferred_title=$old",
                ("$title", wanted), ("$stamp", timestamp), ("$id", entryId), ("$old", entry.Text("preferred_title"))) == 0)
                return c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) as string ?? "";
        }
        return Nonempty(wanted, entry.Text("preferred_title"));
    }

    public JsonObject RecomputeColor(DatabaseSession c, long entryId)
    {
        entryId = PortableIdentityResolver.Canonical(c, entryId);
        var title = c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) as string ?? throw new KeyNotFoundException("Entry not found");
        var records = c.Query("""
            SELECT source_id,source_manga_id,source_title FROM source_matches WHERE entry_id=$id AND active=1
            UNION ALL SELECT source_id,source_manga_id,source_title FROM source_variants WHERE entry_id=$id AND active=1
            """, ("$id", entryId));
        var profiles = new List<(string Mode, double Confidence)>();
        foreach (var row in records)
        {
            var profile = c.Query("SELECT color_mode,confidence FROM source_color_profiles WHERE source_id=$source AND source_manga_id=$manga",
                ("$source", row.Text("source_id")), ("$manga", row.Integer("source_manga_id"))).SingleOrDefault();
            if (profile is not null) profiles.Add((profile.Text("color_mode"), profile.Number("confidence")));
            else if (TextMatcher.TitleColorVariant(row.Text("source_title")) is { } mode && mode != "unknown") profiles.Add((mode, .995));
        }
        var preferred = TextMatcher.TitleColorVariant(title); if (preferred != "unknown") profiles.Add((preferred, .995));
        var minimum = Math.Clamp(minimumColorConfidence(), 0, 1);
        var strong = profiles.Where(p => p.Confidence >= minimum && p.Mode is "colored" or "monochrome").ToArray();
        var modes = strong.Select(p => p.Mode).Distinct(StringComparer.Ordinal).ToArray();
        var resultMode = modes.Length > 1 ? "mixed" : modes.FirstOrDefault() ?? "unknown";
        var confidence = strong.Length == 0 ? 0 : modes.Length > 1 ? strong.Min(p => p.Confidence) : strong.Max(p => p.Confidence);
        c.Execute("UPDATE entries SET color_mode=$mode,color_confidence=$confidence WHERE id=$id", ("$mode", resultMode), ("$confidence", confidence), ("$id", entryId));
        return new JsonObject { ["entry_id"] = entryId, ["color_mode"] = resultMode, ["confidence"] = confidence };
    }

    public string RecomputeMedia(DatabaseSession c, long entryId)
    {
        entryId = PortableIdentityResolver.Canonical(c, entryId);
        var title = c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) as string ?? throw new KeyNotFoundException("Entry not found");
        var modes = c.Query("SELECT source_title,genre FROM source_matches WHERE entry_id=$id AND active=1 UNION ALL SELECT source_title,genre FROM source_variants WHERE entry_id=$id AND active=1", ("$id", entryId))
            .Select(row => CandidateMediaMode(row.Text("source_title"), row.Text("genre"))).Append(TextMatcher.TitleMediaVariant(title))
            .Where(mode => mode != "unknown").Distinct(StringComparer.Ordinal).ToArray();
        var value = modes.Length > 1 ? "mixed" : modes.FirstOrDefault() ?? "unknown";
        c.Execute("UPDATE entries SET media_mode=$mode WHERE id=$id", ("$mode", value), ("$id", entryId)); return value;
    }

    public static IReadOnlyList<JsonObject> NativeTitleRows(DatabaseSession c, long entryId)
    {
        entryId = PortableIdentityResolver.Canonical(c, entryId);
        var result = new List<JsonObject>(); var seen = new HashSet<(string, string, string, long)>();
        void Add(string title, string kind, string language = "", string source = "", long manga = 0)
        {
            title = UnicodeText.Trim(title); var norm = TextMatcher.NormalizeTitle(title);
            if (norm.Length == 0 || !TextMatcher.IsMeaningfulTitle(title) || !seen.Add((norm, kind, source, manga))) return;
            result.Add(new JsonObject { ["title"] = title, ["normalized"] = norm, ["title_type"] = kind, ["language"] = language, ["source_id"] = source, ["source_manga_id"] = manga });
        }
        if (c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", entryId)) is string preferred && preferred.Length != 0) Add(preferred, "preferred");
        foreach (var row in c.Query("SELECT title,title_type,language,source_id,source_manga_id FROM titles WHERE entry_id=$id", ("$id", entryId)))
            Add(row.Text("title"), row.Text("title_type"), row.Text("language"), row.Text("source_id"), row.Integer("source_manga_id"));
        foreach (var (table, kind) in new[] { ("source_matches", "source"), ("source_variants", "source-variant") })
            foreach (var row in c.Query($"SELECT sm.source_title,sc.lang,sm.source_id,sm.source_manga_id FROM {table} sm LEFT JOIN source_config sc ON sc.source_id=sm.source_id WHERE sm.entry_id=$id AND sm.active=1", ("$id", entryId)))
                Add(row.Text("source_title"), kind, row.Text("lang"), row.Text("source_id"), row.Integer("source_manga_id"));
        return result;
    }

    private string CurrentMedia(DatabaseSession c, long id)
    {
        id = PortableIdentityResolver.Canonical(c, id); var mode = c.Scalar("SELECT media_mode FROM entries WHERE id=$id", ("$id", id)) as string ?? throw new KeyNotFoundException("Entry not found");
        return mode == "unknown" || mode.Length == 0 ? RecomputeMedia(c, id) : mode;
    }
    internal static string CandidateMediaMode(string title, string genre)
    {
        var modes = new HashSet<string>(StringComparer.Ordinal); var titleMode = TextMatcher.TitleMediaVariant(title); if (titleMode != "unknown") modes.Add(titleMode);
        foreach (var part in GenreParts().Split(genre))
        {
            var norm = TextMatcher.NormalizeTitle(UnicodeText.Trim(part));
            if (norm is "webtoon" or "web comic" or "webcomic") modes.Add("webtoon"); else if (norm is "manga" or "japanese manga") modes.Add("manga");
        }
        return modes.Count == 1 ? modes.Single() : "unknown";
    }
    private static HashSet<string> MediaBases(DatabaseSession c, long id) => NativeTitleRows(c, id).Select(row => TextMatcher.NormalizedMediaBase(row["title"]!.GetValue<string>()))
        .Where(value => value.Length != 0).ToHashSet(StringComparer.Ordinal);
    private static void SyncColoredTitle(DatabaseSession c, long id, string stamp)
    {
        var raw = c.Scalar("SELECT preferred_title FROM entries WHERE id=$id", ("$id", id)) as string; if (raw is null) return;
        var title = UnicodeText.Trim(raw); if (title.Length == 0 || TextMatcher.TitleColorVariant(title) == "unknown") return;
        var cleaned = UnicodeText.Trim(Nonempty(TextMatcher.ColorBaseTitle(title), title));
        if (cleaned.Length != 0 && cleaned != title) c.Execute("UPDATE entries SET preferred_title=$title,updated_at=$stamp WHERE id=$id AND preferred_title=$old",
            ("$title", cleaned), ("$stamp", stamp), ("$id", id), ("$old", raw));
    }
    private void SyncMediaTitle(DatabaseSession c, long id, string stamp)
    {
        var mode = CurrentMedia(c, id); if (mode is not ("manga" or "webtoon")) return;
        var bases = MediaBases(c, id); if (bases.Count == 0) return;
        var current = c.Query("SELECT preferred_title,manual_preferred_title FROM entries WHERE id=$id", ("$id", id)).SingleOrDefault();
        if (current is null || current.Flag("manual_preferred_title")) return;
        var opposite = mode == "manga" ? "webtoon" : "manga";
        var peers = c.Query("SELECT id FROM entries WHERE id<>$id AND media_mode=$mode", ("$id", id), ("$mode", opposite));
        var counterpart = peers.Any(row => bases.Overlaps(MediaBases(c, row.Integer("id")))); var title = UnicodeText.Trim(current.Text("preferred_title"));
        var wanted = counterpart ? UnicodeText.Trim(MediaSuffix().Replace(Nonempty(TextMatcher.MediaBaseTitle(title), title), "")) + " (" + mode + ")" : UnicodeText.Trim(MediaSuffix().Replace(title, ""));
        if (wanted.Length != 0 && wanted != title) c.Execute("UPDATE entries SET preferred_title=$title,updated_at=$stamp WHERE id=$id AND manual_preferred_title=0 AND preferred_title=$old",
            ("$title", wanted), ("$stamp", stamp), ("$id", id), ("$old", current.Text("preferred_title")));
    }
    internal void SyncRelatedMediaTitles(DatabaseSession c, long id, string stamp)
    {
        var mode = CurrentMedia(c, id); SyncMediaTitle(c, id, stamp); if (mode is not ("manga" or "webtoon")) return;
        var bases = MediaBases(c, id); var opposite = mode == "manga" ? "webtoon" : "manga";
        foreach (var row in c.Query("SELECT id FROM entries WHERE id<>$id AND media_mode=$mode", ("$id", id), ("$mode", opposite)))
            if (bases.Overlaps(MediaBases(c, row.Integer("id")))) SyncMediaTitle(c, row.Integer("id"), stamp);
    }
    private static NativeTitleVote Vote(DatabaseRow row) => new(row.Text("source_title"), row.Text("lang"), ParsePriority(row), row.Flag("is_variant"), row.Flag("is_identity"));
    private static long? ParsePriority(DatabaseRow row) => row["priority"] is null ? 1000 : long.TryParse(row.Text("priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    private static long Priority(DatabaseRow row) => ParsePriority(row) is { } value && value != 0 ? value : 1000;
    private static long Status(object? value) => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status) ? status : 0;
    private static string Nonempty(string value, string fallback) => value.Length == 0 ? fallback : value;
    private sealed record FallbackTitle(string Value, string Language, string Kind, string SourceLanguage)
    {
        public int Tier
        {
            get
            {
                var value = UnicodeText.Trim(Value); if (!TextMatcher.LooksPreferredEnglish(value)) return 8;
                if (Kind == "main-library") return 0; if (Language.ToLowerInvariant() == "en") return 1; if (Kind == "identity-primary") return 2;
                return SourceLanguage.ToLowerInvariant() switch { "en" => 3, "" or "all" => 4, _ => 8 };
            }
        }
        public int TypeRank => Kind switch { "manual" or "main-library" => 0, "identity-primary" => 1, "source" => 2, "source-variant" => 3, "identity-alias" => 4, "description-alias" => 5, _ => 6 };
    }
    [GeneratedRegex("[,;/|]", RegexOptions.CultureInvariant, 1000)] private static partial Regex GenreParts();
    [GeneratedRegex(@"\s*\((?:manga|webtoon)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)] private static partial Regex MediaSuffix();
}
