using System.Text.Json;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Backup;

/// <summary>
/// The settings part of a backup (SET-8): config.json without any secret. Every encrypted field (settings, source
/// host, extra values, connection secrets) is left out and named in the manifest as "not included". config.key is
/// never read. A restore keeps this instance's secrets (hard rule: never overwrite active secrets with nothing).
/// </summary>
public static class BackupSettings
{
    public sealed record Sanitized(ConfigDocument Document, IReadOnlyList<string> NotIncluded);

    public sealed record Merged(ConfigDocument Document, IReadOnlyList<string> KeptSecrets, IReadOnlyList<string> MissingSecrets,
        IReadOnlyList<string> ChangedKeys, IReadOnlyList<string> AddedConnections, IReadOnlyList<string> KeptConnections);

    public static byte[] Serialize(ConfigDocument document) => JsonSerializer.SerializeToUtf8Bytes(document, ConfigJsonContext.Default.ConfigDocument);

    public static ConfigDocument Parse(byte[] raw)
    {
        ConfigDocument? document;
        try { document = JsonSerializer.Deserialize(raw, ConfigJsonContext.Default.ConfigDocument); }
        catch (JsonException error) { throw new BackupRefusedException("The settings in the backup are not a valid config.json.", 400, error); }
        if (document?.Unknown is { Count: > 0 })
            throw new BackupRefusedException("The settings in the backup contain keys a backup never writes; this file was changed by hand.");
        if (document is null || document.Settings is null || document.SourceHost is null || document.Extra is null || document.ManagedKeys is null)
            throw new BackupRefusedException("The settings in the backup are incomplete.");
        if (document.Format > ConfigDocument.CurrentFormat)
            throw new BackupRefusedException($"The settings in the backup use config.json format {document.Format}; this build reads up to {ConfigDocument.CurrentFormat}.");
        foreach (var (name, section) in Sections(document))
        {
            if (section.Values is null || section.Secrets is null || section.Values.Values.Any(value => value is null))
                throw new BackupRefusedException("The settings in the backup have an empty section.");
            if (section.Secrets.Count > 0 || section.Values.Keys.Any(SettingsSecrets.IsSecretKey))
                throw new BackupRefusedException($"The settings in the backup contain a secret ({name}); backups never carry secrets, so this file was changed by hand.");
        }
        if (document.Connections?.Any(connection => connection is null || connection.Values is null || connection.Secrets is null || connection.Secrets.Count > 0) == true)
            throw new BackupRefusedException("The connections in the backup contain a secret or are damaged; backups never carry secrets.");
        return document;
    }

    public static Sanitized Sanitize(ConfigDocument current)
    {
        var copy = JsonSerializer.Deserialize(Serialize(current), ConfigJsonContext.Default.ConfigDocument)!;
        var omitted = new List<string>();
        // Keys this build does not understand could hold anything: they are never copied into a backup.
        if (copy.Unknown is { Count: > 0 } unknown) omitted.AddRange(unknown.Keys.Order(StringComparer.Ordinal).Select(key => "config." + key));
        copy.Unknown = null;
        foreach (var (name, section) in Sections(copy))
        {
            omitted.AddRange(section.Secrets.Keys.Order(StringComparer.Ordinal).Select(key => name + "." + key));
            section.Secrets.Clear();
            foreach (var key in section.Values.Keys.Where(SettingsSecrets.IsSecretKey).ToList()) { section.Values.Remove(key); omitted.Add(name + "." + key); }
        }
        foreach (var connection in copy.Connections ?? [])
        {
            omitted.AddRange(connection.Secrets.Keys.Order(StringComparer.Ordinal).Select(field => "connections." + connection.Id + "." + field));
            connection.Secrets.Clear();
        }
        return new Sanitized(copy, omitted);
    }

    /// <summary>The config.json a restore writes: the backup's values, this instance's secrets, setup and update records.</summary>
    public static Merged Merge(ConfigDocument backup, ConfigDocument? current)
    {
        var result = JsonSerializer.Deserialize(Serialize(backup), ConfigJsonContext.Default.ConfigDocument)!;
        result.Format = ConfigDocument.CurrentFormat;
        var kept = new List<string>(); var dropped = new List<string>(); var changed = new SortedSet<string>(StringComparer.Ordinal);
        var currentSections = current is null ? null : Sections(current).ToDictionary(pair => pair.Name, pair => pair.Section, StringComparer.Ordinal);
        foreach (var (name, section) in Sections(result))
        {
            var mine = currentSections?.GetValueOrDefault(name);
            foreach (var key in section.Values.Keys.Union(mine?.Values.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal))
                if (section.Values.GetValueOrDefault(key) != mine?.Values.GetValueOrDefault(key)) changed.Add(name + "." + key);
            if (mine is null) continue;
            // A saved secret is never sent to a different server (phase 7 rule): when the backup changes an address of
            // the same group (MAIN_SUWAYOMI_URL for MAIN_SUWAYOMI_PASSWORD, TRANSLATION_API_URL for TRANSLATION_API_KEY ...),
            // that secret is not carried over and must be entered again.
            var moved = section.Values.Keys.Union(mine.Values.Keys, StringComparer.Ordinal)
                .Where(key => key.EndsWith("_URL", StringComparison.OrdinalIgnoreCase) && section.Values.GetValueOrDefault(key) != mine.Values.GetValueOrDefault(key))
                .Select(Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, cipher) in mine.Secrets)
            {
                if (moved.Contains(Group(key))) { dropped.Add(name + "." + key); continue; }
                section.Values.Remove(key);
                section.Secrets[key] = cipher;
                kept.Add(name + "." + key);
            }
        }
        var managed = new HashSet<string>(result.ManagedKeys, StringComparer.Ordinal);
        if (current is not null)
            foreach (var key in current.ManagedKeys.Where(key => current.Settings.Secrets.ContainsKey(key))) managed.Add(key);
        result.ManagedKeys = managed.Order(StringComparer.Ordinal).ToList();
        var added = new List<string>(); var keptConnections = new List<string>();
        var restored = result.Connections ?? [];
        foreach (var connection in restored)
        {
            var mine = current?.Connections?.FirstOrDefault(other => other.Id == connection.Id && other.Type == connection.Type);
            if (mine is null) { added.Add(connection.Id); continue; }
            // Same rule for a connection: its secrets stay only when every other value (address included) is unchanged.
            var same = connection.Values.Count == mine.Values.Count && connection.Values.All(pair => mine.Values.GetValueOrDefault(pair.Key) == pair.Value);
            foreach (var (field, cipher) in mine.Secrets)
            {
                if (!same) { dropped.Add("connections." + connection.Id + "." + field); continue; }
                connection.Secrets[field] = cipher; kept.Add("connections." + connection.Id + "." + field);
            }
        }
        foreach (var mine in current?.Connections ?? [])
            if (restored.All(other => other.Id != mine.Id)) { restored.Add(mine); keptConnections.Add(mine.Id); }
        result.Connections = restored.Count == 0 && backup.Connections is null && current?.Connections is null ? null : restored;
        // The running build's own records stay: setup state, update record (SET-9) and conversion history.
        if (current is not null)
        {
            result.Setup = current.Setup ?? result.Setup; result.Update = current.Update; result.Conversion = current.Conversion ?? result.Conversion;
            result.Unknown = current.Unknown;
        }
        else result.Update = null;
        return new Merged(result, kept, dropped, changed.ToList(), added, keptConnections);
    }

    /// <summary>The first two name parts: MAIN_SUWAYOMI_PASSWORD and MAIN_SUWAYOMI_URL share "MAIN_SUWAYOMI".</summary>
    private static string Group(string key)
    {
        var parts = key.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[1] : key;
    }

    /// <summary>Secrets the backup names as "not included" that this instance does not have either: they must be entered again.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> notIncluded, Merged merged) =>
        notIncluded.Where(name => name.Contains('.', StringComparison.Ordinal) && !merged.KeptSecrets.Contains(name, StringComparer.Ordinal))
            .Concat(merged.MissingSecrets).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static IEnumerable<(string Name, ConfigSection Section)> Sections(ConfigDocument document)
    {
        yield return ("settings", document.Settings);
        yield return ("source_host", document.SourceHost);
        yield return ("extra", document.Extra);
    }
}
