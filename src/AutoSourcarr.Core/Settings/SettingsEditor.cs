using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Sources.Repositories;

namespace AutoSourcarr.Core.Settings;

/// <summary>A request that cannot be saved as sent; the message names the setting, never its value.</summary>
public sealed class SettingsValidationException(string message) : Exception(message);

/// <summary>config.json changed since the caller read it; nothing was written.</summary>
public sealed class SettingsConflictException(string message) : Exception(message);

public sealed record SettingsSaveResult(
    string Revision,
    IReadOnlyList<ConfigChange> Changes,
    IReadOnlyList<string> ChangedKeys,
    bool RestartRequired,
    IReadOnlyList<string> OverriddenByEnvironment,
    string? Transaction);

/// <summary>
/// The only writer of config.json after the first start: the Settings page and the setup wizard both save
/// through here. One atomic file replace is the commit, so no recovery journal is needed. Values are checked
/// with the same rules the wizard used in 0.4.x and then with the full settings parser before anything is written.
/// </summary>
public sealed partial class SettingsEditor
{
    public const string ExtensionStores = "EXTENSION_STORES";
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConfigStore _store;
    private readonly Func<AutoSourcarrSettings> _running;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly Action<IReadOnlyDictionary<string, string?>>? _registerSecrets;
    private readonly object _gate;

    public SettingsEditor(string root, Func<AutoSourcarrSettings> running, IReadOnlyDictionary<string, string?> environment,
        Action<IReadOnlyDictionary<string, string?>>? registerSecrets = null)
    {
        _store = new(root);
        _running = running;
        _environment = environment;
        _registerSecrets = registerSecrets;
        _gate = Gate(_store.Root);
    }

    public string Root => _store.Root;

    /// <summary>The one lock for every writer of config.json in this configuration root.</summary>
    internal static object Gate(string root) => Gates.GetOrAdd(Path.GetFullPath(root), _ => new object());

    /// <summary>Opaque version of config.json; a save must name the revision it was based on.</summary>
    public string Revision()
    {
        lock (_gate)
            return RevisionCore();
    }

    public ConfigSetupRecord? SetupState()
    {
        lock (_gate)
            return _store.ReadDocument()?.Setup;
    }

    /// <summary>
    /// True when a Compose variable decides the value: one is set and you have not saved this setting in the app
    /// (a value saved in the app wins over Compose, see ConfigDocument.ManagedKeys).
    /// </summary>
    public bool OverriddenByEnvironment(string key)
    {
        lock (_gate)
            return Overridden(key, _store.ReadDocument()?.ManagedKeys ?? []);
    }

    private bool Overridden(string key, IReadOnlyCollection<string> managed) =>
        _environment.TryGetValue(key, out var value) && value is not null && !managed.Contains(key);

    /// <summary>Effective text of every setting: config.json, else the running value. Includes secrets; never return this to a client.</summary>
    public IReadOnlyDictionary<string, string?> Effective()
    {
        lock (_gate)
        {
            var document = _store.ReadDocument();
            return EffectiveCore(document is null ? ConfigValues.Empty : _store.Load(document), document?.ManagedKeys ?? []).Values;
        }
    }

    public IReadOnlyDictionary<string, string?> SavedSettings()
    {
        lock (_gate)
            return _store.Load().Settings;
    }

    public IReadOnlyList<string> ExtensionRepositories()
    {
        lock (_gate)
            return Repositories(_store.Load());
    }

    public IReadOnlyCollection<string> ManagedKeys()
    {
        lock (_gate)
            return _store.ReadDocument()?.ManagedKeys ?? [];
    }

    /// <summary>
    /// Saves the given settings. A JSON null resets a setting to its default; an empty string removes a
    /// saved secret. With a transaction id a repeated request returns the first result instead of saving twice.
    /// </summary>
    public SettingsSaveResult Save(IReadOnlyDictionary<string, JsonElement> changes, string revision, string? transaction = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (transaction is not null && !Guid.TryParse(transaction, out _))
            throw new SettingsValidationException("Invalid setup request identifier.");
        lock (_gate)
        {
            var document = _store.ReadDocument() ?? new ConfigDocument();
            if (transaction is not null && document.Setup?.Transaction == transaction)
                return new(RevisionCore(), [], document.Setup.Changed, document.Setup.RestartRequired,
                    document.Setup.Changed.Where(key => Overridden(key, document.ManagedKeys)).ToArray(), transaction);
            if (!string.Equals(revision, RevisionCore(), StringComparison.Ordinal))
                throw new SettingsConflictException("Settings changed elsewhere. Reload before saving; your changes were not applied.");
            if (changes.Count > SettingsSchema.Entries.Count + 1)
                throw new SettingsValidationException("Too many settings in one request.");

            var before = _store.Load(document);
            var (current, _) = EffectiveCore(before, document.ManagedKeys);
            var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
            string? repositories = null;
            foreach (var (key, raw) in changes)
            {
                if (key == ExtensionStores)
                {
                    repositories = NormalizeRepositories(raw);
                    continue;
                }
                if (!SettingsSchema.ByKey.TryGetValue(key, out var entry))
                    throw new SettingsValidationException("Unknown setting: " + key + ".");
                if (!entry.Editable)
                    throw new SettingsValidationException(key + " can only be set in Compose: it names a container folder or identity, and a wrong value would stop AutoSourcarr from starting.");
                var value = Normalize(entry, raw);
                if (entry.Secret && value == "")
                    value = null; // removing a secret = no saved value
                var stored = before.Settings.ContainsKey(key);
                if (value is null ? stored : !string.Equals(value, current.GetValueOrDefault(key), StringComparison.Ordinal))
                    updates[key] = value;
            }

            var merged = new Dictionary<string, string?>(current, StringComparer.Ordinal);
            foreach (var (key, value) in updates)
                merged[key] = value ?? (SettingsSchema.ByKey[key].Secret ? "" : SettingsSchema.ByKey[key].Default);
            CheckCombination(merged, updates, changes, current);

            var saved = before.Settings.Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
            foreach (var (key, value) in updates)
            {
                if (value is null) saved.Remove(key);
                else saved[key] = value;
            }
            try { SettingsLoader.Load(configuration: saved.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal)); }
            catch (ConfigurationException error) { throw new SettingsValidationException(error.Message); }

            var sourceHost = before.SourceHost.Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
            var repositoriesChanged = repositories is not null
                && !string.Equals(repositories, JsonSerializer.Serialize(Repositories(before).ToList(), SettingsEditorJson.Default.ListString), StringComparison.Ordinal);
            // Until the extension host moves in-process (phase 4) the source engine and Sources > Repositories
            // read source-host.env, so the wizard writes there too; config.json holds the list only without that file.
            var legacyRepositories = repositoriesChanged && File.Exists(Path.Combine(_store.Root, CanonicalSourceHost));
            if (repositoriesChanged && !legacyRepositories)
                sourceHost[ExtensionStores] = repositories!;
            var extra = before.Extra.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);

            var changedKeys = updates.Keys.Concat(repositoriesChanged ? [ExtensionStores] : []).Order(StringComparer.Ordinal).ToArray();
            var restart = changedKeys.Length > 0;
            if (updates.Count > 0 || repositoriesChanged || transaction is not null)
            {
                var secrets = SettingsSecrets.Open(_store.Root, create: true);
                document.Settings = ConfigStore.Encrypt(saved, secrets);
                document.SourceHost = ConfigStore.Encrypt(sourceHost, secrets);
                document.Extra = ConfigStore.Encrypt(extra, secrets);
                var managed = new HashSet<string>(document.ManagedKeys, StringComparer.Ordinal);
                foreach (var (key, value) in updates)
                {
                    if (value is null) managed.Remove(key);
                    else managed.Add(key);
                }
                document.ManagedKeys = managed.Order(StringComparer.Ordinal).ToList();
                if (transaction is not null)
                    document.Setup = new ConfigSetupRecord
                    {
                        Completed = true,
                        CompletedAt = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                        Transaction = transaction,
                        Changed = changedKeys.ToList(),
                        RestartRequired = restart
                    };
                if (legacyRepositories)
                    new CanonicalConfiguration(_store.Root).SetExtensionStores(repositories!);
                _store.WriteDocument(document);
                _registerSecrets?.Invoke(updates.Where(pair => pair.Value is { Length: > 0 } && SettingsSecrets.IsSecretKey(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            }
            var after = _store.Load();
            var list = ConfigChanges.Compare(before, after);
            return new(RevisionCore(), list, changedKeys, restart, changedKeys.Where(key => Overridden(key, document.ManagedKeys)).ToArray(), transaction);
        }
    }

    /// <summary>
    /// Records where the databases now are, after DatabaseRelocation copied and checked them. DATABASE_DIR is
    /// Compose-only for ordinary edits (a bare value change would start with empty databases); this is the one
    /// writer allowed to set it, and it saves it as a value set in the app so a stale Compose value cannot win.
    /// </summary>
    public void SaveDatabaseDirectory(string directory)
    {
        lock (_gate)
        {
            var document = _store.ReadDocument() ?? new ConfigDocument();
            var before = _store.Load(document);
            var saved = before.Settings.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
            saved["DATABASE_DIR"] = directory;
            var secrets = SettingsSecrets.Open(_store.Root, create: true);
            document.Settings = ConfigStore.Encrypt(saved, secrets);
            document.SourceHost = ConfigStore.Encrypt(before.SourceHost.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal), secrets);
            document.Extra = ConfigStore.Encrypt(before.Extra.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal), secrets);
            if (!document.ManagedKeys.Contains("DATABASE_DIR"))
                document.ManagedKeys.Add("DATABASE_DIR");
            _store.WriteDocument(document);
        }
    }

    private string RevisionCore()
    {
        var raw = ConfigFiles.ReadBounded(_store.FilePath) ?? [];
        return Convert.ToHexStringLower(SHA256.HashData(raw));
    }

    /// <summary>What the next start will use: a value saved in the app, else a Compose variable, else config.json, else the running value.</summary>
    private (Dictionary<string, string?> Values, IReadOnlyList<string> Repositories) EffectiveCore(ConfigValues saved, IReadOnlyCollection<string> managed)
    {
        var running = SettingsLoader.ToEnvironment(_running(), includeSecrets: true);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in SettingsSchema.Entries)
        {
            var stored = saved.Settings.TryGetValue(entry.Key, out var value) ? value : null;
            values[entry.Key] = stored is not null && (managed.Contains(entry.Key) || !Overridden(entry.Key, managed))
                ? stored
                : running.TryGetValue(entry.Key, out var live) ? live : entry.Secret ? "" : entry.Default;
        }
        return (values, Repositories(saved));
    }

    private const string CanonicalSourceHost = "source-host.env";

    private IReadOnlyList<string> Repositories(ConfigValues saved)
    {
        string? raw;
        if (File.Exists(Path.Combine(_store.Root, CanonicalSourceHost)))
            new CanonicalConfiguration(_store.Root).EnvironmentFile(CanonicalSourceHost).TryGetValue(ExtensionStores, out raw);
        else
            saved.SourceHost.TryGetValue(ExtensionStores, out raw);
        if (string.IsNullOrWhiteSpace(raw))
            return [SourceRepositoryStore.KeiyoushiUrl];
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
                return document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
        }
        catch (JsonException) { }
        throw new SettingsValidationException("The saved extension repository list is invalid. Correct it in Sources > Repositories.");
    }

    private static string NormalizeRepositories(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > 20)
            throw new SettingsValidationException("Use at most 20 repository URLs.");
        var urls = new List<string>();
        foreach (var item in raw.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new SettingsValidationException("Extension repositories: use one URL per line.");
            string url;
            try { url = SourceRepositoryStore.NormalizeUrl(item.GetString()); }
            catch (ArgumentException error) { throw new SettingsValidationException("Extension repositories: " + error.Message + "."); }
            if (!urls.Contains(url, StringComparer.Ordinal))
                urls.Add(url);
        }
        return JsonSerializer.Serialize(urls, SettingsEditorJson.Default.ListString);
    }

    /// <summary>Same checks as the 0.4.x wizard (setup_store.normalize), extended to every setting by its type.</summary>
    public static string? Normalize(SettingSchemaEntry field, JsonElement value)
    {
        var key = field.Key;
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        switch (field.Type)
        {
            case "boolean":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return value.GetBoolean() ? "true" : "false";
                if (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().ToLowerInvariant() is var flag
                    && flag is "true" or "false" or "1" or "0" or "yes" or "no" or "on" or "off")
                    return flag is "true" or "1" or "yes" or "on" ? "true" : "false";
                throw new SettingsValidationException(field.Label + ": choose on or off.");
            case "integer":
            case "number":
            {
                double number;
                if (value.ValueKind == JsonValueKind.Number)
                    number = value.GetDouble();
                else if (value.ValueKind != JsonValueKind.String
                    || !double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new SettingsValidationException(field.Label + ": enter a number.");
                if (!double.IsFinite(number) || (field.Type == "integer" && number != Math.Floor(number)))
                    throw new SettingsValidationException(field.Label + ": enter a " + (field.Type == "integer" ? "whole " : "") + "number.");
                if ((field.Minimum is { } minimum && number < minimum) || (field.Maximum is { } maximum && number > maximum))
                    throw new SettingsValidationException(FormattableString.Invariant($"{field.Label}: use {field.Minimum}–{field.Maximum}."));
                return field.Type == "integer"
                    ? ((long)number).ToString(CultureInfo.InvariantCulture)
                    : number.ToString("R", CultureInfo.InvariantCulture);
            }
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new SettingsValidationException(field.Label + ": invalid value.");
        var text = value.GetString()!;
        if (text.Length > 4096 || text.Any(c => c < ' '))
            throw new SettingsValidationException(field.Label + ": invalid value.");
        if (key != "MAIN_SUWAYOMI_PASSWORD")
            text = text.Trim();
        if (text.Length == 0 && (field.AllowEmpty || field.Secret || field.Type is "translation"))
            return "";
        if (field.Secret)
        {
            if (key == "AUTOSOURCARR_READER_API_TOKEN" && !ReaderToken().IsMatch(text))
                throw new SettingsValidationException("Reader API key must contain 32–256 letters, digits, dots, tildes, dashes or underscores.");
            if (key == "REPAIR_AI_API_KEY" && text.Any(c => c > 126))
                throw new SettingsValidationException("AI API key must contain printable ASCII characters.");
            return text;
        }
        switch (field.Type)
        {
            case "choice":
                if (!field.Choices.Any(choice => choice.Value == text))
                    throw new SettingsValidationException(field.Label + ": choose one of the listed options.");
                return text;
            case "url":
            case "translation":
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0
                    || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || text.Any(char.IsWhiteSpace)
                    || text.Contains('@', StringComparison.Ordinal))
                    throw new SettingsValidationException(field.Label + ": use a complete HTTP(S) address without credentials, query or fragment.");
                if (key == "REPAIR_AI_BASE_URL" && Uri.UnescapeDataString(url.AbsolutePath).Split('/').Any(part => part is "." or ".."))
                    throw new SettingsValidationException("AI base address cannot contain path traversal.");
                return text.TrimEnd('/');
            }
            case "path":
                if (field.WizardStep is not null && !text.StartsWith('/'))
                    throw new SettingsValidationException(field.Label + ": use an absolute container path.");
                return text;
            case "languages":
            case "translation_languages":
            case "providers":
            {
                var parts = text.Split(',').Select(part => part.Trim().ToLowerInvariant()).Where(part => part.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                HashSet<string>? allowed = field.Type switch
                {
                    "translation_languages" => ["ja", "ko", "zh"],
                    "providers" => ["mangadex", "anilist", "mangaupdates", "kitsu", "myanimelist", "comicvine"],
                    _ => null
                };
                if (parts.Length == 0 || parts.Any(part => !Identifier().IsMatch(part) || (allowed is not null && !allowed.Contains(part))))
                    throw new SettingsValidationException(field.Label + ": use valid comma-separated identifiers.");
                return string.Join(',', parts);
            }
        }
        switch (key)
        {
            case "TZ":
                try { _ = TimeZoneInfo.FindSystemTimeZoneById(text); }
                catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
                { throw new SettingsValidationException("Time zone must be an IANA name such as Europe/Amsterdam."); }
                break;
            case "AUTHELIA_USER_HEADER":
                if (!HeaderName().IsMatch(text))
                    throw new SettingsValidationException("Identity header must contain only letters, numbers and hyphens.");
                break;
            case "MAIN_SUWAYOMI_USERNAME":
                if (text.Contains(':', StringComparison.Ordinal))
                    throw new SettingsValidationException("HTTP Basic usernames cannot contain a colon.");
                break;
            case "REPAIR_AI_MODEL":
                if (text.Length > 256)
                    throw new SettingsValidationException("AI model identifier must be at most 256 characters.");
                break;
            case "REPAIR_AI_WORKSPACE_ID":
                if (text.Length > 128 || !WorkspaceId().IsMatch(text))
                    throw new SettingsValidationException("Claude workspace ID must contain at most 128 ASCII letters, numbers, underscores or hyphens.");
                break;
        }
        if (field.AllowedValues.Count > 0 && !field.AllowedValues.Contains(text, StringComparer.Ordinal))
            throw new SettingsValidationException(field.Label + ": choose one of " + string.Join(", ", field.AllowedValues) + ".");
        return text;
    }

    /// <summary>Rules that involve more than one setting (setup_store.finish in 0.4.x).</summary>
    private void CheckCombination(IReadOnlyDictionary<string, string?> merged, IReadOnlyDictionary<string, string?> updates,
        IReadOnlyDictionary<string, JsonElement> changes, IReadOnlyDictionary<string, string?> current)
    {
        string Value(string key) => merged.GetValueOrDefault(key) ?? "";
        var provider = Value("REPAIR_AI_PROVIDER");
        if (provider.Length > 0 && provider != "chatgpt")
        {
            if (_running().AppServicesMode != "integrated")
                throw new SettingsValidationException("Custom repair AI requires the integrated AutoSourcarr image. External workers retain their own account configuration.");
            if (Value("REPAIR_AI_BASE_URL").Length == 0 || Value("REPAIR_AI_MODEL").Length == 0)
                throw new SettingsValidationException("Custom repair AI needs an API base URL and model name.");
        }
        if (provider is "openai" or "claude" or "gemini" or "openrouter" && Value("REPAIR_AI_API_KEY").Length == 0)
            throw new SettingsValidationException("This AI provider needs its API key. A consumer subscription is separate from API access.");
        if (updates.ContainsKey("REPAIR_AI_PROVIDER") && (current.GetValueOrDefault("REPAIR_AI_API_KEY") ?? "").Length > 0 && !changes.ContainsKey("REPAIR_AI_API_KEY"))
            throw new SettingsValidationException("AI provider changed: explicitly replace or remove its saved credential.");
        if (Value("TRANSLATION_MODE") == "external" && Value("TRANSLATION_API_URL").Length == 0)
            throw new SettingsValidationException("External translation needs a LibreTranslate API address.");
        foreach (var (address, secret) in new[] { ("REPAIR_AI_BASE_URL", "REPAIR_AI_API_KEY"), ("TRANSLATION_API_URL", "TRANSLATION_API_KEY"), ("MAIN_SUWAYOMI_URL", "MAIN_SUWAYOMI_PASSWORD") })
            if (updates.ContainsKey(address) && (current.GetValueOrDefault(secret) ?? "").Length > 0 && !changes.ContainsKey(secret))
                throw new SettingsValidationException(SettingsSchema.ByKey[address].Label + " changed: explicitly replace or remove its saved credential before sending it to another address.");
        if ((updates.ContainsKey("BACKGROUND_SOURCE_CONCURRENCY") || updates.ContainsKey("SOURCE_HOST_WORK_CONCURRENCY"))
            && Number(Value("BACKGROUND_SOURCE_CONCURRENCY")) >= Number(Value("SOURCE_HOST_WORK_CONCURRENCY")))
            throw new SettingsValidationException("Background concurrency must be below source engine capacity to leave room for interactive work.");
        if ((updates.ContainsKey("AUTO_MATCH_THRESHOLD") || updates.ContainsKey("FUZZY_REVIEW_THRESHOLD"))
            && Number(Value("FUZZY_REVIEW_THRESHOLD")) > Number(Value("AUTO_MATCH_THRESHOLD")))
            throw new SettingsValidationException("Candidate threshold must not exceed the automatic threshold.");
    }

    private static double Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;

    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{32,256}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReaderToken();
    [GeneratedRegex(@"\A[a-z][a-z0-9-]{1,24}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"\A[A-Za-z0-9-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderName();
    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceId();
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal sealed partial class SettingsEditorJson : System.Text.Json.Serialization.JsonSerializerContext;
