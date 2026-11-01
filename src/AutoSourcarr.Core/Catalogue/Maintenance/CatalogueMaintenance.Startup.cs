using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Catalogue.Structure;
using AutoSourcarr.Core.Matching;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Persistence;
using static AutoSourcarr.Core.Matching.Manual.ManualSql;

namespace AutoSourcarr.Core.Catalogue.Maintenance;

/// <summary>The fields the startup repair compares two records by (_record_repair_snapshot).</summary>
public sealed record RecordRepairSnapshot(string SourceId, long SourceMangaId, string Title, string Normalized, string Description, string Author,
    string Fingerprint, string ColorMode, string MediaMode, bool Locked);

public sealed partial class CatalogueMaintenance
{
    public const string StartupMaintenanceKey = "startup_catalog_maintenance_version";
    public const string StartupMaintenanceVersion = "0.2.69";
    public const string MergeSafetyRepairKey = "automatic_merge_safety_repair_version";
    private static readonly char[] GenreSeparators = [',', ';', '/', '|'];

    private bool AtVersion(string key, string version) => _settings.Get(key) is JsonValue value && value.TryGetValue<string>(out var text) && text == version;

    /// <summary>
    /// run_startup_catalog_maintenance: the one-time (v0.2.69) upgrade cleanup, after HTTP startup, in 0.4.5's order.
    /// Steps owned by other areas run through <see cref="CatalogueMaintenanceHooks"/> and are reported "unavailable" when
    /// not wired; the version is stored only after every step ran.
    /// </summary>
    public JsonObject RunStartupCatalogMaintenance(bool force = false)
    {
        if (!force && AtVersion(StartupMaintenanceKey, StartupMaintenanceVersion)) return new JsonObject { ["skipped"] = true, ["version"] = StartupMaintenanceVersion };
        lock (_gate)
        {
            if (!force && AtVersion(StartupMaintenanceKey, StartupMaintenanceVersion)) return new JsonObject { ["skipped"] = true, ["version"] = StartupMaintenanceVersion };
            static JsonNode Step(Func<JsonObject>? step) => step is null ? JsonValue.Create("unavailable")! : step();
            var result = new JsonObject { ["skipped"] = false, ["version"] = StartupMaintenanceVersion };
            result["alias_repair"] = Step(_hooks.RepairEntryAliasIntegrity);
            result["source_title_repairs"] = Step(_hooks.RepairSourceTitleSuffixes);
            result["colored_title_repairs"] = Step(_hooks.RepairColoredDisplayTitles);
            result["sanitation"] = SanitizeCatalogText();
            result["color_repairs"] = Step(_hooks.RepairLegacyColorMarkerProfiles);
            // Only attachments traceable to the old bare-colour-word identity bug are repaired.
            var affected = Read(c => c.QueryAs<long>("""
                SELECT DISTINCT sm.entry_id FROM source_matches sm JOIN source_color_profiles p ON p.source_id=sm.source_id AND p.source_manga_id=sm.source_manga_id
                WHERE sm.active=1 AND p.evidence='invalidated-legacy-title-marker'
                UNION SELECT DISTINCT sv.entry_id FROM source_variants sv JOIN source_color_profiles p ON p.source_id=sv.source_id AND p.source_manga_id=sv.source_manga_id
                WHERE sv.active=1 AND p.evidence='invalidated-legacy-title-marker'
                """).ToHashSet());
            result["false_attachment_repairs"] = _hooks.RepairLegacyColorFalseAttachments is { } falseAttachments ? falseAttachments(affected) : JsonValue.Create("unavailable");
            result["unsafe_auto_merge_repairs"] = RunAutomaticMergeSafetyRepair(force: false);
            result["media_changes"] = Step(_hooks.RefreshMediaVariants);
            result["cache_repairs"] = RepairCachedEntryIds();
            _settings.Set(StartupMaintenanceKey, StartupMaintenanceVersion);
            _log.Record(new DecisionEvent("maintenance.startup", DecisionActor.Upgrade("startup-catalog-maintenance-" + StartupMaintenanceVersion), [], [],
                "Startup catalogue maintenance", After: (JsonObject)result.DeepClone()));
            return result;
        }
    }

    /// <summary>run_automatic_merge_safety_repair: once (v0.2.69), split clearly foreign automatic records, then the merge repair.</summary>
    public JsonObject RunAutomaticMergeSafetyRepair(bool force = false)
    {
        if (!force && AtVersion(MergeSafetyRepairKey, StartupMaintenanceVersion)) return new JsonObject { ["skipped"] = true, ["version"] = StartupMaintenanceVersion };
        lock (_gate)
        {
            if (!force && AtVersion(MergeSafetyRepairKey, StartupMaintenanceVersion)) return new JsonObject { ["skipped"] = true, ["version"] = StartupMaintenanceVersion };
            var result = new JsonObject
            {
                ["skipped"] = false, ["version"] = StartupMaintenanceVersion,
                ["source_attachment_repair"] = RepairUnsafeAutomaticSourceAttachments(),
            };
            if (_hooks.RepairUnsafeAutomaticMerges is { } merges)
                foreach (var (key, value) in merges()) result[key] = value?.DeepClone();
            else result["merge_repair"] = "unavailable";
            _settings.Set(MergeSafetyRepairKey, StartupMaintenanceVersion);
            return result;
        }
    }

    /// <summary>
    /// _repair_unsafe_automatic_source_attachments: in snowball-shaped entries only (8+ automatic records, 5+ with 3+
    /// titles, 4+ from one source, or 16+ generated aliases), splits automatic unlocked records that fail the current
    /// strict evidence and are clearly foreign (or a generic exact-title collision) into their own entries, marked as
    /// different works. Manual, locked, import and split records are never touched.
    /// </summary>
    public JsonObject RepairUnsafeAutomaticSourceAttachments(int limit = 10000)
    {
        if (_hooks.AutomaticRecordEvidence is not { } evidenceFor || _operations is not { } operations)
            return new JsonObject { ["checked"] = 0, ["repaired_entries"] = 0, ["split_records"] = 0, ["skipped"] = "evidence unavailable" };
        var entries = Read(c =>
        {
            var rows = c.Query("""
                SELECT entry_id,source_id,source_title,match_type,locked FROM source_matches WHERE active=1
                UNION ALL SELECT entry_id,source_id,source_title,match_type,locked FROM source_variants WHERE active=1
                """);
            var aliases = c.Query("SELECT entry_id,COUNT(*) n FROM titles WHERE title_type IN ('description-alias','matched-alias') GROUP BY entry_id")
                .ToDictionary(row => row.Integer("entry_id"), row => row.Integer("n"));
            var found = new List<long>();
            foreach (var group in rows.Where(row => !row.Flag("locked") && row.Text("match_type").StartsWith("auto", StringComparison.OrdinalIgnoreCase)).GroupBy(row => row.Integer("entry_id")))
            {
                var list = group.ToList();
                var titles = list.Select(row => TextMatcher.NormalizeTitle(row.Text("source_title"))).Where(title => title.Length > 0).ToHashSet(StringComparer.Ordinal).Count;
                var perSource = list.GroupBy(row => row.Text("source_id"), StringComparer.Ordinal).Max(source => source.Count());
                if (list.Count >= 8 || list.Count >= 5 && titles >= 3 || perSource >= 4 || aliases.GetValueOrDefault(group.Key) >= 16) found.Add(group.Key);
                if (found.Count >= Math.Max(1, limit)) break;
            }
            return found;
        });
        int checkedRecords = 0, splitRecords = 0, repairedEntries = 0;
        foreach (var raw in entries)
        {
            var entry = Read(c => Canonical(c, raw));
            var rows = Read(c => new[] { ("source_matches", "primary"), ("source_variants", "additional") }.SelectMany(item =>
                c.Query("SELECT $p0 kind,r.* FROM " + item.Item1 + " r WHERE entry_id=$p1 AND active=1 AND locked=0 AND lower(match_type) LIKE 'auto%'", P(item.Item2, entry))).ToList());
            var changed = false;
            foreach (var row in rows)
            {
                var evidence = evidenceFor(entry, Payload(row), row.Text("kind"));
                if (evidence is null) continue; // a trusted record of the entry
                checkedRecords++;
                if (Truth(evidence["eligible"])) continue;
                static double Score(JsonObject source, string key) => source[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : Integer(source[key]);
                var title = Score(evidence, "title_score"); var summary = Score(evidence, "summary_similarity"); var author = Score(evidence, "author_similarity");
                var clearlyForeign = title < 0.75 && summary < 0.90;
                var genericCollision = title >= 0.985 && !Truth(evidence["distinctive_title"]) && summary < 0.82 && author < 0.90;
                if (!(clearlyForeign || genericCollision)) continue;
                long split;
                try { split = operations.SplitRecords(entry, [new NativeSourceRecord(row.Text("source_id"), row.Integer("source_manga_id"))]); }
                catch (Exception error) when (error is KeyNotFoundException or ArgumentException or InvalidOperationException) { continue; }
                if (split > 0)
                    Write(c =>
                    {
                        _manual.ReviewPair(c, entry, split, "not_duplicate");
                        Log("split.automatic.repair", DecisionActor.Upgrade("automatic-source-attachment-repair-0.2.69"), [entry, split],
                            [RecordKey(row.Text("source_id"), row.Integer("source_manga_id"))], clearlyForeign ? "Clearly foreign automatic record split away" : "Generic exact-title collision split away");
                        return 0;
                    });
                splitRecords++; changed = true;
            }
            if (changed) repairedEntries++;
        }
        if (repairedEntries > 0) operations.Invalidate([]);
        return new JsonObject { ["checked"] = checkedRecords, ["repaired_entries"] = repairedEntries, ["split_records"] = splitRecords };
    }

    /// <summary>_record_repair_snapshot: one stored record reduced to what the repair compares (title, summary, author, thumbnail, edition).</summary>
    public RecordRepairSnapshot RecordRepairSnapshotOf(DatabaseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var title = row.Text("source_title").Trim();
        var subject = "source:" + row.Text("source_id") + ":" + row.Integer("source_manga_id").ToString(CultureInfo.InvariantCulture);
        var fingerprint = Read(c => c.Scalar("SELECT fingerprint FROM thumbnail_fingerprints WHERE subject_key=$p0", P(subject)) as string ?? "");
        return new RecordRepairSnapshot(row.Text("source_id"), row.Integer("source_manga_id"), title, TextMatcher.NormalizeTitle(title), row.Text("description"),
            row.Text("author"), fingerprint, TextMatcher.TitleColorVariant(title), MediaMode(title, row.Text("genre")), row.Flag("locked"));
    }

    /// <summary>_candidate_media_mode: the title marker and webtoon/manga genres; unknown when absent or contradictory.</summary>
    public static string MediaMode(string title, string genres)
    {
        var modes = new List<string>();
        var fromTitle = TextMatcher.TitleMediaVariant(title);
        if (fromTitle != "unknown") modes.Add(fromTitle);
        foreach (var genre in (genres ?? "").Split(GenreSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = TextMatcher.NormalizeTitle(genre);
            if (normalized is "webtoon" or "web comic" or "webcomic") modes.Add("webtoon");
            else if (normalized is "manga" or "japanese manga") modes.Add("manga");
        }
        return modes.Count == 0 || modes.Distinct(StringComparer.Ordinal).Count() > 1 ? "unknown" : modes[0];
    }

    /// <summary>_repair_records_directly_compatible: never across colour or media editions; otherwise strong title, summary, author or thumbnail agreement.</summary>
    public static bool RecordsDirectlyCompatible(RecordRepairSnapshot left, RecordRepairSnapshot right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        static bool Known(string mode, string a, string b) => mode == a || mode == b;
        if (Known(left.ColorMode, "colored", "monochrome") && Known(right.ColorMode, "colored", "monochrome") && left.ColorMode != right.ColorMode) return false;
        if (Known(left.MediaMode, "manga", "webtoon") && Known(right.MediaMode, "manga", "webtoon") && left.MediaMode != right.MediaMode) return false;
        var title = left.Title.Length > 0 && right.Title.Length > 0 ? TextMatcher.TitleSimilarity(left.Title, right.Title) : 0;
        var summary = TextMatcher.DescriptionSimilarity(left.Description, right.Description);
        var author = left.Author.Length > 0 && right.Author.Length > 0 ? TextMatcher.NameSimilarity(left.Author, right.Author) : 0;
        var thumbnail = left.Fingerprint.Length > 0 && left.Fingerprint == right.Fingerprint;
        if (left.Normalized.Length > 0 && left.Normalized == right.Normalized) return true;
        return thumbnail && title >= 0.90 && summary >= 0.55 || title >= 0.94 && summary >= 0.65
            || title >= 0.90 && author >= 0.80 && summary >= 0.45 || summary >= 0.92 && title >= 0.70;
    }

    /// <summary>
    /// _sanitize_catalog_text: keeps only provenance-strict alternate titles (description aliases, matched aliases, manual
    /// titles), splits comma-packed legacy aliases, rebuilds source-owned aliases from the raw source payloads and cleans
    /// stored summaries. Manual titles are kept as they are.
    /// </summary>
    public JsonObject SanitizeCatalogText()
    {
        var (removed, rebuilt, cleaned) = Write(c =>
        {
            int removedTitles = 0, rebuiltTitles = 0, cleanedDescriptions = 0;
            foreach (var row in c.Query("SELECT id,entry_id,title,title_type,language,source_id,source_manga_id FROM titles"))
            {
                var type = row.Text("title_type").Trim().ToLowerInvariant();
                var title = row.Text("title");
                if (type is not ("description-alias" or "matched-alias" or "manual") || !TextMatcher.IsMeaningfulTitle(title)
                    || type == "description-alias" && TextMatcher.IsAliasFragment(title))
                {
                    c.Execute("DELETE FROM titles WHERE id=$p0", P(row.Integer("id"))); removedTitles++;
                    continue;
                }
                if (type != "description-alias") continue;
                var original = title.Trim();
                var split = TextMatcher.ExtractAltTitles("Alternative titles: " + original);
                var norms = split.Select(TextMatcher.NormalizeTitle).Where(value => value.Length > 0).ToList();
                if (norms.Count <= 1 || norms.Contains(TextMatcher.NormalizeTitle(original))) continue;
                c.Execute("DELETE FROM titles WHERE id=$p0", P(row.Integer("id"))); removedTitles++;
                foreach (var value in split)
                {
                    var normalized = TextMatcher.NormalizeTitle(value);
                    if (normalized.Length == 0) continue;
                    c.Execute("INSERT OR IGNORE INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($p0,$p1,$p2,$p3,'description-alias','',$p4)",
                        P(row.Integer("entry_id"), value, normalized, row.Text("language"), row.Integer("source_manga_id")));
                }
            }
            // Description aliases without a source owner are legacy parser debris; manual titles have their own type.
            removedTitles += c.Execute("DELETE FROM titles WHERE title_type='description-alias' AND COALESCE(source_id,'')=''");
            // Matched source titles are rebuilt from the records that are attached now.
            removedTitles += c.Execute("DELETE FROM titles WHERE title_type='matched-alias'");
            var languages = c.Query("SELECT source_id,lang FROM source_config").ToDictionary(row => row.Text("source_id"), row => row.Text("lang"), StringComparer.Ordinal);
            const string Upsert = """
                INSERT INTO titles(entry_id,title,normalized,language,title_type,source_id,source_manga_id) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)
                ON CONFLICT(entry_id,normalized,source_id,title_type) DO UPDATE SET title=excluded.title,language=excluded.language,source_manga_id=excluded.source_manga_id
                """;
            foreach (var record in c.Query("""
                SELECT entry_id,source_id,source_manga_id,source_title,metadata_json FROM source_matches WHERE active=1
                UNION ALL SELECT entry_id,source_id,source_manga_id,source_title,metadata_json FROM source_variants WHERE active=1
                """))
            {
                var entry = record.Integer("entry_id"); var sourceId = record.Text("source_id"); var mangaId = record.Integer("source_manga_id");
                var language = languages.GetValueOrDefault(sourceId, "");
                var sourceTitle = record.Text("source_title").Trim();
                if (TextMatcher.IsMeaningfulTitle(sourceTitle))
                {
                    c.Execute(Upsert, P(entry, sourceTitle, TextMatcher.NormalizeTitle(sourceTitle), language, "matched-alias", sourceId, mangaId));
                    rebuiltTitles++;
                }
                string description;
                try { description = Text((JsonNode.Parse(record.Text("metadata_json", "{}")) as JsonObject)?["description"]); }
                catch (System.Text.Json.JsonException) { description = ""; }
                c.Execute("DELETE FROM titles WHERE entry_id=$p0 AND source_id=$p1 AND source_manga_id=$p2 AND title_type='description-alias'", P(entry, sourceId, mangaId));
                if (description.Trim().Length == 0) continue;
                foreach (var value in TextMatcher.ExtractAltTitles(description))
                {
                    var normalized = TextMatcher.NormalizeTitle(value);
                    if (normalized.Length == 0) continue;
                    c.Execute(Upsert, P(entry, value, normalized, language, "description-alias", sourceId, mangaId));
                    rebuiltTitles++;
                }
            }
            foreach (var table in new[] { "entries", "source_matches", "source_variants" })
                foreach (var row in c.Query("SELECT id,description FROM " + table + " WHERE description<>''"))
                {
                    var original = row.Text("description");
                    var summary = TextMatcher.CleanSummary(original);
                    if (summary == original) continue;
                    c.Execute("UPDATE " + table + " SET description=$p0 WHERE id=$p1", P(summary, row.Integer("id")));
                    cleanedDescriptions++;
                }
            return (removedTitles, rebuiltTitles, cleanedDescriptions);
        });
        if (removed + rebuilt + cleaned > 0) _operations?.Invalidate([]);
        return new JsonObject { ["removed_titles"] = removed, ["rebuilt_titles"] = rebuilt, ["cleaned_descriptions"] = cleaned };
    }
}
