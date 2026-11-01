using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Ingest;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Translation;

/// <summary>translation.py script_language / infer_origin_languages: which language a title's script suggests (search hints only).</summary>
public static class TitleScripts
{
    /// <summary>"ko" for Hangul, "ja" for kana, "zh" for Han only, "en" for Latin letters, else "".</summary>
    public static string Language(string? title)
    {
        var text = title ?? "";
        if (text.Any(c => c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ')) return "ko";
        if (text.Any(c => c is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ')) return "ja";
        if (text.Any(c => c is >= '㐀' and <= '䶿' or >= '一' and <= '鿿')) return "zh";
        return text.Any(c => char.IsLetter(c) && c <= 'ɏ') ? "en" : "";
    }

    /// <summary>The origin languages to translate toward: from native-script titles, then genre words, else the configured targets.</summary>
    public static IReadOnlyList<string> OriginLanguages(IEnumerable<string> titles, string genre, IEnumerable<string> configuredTargets)
    {
        ArgumentNullException.ThrowIfNull(titles); ArgumentNullException.ThrowIfNull(configuredTargets);
        var hints = new List<string>();
        foreach (var title in titles)
            if (Language(title) is "ja" or "ko" or "zh" && !hints.Contains(Language(title))) hints.Add(Language(title));
        var lowered = (genre ?? "").ToLowerInvariant();
        foreach (var (needle, language) in new[] { ("manhwa", "ko"), ("korean", "ko"), ("manhua", "zh"), ("chinese", "zh"), ("manga", "ja"), ("japanese", "ja") })
            if (lowered.Contains(needle, StringComparison.Ordinal) && !hints.Contains(language)) hints.Add(language);
        return hints.Count > 0 ? hints : configuredTargets.Where(language => language is "ja" or "ko" or "zh").ToArray();
    }
}

/// <summary>
/// The machine-translation part of _expanded_search_names (MAT-20): Latin titles translated toward the inferred origin
/// language ("translated-origin") and native titles translated to English ("translated-literal"), at most
/// MANUAL_TRANSLATION_QUERY_LIMIT requests, each bounded by TRANSLATION_TIMEOUT and all by TRANSLATION_OVERALL_TIMEOUT.
/// The results are appended to the given search names (each normalized title once, MANUAL_SEARCH_ALIAS_LIMIT in total) and
/// are transient: they help find a source record but are never stored as titles, never used as match evidence (title
/// types starting with "translated-" are excluded by the matcher) and never displayed as the title.
/// </summary>
public sealed class TranslatedSearchNames(TranslationClient translator, Func<AutoSourcarrSettings> settings)
{
    /// <param name="names">The entry's own search names (EntryIngest.ExpandedSearchNames).</param>
    /// <param name="genre">The entry's genre text (manga/manhwa/manhua words hint at the origin language).</param>
    /// <param name="sourceLanguage">The searched source's language; ja/ko/zh is tried first.</param>
    public async Task<IReadOnlyList<SearchName>> ExpandAsync(IReadOnlyList<SearchName> names, string genre = "", string sourceLanguage = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var options = settings();
        var result = names.ToList();
        var seen = new HashSet<string>(names.Where(name => name.Kind != "transliteration").Select(name => TextMatcher.NormalizeTitle(name.Query)), StringComparer.Ordinal);
        var limit = Math.Max(1, options.ManualSearchAliasLimit);
        if (!translator.Enabled || options.ManualTranslationQueryLimit <= 0) return result.Take(limit).ToArray();
        var queries = names.Select(name => name.Query).ToArray();
        var targets = TitleScripts.OriginLanguages(queries, genre, options.TranslationTargetLanguagesList).ToList();
        if (sourceLanguage is "ja" or "ko" or "zh" && !targets.Contains(sourceLanguage)) targets.Insert(0, sourceLanguage);
        var existing = queries.Select(TitleScripts.Language).ToHashSet(StringComparer.Ordinal);
        var latin = queries.Where(query => TitleScripts.Language(query) == "en").Take(3).ToArray();
        var native = queries.Where(query => TitleScripts.Language(query) is "ja" or "ko" or "zh").Take(3).ToArray();
        var jobs = new List<(string Text, string Target, string Kind)>();
        foreach (var target in targets.Where(target => !existing.Contains(target)))
            jobs.AddRange(latin.Take(2).Select(text => (text, target, "translated-origin")));
        jobs.AddRange(native.Select(text => (text, "en", "translated-literal")));
        jobs = jobs.Take(options.ManualTranslationQueryLimit).ToList();
        if (jobs.Count == 0) return result.Take(limit).ToArray();

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.5, options.TranslationOverallTimeout)));
        var answers = new string[jobs.Count];
        try
        {
            await Task.WhenAll(jobs.Select(async (job, index) =>
            {
                try { answers[index] = await translator.TranslateAsync(job.Text, job.Target, "auto", overall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { answers[index] = ""; }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        for (var index = 0; index < jobs.Count; index++)
        {
            var value = (answers[index] ?? "").Trim();
            var normalized = TextMatcher.NormalizeTitle(value);
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;
            result.Add(new SearchName(value, jobs[index].Kind, jobs[index].Target));
        }
        return result.Take(limit).ToArray();
    }
}

/// <summary>
/// The Status page's translation check without blocking health: the last result is answered at once, and a missing or
/// older-than-five-minutes result starts one background check for a later read.
/// </summary>
public sealed class TranslationHealthCheck(TranslationClient translator, TimeProvider? clock = null)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private JsonObject? _last;
    private DateTimeOffset _checkedAt;
    private Task? _running;

    /// <summary>The cached result (checked, checked_at), or {checked: false} while the first check runs.</summary>
    public JsonObject Snapshot(CancellationToken stopping = default)
    {
        JsonObject? last; DateTimeOffset at;
        lock (_gate)
        {
            last = _last; at = _checkedAt;
            if ((last is null || _clock.GetUtcNow() - at > Freshness) && _running is null && !stopping.IsCancellationRequested)
                _running = Task.Run(() => RunAsync(stopping), CancellationToken.None);
        }
        if (!translator.Enabled && last is null)
            return new JsonObject { ["ok"] = null, ["checked"] = false, ["configured"] = false, ["url"] = "", ["warning"] = "Translation is off or not configured (TRANSLATION_MODE, TRANSLATION_API_URL)." };
        if (last is null)
            return new JsonObject { ["ok"] = null, ["checked"] = false, ["configured"] = translator.Enabled, ["url"] = translator.BaseUrl, ["warning"] = "Not checked yet; a background check has started." };
        var copy = (JsonObject)last.DeepClone();
        copy["checked"] = true;
        copy["checked_at"] = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        if (copy["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var fine) && !fine && copy["configured"] is JsonValue configured && configured.TryGetValue<bool>(out var set) && set)
            copy["warning"] = copy["error"]?.DeepClone();
        return copy;
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            var result = await translator.HealthAsync(stopping).ConfigureAwait(false);
            lock (_gate) { _last = result; _checkedAt = _clock.GetUtcNow(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { lock (_gate) _running = null; }
    }
}
