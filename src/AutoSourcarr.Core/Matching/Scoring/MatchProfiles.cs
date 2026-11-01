using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Matching.Decisions;
using AutoSourcarr.Core.Matching.Review;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Reader;
using AutoSourcarr.Core.Sources.Configuration;

namespace AutoSourcarr.Core.Matching.Scoring;

/// <summary>A refused profile, preset or rule change with the HTTP status it maps to (400, 404 or 409).</summary>
public sealed class MatchingProfileException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// One matching profile. <see cref="Rule"/> is the 0.4.x rule contract ({enabled, checks}) stored in matching_presets /
/// matching_policies, so a rolled-back 0.4.5 reads it unchanged; <see cref="Settings"/> holds the simple switches or the
/// advanced thresholds and weights (kept beside it, in the settings table); <see cref="Options"/> is what the scorer uses.
/// </summary>
public sealed record MatchProfile(string Id, string Name, string Description, string Kind, bool Builtin, JsonObject Rule, JsonObject Settings, MatchScoringOptions Options)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["name"] = Name, ["description"] = Description, ["kind"] = Kind, ["builtin"] = Builtin,
        ["rule"] = Rule.DeepClone(), ["settings"] = Settings.DeepClone(), ["options"] = MatchProfiles.OptionsJson(Options),
    };
}

/// <summary>
/// Matching profiles: "ready-made" (the fixed 0.4.5 presets), "simple" (a few switches) and "advanced" (per-field
/// thresholds and weights). Whatever a profile says, the title floor stays at or above 0.94 and the identity and edition
/// safeguards of <see cref="MatchScorer"/> stay on.
/// </summary>
public static class MatchProfiles
{
    public const string ReadyMade = "ready-made";
    public const string Simple = "simple";
    public const string Advanced = "advanced";
    public static readonly IReadOnlyList<string> SimpleSwitches = ["strict_titles", "require_creators", "require_summary", "require_identity_evidence", "exact_year"];

    /// <summary>The released presets (matching_policies.presets()), as fresh copies.</summary>
    public static IReadOnlyList<MatchProfile> ReadyMadeProfiles() =>
    [
        Builtin("title", "High title similarity", "Require native titles to be at least 97% similar.", new() { ["title"] = .97 }),
        Builtin("creators", "Titles + authors / artists", "Require 97% title and 90% author / artist similarity. Missing creators block automatic matching.",
            new() { ["title"] = .97, ["author"] = .90 }),
        Builtin("summary", "Titles + summaries", "Require 97% title and 90% summary similarity. Missing summaries block automatic matching.",
            new() { ["title"] = .97, ["summary"] = .90 }),
        Builtin("gallery", "Titles + informative tags", "Require 97% title similarity and 70% informative tag overlap. Missing informative tags block automatic matching.",
            new() { ["title"] = .97, ["tags"] = .70 }),
        Builtin("strict", "Strict metadata", "Require 99% title, 95% summary and 95% author / artist similarity. All three must be available.",
            new() { ["title"] = .99, ["summary"] = .95, ["author"] = .95 }),
    ];

    private static MatchProfile Builtin(string id, string name, string description, JsonObject checks)
    {
        var rule = new JsonObject { ["enabled"] = true, ["checks"] = checks };
        return new(id, name, description, ReadyMade, true, rule, new JsonObject(), OptionsFor(rule, null));
    }

    /// <summary>Scorer options for a stored rule plus optional simple/advanced settings.</summary>
    public static MatchScoringOptions OptionsFor(JsonObject? rule, JsonObject? settings)
    {
        var options = MatchScoringOptions.Default;
        if (rule?["checks"] is JsonObject checks)
        {
            if (Number(checks["title"]) is { } title) options = options with { Floor = title };
            if (Number(checks["author"]) is { } author) options = options with { RequiredAuthor = author };
            if (Number(checks["summary"]) is { } summary) options = options with { RequiredSummary = summary };
        }
        if (settings is not null)
        {
            if (Flag(settings["require_identity_evidence"])) options = options with { AlwaysRequireIdentityEvidence = true };
            if (Flag(settings["exact_year"])) options = options with { YearTolerance = 0 };
            options = options with
            {
                GenericFloor = Number(settings["generic_floor"]) ?? options.GenericFloor,
                AuthorAgreement = Number(settings["author_agreement"]) ?? options.AuthorAgreement,
                AuthorConflict = Number(settings["author_conflict"]) ?? options.AuthorConflict,
                YearTolerance = Number(settings["year_tolerance"]) is { } years ? (int)Math.Round(years) : options.YearTolerance,
                CoverAgreementDistance = Number(settings["cover_agreement_distance"]) is { } near ? (int)Math.Round(near) : options.CoverAgreementDistance,
                CoverConflictDistance = Number(settings["cover_conflict_distance"]) is { } far ? (int)Math.Round(far) : options.CoverConflictDistance,
                SummaryCorroboration = Number(settings["summary_corroboration"]) ?? options.SummaryCorroboration,
            };
            if (settings["weights"] is JsonObject weights)
                options = options with
                {
                    AuthorWeight = Number(weights["author"]) ?? options.AuthorWeight,
                    YearWeight = Number(weights["year"]) ?? options.YearWeight,
                    CoverWeight = Number(weights["cover"]) ?? options.CoverWeight,
                    SummaryWeight = Number(weights["summary"]) ?? options.SummaryWeight,
                };
        }
        return options.Normalized();
    }

    /// <summary>
    /// Builds the stored rule and settings of a simple or advanced profile. Simple: switches strict_titles (97% titles),
    /// require_creators (90% creators), require_summary (90% summaries), require_identity_evidence, exact_year.
    /// Advanced: checks {title, author, summary, tags} plus generic_floor, author_agreement, author_conflict, year_tolerance,
    /// cover_agreement_distance, cover_conflict_distance, summary_corroboration, require_identity_evidence and weights
    /// {author, year, cover, summary}.
    /// </summary>
    public static (JsonObject Rule, JsonObject Settings) Build(string kind, JsonObject? settings)
    {
        settings ??= new JsonObject();
        if (kind == Simple)
        {
            var unknown = settings.Select(pair => pair.Key).Where(key => !SimpleSwitches.Contains(key)).ToArray();
            if (unknown.Length > 0) throw new MatchingProfileException(400, "Unknown simple switch: " + string.Join(", ", unknown) + ".");
            var clean = new JsonObject();
            foreach (var key in SimpleSwitches)
            {
                if (settings[key] is null) continue;
                if (settings[key] is not JsonValue value || !value.TryGetValue<bool>(out var on)) throw new MatchingProfileException(400, key + " must be true or false.");
                clean[key] = on;
            }
            var checks = new JsonObject { ["title"] = Flag(clean["strict_titles"]) ? .97 : MatchScorer.MinimumFloor };
            if (Flag(clean["require_creators"])) checks["author"] = .90;
            if (Flag(clean["require_summary"])) checks["summary"] = .90;
            return (Validated(new JsonObject { ["enabled"] = true, ["checks"] = checks }), clean);
        }
        if (kind == Advanced)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "checks", "generic_floor", "author_agreement", "author_conflict", "year_tolerance",
                "cover_agreement_distance", "cover_conflict_distance", "summary_corroboration", "require_identity_evidence", "exact_year", "weights" };
            var unknown = settings.Select(pair => pair.Key).Where(key => !allowed.Contains(key)).ToArray();
            if (unknown.Length > 0) throw new MatchingProfileException(400, "Unknown advanced setting: " + string.Join(", ", unknown) + ".");
            foreach (var pair in settings)
            {
                if (pair.Key is "checks" or "weights")
                {
                    if (pair.Value is not JsonObject inner || inner.Any(item => Number(item.Value) is null))
                        throw new MatchingProfileException(400, pair.Key + " must be an object of numbers.");
                    if (pair.Key == "weights" && inner.Any(item => item.Key is not ("author" or "year" or "cover" or "summary")))
                        throw new MatchingProfileException(400, "Weights exist for author, year, cover and summary.");
                }
                else if (pair.Key is "require_identity_evidence" or "exact_year")
                {
                    if (pair.Value is not JsonValue flag || !flag.TryGetValue<bool>(out _)) throw new MatchingProfileException(400, pair.Key + " must be true or false.");
                }
                else if (Number(pair.Value) is null) throw new MatchingProfileException(400, pair.Key + " must be a number.");
            }
            var checks = settings["checks"] as JsonObject ?? new JsonObject { ["title"] = MatchScorer.MinimumFloor };
            var rule = Validated(new JsonObject { ["enabled"] = true, ["checks"] = checks.DeepClone() });
            var clean = (JsonObject)settings.DeepClone(); clean.Remove("checks");
            return (rule, clean);
        }
        throw new MatchingProfileException(400, "Profile kind must be simple or advanced (ready-made profiles are fixed).");
    }

    /// <summary>The released rule contract (MatchingRuleConfiguration.Validate); a title check can never go below 0.94.</summary>
    public static JsonObject Validated(JsonNode? rule)
    {
        try { return MatchingRuleConfiguration.Validate(rule) ?? throw new MatchingProfileException(400, "Choose matching checks or save a Disabled preset."); }
        catch (ArgumentException error) { throw new MatchingProfileException(400, error.Message); }
    }

    public static JsonObject OptionsJson(MatchScoringOptions options) => new()
    {
        ["floor"] = options.Floor, ["generic_floor"] = options.GenericFloor, ["require_identity_evidence"] = options.AlwaysRequireIdentityEvidence,
        ["year_tolerance"] = options.YearTolerance, ["author_agreement"] = options.AuthorAgreement, ["author_conflict"] = options.AuthorConflict,
        ["cover_agreement_distance"] = options.CoverAgreementDistance, ["cover_conflict_distance"] = options.CoverConflictDistance,
        ["summary_corroboration"] = options.SummaryCorroboration, ["required_author"] = options.RequiredAuthor, ["required_summary"] = options.RequiredSummary,
        ["weights"] = new JsonObject { ["author"] = options.AuthorWeight, ["year"] = options.YearWeight, ["cover"] = options.CoverWeight, ["summary"] = options.SummaryWeight },
        ["editions_separate"] = true,
    };

    /// <summary>The stricter of two option sets (higher floors and requirements, smaller tolerances); weights come from <paramref name="first"/>.</summary>
    public static MatchScoringOptions Stricter(MatchScoringOptions first, MatchScoringOptions second)
    {
        ArgumentNullException.ThrowIfNull(first); ArgumentNullException.ThrowIfNull(second);
        static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
        return (first with
        {
            Floor = Math.Max(first.Floor, second.Floor), GenericFloor = Math.Max(first.GenericFloor, second.GenericFloor),
            AlwaysRequireIdentityEvidence = first.AlwaysRequireIdentityEvidence || second.AlwaysRequireIdentityEvidence,
            YearTolerance = Math.Min(first.YearTolerance, second.YearTolerance),
            AuthorAgreement = Math.Max(first.AuthorAgreement, second.AuthorAgreement), AuthorConflict = Math.Max(first.AuthorConflict, second.AuthorConflict),
            CoverAgreementDistance = Math.Min(first.CoverAgreementDistance, second.CoverAgreementDistance),
            CoverConflictDistance = Math.Min(first.CoverConflictDistance, second.CoverConflictDistance),
            SummaryCorroboration = Math.Max(first.SummaryCorroboration, second.SummaryCorroboration),
            RequiredAuthor = Max(first.RequiredAuthor, second.RequiredAuthor), RequiredSummary = Max(first.RequiredSummary, second.RequiredSummary),
        }).Normalized();
    }

    /// <summary>The stricter combination of several option sets, or null when there are none.</summary>
    public static MatchScoringOptions? Combine(IEnumerable<MatchScoringOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        MatchScoringOptions? result = null;
        foreach (var item in options) result = result is null ? item.Normalized() : Stricter(result, item);
        return result;
    }

    internal static double? Number(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    internal static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}

/// <summary>Inputs of the default source ranking.</summary>
public sealed record SourceRank(string SourceId, string Name, bool Official, string Health, long Priority)
{
    /// <summary>0 working, 1 not checked / inconclusive, 2 rate limited or waiting for verification, 3 failed.</summary>
    public int HealthTier => Health switch
    {
        "working" => 0,
        "rate_limited" or "verification" => 2,
        "failed" => 3,
        _ => 1,
    };
}

/// <summary>
/// The default ranking every profile starts from: official publisher sources first (source_config.official, or the
/// conservative name/domain detection of MangaPlus, Webtoons, K Manga, Azuki, Comikey, ...), then source health
/// (source_health.state), then quality (the source priority the user set; lower is better), then name.
/// </summary>
public static class SourceRanking
{
    public static IReadOnlyList<SourceRank> Order(IEnumerable<SourceRank> sources) => sources
        .OrderBy(source => source.Official ? 0 : 1).ThenBy(source => source.HealthTier).ThenBy(source => source.Priority)
        .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase).ThenBy(source => source.SourceId, StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<SourceRank> Load(DatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Order(session.Query("""
            SELECT sc.source_id,sc.name,sc.base_url,sc.official,sc.priority,COALESCE(h.state,'unchecked') state
            FROM source_config sc LEFT JOIN source_health h ON h.source_id=sc.source_id WHERE sc.available=1
            """).Select(row => new SourceRank(row.Text("source_id"), row.Text("name"),
                row.Flag("official") || ChapterResolver.ProbablyOfficialSource(row.Text("name"), row.Text("base_url")),
                row.Text("state", "unchecked"), row.Integer("priority", 1000))).ToArray());
    }

    /// <summary>Rank position per source id (0 = best); unknown sources sort last.</summary>
    public static IReadOnlyDictionary<string, int> Positions(IReadOnlyList<SourceRank> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < ranked.Count; index++) positions.TryAdd(ranked[index].SourceId, index);
        return positions;
    }
}

/// <summary>
/// matching_presets (profiles) over the catalogue database with the released revision check
/// (sha256 of the rows as Python json.dumps(sort_keys=True)). Ready-made profiles are built in and read-only; simple and
/// advanced settings live in the settings table (<see cref="SettingsKey"/>) so the preset rows stay readable by 0.4.5.
/// Every change records a "profile.write" / "profile.delete" decision.
/// </summary>
public sealed class MatchingPresetStore(SqliteDatabase database, object sourceWriteGate, IDecisionLog decisions, TimeProvider? clock = null)
{
    public const string SettingsKey = "matching_profile_settings_v1";
    private const int MaximumPresets = 205;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _cacheGate = new();
    private (long At, IReadOnlyList<MatchProfile> Profiles)? _cache;

    /// <summary>
    /// The scorer options a stored matching rule stands for: when the rule is the rule of a simple or advanced profile
    /// (the policy was set from that profile), that profile's thresholds and weights (identity-evidence requirement, year
    /// tolerance, cover distances, weights); several such profiles combine strictly. Otherwise the rule's own checks.
    /// </summary>
    public MatchScoringOptions OptionsForRule(JsonObject rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        IReadOnlyList<MatchProfile> profiles;
        var now = _clock.GetTimestamp();
        lock (_cacheGate) profiles = _cache is { } cached && _clock.GetElapsedTime(cached.At, now) < TimeSpan.FromSeconds(10) ? cached.Profiles : [];
        if (profiles.Count == 0)
        {
            profiles = Profiles();
            lock (_cacheGate) _cache = (now, profiles);
        }
        var key = SourceConfigurationJson.PythonJson(rule, sortKeys: true);
        var matching = profiles.Where(profile => !profile.Builtin && profile.Kind is MatchProfiles.Simple or MatchProfiles.Advanced
                && SourceConfigurationJson.PythonJson(profile.Rule, sortKeys: true) == key)
            .Select(profile => profile.Options).ToArray();
        return MatchProfiles.Combine(matching) ?? MatchProfiles.OptionsFor(rule, null);
    }

    private void Invalidate() { lock (_cacheGate) _cache = null; }

    public JsonObject Configuration()
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, Configuration);
    }

    public IReadOnlyList<MatchProfile> Profiles()
    {
        lock (sourceWriteGate) return database.Read(DatabaseFile.Catalog, Profiles);
    }

    /// <summary>
    /// Creates (presetId null) or edits a preset: {revision, name, description?, rule} or {revision, name, description?,
    /// kind: "simple"|"advanced", settings}. Built-in presets are copied with Save as new, never edited.
    /// </summary>
    public JsonObject Save(JsonObject body, string? presetId, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(actor);
        var revision = Text(body["revision"]);
        var name = Text(body["name"]).Trim(); var description = Text(body["description"]).Trim();
        if (name.Length == 0 || name.Length > 80 || name.Any(character => character < 32 || character == 127))
            throw new MatchingProfileException(400, "Use a preset name of 1–80 characters without line breaks.");
        if (description.Length > 500 || description.Any(character => character < 32 && character is not ('\n' or '\t')))
            throw new MatchingProfileException(400, "Preset descriptions are limited to 500 characters.");
        var kind = Text(body["kind"]);
        JsonObject rule; JsonObject settings;
        if (kind is MatchProfiles.Simple or MatchProfiles.Advanced) (rule, settings) = MatchProfiles.Build(kind, body["settings"] as JsonObject);
        else
        {
            if (kind.Length > 0 && kind != "custom") throw new MatchingProfileException(400, "Profile kind must be simple or advanced.");
            if (body["rule"] is JsonObject { } raw && raw["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && !on && raw["checks"] is JsonObject { Count: 0 })
                rule = new JsonObject { ["enabled"] = false, ["checks"] = new JsonObject() };
            else rule = MatchProfiles.Validated(body["rule"]);
            kind = "custom"; settings = new JsonObject();
        }
        lock (sourceWriteGate)
        {
            return database.Write(DatabaseFile.Catalog, session =>
            {
                if (revision != Revision(session)) throw new MatchingProfileException(409, "Presets changed in another window. Reload presets before saving.");
                var current = Profiles(session);
                var selected = current.FirstOrDefault(profile => profile.Id == presetId);
                if (presetId is not null)
                {
                    if (selected is null) throw new MatchingProfileException(404, "Preset no longer exists.");
                    if (selected.Builtin) throw new MatchingProfileException(400, "Use Save as new to customize a built-in preset.");
                }
                else if (current.Count >= MaximumPresets) throw new MatchingProfileException(400, "This instance already has 200 custom presets. Edit or delete an existing preset.");
                if (current.Any(profile => profile.Id != presetId && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new MatchingProfileException(400, "A preset with this name already exists. Choose a different name.");
                var id = presetId ?? "custom-" + Guid.NewGuid().ToString("D");
                session.Execute("""
                    INSERT INTO matching_presets(id,name,description,rule_json,updated_at) VALUES($id,$name,$description,$rule,$time)
                    ON CONFLICT(id) DO UPDATE SET name=excluded.name,description=excluded.description,rule_json=excluded.rule_json,updated_at=excluded.updated_at
                    """, ("$id", id), ("$name", name), ("$description", description), ("$rule", SourceConfigurationJson.PythonJson(rule)),
                    ("$time", SourceConfigurationJson.Timestamp(_clock)));
                var stored = StoredSettings(session);
                if (kind == "custom") stored.Remove(id); else stored[id] = new JsonObject { ["kind"] = kind, ["settings"] = settings };
                WriteSettings(session, stored);
                decisions.Record(new("profile.write", actor, [], [], (selected is null ? "created " : "changed ") + "matching profile " + name,
                    selected?.ToJson(), new JsonObject { ["id"] = id, ["name"] = name, ["kind"] = kind, ["rule"] = rule.DeepClone(), ["settings"] = settings.DeepClone() }));
                Invalidate();
                return Configuration(session);
            });
        }
    }

    public JsonObject Delete(string presetId, string revision, DecisionActor actor)
    {
        ArgumentNullException.ThrowIfNull(presetId); ArgumentNullException.ThrowIfNull(actor);
        lock (sourceWriteGate)
        {
            return database.Write(DatabaseFile.Catalog, session =>
            {
                if (revision != Revision(session)) throw new MatchingProfileException(409, "Presets changed in another window. Reload presets before deleting.");
                var selected = Profiles(session).FirstOrDefault(profile => profile.Id == presetId) ?? throw new MatchingProfileException(404, "Preset no longer exists.");
                if (selected.Builtin) throw new MatchingProfileException(400, "Built-in presets cannot be deleted.");
                session.Execute("DELETE FROM matching_presets WHERE id=$id", ("$id", presetId));
                var stored = StoredSettings(session);
                if (stored.Remove(presetId)) WriteSettings(session, stored);
                decisions.Record(new("profile.delete", actor, [], [], "deleted matching profile " + selected.Name, selected.ToJson()));
                Invalidate();
                return Configuration(session);
            });
        }
    }

    private JsonObject Configuration(DatabaseSession session) => new()
    {
        ["presets"] = new JsonArray(Profiles(session).Select(profile => (JsonNode)profile.ToJson()).ToArray()),
        ["preset_revision"] = Revision(session),
        ["kinds"] = new JsonArray(MatchProfiles.ReadyMade, MatchProfiles.Simple, MatchProfiles.Advanced),
        ["simple_switches"] = new JsonArray(MatchProfiles.SimpleSwitches.Select(name => (JsonNode)name).ToArray()),
        ["default_options"] = MatchProfiles.OptionsJson(MatchScoringOptions.Default.Normalized()),
        ["source_ranking"] = new JsonArray(SourceRanking.Load(session).Select(rank => (JsonNode)new JsonObject
        {
            ["source_id"] = rank.SourceId, ["name"] = rank.Name, ["official"] = rank.Official, ["health"] = rank.Health, ["priority"] = rank.Priority,
        }).ToArray()),
    };

    private static List<MatchProfile> Profiles(DatabaseSession session)
    {
        var profiles = MatchProfiles.ReadyMadeProfiles().ToList();
        var stored = StoredSettings(session);
        foreach (var row in Rows(session))
        {
            JsonObject rule;
            try { rule = JsonNode.Parse(row.Text("rule_json")) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { rule = new JsonObject(); }
            var extra = stored[row.Text("id")] as JsonObject;
            var kind = Text(extra?["kind"]) is { Length: > 0 } known ? known : "custom";
            var settings = extra?["settings"] as JsonObject ?? new JsonObject();
            profiles.Add(new(row.Text("id"), row.Text("name"), row.Text("description"), kind, false, rule, (JsonObject)settings.DeepClone(),
                MatchProfiles.OptionsFor(rule, settings)));
        }
        return profiles;
    }

    private static IReadOnlyList<DatabaseRow> Rows(DatabaseSession session) =>
        session.Query("SELECT id,name,description,rule_json,updated_at FROM matching_presets ORDER BY name COLLATE NOCASE,id");

    /// <summary>The released preset revision: sha256 of json.dumps(rows, sort_keys=True).</summary>
    private static string Revision(DatabaseSession session)
    {
        var rows = new JsonArray(Rows(session).Select(row => (JsonNode)new JsonObject
        {
            ["id"] = row.Text("id"), ["name"] = row.Text("name"), ["description"] = row.Text("description"),
            ["rule_json"] = row.Text("rule_json"), ["updated_at"] = row.Text("updated_at"),
        }).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceConfigurationJson.PythonJson(rows, sortKeys: true)))).ToLowerInvariant();
    }

    private static JsonObject StoredSettings(DatabaseSession session)
    {
        if (session.Scalar("SELECT value FROM settings WHERE key=$key", ("$key", SettingsKey)) is not string raw) return new JsonObject();
        try { return JsonNode.Parse(raw) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static void WriteSettings(DatabaseSession session, JsonObject value) => session.Execute("""
        INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value
        """, ("$key", SettingsKey), ("$value", value.ToJsonString()));

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}

/// <summary>
/// matching_policies (per source / group, internal / external) over the released <see cref="MatchingRuleConfiguration"/>,
/// as the <see cref="IPolicyStore"/> the policy simulator and review workbench use. Every write records "policy.write".
/// </summary>
public sealed class MatchingPolicyStore : IPolicyStore
{
    private readonly SqliteDatabase _database;
    private readonly object _gate;
    private readonly IDecisionLog _decisions;
    private readonly MatchingRuleConfiguration _rules;

    public MatchingPolicyStore(SqliteDatabase database, object sourceWriteGate, IDecisionLog decisions, Action<SourceConfigurationEffect>? committed = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(sourceWriteGate); ArgumentNullException.ThrowIfNull(decisions);
        _database = database; _gate = sourceWriteGate; _decisions = decisions;
        _rules = new(database, sourceWriteGate, committed ?? (_ => { }), clock);
    }

    /// <summary>{revision, rules, sources, groups} as in 0.4.5, plus the presets and the default source ranking.</summary>
    public JsonObject Configuration(MatchingPresetStore? presets = null)
    {
        var value = _rules.Read();
        if (presets is not null) foreach (var pair in presets.Configuration()) value[pair.Key] = pair.Value?.DeepClone();
        return value;
    }

    public IReadOnlyDictionary<MatchingRuleKey, JsonObject?> Rules() => _rules.Rules();

    public bool SubjectExists(MatchingRuleKey key)
    {
        lock (_gate)
            return _database.Read(DatabaseFile.Catalog, session => key.Scope == "source"
                ? session.Scalar("SELECT 1 FROM source_config WHERE source_id=$subject", ("$subject", key.Subject)) is not null
                : key.Scope == "group" && session.Scalar("SELECT 1 FROM source_groups WHERE CAST(id AS TEXT)=$subject", ("$subject", key.Subject)) is not null);
    }

    public PolicyOptions Options()
    {
        var configuration = _rules.Read();
        var sources = new JsonArray(((configuration["sources"] as JsonArray) ?? new JsonArray()).OfType<JsonObject>().Select(source => (JsonNode)new JsonObject
        {
            ["source_id"] = source["id"]?.DeepClone(), ["name"] = source["name"]?.DeepClone(), ["lang"] = source["lang"]?.DeepClone(),
        }).ToArray());
        var groups = new JsonArray(((configuration["groups"] as JsonArray) ?? new JsonArray()).OfType<JsonObject>().Select(group => (JsonNode)new JsonObject
        {
            ["id"] = group["id"]?.DeepClone(), ["name"] = group["name"]?.DeepClone(),
        }).ToArray());
        var presets = new JsonArray(MatchProfiles.ReadyMadeProfiles().Select(profile => (JsonNode)new JsonObject
        {
            ["id"] = profile.Id, ["name"] = profile.Name, ["description"] = profile.Description, ["rule"] = profile.Rule.DeepClone(),
        }).ToArray());
        return new(sources, groups, presets);
    }

    /// <summary>Saves (or with null removes) one rule; refuses a changed revision (MatchingRuleConflictException) and unknown subjects (KeyNotFoundException).</summary>
    public long Write(MatchingRuleKey key, JsonObject? rule, string expectedRevision, DecisionActor actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var before = Rules().GetValueOrDefault(key);
        _rules.Save(key.Scope, key.Subject, key.Direction, rule, expectedRevision);
        return _decisions.Record(new("policy.write", actor, [], [], reason.Length > 0 ? reason : "matching rule " + key.Scope + " " + key.Subject + " " + key.Direction,
            before is null ? null : new JsonObject { ["rule"] = before.DeepClone() }, new JsonObject
            {
                ["scope"] = key.Scope, ["subject"] = key.Subject, ["direction"] = key.Direction, ["rule"] = rule?.DeepClone(),
            }));
    }

    /// <summary>The effective rule for a source (its own, else its group's) and where it comes from.</summary>
    public (JsonObject? Rule, string Origin) Effective(string sourceId, string direction)
    {
        var rules = Rules();
        var sources = _database.Read(DatabaseFile.Catalog, session => SourceConfigurationStore.Sources(session))
            .ToDictionary(source => SourceConfigurationJson.Text(source["source_id"]), StringComparer.Ordinal);
        return MatchingRuleConfiguration.Effective(rules, sources, sourceId, direction);
    }
}
