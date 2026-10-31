using System.Globalization;

namespace AutoSourcarr.Core.Settings;

/// <summary>
/// ARC-7: an update keeps every saved setting, and you are told exactly what it changed. config.json remembers
/// the last version and its built-in defaults; on the first start of a new version the differences become a
/// notice that stays until dismissed. Only key names and built-in defaults appear, never your saved values.
/// </summary>
public static class UpdateNotices
{
    /// <summary>Records this version and returns a new notice when the version changed with visible effects.</summary>
    public static UpdateNotice? Check(string root, string appVersion, DateTimeOffset? now = null)
    {
        lock (SettingsEditor.Gate(root))
            return CheckCore(root, appVersion, now);
    }

    private static UpdateNotice? CheckCore(string root, string appVersion, DateTimeOffset? now)
    {
        var store = new ConfigStore(root);
        var document = store.ReadDocument();
        if (document is null)
            return null; // no config.json yet: nothing of yours to keep or report
        var defaults = SettingsSchema.Entries.Where(entry => !entry.Secret)
            .ToDictionary(entry => entry.Key, entry => entry.Default ?? "", StringComparer.Ordinal);
        var previous = document.Update;
        if (previous is not null && previous.AppVersion == appVersion)
            return null;

        UpdateNotice? notice = null;
        if (previous is not null)
        {
            var saved = store.Load(document).Settings;
            var items = new List<UpdateNoticeItem>();
            foreach (var (key, value) in defaults.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (saved.ContainsKey(key) || !previous.Defaults.TryGetValue(key, out var old) || old == value)
                    continue;
                items.Add(new UpdateNoticeItem
                {
                    Key = key, Kind = "default_changed", OldDefault = old, NewDefault = value,
                    Message = $"{SettingsSchema.ByKey[key].Label}: the built-in default changed from \"{old}\" to \"{value}\". You never changed this setting, so the new default is used."
                });
            }
            foreach (var key in saved.Keys.Where(key => !SettingsSchema.ByKey.ContainsKey(key)).Order(StringComparer.Ordinal))
                items.Add(new UpdateNoticeItem { Key = key, Kind = "removed", Message = $"{key} is no longer used by this version. Its saved value is kept in config.json but has no effect." });
            foreach (var (key, value) in saved.Where(pair => SettingsSchema.ByKey.ContainsKey(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                try { SettingsLoader.Load(configuration: new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value }); }
                catch (ConfigurationException)
                {
                    items.Add(new UpdateNoticeItem { Key = key, Kind = "must_change", Message = $"{SettingsSchema.ByKey[key].Label}: your saved value is not accepted by this version. Open Settings and choose a new value." });
                }
            }
            if (items.Count > 0)
                notice = new UpdateNotice
                {
                    FromVersion = previous.AppVersion, ToVersion = appVersion, Items = items,
                    CreatedAt = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                };
        }
        document.Update = new ConfigUpdateRecord { AppVersion = appVersion, Defaults = defaults, Notice = notice ?? previous?.Notice };
        store.WriteDocument(document);
        return notice;
    }

    public static UpdateNotice? Current(string root) => new ConfigStore(root).ReadDocument()?.Update?.Notice;

    public static bool Dismiss(string root)
    {
        lock (SettingsEditor.Gate(root))
            return DismissCore(root);
    }

    private static bool DismissCore(string root)
    {
        var store = new ConfigStore(root);
        var document = store.ReadDocument();
        if (document?.Update?.Notice is null)
            return false;
        document.Update.Notice = null;
        store.WriteDocument(document);
        return true;
    }
}
