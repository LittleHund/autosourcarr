namespace AutoSourcarr.Core.Settings;

/// <summary>One changed setting: key and kind only. Values never appear, so the list is safe to log and show.</summary>
public sealed record ConfigChange(string Section, string Key, string Kind, bool Secret)
{
    public override string ToString() => Secret
        ? $"{Section}.{Key}: {Kind} (secret, value hidden)"
        : $"{Section}.{Key}: {Kind}";
}

/// <summary>The exact change list shown after a settings save or an update (CON-5 "exact update notices").</summary>
public static class ConfigChanges
{
    public const string Added = "added";
    public const string Changed = "changed";
    public const string Removed = "removed";

    public static IReadOnlyList<ConfigChange> Compare(ConfigValues before, ConfigValues after)
    {
        var changes = new List<ConfigChange>();
        foreach (var ((section, old), (_, current)) in before.Sections().Zip(after.Sections()))
        {
            foreach (var key in old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var had = old.TryGetValue(key, out var previous);
                var has = current.TryGetValue(key, out var next);
                var kind = (had, has) switch
                {
                    (false, true) => Added,
                    (true, false) => Removed,
                    _ when !string.Equals(previous, next, StringComparison.Ordinal) => Changed,
                    _ => null
                };
                if (kind is not null)
                    changes.Add(new(section, key, kind, SettingsSecrets.IsSecretKey(key)));
            }
        }
        return changes;
    }
}
