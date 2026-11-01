using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Connections;

/// <summary>No connection with this id (or it comes from Settings and cannot be changed here).</summary>
public sealed class ConnectionNotFoundException(string message) : Exception(message);

/// <summary>The last "Test" of a connection: when, whether it passed and its short message.</summary>
public sealed record ConnectionTestRecord(DateTimeOffset At, bool Ok, string Message);

/// <summary>
/// An older setting group that is really a connection. The Connections menu shows it read-only as
/// <c>settings-&lt;type&gt;</c> (it is still edited on the Settings page) so every external service is in one place.
/// <see cref="Fields"/> maps the type's field key to the setting key.
/// </summary>
public sealed record SettingsConnectionSource(string Type, string Label, IReadOnlyDictionary<string, string> Fields);

/// <summary>One connection as the menu shows it: secret fields are <c>{"set": bool}</c> markers, never values.</summary>
public sealed record ConnectionView(string Id, string Type, string Label, bool Enabled, string Source, bool Editable, bool TypeKnown,
    JsonObject Values, ConnectionTestRecord? LastTest);

public sealed record ConnectionListing(string Revision, IReadOnlyList<ConnectionView> Connections);

/// <summary>
/// A saved change. <paramref name="FirstSaved"/>: the first connection written to config.json (older builds refuse that
/// key). <paramref name="Effective"/>: the enabled switch or a value changed (a label-only change is not).
/// </summary>
public sealed record ConnectionChange(string Revision, ConnectionView? Connection, bool FirstSaved = false, bool Effective = true);

/// <summary>A connection was added, changed (switch or values) or deleted.</summary>
public sealed record ConnectionChangedEvent(string Type, string Id, string Kind);

/// <summary>
/// Connections in config.json under the top-level <c>connections</c> array (CON-1, CON-5). Secret fields are
/// encrypted with the instance's config.key (associated data <c>connections.&lt;id&gt;.&lt;field&gt;</c>), never
/// listed (a <c>{"set": true}</c> marker instead), kept when an update sends the marker or an empty text, and
/// registered with the support redaction. Every change names the revision it was based on (optimistic concurrency
/// like PUT /api/v1/settings) and is one atomic replace of config.json under the same lock as the settings editor.
/// Tests run with a deadline and a fresh handler per test; the last result per connection is kept in memory and in
/// a small JSON file in the data folder.
/// </summary>
public sealed class ConnectionStore
{
    public const string SettingsIdPrefix = "settings-";
    public const string LastTestsFileName = "connection-tests.json";
    public const int MaxConnections = 200;
    public const string MainSuwayomiType = "suwayomi";
    /// <summary>CON-5 update notices: what happens after a change of a connection of that type.</summary>
    public const string SolverChangeNotice = "The source host restarts to use the new solver list.";
    public const string SuwayomiChangeNotice = "The Suwayomi change feed reconnects.";
    public const string RollbackNotice = "Connections are saved in config.json under \"connections\"; before going back to AutoSourcarr 0.4.x, remove that key, because 0.4.x refuses to start with it.";
    private const int MaxValueLength = 4096;
    private const int MaxLabelLength = 100;
    private const int MaxMessageLength = 500;

    /// <summary>Settings that describe a connection today (mapped to field keys "url", "username", "password", "api_key").</summary>
    public static IReadOnlyList<SettingsConnectionSource> SettingsSources { get; } =
    [
        new("suwayomi", "Main Suwayomi library (Settings)", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = "MAIN_SUWAYOMI_URL", ["username"] = "MAIN_SUWAYOMI_USERNAME", ["password"] = "MAIN_SUWAYOMI_PASSWORD"
        }),
        new("flaresolverr", "FlareSolverr (source engine settings)", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = "FLARESOLVERR_URL"
        }),
        new("komf", "Komf (Settings)", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = "KOMF_BROWSER_URL"
        }),
        new("libretranslate", "LibreTranslate (Settings)", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = "TRANSLATION_API_URL", ["api_key"] = "TRANSLATION_API_KEY"
        }),
    ];

    private readonly ConfigStore _config;
    private readonly object _gate;
    private readonly string? _lastTestsPath;
    private readonly Action<IReadOnlyDictionary<string, string?>>? _registerSecrets;
    private readonly Func<IReadOnlyDictionary<string, string?>>? _settingValues;
    private readonly Func<HttpMessageHandler> _handlers;
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _clock;
    private readonly object _testsGate = new();
    private readonly Action<string>? _warn;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private Dictionary<string, ConnectionTestRecord>? _lastTests;

    /// <param name="configurationRoot">The folder of config.json and config.key (SETTINGS_CONFIG_DIR).</param>
    /// <param name="types">Registered connection types.</param>
    /// <param name="dataDirectory">Where the last test results are kept; null keeps them in memory only.</param>
    /// <param name="registerSecrets">Adds secret values to the support redaction (SupportPrivacy.Register).</param>
    /// <param name="settingValues">Effective setting values (with secrets) for the read-only "from settings" view;
    /// null reads config.json's settings and source_host sections.</param>
    /// <param name="handlerFactory">A fresh handler per test (disposed after it); tests pass a stub.</param>
    /// <param name="testDeadline">Bound of one test (default 15 s).</param>
    /// <param name="warn">Reports a saved connection that cannot be read (once per connection and message).</param>
    public ConnectionStore(string configurationRoot, ConnectionTypes types, string? dataDirectory = null,
        Action<IReadOnlyDictionary<string, string?>>? registerSecrets = null,
        Func<IReadOnlyDictionary<string, string?>>? settingValues = null,
        Func<HttpMessageHandler>? handlerFactory = null, TimeSpan? testDeadline = null, TimeProvider? clock = null,
        Action<string>? warn = null)
    {
        _warn = warn;
        ArgumentNullException.ThrowIfNull(configurationRoot);
        Types = types ?? throw new ArgumentNullException(nameof(types));
        _config = new ConfigStore(configurationRoot);
        _gate = SettingsEditor.Gate(_config.Root);
        _lastTestsPath = string.IsNullOrEmpty(dataDirectory) ? null : Path.Combine(Path.GetFullPath(dataDirectory), LastTestsFileName);
        _registerSecrets = registerSecrets;
        _settingValues = settingValues;
        _handlers = handlerFactory ?? (() => DefaultHandler());
        _deadline = testDeadline ?? TimeSpan.FromSeconds(15);
        _clock = clock ?? TimeProvider.System;
        // Saved secrets are redacted from the first log line on, not only after their next change.
        try
        {
            lock (_gate)
            {
                var document = _config.ReadDocument();
                if (document?.Connections is { Count: > 0 } saved)
                {
                    var secrets = OpenSecrets(document!, create: false);
                    Register(saved.Select(connection => Decrypt(connection, secrets)).ToArray());
                }
            }
        }
        catch (ConfigurationException) { /* List and Test report the damage; startup does not fail on it */ }
    }

    public ConnectionTypes Types { get; }
    public TimeSpan TestDeadline => _deadline;

    /// <summary>
    /// Raised after a connection was added, changed (enabled switch or values; not a label-only change) or deleted, outside
    /// the store's lock. Handlers must be quick; an exception in one is reported through the warning callback.
    /// </summary>
    public event Action<ConnectionChangedEvent>? Changed;

    /// <summary>
    /// A fresh handler for one test: no cookies, bounded connect. Redirects are not followed: a test sends keys and
    /// passwords (Auth-Key, Bearer, Basic) that must never be replayed to another host.
    /// </summary>
    public static HttpMessageHandler DefaultHandler() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
    };

    /// <summary>Opaque version of the saved connections; a change must name the revision it was based on.</summary>
    public string Revision()
    {
        lock (_gate)
            return RevisionOf(_config.ReadDocument());
    }

    /// <summary>Saved connections plus the read-only ones derived from Settings, with secrets as markers.</summary>
    public ConnectionListing List()
    {
        lock (_gate)
        {
            var document = _config.ReadDocument();
            var items = new List<ConnectionView>();
            if (document?.Connections is { } saved)
            {
                var secrets = OpenSecrets(document!, create: false);
                foreach (var connection in saved)
                    items.Add(View(Decrypt(connection, secrets), "config"));
            }
            items.AddRange(FromSettings(document).Select(connection => View(connection, "settings")));
            return new(RevisionOf(document), items);
        }
    }

    /// <summary>One connection with decrypted values (saved or from Settings); null when unknown.</summary>
    public ConnectionConfig? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            var document = _config.ReadDocument();
            if (id.StartsWith(SettingsIdPrefix, StringComparison.Ordinal))
                return FromSettings(document).FirstOrDefault(connection => connection.Id == id);
            var saved = document?.Connections?.FirstOrDefault(connection => connection.Id == id);
            return saved is null ? null : Decrypt(saved, OpenSecrets(document!, create: false));
        }
    }

    /// <summary>
    /// Enabled connections of one type (saved first, then the one from Settings), for the services that use them. Never
    /// throws for damage: a saved connection whose secrets cannot be decrypted (other config.key, edited ciphertext) or
    /// an unreadable config.json is skipped and reported once through the warning callback.
    /// </summary>
    public IReadOnlyList<ConnectionConfig> Enabled(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (_gate)
        {
            ConfigDocument? document;
            try { document = _config.ReadDocument(); }
            catch (ConfigurationException error) { Warn("config", "config.json could not be read, so saved connections are not used: " + error.Message); document = null; }
            var result = new List<ConnectionConfig>();
            if (document?.Connections is { } saved)
            {
                SettingsSecrets? secrets = null;
                ConfigurationException? keyError = null;
                try { secrets = OpenSecrets(document, create: false); }
                catch (ConfigurationException error) { keyError = error; }
                void Skip(ConfigConnection connection, string reason) => Warn(connection.Id, "The " + type + " connection \"" + connection.Label
                    + "\" (" + connection.Id + ") is skipped: its saved secrets cannot be read (" + reason + "). Enter them again in Connections.");
                foreach (var connection in saved.Where(connection => connection.Enabled && connection.Type == type))
                {
                    if (keyError is not null && connection.Secrets.Count > 0) { Skip(connection, keyError.Message); continue; }
                    try { result.Add(Decrypt(connection, secrets)); }
                    catch (ConfigurationException error) { Skip(connection, error.Message); }
                }
            }
            result.AddRange(FromSettings(document).Where(connection => connection.Type == type && connection.Enabled));
            return result;
        }
    }

    /// <summary>
    /// The main Suwayomi every feature uses (library, change feed, import): the one from Settings (MAIN_SUWAYOMI_URL) when
    /// set, else the enabled saved connection. Null when there is none or it cannot be read.
    /// </summary>
    public ConnectionConfig? MainSuwayomi()
    {
        var all = Enabled(MainSuwayomiType);
        return all.FirstOrDefault(connection => connection.Id.StartsWith(SettingsIdPrefix, StringComparison.Ordinal)) ?? all.FirstOrDefault();
    }

    /// <summary>The type of a saved or Settings-derived connection without decrypting it; null when unknown.</summary>
    public string? TypeOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.StartsWith(SettingsIdPrefix, StringComparison.Ordinal))
            return SettingsSources.FirstOrDefault(source => SettingsIdPrefix + source.Type == id)?.Type;
        lock (_gate)
            return _config.ReadDocument()?.Connections?.FirstOrDefault(connection => connection.Id == id)?.Type;
    }

    /// <summary>The CON-5 notice for a change of a connection of <paramref name="type"/>, or null when nothing restarts.</summary>
    public static string? ChangeNotice(string? type) =>
        global::AutoSourcarr.Core.Connections.Types.SolverConnectionType.IsSolver(type) ? SolverChangeNotice : type == MainSuwayomiType ? SuwayomiChangeNotice : null;

    private void Warn(string key, string message)
    {
        lock (_warned)
            if (!_warned.Add(key + "|" + message)) return;
        try { _warn?.Invoke(message); }
        catch (Exception error) when (error is not OutOfMemoryException) { _ = error; }
    }

    private void Raise(string type, string id, string kind)
    {
        try { Changed?.Invoke(new ConnectionChangedEvent(type, id, kind)); }
        catch (Exception error) when (error is not OutOfMemoryException) { Warn("changed|" + id, "Applying the connection change failed: " + error.Message); }
    }

    /// <summary>Adds a connection. Values are checked per field kind; a single-connection type refuses a second one.</summary>
    public ConnectionChange Add(string revision, string type, string? label, bool enabled, JsonObject? values)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var definition = Types.Find(type) ?? throw new SettingsValidationException("Unknown connection type: " + type + ".");
        var name = Label(label, definition.Label);
        ConnectionChange change;
        lock (_gate)
        {
            var document = _config.ReadDocument() ?? new ConfigDocument();
            CheckRevision(document, revision);
            var list = document.Connections ?? [];
            if (list.Count >= MaxConnections)
                throw new SettingsValidationException($"At most {MaxConnections} connections can be saved.");
            if (!definition.Multiple && list.Any(connection => connection.Type == definition.Type))
                throw new SettingsValidationException(definition.Label + " allows one connection; change or delete the existing one.");
            // One main Suwayomi: while Settings names one (MAIN_SUWAYOMI_URL), that is the one the library uses.
            if (definition.Type == MainSuwayomiType && FromSettings(document).Any(connection => connection.Type == MainSuwayomiType))
                throw new SettingsConflictException("The main Suwayomi is set on the Settings page (MAIN_SUWAYOMI_URL). Change it there, or clear it there before adding a Suwayomi connection here.");
            var first = list.Count == 0;
            var merged = Merge(definition, new Dictionary<string, string>(StringComparer.Ordinal), values, update: false);
            string id;
            do id = RandomNumberGenerator.GetHexString(12, lowercase: true);
            while (list.Any(connection => connection.Id == id));
            var plain = new ConnectionConfig(id, definition.Type, name, enabled, merged);
            var secrets = SettingsSecrets.Open(_config.Root, create: true);
            list.Add(Encrypt(plain, definition, secrets));
            document.Connections = list;
            _config.WriteDocument(document);
            Register([plain]);
            change = new(RevisionOf(_config.ReadDocument()), View(plain, "config"), FirstSaved: first);
        }
        Raise(definition.Type, change.Connection!.Id, "added");
        return change;
    }

    /// <summary>
    /// Changes label, enabled switch and/or values. Fields left out keep their value; a secret sent as the
    /// <c>{"set": true}</c> marker or as empty text is kept; JSON null clears a field.
    /// </summary>
    public ConnectionChange Update(string id, string revision, string? label, bool? enabled, JsonObject? values)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(revision);
        if (id.StartsWith(SettingsIdPrefix, StringComparison.Ordinal))
            throw new SettingsConflictException("This connection comes from Settings; change it on the Settings page.");
        ConnectionChange change;
        lock (_gate)
        {
            var document = _config.ReadDocument() ?? new ConfigDocument();
            var list = document.Connections ?? [];
            var index = list.FindIndex(connection => connection.Id == id);
            if (index < 0)
                throw new ConnectionNotFoundException("No connection " + id + ".");
            CheckRevision(document, revision);
            var secrets = OpenSecrets(document, create: true)!;
            var current = Decrypt(list[index], secrets);
            var definition = Types.Find(current.Type);
            if (definition is null && values is { Count: > 0 })
                throw new SettingsValidationException("The connection type " + current.Type + " is not available in this version; only the label, enabled switch and delete work.");
            var merged = definition is null ? current.Values.ToDictionary(StringComparer.Ordinal) : Merge(definition, current.Values, values, update: true);
            var plain = new ConnectionConfig(id, current.Type, label is null ? current.Label : Label(label, definition?.Label ?? current.Type),
                enabled ?? current.Enabled, merged);
            list[index] = definition is null
                ? new ConfigConnection { Id = id, Type = current.Type, Label = plain.Label, Enabled = plain.Enabled, Values = list[index].Values, Secrets = list[index].Secrets }
                : Encrypt(plain, definition, secrets);
            document.Connections = list;
            _config.WriteDocument(document);
            Register([plain]);
            var effective = plain.Enabled != current.Enabled || plain.Values.Count != current.Values.Count
                || plain.Values.Any(pair => !current.Values.TryGetValue(pair.Key, out var before) || before != pair.Value);
            change = new(RevisionOf(_config.ReadDocument()), View(plain, "config"), Effective: effective);
        }
        if (change.Effective) Raise(change.Connection!.Type, id, "changed");
        return change;
    }

    public string Delete(string id, string revision)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(revision);
        if (id.StartsWith(SettingsIdPrefix, StringComparison.Ordinal))
            throw new SettingsConflictException("This connection comes from Settings; clear it on the Settings page.");
        string next, type;
        lock (_gate)
        {
            var document = _config.ReadDocument() ?? new ConfigDocument();
            var list = document.Connections ?? [];
            var index = list.FindIndex(connection => connection.Id == id);
            if (index < 0)
                throw new ConnectionNotFoundException("No connection " + id + ".");
            CheckRevision(document, revision);
            type = list[index].Type;
            list.RemoveAt(index);
            document.Connections = list.Count == 0 ? null : list;
            _config.WriteDocument(document);
            next = RevisionOf(_config.ReadDocument());
        }
        ForgetTest(id);
        Raise(type, id, "deleted");
        return next;
    }

    /// <summary>Tests a saved (or Settings-derived) connection and records the result.</summary>
    public async Task<ConnectionTestResult> TestAsync(string id, CancellationToken cancellationToken)
    {
        var connection = Find(id) ?? throw new ConnectionNotFoundException("No connection " + id + ".");
        var type = Types.Find(connection.Type);
        var result = type is null
            ? ConnectionTestResult.Fail("The connection type " + connection.Type + " is not available in this version.")
            : await RunAsync(type, connection, cancellationToken).ConfigureAwait(false);
        RecordTest(id, result);
        return result;
    }

    /// <summary>
    /// Tests values that are not saved (the Add/Edit form). With <paramref name="basedOn"/> the values are merged
    /// onto that connection like an update, so a kept secret (marker or empty) is tested with its saved value.
    /// Nothing is written and no result is recorded.
    /// </summary>
    public async Task<ConnectionTestResult> TestValuesAsync(string type, JsonObject? values, string? basedOn, CancellationToken cancellationToken)
    {
        var definition = Types.Find(type) ?? throw new SettingsValidationException("Unknown connection type: " + type + ".");
        ConnectionConfig? current = null;
        if (!string.IsNullOrEmpty(basedOn))
        {
            current = Find(basedOn) ?? throw new ConnectionNotFoundException("No connection " + basedOn + ".");
            if (current.Type != definition.Type)
                throw new SettingsValidationException("The connection " + basedOn + " is of another type.");
        }
        var merged = Merge(definition, current?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), values, update: current is not null);
        // SupportPrivacy.Register adds every value to one set (keys only select which values count as secret), so
        // registering each unsaved test under the same key never drops an earlier one.
        Register([new ConnectionConfig("unsaved", definition.Type, definition.Label, true, merged)]);
        return await RunAsync(definition, new ConnectionConfig(current?.Id ?? "unsaved", definition.Type, current?.Label ?? definition.Label, true, merged),
            cancellationToken).ConfigureAwait(false);
    }

    public ConnectionTestRecord? LastTest(string id)
    {
        lock (_testsGate)
            return LoadTests().GetValueOrDefault(id);
    }

    private async Task<ConnectionTestResult> RunAsync(IConnectionType type, ConnectionConfig connection, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        var handler = _handlers();
        try
        {
            // WaitAsync bounds even a type that ignores its token; the handler is disposed below either way.
            var result = await type.TestAsync(connection, handler, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            return result is null ? ConnectionTestResult.Fail("The test gave no result.") : result with { Message = Short(result.Message) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConnectionTestResult.Fail(string.Create(CultureInfo.InvariantCulture, $"No answer within {_deadline.TotalSeconds:0.#} s."));
        }
        catch (HttpRequestException error)
        {
            return ConnectionTestResult.Fail(Short("Could not connect: " + error.Message));
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            return ConnectionTestResult.Fail(Short("The test failed: " + error.Message));
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>
    /// Applies a request's values onto <paramref name="current"/> and checks the result per field kind. When an address
    /// field (kind Url) now points at another scheme, host or port, every saved secret the request did not send again is
    /// dropped: a kept password or key is never sent to a server it was not entered for (updates and tests with an id).
    /// </summary>
    private static Dictionary<string, string> Merge(IConnectionType type, IReadOnlyDictionary<string, string> current, JsonObject? values, bool update)
    {
        var fields = type.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in current)
            if (fields.ContainsKey(key))
                result[key] = value;
        if (values is not null)
        {
            if (values.Count > fields.Count + 1)
                throw new SettingsValidationException("Too many values in one request.");
            foreach (var (key, node) in values)
            {
                if (!fields.TryGetValue(key, out var field))
                    throw new SettingsValidationException("Unknown setting " + key + " for " + type.Label + ".");
                if (node is null)
                {
                    result.Remove(key);
                    continue;
                }
                if (node is JsonObject marker)
                {
                    if (field.Kind != ConnectionFieldKind.Secret || marker.Count != 1 || !marker.ContainsKey("set"))
                        throw new SettingsValidationException(field.Label + ": invalid value.");
                    continue; // keep the saved secret (none on add)
                }
                if (node is not JsonValue scalar)
                    throw new SettingsValidationException(field.Label + ": invalid value.");
                var text = Text(field, scalar);
                if (text.Length == 0)
                {
                    if (field.Kind != ConnectionFieldKind.Secret)
                        result.Remove(key);
                    continue; // an empty secret keeps the saved one
                }
                result[key] = Normalize(field, text);
                if (field.Kind == ConnectionFieldKind.Secret) sent.Add(key);
            }
        }
        if (update && type.Fields.Any(field => field.Kind == ConnectionFieldKind.Url
                && Origin(current.GetValueOrDefault(field.Key)) != Origin(result.GetValueOrDefault(field.Key))))
            foreach (var field in type.Fields.Where(field => field.Kind == ConnectionFieldKind.Secret && !sent.Contains(field.Key)))
            {
                if (!result.Remove(field.Key)) continue;
                if (field.Required)
                    throw new SettingsValidationException(field.Label + ": the address changed, so enter it again; a saved secret is never sent to a different server.");
            }
        foreach (var field in type.Fields)
        {
            if (!result.ContainsKey(field.Key) && !update && !string.IsNullOrEmpty(field.Default))
                result[field.Key] = field.Default;
            if (field.Required && (!result.TryGetValue(field.Key, out var value) || value.Length == 0))
                throw new SettingsValidationException(field.Label + " is required.");
        }
        return result;
    }

    /// <summary>scheme://host:port of an address (the raw text when it is not one); "" when unset.</summary>
    private static string Origin(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Host.Length > 0
            ? url.Scheme.ToLowerInvariant() + "://" + url.IdnHost.ToLowerInvariant() + ":" + url.Port.ToString(CultureInfo.InvariantCulture)
            : value;
    }

    private static string Text(ConnectionField field, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                var text = value.GetValue<string>();
                if (text.Length > MaxValueLength || text.Any(char.IsControl))
                    throw new SettingsValidationException(field.Label + ": invalid value.");
                return field.Kind == ConnectionFieldKind.Secret ? text : text.Trim();
            case JsonValueKind.Number when field.Kind is ConnectionFieldKind.Integer or ConnectionFieldKind.Text:
                return value.ToJsonString();
            case JsonValueKind.True or JsonValueKind.False when field.Kind == ConnectionFieldKind.Boolean:
                return value.GetValue<bool>() ? "true" : "false";
            default:
                throw new SettingsValidationException(field.Label + ": invalid value.");
        }
    }

    /// <summary>Checks one non-empty value by its field kind and returns the stored text.</summary>
    public static string Normalize(ConnectionField field, string text)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(text);
        switch (field.Kind)
        {
            case ConnectionFieldKind.Url:
                if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.Host.Length == 0
                    || url.UserInfo.Length != 0 || url.Fragment.Length != 0 || text.Any(char.IsWhiteSpace))
                    throw new SettingsValidationException(field.Label + ": use a complete http:// or https:// address without credentials.");
                return url.Query.Length == 0 ? text.TrimEnd('/') : text;
            case ConnectionFieldKind.Integer:
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    throw new SettingsValidationException(field.Label + ": enter a whole number.");
                return number.ToString(CultureInfo.InvariantCulture);
            case ConnectionFieldKind.Boolean:
                return text.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on" => "true",
                    "false" or "0" or "no" or "off" => "false",
                    _ => throw new SettingsValidationException(field.Label + ": choose on or off.")
                };
            default:
                return text;
        }
    }

    private static string Label(string? label, string fallback)
    {
        var text = (label ?? "").Trim();
        if (text.Length == 0)
            return fallback;
        if (text.Length > MaxLabelLength || text.Any(char.IsControl))
            throw new SettingsValidationException($"Name: use at most {MaxLabelLength} characters on one line.");
        return text;
    }

    private static void CheckRevision(ConfigDocument document, string revision)
    {
        if (!string.Equals(revision, RevisionOf(document), StringComparison.Ordinal))
            throw new SettingsConflictException("Connections changed elsewhere. Reload before saving; your changes were not applied.");
    }

    private static string RevisionOf(ConfigDocument? document)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(document?.Connections ?? [], ConfigJsonContext.Default.ListConfigConnection);
        return Convert.ToHexStringLower(SHA256.HashData(raw))[..32];
    }

    private SettingsSecrets? OpenSecrets(ConfigDocument document, bool create) =>
        create || document.Connections?.Any(connection => connection.Secrets.Count > 0) == true
            ? SettingsSecrets.Open(_config.Root, create)
            : null;

    private static string SecretKey(string id, string field) => "connections." + id + "." + field;

    private static ConnectionConfig Decrypt(ConfigConnection connection, SettingsSecrets? secrets)
    {
        var values = new Dictionary<string, string>(connection.Values, StringComparer.Ordinal);
        foreach (var (key, value) in connection.Secrets)
        {
            if (values.ContainsKey(key))
                throw new ConfigurationException("Connection " + connection.Id + " has " + key + " twice in config.json.");
            values[key] = secrets!.Unprotect(SecretKey(connection.Id, key), value);
        }
        return new(connection.Id, connection.Type, connection.Label, connection.Enabled, values);
    }

    private static ConfigConnection Encrypt(ConnectionConfig plain, IConnectionType type, SettingsSecrets secrets)
    {
        var secret = type.Fields.Where(field => field.Kind == ConnectionFieldKind.Secret).Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        var stored = new ConfigConnection { Id = plain.Id, Type = plain.Type, Label = plain.Label, Enabled = plain.Enabled };
        foreach (var (key, value) in plain.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (secret.Contains(key))
                stored.Secrets[key] = secrets.Protect(SecretKey(plain.Id, key), value);
            else
                stored.Values[key] = value;
        }
        return stored;
    }

    private void Register(IEnumerable<ConnectionConfig> connections)
    {
        if (_registerSecrets is null)
            return;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            var type = Types.Find(connection.Type);
            foreach (var (key, value) in connection.Values)
                // Unknown types: everything that is not a plain value of a known field counts as secret.
                if (value.Length > 0 && (type is null || type.Fields.Any(field => field.Key == key && field.Kind == ConnectionFieldKind.Secret)))
                    values["connection_" + connection.Id + "_" + key + "_secret"] = value;
        }
        if (values.Count > 0)
            _registerSecrets(values);
    }

    private ConnectionView View(ConnectionConfig connection, string source)
    {
        var type = Types.Find(connection.Type);
        var values = new JsonObject();
        if (type is null)
        {
            foreach (var (key, value) in connection.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                values[key] = new JsonObject { ["set"] = value.Length > 0 }; // cannot tell secrets apart: show none
        }
        else
        {
            foreach (var field in type.Fields)
            {
                var value = connection.Values.GetValueOrDefault(field.Key);
                values[field.Key] = field.Kind == ConnectionFieldKind.Secret
                    ? new JsonObject { ["set"] = !string.IsNullOrEmpty(value) }
                    : value is null ? null : JsonValue.Create(value);
            }
        }
        return new(connection.Id, connection.Type, connection.Label, connection.Enabled, source, source == "config", type is not null, values, LastTest(connection.Id));
    }

    /// <summary>Read-only connections derived from older settings (shown when the type is registered and its address is set).</summary>
    private List<ConnectionConfig> FromSettings(ConfigDocument? document)
    {
        var result = new List<ConnectionConfig>();
        var sources = SettingsSources.Where(source => Types.Contains(source.Type)).ToArray();
        if (sources.Length == 0)
            return result;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var saved = document is null ? ConfigValues.Empty : _config.Load(document);
        if (File.Exists(Path.Combine(_config.Root, "source-host.env")))
            foreach (var (key, value) in new CanonicalConfiguration(_config.Root).EnvironmentFile("source-host.env"))
                values[key] = value;
        else
            foreach (var (key, value) in saved.SourceHost)
                values[key] = value;
        foreach (var (key, value) in saved.Settings)
            values[key] = value;
        if (_settingValues?.Invoke() is { } live)
            foreach (var (key, value) in live)
                if (value is not null)
                    values[key] = value;
        foreach (var source in sources)
        {
            var type = Types.Find(source.Type)!;
            var first = source.Fields.First();
            if (string.IsNullOrWhiteSpace(values.GetValueOrDefault(first.Value)))
                continue;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, setting) in source.Fields)
                if (type.Fields.Any(item => item.Key == field) && values.GetValueOrDefault(setting) is { Length: > 0 } value)
                    fields[field] = value;
            // FlareSolverr from the source engine settings is only used when FLARESOLVERR_ENABLED is on (default off,
            // as in the source host) and automatic challenges are not switched off (SOURCE_AUTO_CHALLENGES, default on).
            var enabled = source.Type != "flaresolverr"
                || Flag(values.GetValueOrDefault("FLARESOLVERR_ENABLED"), fallback: false) && Flag(values.GetValueOrDefault("SOURCE_AUTO_CHALLENGES"), fallback: true);
            result.Add(new(SettingsIdPrefix + source.Type, source.Type, source.Label, enabled, fields));
        }
        return result;
    }

    private static bool Flag(string? value, bool fallback) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => fallback,
    };

    private static string Short(string? message)
    {
        var text = (message ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxMessageLength ? text : text[..(MaxMessageLength - 1)] + "…";
    }

    private void RecordTest(string id, ConnectionTestResult result)
    {
        lock (_testsGate)
        {
            var tests = LoadTests();
            tests[id] = new(_clock.GetUtcNow(), result.Ok, Short(result.Message));
            SaveTests(tests);
        }
    }

    private void ForgetTest(string id)
    {
        lock (_testsGate)
            if (LoadTests().Remove(id))
                SaveTests(LoadTests());
    }

    private Dictionary<string, ConnectionTestRecord> LoadTests()
    {
        if (_lastTests is not null)
            return _lastTests;
        _lastTests = new(StringComparer.Ordinal);
        if (_lastTestsPath is null)
            return _lastTests;
        try
        {
            if (ConfigFiles.ReadBounded(_lastTestsPath) is { } raw && JsonNode.Parse(raw) is JsonObject root)
                foreach (var (id, node) in root)
                    if (node is JsonObject item && item["at"] is JsonValue at && at.TryGetValue<string>(out var stamp)
                        && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                        && item["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var passed))
                        _lastTests[id] = new(time, passed, item["message"] is JsonValue message && message.TryGetValue<string>(out var text) ? Short(text) : "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ConfigurationException)
        {
            // A damaged or unreadable results file only loses the "last tested" column.
        }
        return _lastTests;
    }

    private void SaveTests(Dictionary<string, ConnectionTestRecord> tests)
    {
        if (_lastTestsPath is null)
            return;
        var root = new JsonObject();
        foreach (var (id, test) in tests.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            root[id] = new JsonObject
            {
                ["at"] = test.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["ok"] = test.Ok,
                ["message"] = test.Message
            };
        try { ConfigFiles.WriteAtomic(_lastTestsPath, Encoding.UTF8.GetBytes(root.ToJsonString())); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ConfigurationException)
        {
            // Kept in memory; the file is only a convenience across restarts.
        }
    }
}
