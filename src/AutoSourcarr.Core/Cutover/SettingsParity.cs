using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Cutover;

/// <summary>
/// One 0.4.x setting and what became of it. Secret values are never kept here: <see cref="Before"/>/<see cref="After"/>
/// are null for secrets (compared by presence and equality only); other values have URL credentials masked.
/// </summary>
public sealed record SettingParityItem(string File, string Key, bool Secret, string Status, string? Before, string? After, bool Lost)
{
    public JsonObject ToJson() => new()
    {
        ["file"] = File, ["key"] = Key, ["secret"] = Secret, ["status"] = Status, ["before"] = Before, ["after"] = After, ["lost"] = Lost,
    };
}

/// <summary>An ARC-7 update notice line: a setting the update had to change, drop or now needs, and why.</summary>
public sealed record UpdateNoticeLine(string Key, string Kind, string Message)
{
    public JsonObject ToJson() => new() { ["key"] = Key, ["kind"] = Kind, ["message"] = Message };
}

/// <summary>Compares autosourcarr.env and source-host.env of the 0.4.x config with the migrated config.json (ARC-7).</summary>
public static partial class SettingsParity
{
    [GeneratedRegex(@"(?<=://)[^/@\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex(@"([?&;][^=&#;\s]+=)[^&#;\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex QueryValues();

    [GeneratedRegex(@"#\S*", RegexOptions.CultureInvariant)]
    private static partial Regex Fragment();

    /// <summary>Path segments that look like tokens (webhook ids, keys): 16+ URL-safe characters with a digit, or 24+.</summary>
    [GeneratedRegex(@"(?<=/)(?=[A-Za-z0-9_\-.~]*\d)[A-Za-z0-9_\-.~]{16,}(?=/|$|\?|#)|(?<=/)[A-Za-z0-9_\-.~]{24,}(?=/|$|\?|#)", RegexOptions.CultureInvariant)]
    private static partial Regex PathTokens();

    /// <summary>
    /// A non-secret setting value as the report may show it: in anything that looks like a URL the credentials, every
    /// query value, the fragment and token-like path segments are replaced by ***.
    /// </summary>
    public static string Mask(string? value)
    {
        if (value is null) return "";
        if (!value.Contains("://", StringComparison.Ordinal)) return value;
        var masked = UrlCredentials().Replace(value, "***@");
        masked = QueryValues().Replace(masked, "$1***");
        masked = Fragment().Replace(masked, "#***");
        return PathTokens().Replace(masked, "***");
    }

    public static List<SettingParityItem> Compare(string originalConfig, string migratedConfig)
    {
        var items = new List<SettingParityItem>();
        var store = new ConfigStore(migratedConfig);
        ConfigValues values;
        try { values = store.Exists ? store.Load() : ConfigValues.Empty; }
        catch (ConfigurationException error)
        {
            // config.json that 0.5.0 cannot read (for example config.key missing): every setting counts as lost.
            items.Add(new("config.json", "config.json", false, "LOST: " + error.Message, null, null, true));
            values = ConfigValues.Empty;
        }
        foreach (var (file, section) in new[] { (EnvConversion.MainEnv, "main"), (EnvConversion.SourceHostEnv, "host") })
        {
            var path = Path.Combine(originalConfig, file);
            if (!File.Exists(path)) continue;
            var parsed = DotEnv.Parse(StrictUtf8.GetString(File.ReadAllBytes(path)));
            foreach (var (key, before) in parsed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var secret = SettingsSecrets.IsSecretKey(key);
                if (before is null) { items.Add(new(file, key, secret, "no value in 0.4.x (nothing to keep)", null, null, false)); continue; }
                string? after = null; string where = "";
                if (section == "host") { if (values.SourceHost.TryGetValue(key, out var hosted)) { after = hosted; where = "source_host"; } }
                else if (values.Settings.TryGetValue(key, out var setting)) { after = setting; where = "settings"; }
                else if (values.Extra.TryGetValue(key, out var extra)) { after = extra; where = "extra"; }
                string status; var lost = false;
                if (after is null && UpdateNotices.RetiredLinkSettings.Contains(key, StringComparer.Ordinal)) status = "dropped: retired link setting (SET-4, 0.5.0 has no Komf/AI dashboard links)";
                else if (after is null) { status = "LOST: not in config.json"; lost = true; }
                else if (!string.Equals(after, before, StringComparison.Ordinal)) { status = "CHANGED"; lost = true; }
                else status = where == "extra" ? "kept in config.json (extra: not used by 0.5.0)" : "kept";
                items.Add(secret
                    ? new(file, key, true, status + (status.StartsWith("kept", StringComparison.Ordinal) ? " (secret, value not shown)" : ""), null, null, lost)
                    : new(file, key, false, status, Mask(before), after is null ? null : Mask(after), lost));
            }
        }
        return items;
    }

    /// <summary>
    /// The update notice: the items the app itself records in config.json (env → config.json, env_file, invalid values,
    /// kept-but-unused keys, defaults that changed), plus what 0.5.0 needs at deploy time (sign-in behind a proxy).
    /// Only key names and fixed text; never a value.
    /// </summary>
    /// <summary>
    /// The 0.4.x Compose <c>environment:</c> values (they overrode autosourcarr.env in 0.4.x and override config.json in
    /// 0.5.0 when you keep them). A value 0.5.0 refuses is lost; a retired key is dropped; secrets are never shown.
    /// </summary>
    public static List<SettingParityItem> CompareCompose(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var items = new List<SettingParityItem>();
        var known = AutoSourcarrSettings.Definitions.Select(definition => definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var secret = SettingsSecrets.IsSecretKey(key);
            string status; var lost = false;
            if (UpdateNotices.RetiredLinkSettings.Contains(key, StringComparer.Ordinal)) status = "dropped: retired link setting (SET-4)";
            else if (!known.Contains(key)) status = "Compose override not used by 0.5.0 (kept only if you keep it in Compose)";
            else
            {
                try
                {
                    SettingsLoader.Load(environment: new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value });
                    status = "Compose override: keep it in docker-compose.0.5.0.yml to keep this value (it overrides config.json)";
                }
                catch (ConfigurationException) { status = "LOST: 0.5.0 refuses this Compose value; change it before deploying"; lost = true; }
            }
            items.Add(new("compose environment", key, secret, status, secret ? null : Mask(value), null, lost));
        }
        return items;
    }

    public static List<UpdateNoticeLine> Notices(string originalConfig, string migratedConfig)
    {
        var lines = new List<UpdateNoticeLine>();
        var store = new ConfigStore(migratedConfig);
        var document = store.ReadDocument();
        if (document?.Update?.Notice is { } notice)
            foreach (var item in notice.Items) lines.Add(new(item.Key, item.Kind, item.Message));
        var original = Path.Combine(originalConfig, EnvConversion.MainEnv);
        var before = File.Exists(original) ? DotEnv.Parse(StrictUtf8.GetString(File.ReadAllBytes(original))) : new Dictionary<string, string?>();
        foreach (var key in UpdateNotices.RetiredLinkSettings.Where(before.ContainsKey))
            lines.Add(new(key, "dropped", key + " was removed: 0.5.0 no longer shows Komf or AI-dashboard shortcut links (SET-4). Nothing else depends on it."));
        if (document is null) return lines;
        ConfigValues values;
        try { values = store.Load(document); }
        catch (ConfigurationException error)
        {
            lines.Add(new("config.json", "must_change", "0.5.0 cannot read config.json: " + error.Message));
            return lines;
        }
        AutoSourcarrSettings? settings = null;
        try { settings = SettingsLoader.Load(configuration: values.Settings, managedOverrides: values.Settings.Where(pair => document.ManagedKeys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)); }
        catch (ConfigurationException error) { lines.Add(new("config.json", "must_change", "0.5.0 would refuse to start with these settings: " + error.Message)); }
        if (settings is not null)
        {
            if (!before.ContainsKey("AUTH_METHOD"))
                lines.Add(new("AUTH_METHOD", "action", "0.4.x relied on Authelia alone. 0.5.0 has its own sign-in; the default is AUTH_METHOD=forms (an owner account is created at the first visit). "
                    + "To keep Authelia as the only sign-in, set AUTH_METHOD=external and AUTH_TRUSTED_PROXIES to the address of your reverse proxy."));
            if (settings.AuthMethod == "external" && TrustedProxies.Problems(settings.AuthTrustedProxies, external: true).FirstOrDefault() is { } problem)
                lines.Add(new("AUTH_TRUSTED_PROXIES", "must_change", "AUTH_METHOD=external needs AUTH_TRUSTED_PROXIES (the reverse proxy's address); identity headers are refused otherwise. " + problem));
        }
        lines.Add(new("reader-repo", "action", "Add the GET/HEAD-only Authelia/NPM bypass for /api/reader/repo/ (the reader extension's repository) next to the unchanged ^/api/reader/v1([/?].*)?$ rule."));
        lines.Add(new("compose", "action", "Variables set in your Compose environment: are not part of the snapshot. They keep overriding config.json in 0.5.0; check them against docker-compose.0.5.0.yml."));
        return lines;
    }
}

/// <summary>A file of the 0.4.x folders and whether the migrated folders still have it (names only, never content).</summary>
public sealed record FileParityItem(string Path, string Status, bool Missing = false)
{
    public JsonObject ToJson() => new() { ["path"] = Path, ["status"] = Status };
}

public sealed class FileParityResult
{
    public int Same { get; set; }
    public List<FileParityItem> Changed { get; } = [];
    public List<FileParityItem> Missing { get; } = [];
    public int Added { get; set; }
    /// <summary>Keys and keystores (presence only).</summary>
    public List<FileParityItem> Keys { get; } = [];
    /// <summary>Missing or changed files that are not on the allowlist (repair packages, state files, uploads, ...): any → FAILED.</summary>
    public List<FileParityItem> Lost { get; } = [];
    /// <summary>Missing or changed files on the allowlist, with the reason.</summary>
    public List<FileParityItem> Allowed { get; } = [];

    public JsonObject ToJson() => new()
    {
        ["same"] = Same, ["added"] = Added,
        ["changed"] = new JsonArray(Changed.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["missing"] = new JsonArray(Missing.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["keys"] = new JsonArray(Keys.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["lost"] = new JsonArray(Lost.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["allowed"] = new JsonArray(Allowed.Select(item => (JsonNode)item.ToJson()).ToArray()),
    };
}

/// <summary>Compares the files of the 0.4.x folders with the migrated ones by SHA-256 (the databases are compared by rows instead).</summary>
public static partial class FileParity
{
    [GeneratedRegex(@"(\.(key|pem|jks|keystore|p12|pfx|bks)$)|keystore|signing", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyFile();

    private static readonly string[] DatabaseNames = ["catalog.sqlite", "chapter-origins.sqlite"];

    /// <summary>
    /// Files 0.5.0 may change or drop on purpose (prefix of "config/..." or "data/..." → reason). Everything else that
    /// is missing or changed after the migration fails: last-working repair packages (hard rule 5), source and repair
    /// state, setup state, import uploads, reset hints, keys.
    /// </summary>
    public static readonly IReadOnlyList<(string Prefix, string Reason)> Allowlist =
    [
        ("data/cover-cache/", "cover cache, refetched"), ("data/cache/", "cache"), ("data/.cache/", "cache"), ("data/diagnostics/", "diagnostic logs the app appends to"),
        ("data/support-logs/", "support logs the app appends to"), ("data/logs/", "logs the app appends to"),
        ("data/instance-backups/upgrade-state.json", "the update guard records the version it migrated to"),
    ];

    public static string? AllowedReason(string name) => Allowlist.FirstOrDefault(item => name.StartsWith(item.Prefix, StringComparison.Ordinal)).Reason;

    public static bool IsDatabaseFile(string name) => DatabaseNames.Any(db => name == db || name == db + "-wal" || name == db + "-shm" || name == db + "-journal");

    public static FileParityResult Compare(IEnumerable<(string Label, string Original, string Migrated)> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var result = new FileParityResult();
        foreach (var (label, original, migrated) in roots)
        {
            if (!Directory.Exists(original)) continue;
            var before = Files(original);
            var after = Directory.Exists(migrated) ? Files(migrated) : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (relative, path) in before.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var name = label + "/" + relative;
                if (IsDatabaseFile(System.IO.Path.GetFileName(relative))) continue;
                var keyFile = KeyFile().IsMatch(relative);
                if (!after.TryGetValue(relative, out var now))
                {
                    result.Missing.Add(new(name, "missing after the migration"));
                    if (keyFile) result.Keys.Add(new(name, "MISSING", Missing: true));
                    else if (AllowedReason(name) is { } why) result.Allowed.Add(new(name, "missing: " + why));
                    else result.Lost.Add(new(name, "MISSING after the migration"));
                    continue;
                }
                if (Hash(path) == Hash(now)) result.Same++;
                else
                {
                    result.Changed.Add(new(name, "content changed"));
                    if (AllowedReason(name) is { } why) result.Allowed.Add(new(name, "changed: " + why));
                    else result.Lost.Add(new(name, "CHANGED by the migration"));
                }
                if (keyFile) result.Keys.Add(new(name, "present"));
            }
            foreach (var relative in after.Keys.Where(key => !before.ContainsKey(key)))
            {
                result.Added++;
                if (KeyFile().IsMatch(relative)) result.Keys.Add(new(label + "/" + relative, "new in 0.5.0"));
            }
        }
        return result;
    }

    // Nothing is skipped: an unreadable folder throws instead of disappearing from both sides of the comparison.
    private static Dictionary<string, string> Files(string root) => Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
        .ToDictionary(path => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'), path => path, StringComparer.Ordinal);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
