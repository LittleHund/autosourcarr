using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Compatibility;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Backup;

/// <summary>One saved backup as the list shows it (no paths, no secrets).</summary>
public sealed record BackupSummary(string Id, string CreatedAt, string AppVersion, string Kind, string? Reason, long Bytes, bool Protected,
    int Files, long Entries, long Chapters, bool ShareExport, int FormatVersion)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["created_at"] = CreatedAt, ["app_version"] = AppVersion, ["kind"] = Kind, ["reason"] = Reason, ["bytes"] = Bytes,
        ["protected"] = Protected, ["files"] = Files, ["entries"] = Entries, ["chapters"] = Chapters, ["share_export"] = ShareExport,
        ["format_version"] = FormatVersion,
    };

    public static BackupSummary? FromJson(JsonObject? row)
    {
        if (row is null) return null;
        try
        {
            return new(row["id"]!.GetValue<string>(), row["created_at"]!.GetValue<string>(), row["app_version"]?.GetValue<string>() ?? "",
                row["kind"]?.GetValue<string>() ?? "manual", row["reason"]?.GetValue<string>(), row["bytes"]?.GetValue<long>() ?? 0,
                row["protected"]?.GetValue<bool>() ?? false, row["files"]?.GetValue<int>() ?? 0, row["entries"]?.GetValue<long>() ?? 0,
                row["chapters"]?.GetValue<long>() ?? 0, row["share_export"]?.GetValue<bool>() ?? false, row["format_version"]?.GetValue<int>() ?? 0);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }
}

/// <summary>
/// SET-8 instance backup and restore (port of 0.4.x instance_backup.py, format 3). One file, a zip:
/// <code>
/// manifest.json                    format, app and schema versions, PIPE-4 compatibility list, created_at, contents with sha256
/// settings/config.json             config.json without any secret (each left-out field is listed under not_included)
/// profiles/profiles.json           matching presets/policies, serving profiles/policies, gallery policies, source choices
/// databases/catalog.sqlite         consistent online snapshot (both files taken in one drained storage lease)
/// databases/chapter-origins.sqlite
/// catalogue/share-0001.json …      the phase 9 portable catalogue share export (for sharing; restore uses the databases)
/// </code>
/// Optionally password protected (<see cref="BackupCrypto"/>). config.key, accounts, tokens, cookies, signing keys,
/// source-host data, downloads, covers and logs are never included. A restore is previewed (what changes, version
/// check, preview token), then applied as one job: background work stopped, a pre-restore backup kept, both databases
/// swapped and checked, older schemas migrated, settings merged with this instance's secrets; any failure rolls back.
/// </summary>
public sealed partial class InstanceBackupStore
{
    public const string FormatName = "autosourcarr-instance-backup";
    public const int FormatVersion = 3;
    public const int MinReaderVersion = 3;
    public const long MaxArchiveBytes = 1L << 30;
    public const int ManualLimit = 10;
    public const int AutomaticKeep = 3;
    public const string Extension = ".ascbackup";
    private const int MaxSharePages = 400;

    /// <summary>Profile tables exported to profiles/profiles.json (SET-8 "profiles"; they are inside catalog.sqlite too).</summary>
    public static readonly string[] ProfileTables =
        ["matching_presets", "matching_policies", "serving_profiles", "serving_upgrade_policies", "gallery_policies", "source_config", "source_groups"];

    /// <summary>What a backup never contains, besides the secret fields it names one by one.</summary>
    public static readonly string[] AlwaysExcluded =
    [
        "config.key (the key that encrypts secrets in config.json)", "accounts and password hashes", "admin token, reader API keys and script keys",
        "cookies and source-host data", "signing keys", "downloaded chapters (library folder)", "cover cache", "logs and diagnostics",
    ];

    private static readonly string[] KeyTables =
        ["entries", "source_matches", "manual_match_groups", "manual_match_bindings", "manual_separate_records", "preferred_title_overrides",
         "duplicate_reviews", "import_runs", "matching_presets", "serving_profiles", "virtual_chapters", "chapter_candidates", "origin_history"];

    private readonly object _gate = new();
    private readonly SqliteDatabase _database;
    private readonly TimeProvider _clock;

    public InstanceBackupStore(string dataDirectory, string configurationRoot, SqliteDatabase database, string appVersion, TimeProvider? clock = null)
    {
        Root = Path.Combine(Path.GetFullPath(dataDirectory), "instance-backups");
        ConfigurationRoot = Path.GetFullPath(configurationRoot);
        _database = database; AppVersion = appVersion; _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string Root { get; }
    public string ConfigurationRoot { get; }
    public string AppVersion { get; }
    private string Staged => Path.Combine(Root, "staged");
    private string Now() => _clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(?:manifest\.json|settings/config\.json|profiles/profiles\.json|databases/(?:catalog|chapter-origins)\.sqlite|catalogue/share-[0-9]{4}\.json)$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern();

    public static bool ValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    // ---- list / file / delete ---------------------------------------------------------------------------------

    public IReadOnlyList<BackupSummary> List()
    {
        var rows = new List<BackupSummary>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!ValidId(id) || !File.Exists(Path.Combine(Root, id + Extension))) continue;
            try { if (BackupSummary.FromJson(JsonNode.Parse(File.ReadAllText(file)) as JsonObject) is { } row && row.Id == id) rows.Add(row); }
            catch (Exception error) when (error is JsonException or IOException) { _ = error; }
        }
        return rows.OrderByDescending(row => row.CreatedAt, StringComparer.Ordinal).ToList();
    }

    public BackupSummary? Find(string id) => ValidId(id) ? List().FirstOrDefault(row => row.Id == id) : null;

    /// <summary>The backup file for a download; <see cref="KeyNotFoundException"/> when there is none.</summary>
    public string FilePath(string id)
    {
        if (!ValidId(id)) throw new KeyNotFoundException("No backup with this id.");
        var path = Path.Combine(Root, id + Extension);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null) throw new KeyNotFoundException("No backup with this id.");
        return path;
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var path = FilePath(id);
            File.Delete(path);
            File.Delete(Path.Combine(Root, id + ".json"));
        }
    }

    // ---- create ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes a backup. <paramref name="kind"/> is manual, automatic (before an update/migration, SET-9) or pre-restore.
    /// The share export runs first (it records portable ids); then both databases are snapshotted in one drained lease.
    /// </summary>
    public BackupSummary Create(string kind, string? reason = null, string? passphrase = null, Func<long, JsonObject>? sharePage = null,
        TimeSpan? drain = null, Action<string>? report = null)
    {
        if (kind is not ("manual" or "automatic" or "pre-restore")) throw new ArgumentException("Unknown backup kind.", nameof(kind));
        if (passphrase is not null) BackupCrypto.CheckPassphrase(passphrase);
        lock (_gate)
        {
            if (kind == "manual" && List().Count(row => row.Kind == "manual") >= ManualLimit)
                throw new BackupRefusedException($"{ManualLimit} manual backups are kept. Download and delete an old one before creating another.", 409);
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var work = Path.Combine(Root, ".work-" + id);
            Directory.CreateDirectory(work);
            try
            {
                var contents = new List<(string Entry, string File)>();
                var notes = new List<string>();
                var share = 0;
                if (sharePage is not null)
                {
                    report?.Invoke("Exporting the portable catalogue");
                    long after = 0;
                    while (share < MaxSharePages)
                    {
                        var page = sharePage(after);
                        share++;
                        var file = Path.Combine(work, "share-" + share.ToString("D4", CultureInfo.InvariantCulture) + ".json");
                        File.WriteAllText(file, page["catalogue"]?.ToJsonString() ?? "{}", Encoding.UTF8);
                        contents.Add(("catalogue/share-" + share.ToString("D4", CultureInfo.InvariantCulture) + ".json", file));
                        if (page["next_cursor"] is JsonValue next && next.TryGetValue<long>(out var cursor) && cursor > after) after = cursor;
                        else break;
                    }
                }
                else notes.Add("The portable catalogue share export is not included in this " + kind + " backup; the databases are.");

                report?.Invoke("Copying both databases");
                var facts = new Dictionary<string, DatabaseFacts>(StringComparer.Ordinal);
                _database.Quiescent(paths =>
                {
                    foreach (var item in BackupDatabases.Files)
                    {
                        var source = BackupDatabases.PathOf(paths, item.File);
                        if (!File.Exists(source)) throw new BackupRefusedException(item.Name + " does not exist yet; there is nothing to back up.", 409);
                        BackupDatabases.Snapshot(source, Path.Combine(work, item.Name));
                    }
                    return 0;
                }, drain ?? TimeSpan.FromSeconds(120));
                foreach (var item in BackupDatabases.Files)
                {
                    var copy = Path.Combine(work, item.Name);
                    if (item.File == DatabaseFile.Catalog) BackupDatabases.ScrubSecrets(copy);
                    var fact = BackupDatabases.Facts(copy);
                    if (!fact.Intact) throw new BackupRefusedException(item.Name + " failed SQLite's integrity check; no backup was written.", 409);
                    facts[item.Name] = fact;
                    contents.Add((item.Entry, copy));
                }

                var profiles = Path.Combine(work, "profiles.json");
                var profileRows = BackupDatabases.TableRows(Path.Combine(work, "catalog.sqlite"), ProfileTables);
                File.WriteAllText(profiles, new JsonObject
                {
                    ["format"] = "autosourcarr-profiles", ["profile_format"] = CompatibilityList.Current.ProfileFormat, ["tables"] = profileRows,
                }.ToJsonString(), Encoding.UTF8);
                contents.Add(("profiles/profiles.json", profiles));

                var notIncluded = new List<string>();
                ConfigDocument? config;
                lock (SettingsEditor.Gate(ConfigurationRoot)) config = new ConfigStore(ConfigurationRoot).ReadDocument();
                if (config is not null)
                {
                    var clean = BackupSettings.Sanitize(config);
                    notIncluded.AddRange(clean.NotIncluded);
                    var file = Path.Combine(work, "config.json");
                    File.WriteAllBytes(file, BackupSettings.Serialize(clean.Document));
                    contents.Add(("settings/config.json", file));
                }
                else notes.Add("This instance has no config.json yet; no settings are included.");

                var compatibility = CompatibilityList.Current.ToJson();
                var manifest = new JsonObject
                {
                    ["format"] = FormatName, ["format_version"] = FormatVersion, ["min_reader_version"] = MinReaderVersion,
                    ["app_version"] = AppVersion, ["created_at"] = Now(), ["kind"] = kind, ["reason"] = reason,
                    ["compatibility"] = compatibility,
                    ["schema"] = new JsonObject
                    {
                        ["catalog"] = facts["catalog.sqlite"].ToJson(), ["origins"] = facts["chapter-origins.sqlite"].ToJson(),
                    },
                    ["contents"] = new JsonArray(contents.OrderBy(item => item.Entry, StringComparer.Ordinal).Select(item => (JsonNode?)new JsonObject
                    {
                        ["path"] = item.Entry, ["sha256"] = Sha256File(item.File), ["bytes"] = new FileInfo(item.File).Length,
                    }).ToArray()),
                    ["not_included"] = new JsonArray(notIncluded.Select(name => (JsonNode?)name).ToArray()),
                    ["always_excluded"] = new JsonArray(AlwaysExcluded.Select(name => (JsonNode?)name).ToArray()),
                    ["notes"] = new JsonArray(notes.Select(name => (JsonNode?)name).ToArray()),
                };

                report?.Invoke("Writing the backup file");
                var archive = Path.Combine(work, "backup.zip");
                using (var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using (var output = entry.Open()) output.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
                    foreach (var (name, file) in contents.OrderBy(item => item.Entry, StringComparer.Ordinal))
                        zip.CreateEntryFromFile(file, name, CompressionLevel.Fastest);
                }
                var length = new FileInfo(archive).Length;
                if (length > MaxArchiveBytes) throw new BackupRefusedException("The backup is larger than 1 GiB; this build cannot write it.", 413);
                var target = Path.Combine(Root, id + Extension);
                if (passphrase is not null)
                {
                    var sealedBytes = BackupCrypto.Protect(File.ReadAllBytes(archive), passphrase);
                    WriteAtomic(target, sealedBytes);
                    length = sealedBytes.Length;
                }
                else
                {
                    using (var stream = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
                    File.Move(archive, target);
                }
                Private(target);
                var summary = new BackupSummary(id, manifest["created_at"]!.GetValue<string>(), AppVersion, kind, reason, length, passphrase is not null,
                    contents.Count, facts["catalog.sqlite"].Rows.GetValueOrDefault("entries"), facts["chapter-origins.sqlite"].Rows.GetValueOrDefault("virtual_chapters"),
                    share > 0, FormatVersion);
                WriteAtomic(Path.Combine(Root, id + ".json"), Encoding.UTF8.GetBytes(summary.ToJson().ToJsonString()));
                Prune(kind);
                return summary;
            }
            finally { TryDeleteDirectory(work); }
        }
    }

    /// <summary>Automatic and pre-restore backups keep the newest <see cref="AutomaticKeep"/> of their kind.</summary>
    private void Prune(string kind)
    {
        if (kind == "manual") return;
        foreach (var old in List().Where(row => row.Kind == kind).Skip(AutomaticKeep))
        {
            TryDelete(Path.Combine(Root, old.Id + Extension));
            TryDelete(Path.Combine(Root, old.Id + ".json"));
        }
    }

    // ---- read / stage / preview -------------------------------------------------------------------------------

    /// <summary>Decrypts (when protected) and checks the outer format; refuses 0.4.x files and anything that is not a backup.</summary>
    public static byte[] Open(byte[] data, string? passphrase)
    {
        if (BackupCrypto.IsLegacy(data))
            throw new BackupRefusedException("This is an AutoSourcarr 0.4.x instance backup (format 2). Restore it with the 0.4.x version that made it, then update; "
                + CatalogueFormatMessage("0.4.5"));
        var plain = BackupCrypto.IsProtected(data) ? BackupCrypto.Unprotect(data, passphrase) : data;
        if (plain.Length < 4 || plain[0] != (byte)'P' || plain[1] != (byte)'K')
            throw new BackupRefusedException("This file is not an AutoSourcarr backup.");
        return plain;
    }

    private static string CatalogueFormatMessage(string version) => Catalogue.Sharing.CatalogueFormat.MismatchMessage(version) + ".";

    /// <summary>
    /// Reads a backup into a fresh staging folder, checks every content hash, the format and schema versions, and
    /// answers what a restore would change with its preview token. Only the newest staged backup is kept.
    /// </summary>
    public JsonObject Stage(byte[] data, string? passphrase, string source)
    {
        if (data.LongLength > MaxArchiveBytes + 64) throw new BackupRefusedException("The backup is larger than 1 GiB.", 413);
        var plain = Open(data, passphrase);
        lock (_gate)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var target = Path.Combine(Staged, id);
            Directory.CreateDirectory(target);
            try
            {
                var manifest = Extract(plain, target);
                var facts = CheckDatabases(target, manifest);
                var preview = Preview(id, target, manifest, facts, source);
                File.WriteAllText(Path.Combine(target, "stage.json"), new JsonObject
                {
                    ["id"] = id, ["token"] = preview["preview_token"]!.GetValue<string>(), ["source"] = source, ["created_at"] = Now(),
                    ["manifest_sha256"] = Sha256File(Path.Combine(target, "manifest.json")),
                }.ToJsonString());
                foreach (var old in Directory.EnumerateDirectories(Staged).Where(path => !string.Equals(Path.GetFileName(path), id, StringComparison.Ordinal)))
                    TryDeleteDirectory(old);
                return preview;
            }
            catch
            {
                TryDeleteDirectory(target);
                throw;
            }
        }
    }

    /// <summary>Stages one of the saved backups (the automatic one before an update, for the one-click rollback).</summary>
    public JsonObject StageSaved(string backupId, string? passphrase) => Stage(File.ReadAllBytes(FilePath(backupId)), passphrase, "backup:" + backupId);

    private static JsonObject Extract(byte[] plain, string target)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(plain, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException error) { throw new BackupRefusedException("The backup file is damaged (not a readable zip).", 400, error); }
        using (zip)
        {
            var names = zip.Entries.Select(entry => entry.FullName).ToList();
            if (names.Count != names.Distinct(StringComparer.Ordinal).Count()) throw new BackupRefusedException("The backup lists a file twice.");
            if (names.Any(name => !EntryPattern().IsMatch(name))) throw new BackupRefusedException("The backup contains a file that does not belong in a backup.");
            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new BackupRefusedException("The backup has no manifest.");
            if (manifestEntry.Length > 8 * 1024 * 1024) throw new BackupRefusedException("The backup manifest is too large.");
            JsonObject manifest;
            using (var stream = manifestEntry.Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                File.WriteAllBytes(Path.Combine(target, "manifest.json"), buffer.ToArray());
                try { manifest = JsonNode.Parse(buffer.ToArray()) as JsonObject ?? throw new BackupRefusedException("The backup manifest is not an object."); }
                catch (JsonException error) { throw new BackupRefusedException("The backup manifest is not valid JSON.", 400, error); }
            }
            CheckManifest(manifest);
            var listed = new Dictionary<string, (string Sha, long Bytes)>(StringComparer.Ordinal);
            foreach (var row in manifest["contents"]!.AsArray().OfType<JsonObject>())
            {
                var path = row["path"]?.GetValue<string>() ?? "";
                if (!EntryPattern().IsMatch(path) || path == "manifest.json" || !listed.TryAdd(path, (row["sha256"]?.GetValue<string>() ?? "", row["bytes"]?.GetValue<long>() ?? -1)))
                    throw new BackupRefusedException("The backup manifest lists an invalid file.");
            }
            if (!listed.Keys.Order(StringComparer.Ordinal).SequenceEqual(names.Where(name => name != "manifest.json").Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new BackupRefusedException("The backup does not contain exactly the files its manifest lists (tampered or incomplete).");
            foreach (var entry in zip.Entries.Where(entry => entry.FullName != "manifest.json"))
            {
                var (sha, bytes) = listed[entry.FullName];
                var destination = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long copied = 0;
                using (var input = entry.Open())
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        copied += read;
                        if (copied > bytes) throw new BackupRefusedException("A file in the backup is larger than its manifest says (tampered).");
                        hash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read);
                    }
                }
                if (copied != bytes || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha, StringComparison.Ordinal))
                    throw new BackupRefusedException("A file in the backup does not match its manifest checksum (" + entry.FullName + "); the backup was changed or damaged.");
            }
            foreach (var item in BackupDatabases.Files)
                if (!listed.ContainsKey(item.Entry)) throw new BackupRefusedException("The backup is missing " + item.Name + ".");
            return manifest;
        }
    }

    /// <summary>
    /// SET-8 version check (PIPE-4): a newer format is refused with "Version mismatch: version X required"; older
    /// formats of this file kind are upgraded (their databases are migrated after the restore).
    /// </summary>
    public static void CheckManifest(JsonObject manifest)
    {
        if (manifest["format"] is not JsonValue format || format.GetValueKind() != JsonValueKind.String || format.GetValue<string>() != FormatName)
            throw new BackupRefusedException("This file is not an AutoSourcarr instance backup.");
        static int Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : -1;
        var version = Number(manifest["format_version"]); var reader = Number(manifest["min_reader_version"]);
        var writer = manifest["app_version"] is JsonValue app && app.TryGetValue<string>(out var text) && text.Length is > 0 and <= 64 ? text : "newer";
        if (version < 1 || reader < 1) throw new BackupRefusedException("The backup manifest has no valid format version.");
        if (version > FormatVersion || reader > FormatVersion)
            throw new BackupRefusedException(CatalogueFormatMessage(writer) + $" This backup uses format {version}; this AutoSourcarr reads up to format {FormatVersion}.");
        if (version < 3)
            throw new BackupRefusedException($"Backup format {version} is from AutoSourcarr 0.4.x; restore it with that version, then update.");
        if (manifest["contents"] is not JsonArray || manifest["schema"] is not JsonObject)
            throw new BackupRefusedException("The backup manifest is incomplete.");
    }

    private static Dictionary<string, DatabaseFacts> CheckDatabases(string target, JsonObject manifest)
    {
        var writer = manifest["app_version"]?.GetValue<string>() ?? "newer";
        var result = new Dictionary<string, DatabaseFacts>(StringComparer.Ordinal);
        foreach (var item in BackupDatabases.Files)
        {
            var facts = BackupDatabases.Check(Path.Combine(target, "databases", item.Name), item, writer);
            var listed = manifest["schema"]?[item.File == DatabaseFile.Catalog ? "catalog" : "origins"]?["rows"] as JsonObject;
            if (listed is null || listed.Count != facts.Rows.Count || facts.Rows.Any(pair => listed[pair.Key]?.GetValue<long>() != pair.Value))
                throw new BackupRefusedException($"{item.Name} in the backup does not match the row counts of its manifest.");
            result[item.Name] = facts;
        }
        return result;
    }

    private string CurrentConfigDigest()
    {
        var path = Path.Combine(ConfigurationRoot, ConfigStore.FileName);
        return File.Exists(path) ? Sha256File(path) : "none";
    }

    private JsonObject Preview(string id, string target, JsonObject manifest, Dictionary<string, DatabaseFacts> facts, string source)
    {
        var databases = new JsonArray();
        var paths = _database.Paths;
        var upgrade = false;
        foreach (var item in BackupDatabases.Files)
        {
            var backup = facts[item.Name];
            var live = File.Exists(BackupDatabases.PathOf(paths, item.File)) ? BackupDatabases.Facts(BackupDatabases.PathOf(paths, item.File), integrity: false) : null;
            upgrade |= backup.Migration < SchemaMigrator.LatestMigration(item.File);
            databases.Add(new JsonObject
            {
                ["name"] = item.Name, ["backup_migration"] = backup.Migration, ["current_migration"] = live?.Migration,
                ["tables"] = new JsonArray(KeyTables.Where(table => backup.Rows.ContainsKey(table) || live?.Rows.ContainsKey(table) == true)
                    .Select(table => (JsonNode?)new JsonObject
                    {
                        ["table"] = table, ["current"] = live?.Rows.TryGetValue(table, out var now) == true ? now : null,
                        ["backup"] = backup.Rows.TryGetValue(table, out var then) ? then : null,
                    }).ToArray()),
            });
        }
        var notIncluded = (manifest["not_included"] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? "").Where(name => name.Length > 0).ToList();
        JsonObject settings;
        var configFile = Path.Combine(target, "settings", "config.json");
        if (File.Exists(configFile))
        {
            var backupConfig = BackupSettings.Parse(File.ReadAllBytes(configFile));
            ConfigDocument? current;
            lock (SettingsEditor.Gate(ConfigurationRoot)) current = new ConfigStore(ConfigurationRoot).ReadDocument();
            var merged = BackupSettings.Merge(backupConfig, current);
            settings = new JsonObject
            {
                ["included"] = true, ["changed"] = Names(merged.ChangedKeys), ["connections_added"] = Names(merged.AddedConnections),
                ["connections_kept"] = Names(merged.KeptConnections), ["secrets_kept_from_this_instance"] = Names(merged.KeptSecrets),
                ["secrets_to_enter_again"] = Names(BackupSettings.Missing(notIncluded, merged)),
            };
        }
        else settings = new JsonObject { ["included"] = false, ["changed"] = new JsonArray() };
        var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id + "|" + Sha256File(Path.Combine(target, "manifest.json")) + "|" + CurrentConfigDigest())));
        var writer = manifest["app_version"]?.GetValue<string>() ?? "";
        return new JsonObject
        {
            ["stage_id"] = id, ["preview_token"] = token, ["source"] = source,
            ["backup"] = new JsonObject
            {
                ["created_at"] = manifest["created_at"]?.DeepClone(), ["app_version"] = writer, ["kind"] = manifest["kind"]?.DeepClone(),
                ["reason"] = manifest["reason"]?.DeepClone(), ["format_version"] = manifest["format_version"]?.DeepClone(),
                ["compatibility"] = manifest["compatibility"]?.DeepClone(),
            },
            ["version_check"] = new JsonObject
            {
                ["ok"] = true, ["upgrade"] = upgrade,
                ["message"] = upgrade ? "This backup has an older database schema; its databases are upgraded right after the restore."
                    : "This backup matches this version's database schema.",
            },
            ["integrity"] = "ok",
            ["databases"] = databases, ["settings"] = settings,
            ["share_export"] = (manifest["contents"] as JsonArray ?? []).Any(row => row?["path"]?.GetValue<string>().StartsWith("catalogue/", StringComparison.Ordinal) == true),
            ["not_included"] = new JsonArray(notIncluded.Select(name => (JsonNode?)name).ToArray()),
            ["always_excluded"] = manifest["always_excluded"]?.DeepClone() ?? new JsonArray(),
            ["effect"] = "The restore replaces both databases (catalogue, manual decisions, chapter provenance, profiles) and the settings with the backup's. "
                + "Background work stops, a pre-restore backup is kept, and any failure rolls this instance back. Secrets of this instance are kept.",
            ["restart_required"] = true,
        };
    }

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(name => (JsonNode?)name).ToArray());

    // ---- apply ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies a staged backup (hard rule 1: only with its preview token; the caller asked for confirm and stopped
    /// background work). Pre-restore backup → swap both databases → verify rows/stamp/integrity → migrate older
    /// schemas → write merged settings. Any failure restores the pre-restore state and throws.
    /// </summary>
    public JsonObject Apply(string stageId, string token, Action<string>? report = null)
    {
        if (!ValidId(stageId)) throw new BackupRefusedException("Unknown staged backup; preview it again.", 409);
        lock (_gate)
        {
            var target = Path.Combine(Staged, stageId);
            var stagePath = Path.Combine(target, "stage.json");
            if (!File.Exists(stagePath)) throw new BackupRefusedException("This staged backup is gone; preview it again.", 409);
            var stage = JsonNode.Parse(File.ReadAllText(stagePath))!.AsObject();
            var manifestBytes = File.ReadAllBytes(Path.Combine(target, "manifest.json"));
            var manifest = JsonNode.Parse(manifestBytes)!.AsObject();
            var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stageId + "|" + Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) + "|" + CurrentConfigDigest())));
            if (!string.Equals(stage["token"]?.GetValue<string>(), token, StringComparison.Ordinal) || !string.Equals(expected, token, StringComparison.Ordinal))
                throw new BackupRefusedException("The settings or the staged backup changed since the preview; open the preview again.", 409);
            // The staged files are checked again: nothing may have changed them since the preview.
            foreach (var row in manifest["contents"]!.AsArray().OfType<JsonObject>())
            {
                var file = Path.Combine(target, row["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file) || Sha256File(file) != row["sha256"]!.GetValue<string>())
                    throw new BackupRefusedException("A staged backup file changed since the preview; open the preview again.", 409);
            }
            var facts = CheckDatabases(target, manifest);
            var configFile = Path.Combine(target, "settings", "config.json");
            var configPath = Path.Combine(ConfigurationRoot, ConfigStore.FileName);
            BackupSettings.Merged? merged = null;
            if (File.Exists(configFile))
            {
                ConfigDocument? current;
                lock (SettingsEditor.Gate(ConfigurationRoot)) current = new ConfigStore(ConfigurationRoot).ReadDocument();
                merged = BackupSettings.Merge(BackupSettings.Parse(File.ReadAllBytes(configFile)), current);
                // The merged file must open with this instance's config.key before anything is replaced.
                new ConfigStore(ConfigurationRoot).Load(merged.Document);
            }

            report?.Invoke("Keeping a backup of the current state");
            var before = Create("pre-restore", "Before restoring the backup from " + (manifest["created_at"]?.GetValue<string>() ?? "?"));
            var oldConfig = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
            try
            {
                report?.Invoke("Replacing both databases");
                _database.Quiescent(paths =>
                {
                    foreach (var item in BackupDatabases.Files)
                        BackupDatabases.Place(Path.Combine(target, "databases", item.Name), BackupDatabases.PathOf(paths, item.File));
                    foreach (var item in BackupDatabases.Files)
                    {
                        var placed = BackupDatabases.Facts(BackupDatabases.PathOf(paths, item.File));
                        if (!placed.Intact || !placed.SameData(facts[item.Name]))
                            throw new BackupRefusedException(item.Name + " did not verify after it was put in place.", 409);
                        BackupDatabases.UseWal(BackupDatabases.PathOf(paths, item.File));
                    }
                    new SchemaMigrator(_database).Initialize();
                    foreach (var item in BackupDatabases.Files)
                    {
                        var migrated = BackupDatabases.Facts(BackupDatabases.PathOf(paths, item.File));
                        if (!migrated.Intact || migrated.Migration != SchemaMigrator.LatestMigration(item.File))
                            throw new BackupRefusedException(item.Name + " did not pass the check after its upgrade.", 409);
                    }
                    return 0;
                }, TimeSpan.FromSeconds(120));
                if (merged is not null)
                {
                    report?.Invoke("Restoring the settings");
                    lock (SettingsEditor.Gate(ConfigurationRoot)) new ConfigStore(ConfigurationRoot).WriteDocument(merged.Document);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                report?.Invoke("The restore failed; rolling back");
                RestoreDatabases(before.Id, migrate: true);
                if (oldConfig is not null) lock (SettingsEditor.Gate(ConfigurationRoot)) ConfigFiles.WriteAtomic(configPath, oldConfig);
                throw new BackupRefusedException("The restore failed and this instance was rolled back to its state before the restore: " + error.Message, 409, error);
            }
            TryDeleteDirectory(target);
            return new JsonObject
            {
                ["restored"] = true, ["backup_created_at"] = manifest["created_at"]?.DeepClone(), ["pre_restore_backup"] = before.Id,
                ["settings_restored"] = merged is not null,
                ["secrets_kept_from_this_instance"] = Names(merged?.KeptSecrets ?? []),
                ["entries"] = facts["catalog.sqlite"].Rows.GetValueOrDefault("entries"),
                ["restart_required"] = true,
                ["message"] = "Restored. Restart AutoSourcarr so every part uses the restored data and settings.",
            };
        }
    }

    /// <summary>
    /// Puts the databases of a saved (unprotected) backup back in place in one drained lease: the rollback of a failed
    /// restore (then migrated again) and of a failed update (SET-9, not migrated). Settings are not touched.
    /// </summary>
    public IReadOnlyDictionary<string, DatabaseFacts> RestoreDatabases(string backupId, bool migrate)
    {
        var plain = Open(File.ReadAllBytes(FilePath(backupId)), null);
        var work = Path.Combine(Root, ".rollback-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(work);
        try
        {
            var manifest = Extract(plain, work);
            var facts = CheckDatabases(work, manifest);
            _database.Quiescent(paths =>
            {
                foreach (var item in BackupDatabases.Files)
                    BackupDatabases.Place(Path.Combine(work, "databases", item.Name), BackupDatabases.PathOf(paths, item.File));
                foreach (var item in BackupDatabases.Files) BackupDatabases.UseWal(BackupDatabases.PathOf(paths, item.File));
                if (migrate) new SchemaMigrator(_database).Initialize();
                return 0;
            }, TimeSpan.FromSeconds(120));
            return facts;
        }
        finally { TryDeleteDirectory(work); }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var temporary = path + ".pending";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
        Private(path);
    }

    private static void Private(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
