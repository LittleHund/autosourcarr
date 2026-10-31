using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Settings;

public sealed partial class CanonicalConfiguration
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DurableConfigurationFiles _files;
    private readonly object _gate;

    public CanonicalConfiguration(string root)
    {
        _files = new(root);
        _gate = Gates.GetOrAdd(_files.Root, _ => new object());
    }

    public IReadOnlyDictionary<string, string?> StartupOverrides()
    {
        lock (_gate)
        {
            var recovered = RecoverCore();
            var state = State();
            var saved = EnvironmentFile("autosourcarr.env");
            var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (state["managed_keys"] is JsonArray keys)
                foreach (var node in keys)
                {
                    var key = node?.GetValue<string>();
                    if (key is not null && key != "EXTENSION_STORES" && SetupFields.ByKey.ContainsKey(key)
                        && saved.TryGetValue(key, out var value) && value is not null)
                        overrides[key] = value;
                }
            foreach (var (key, value) in recovered)
                overrides[key] = value;
            return overrides;
        }
    }

    public IReadOnlyDictionary<string, string?> Recover()
    {
        lock (_gate)
            return RecoverCore();
    }

    public string Revision()
    {
        lock (_gate)
        {
            var state = State();
            state.Remove("cleanup");
            using var bytes = new MemoryStream();
            foreach (var name in DurableConfigurationFiles.ManagedFiles.Take(2))
            {
                bytes.Write(_files.Read(name) ?? []);
                bytes.WriteByte(0);
            }
            // A new revision representation invalidates old open drafts safely.
            bytes.Write(StrictUtf8.GetBytes(SortedJson(state)));
            return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
        }
    }

    public JsonObject State()
    {
        var raw = _files.Read(DurableConfigurationFiles.State);
        return raw is null || raw.Length == 0 ? new() : Object(raw, "Invalid setup state.");
    }

    public IReadOnlyDictionary<string, string?> EnvironmentFile(string name)
    {
        if (name is not ("autosourcarr.env" or "source-host.env"))
            throw new ArgumentException("Unknown configuration environment file.", nameof(name));
        return DotEnv.Parse(StrictUtf8.GetString(_files.Read(name) ?? []));
    }

    public static byte[] PatchEnvironment(byte[]? raw, IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var text = StrictUtf8.GetString(raw ?? []);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 1 && lines[0].Length == 0)
            lines.Clear();
        foreach (var (key, node) in changes)
        {
            if (!SetupFields.ByKey.ContainsKey(key))
                throw new InvalidDataException("Unsupported canonical configuration key.");
            var value = Text(node);
            var encoded = SafeUnquoted().IsMatch(value) ? value : "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
            var pattern = new Regex(@"^\s*(?:export\s+)?" + Regex.Escape(key) + @"\s*=", RegexOptions.CultureInvariant);
            var indexes = Enumerable.Range(0, lines.Count).Where(i => pattern.IsMatch(lines[i])).ToArray();
            if (indexes.Length > 1)
                throw new InvalidDataException("Duplicate configuration key: " + key + ". Resolve it before saving.");
            if (indexes.Length == 0)
                lines.Add(key + "=" + encoded);
            else
                lines[indexes[0]] = key + "=" + encoded;
        }
        return StrictUtf8.GetBytes(string.Join('\n', lines) + "\n");
    }

    private IReadOnlyDictionary<string, string?> RecoverCore()
    {
        var raw = _files.Read(DurableConfigurationFiles.Journal);
        if (raw is null || raw.Length == 0)
            return new Dictionary<string, string?>();
        var journal = Object(raw, "Invalid setup recovery journal; configuration was not modified.");
        if (journal["before"] is not JsonObject saved || !saved.Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(DurableConfigurationFiles.ManagedFiles)
            || !Guid.TryParse(journal["transaction"]?.GetValue<string>(), out _))
            throw new InvalidDataException("Invalid setup recovery journal; configuration was not modified.");
        var before = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var (name, value) in saved)
        {
            byte[]? decoded = null;
            if (value is not null)
            {
                var encoded = value.GetValue<string>();
                if (encoded.Length % 4 != 0 || !Base64().IsMatch(encoded))
                    throw new InvalidDataException("Invalid setup recovery encoding.");
                decoded = Convert.FromBase64String(encoded);
                if (decoded.Length > DurableConfigurationFiles.FileLimit)
                    throw new InvalidDataException("Setup recovery exceeds its supported size.");
            }
            before[name] = decoded;
        }
        var restoredState = before[DurableConfigurationFiles.State] is { Length: > 0 } stateBytes
            ? Object(stateBytes, "Invalid setup recovery state.") : new JsonObject();
        var effective = journal["effective_before"] as JsonObject ?? (journal.ContainsKey("effective_before")
            ? throw new InvalidDataException("Invalid setup recovery values.") : new JsonObject());
        if (effective.Any(pair => pair.Key == "EXTENSION_STORES" || !SetupFields.ByKey.ContainsKey(pair.Key)))
            throw new InvalidDataException("Invalid setup recovery keys.");
        if (State()["transaction"]?.GetValue<string>() == journal["transaction"]!.GetValue<string>())
        {
            _files.Write(DurableConfigurationFiles.Journal, null);
            return new Dictionary<string, string?>();
        }
        if (effective.Count > 0)
        {
            before["autosourcarr.env"] = PatchEnvironment(before["autosourcarr.env"], effective.ToDictionary(p => p.Key, p => p.Value));
            var managed = (restoredState["managed_keys"] as JsonArray ?? [])
                .Select(node => node!.GetValue<string>()).Concat(effective.Select(p => p.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            restoredState["managed_keys"] = new JsonArray(managed.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
            before[DurableConfigurationFiles.State] = StrictUtf8.GetBytes(restoredState.ToJsonString());
        }
        // Validate every current target before the first recovery write, too.
        foreach (var name in DurableConfigurationFiles.ManagedFiles)
            _ = _files.Read(name);
        foreach (var name in DurableConfigurationFiles.ManagedFiles)
            _files.Write(name, before[name]);
        _files.Write(DurableConfigurationFiles.Journal, null);
        return effective.ToDictionary(pair => pair.Key, pair => (string?)Text(pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static JsonObject Object(byte[] value, string error) => JsonNode.Parse(StrictUtf8.GetString(value),
        documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw new InvalidDataException(error);

    private static string Text(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            return text;
        if (value is JsonValue flag && flag.TryGetValue<bool>(out var enabled))
            return enabled ? "true" : "false";
        return value?.ToJsonString() ?? "None";
    }

    private static string SortedJson(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(',', obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + SortedJson(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(SortedJson)) + "]",
        _ => node?.ToJsonString() ?? "null"
    };

    [GeneratedRegex(@"^[A-Za-z0-9_.,:/@%+~=-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeUnquoted();
    [GeneratedRegex(@"^[A-Za-z0-9+/]*={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64();
}
