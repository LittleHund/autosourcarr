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
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Backup;

/// <summary>One saved backup as the list shows it (no paths, no secrets).</summary>
public sealed record BackupSummary(string Id, string CreatedAt, string AppVersion, string Kind, string? Reason, long Bytes, bool Protected,
    int Files, long Entries, long Chapters, bool ShareExport, int FormatVersion, long CatalogMigration = 0, long OriginsMigration = 0)
{
    /// <summary>The databases are at this build's schema: restoring needs no migration.</summary>
    public bool SchemaMatchesBuild => CatalogMigration == SchemaMigrator.LatestMigration(DatabaseFile.Catalog)
        && OriginsMigration == SchemaMigrator.LatestMigration(DatabaseFile.Origins);

    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["created_at"] = CreatedAt, ["app_version"] = AppVersion, ["kind"] = Kind, ["reason"] = Reason, ["bytes"] = Bytes,
        ["protected"] = Protected, ["files"] = Files, ["entries"] = Entries, ["chapters"] = Chapters, ["share_export"] = ShareExport,
        ["format_version"] = FormatVersion, ["catalog_migration"] = CatalogMigration, ["origins_migration"] = OriginsMigration,
        ["schema_matches_build"] = SchemaMatchesBuild,
    };

    public static BackupSummary? FromJson(JsonObject? row)
    {
        if (row is null) return null;
        try
        {
            return new(row["id"]!.GetValue<string>(), row["created_at"]!.GetValue<string>(), row["app_version"]?.GetValue<string>() ?? "",
                row["kind"]?.GetValue<string>() ?? "manual", row["reason"]?.GetValue<string>(), row["bytes"]?.GetValue<long>() ?? 0,
                row["protected"]?.GetValue<bool>() ?? false, row["files"]?.GetValue<int>() ?? 0, row["entries"]?.GetValue<long>() ?? 0,
                row["chapters"]?.GetValue<long>() ?? 0, row["share_export"]?.GetValue<bool>() ?? false, row["format_version"]?.GetValue<int>() ?? 0,
                row["catalog_migration"]?.GetValue<long>() ?? 0, row["origins_migration"]?.GetValue<long>() ?? 0);
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
/// Optionally password protected (<see cref="BackupCrypto"/>, container ASCBK4). Files stream through disk; nothing holds
/// a whole backup in memory. config.key, accounts, tokens, cookies, signing keys, source-host data, downloads, covers
/// and logs are never included.
/// <para>A restore is previewed (what changes, version check, preview token over the staged manifest, config.json and
/// the saved manual decisions), then armed. It is applied at the next start, before any database is opened (the 0.4.5
/// design): a pre-restore backup, then <see cref="DatabaseSwap"/> replaces both databases and config.json with a journal,
/// so a crash mid-swap is undone on the following start. Older schemas are migrated afterwards by the update check.</para>
/// </summary>
public sealed partial class InstanceBackupStore
{
    public const string FormatName = "autosourcarr-instance-backup";
    public const int FormatVersion = 3;
    public const int MinReaderVersion = 3;
    /// <summary>Largest total a staged backup may declare (its decoded files); free space is checked as well.</summary>
    public const long MaxDeclaredBytes = 64L << 30;
    /// <summary>Largest upload the restore preview accepts.</summary>
    public const long MaxUploadBytes = 64L << 30;
    public const int ManualLimit = 10;
    public const int AutomaticKeep = 3;
    public const string Extension = ".ascbackup";
    public const string PendingFile = "restore-pending.json";
    public const string ResultFile = "restore-result.json";
    private const long FreeSpaceMargin = 256L << 20;
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

    /// <summary>The saved decisions a preview token covers (hard rule 1): a change after the preview invalidates it.</summary>
    private static readonly (string Table, string Filter)[] DecisionTables =
    [
        ("manual_match_groups", ""), ("manual_match_bindings", ""), ("manual_separate_records", ""), ("preferred_title_overrides", ""),
        ("manual_title_scopes", ""), ("duplicate_reviews", ""), ("import_match_rejections", ""), ("mixed_entry_reviews", ""),
        ("source_matches", " WHERE match_type='manual' OR locked=1"), ("import_manual_autoresolve", ""), ("catalogue_decisions", ""),
    ];

    /// <summary>Chapter provenance decisions in chapter-origins.sqlite that a preview token covers too.</summary>
    private static readonly (string Table, string Filter)[] OriginDecisionTables =
        [("chapter_candidate_rejections", ""), ("served_origin", ""), ("origin_history", "")];

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
        // 0.5.3: a backup interrupted by a kill leaves its work folder; nothing runs yet at construction, so every one is stale.
        foreach (var stale in Directory.EnumerateDirectories(Root, ".work-*")) TryDeleteDirectory(stale);
    }

    public string Root { get; }
    public string ConfigurationRoot { get; }
    public string AppVersion { get; }
    public SqliteDatabase Database => _database;
    /// <summary>Removes secrets and absolute paths from error texts kept in markers and notices (the runtime sets SupportPrivacy.Text).</summary>
    public Func<string?, string> Scrub { get; set; } = text => text ?? "";
    public string Staged => Path.Combine(Root, "staged");
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
            if (PendingRestore()?["backup_id"]?.GetValue<string>() == id)
                throw new BackupRefusedException("This backup is armed for a restore; cancel the restore first.", 409);
            File.Delete(path);
            File.Delete(Path.Combine(Root, id + ".json"));
        }
    }

    // ---- create ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Takes a backup. <paramref name="kind"/> is manual, automatic (before an update/migration, SET-9) or pre-restore.
    /// The share export runs first (it records portable ids); then both databases are snapshotted in one drained lease.
    /// Everything streams through files in a work folder; a full disk answers 507 and leaves nothing behind.
    /// </summary>
    /// <param name="cancellationToken">0.5.3: a manual backup can be cancelled (and is, when the app stops); it is checked
    /// between export pages and before the databases are copied, so nothing half-written is kept.</param>
    public BackupSummary Create(string kind, string? reason = null, string? passphrase = null, Func<long, JsonObject>? sharePage = null,
        TimeSpan? drain = null, Action<string>? report = null, CancellationToken cancellationToken = default)
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
            try { return CreateIn(work, id, kind, reason, passphrase, sharePage, drain, report, cancellationToken); }
            catch (IOException error) when (error is not FileNotFoundException and not DirectoryNotFoundException)
            { throw new BackupRefusedException("The backup could not be written (" + error.Message + "). Check the free space of the data folder.", 507, error); }
            finally { TryDeleteDirectory(work); }
        }
    }

    private BackupSummary CreateIn(string work, string id, string kind, string? reason, string? passphrase, Func<long, JsonObject>? sharePage,
        TimeSpan? drain, Action<string>? report, CancellationToken cancellationToken)
    {
        var contents = new List<(string Entry, string File)>();
        var notes = new List<string>();
        var share = 0;
        if (sharePage is not null)
        {
            report?.Invoke("Exporting the portable catalogue");
            long after = 0; var complete = false;
            while (share < MaxSharePages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = sharePage(after);
                share++;
                report?.Invoke("Exporting the portable catalogue: page " + share.ToString(CultureInfo.InvariantCulture));
                var name = "share-" + share.ToString("D4", CultureInfo.InvariantCulture) + ".json";
                var file = Path.Combine(work, name);
                // A page without its catalogue is not an empty catalogue: the export is incomplete and says so.
                if (page["catalogue"] is not JsonObject catalogue) throw new InvalidDataException("The portable catalogue export page " + share.ToString(CultureInfo.InvariantCulture) + " has no catalogue; the backup was not made.");
                File.WriteAllText(file, catalogue.ToJsonString(), Encoding.UTF8);
                contents.Add(("catalogue/" + name, file));
                if (page["next_cursor"] is JsonValue next && next.TryGetValue<long>(out var cursor) && cursor > after) after = cursor;
                else { complete = true; break; }
            }
            if (!complete)
                notes.Add("The portable catalogue share export stopped after " + MaxSharePages.ToString(CultureInfo.InvariantCulture)
                    + " pages and is incomplete in this " + kind + " backup; the databases are complete.");
        }
        else notes.Add("The portable catalogue share export is not included in this " + kind + " backup; the databases are.");

        cancellationToken.ThrowIfCancellationRequested();
        report?.Invoke("Copying both databases");
        var facts = new Dictionary<string, DatabaseFacts>(StringComparer.Ordinal);
        _database.Quiescent(paths =>
        {
            foreach (var item in BackupDatabases.Files)
            {
                var source = BackupDatabases.PathOf(paths, item.File);
                if (!File.Exists(source)) continue; // backed up with what exists (a fresh instance has no chapter origins yet)
                BackupDatabases.Snapshot(source, Path.Combine(work, item.Name));
            }
            return 0;
        }, drain ?? TimeSpan.FromSeconds(120));
        if (!File.Exists(Path.Combine(work, "catalog.sqlite")))
            throw new BackupRefusedException("catalog.sqlite does not exist; there is nothing to back up.", 409);
        foreach (var item in BackupDatabases.Files)
        {
            var copy = Path.Combine(work, item.Name);
            if (!File.Exists(copy)) { notes.Add(item.Name + " did not exist yet; it is not in this backup (a restore removes it)."); continue; }
            if (item.File == DatabaseFile.Catalog) BackupDatabases.ScrubSecrets(copy);
            var fact = BackupDatabases.Facts(copy);
            if (!fact.Intact) throw new BackupRefusedException(item.Name + " failed SQLite's integrity check; no backup was written.", 409);
            facts[item.Name] = fact;
            contents.Add((item.Entry, copy));
        }

        var profiles = Path.Combine(work, "profiles.json");
        File.WriteAllText(profiles, new JsonObject
        {
            ["format"] = "autosourcarr-profiles", ["profile_format"] = CompatibilityList.Current.ProfileFormat,
            ["tables"] = BackupDatabases.TableRows(Path.Combine(work, "catalog.sqlite"), ProfileTables),
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

        var manifest = new JsonObject
        {
            ["format"] = FormatName, ["format_version"] = FormatVersion, ["min_reader_version"] = MinReaderVersion,
            ["app_version"] = AppVersion, ["created_at"] = Now(), ["kind"] = kind, ["reason"] = reason,
            ["compatibility"] = CompatibilityList.Current.ToJson(),
            ["schema"] = new JsonObject { ["catalog"] = facts["catalog.sqlite"].ToJson(), ["origins"] = facts.GetValueOrDefault("chapter-origins.sqlite")?.ToJson() },
            ["contents"] = new JsonArray(contents.OrderBy(item => item.Entry, StringComparer.Ordinal).Select(item => (JsonNode?)new JsonObject
            {
                ["path"] = item.Entry, ["sha256"] = Sha256File(item.File), ["bytes"] = new FileInfo(item.File).Length,
            }).ToArray()),
            ["not_included"] = Names(notIncluded),
            ["always_excluded"] = Names(AlwaysExcluded),
            ["notes"] = Names(notes),
        };

        report?.Invoke("Writing the backup file");
        var archive = Path.Combine(work, "backup.zip");
        using (var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open())
                output.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            foreach (var (name, file) in contents.OrderBy(item => item.Entry, StringComparer.Ordinal))
                zip.CreateEntryFromFile(file, name, CompressionLevel.Fastest);
        }
        var target = Path.Combine(Root, id + Extension);
        var pending = target + ".pending";
        if (passphrase is not null)
        {
            using (var input = File.OpenRead(archive))
            using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                BackupCrypto.Encrypt(input, output, passphrase);
                output.Flush(flushToDisk: true);
            }
        }
        else
        {
            using (var stream = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(flushToDisk: true);
            File.Move(archive, pending);
        }
        File.Move(pending, target, overwrite: true);
        Private(target);
        var summary = new BackupSummary(id, manifest["created_at"]!.GetValue<string>(), AppVersion, kind, reason, new FileInfo(target).Length, passphrase is not null,
            contents.Count, facts["catalog.sqlite"].Rows.GetValueOrDefault("entries"), facts.GetValueOrDefault("chapter-origins.sqlite")?.Rows.GetValueOrDefault("virtual_chapters") ?? 0,
            share > 0, FormatVersion, facts["catalog.sqlite"].Migration, facts.GetValueOrDefault("chapter-origins.sqlite")?.Migration ?? SchemaMigrator.LatestMigration(DatabaseFile.Origins));
        WriteAtomic(Path.Combine(Root, id + ".json"), Encoding.UTF8.GetBytes(summary.ToJson().ToJsonString()));
        Prune(kind);
        return summary;
    }

    /// <summary>Automatic and pre-restore backups keep the newest <see cref="AutomaticKeep"/> of their kind.</summary>
    private void Prune(string kind)
    {
        if (kind == "manual") return;
        var marker = UpgradeGuard.Marker(this);
        var named = new HashSet<string?>(StringComparer.Ordinal)
        {
            UpgradeGuard.Text(PendingRestore(), "backup_id"), UpgradeGuard.Text(marker, "backup_id"), UpgradeGuard.Text(marker, "retry_backup_id"),
            UpgradeGuard.Text(UpgradeGuard.ReadState(this)["update"] as JsonObject, "backup_id"),
        };
        // The newest AutomaticKeep stay, plus every backup a marker (the clean state before a failed update), the update
        // notice or an armed restore names.
        foreach (var old in List().Where(row => row.Kind == kind).Skip(AutomaticKeep).Where(row => !named.Contains(row.Id)))
        {
            TryDelete(Path.Combine(Root, old.Id + Extension));
            TryDelete(Path.Combine(Root, old.Id + ".json"));
        }
    }

    // ---- read / stage / preview -------------------------------------------------------------------------------

    private static string CatalogueFormatMessage(string version) => Catalogue.Sharing.CatalogueFormat.MismatchMessage(version) + ".";

    /// <summary>A fresh file name under staged/ for an upload the route streams to disk.</summary>
    public string UploadPath()
    {
        Directory.CreateDirectory(Staged);
        return Path.Combine(Staged, ".upload-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
    }

    /// <summary>
    /// Reads a backup file into a fresh staging folder, checks every content hash, the format and schema versions,
    /// and answers what a restore would change with its preview token. Only the newest staged backup is kept.
    /// With <paramref name="consume"/> the file (an upload) is moved or deleted; otherwise it is only read.
    /// </summary>
    public JsonObject Stage(string file, string? passphrase, string source, bool consume)
    {
        lock (_gate)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var target = Path.Combine(Staged, id);
            Directory.CreateDirectory(target);
            try
            {
                var container = BackupCrypto.Detect(file);
                var zipPath = file;
                switch (container)
                {
                    case BackupContainer.Legacy2:
                        throw new BackupRefusedException("This is an AutoSourcarr 0.4.x instance backup (format 2). Restore it with the 0.4.x version that made it, then update; "
                            + CatalogueFormatMessage("0.4.5"));
                    case BackupContainer.Unknown:
                        throw new BackupRefusedException("This file is not an AutoSourcarr backup.");
                    case BackupContainer.Protected3 or BackupContainer.Protected4:
                        zipPath = Path.Combine(target, "backup.zip");
                        RequireSpace(target, new FileInfo(file).Length);
                        BackupCrypto.Decrypt(file, zipPath, passphrase);
                        if (consume) TryDelete(file);
                        break;
                    default:
                        if (consume) { zipPath = Path.Combine(target, "backup.zip"); File.Move(file, zipPath); }
                        break;
                }
                var manifest = Extract(zipPath, target);
                if (!string.Equals(zipPath, file, StringComparison.Ordinal)) TryDelete(zipPath);
                var facts = CheckDatabases(target, manifest);
                var preview = Preview(id, target, manifest, facts, source);
                File.WriteAllText(Path.Combine(target, "stage.json"), new JsonObject
                {
                    ["id"] = id, ["token"] = preview["preview_token"]!.GetValue<string>(), ["source"] = source, ["created_at"] = Now(),
                    ["backup_id"] = source.StartsWith("backup:", StringComparison.Ordinal) ? source["backup:".Length..] : null,
                }.ToJsonString());
                var armed = PendingRestore()?["stage_id"]?.GetValue<string>();
                foreach (var old in Directory.EnumerateDirectories(Staged).Where(path => Path.GetFileName(path) is var name && name != id && name != armed))
                    TryDeleteDirectory(old);
                return preview;
            }
            catch
            {
                TryDeleteDirectory(target);
                if (consume) TryDelete(file);
                throw;
            }
        }
    }

    /// <summary>Stages one of the saved backups (read in place, never copied whole into memory).</summary>
    public JsonObject StageSaved(string backupId, string? passphrase) => Stage(FilePath(backupId), passphrase, "backup:" + backupId, consume: false);

    /// <summary>Test and tool helper: stages bytes through a temporary upload file.</summary>
    public JsonObject Stage(byte[] data, string? passphrase, string source)
    {
        var upload = UploadPath();
        File.WriteAllBytes(upload, data);
        return Stage(upload, passphrase, source, consume: true);
    }

    private static void RequireSpace(string folder, long bytes)
    {
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { return; }
        if (free < bytes + FreeSpaceMargin)
            throw new BackupRefusedException($"Not enough free space to stage this backup ({bytes / 1048576} MiB needed, {free / 1048576} MiB free).", 507);
    }

    private static JsonObject Extract(string zipPath, string target)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
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
                if (buffer.Length > 8 * 1024 * 1024) throw new BackupRefusedException("The backup manifest is too large.");
                File.WriteAllBytes(Path.Combine(target, "manifest.json"), buffer.ToArray());
                try { manifest = JsonNode.Parse(buffer.ToArray()) as JsonObject ?? throw new BackupRefusedException("The backup manifest is not an object."); }
                catch (JsonException error) { throw new BackupRefusedException("The backup manifest is not valid JSON.", 400, error); }
            }
            CheckManifest(manifest);
            var listed = new Dictionary<string, (string Sha, long Bytes)>(StringComparer.Ordinal);
            foreach (var row in manifest["contents"]!.AsArray().OfType<JsonObject>())
            {
                var path = row["path"]?.GetValue<string>() ?? "";
                var bytes = row["bytes"]?.GetValue<long>() ?? -1;
                if (!EntryPattern().IsMatch(path) || path == "manifest.json" || bytes < 0 || !listed.TryAdd(path, (row["sha256"]?.GetValue<string>() ?? "", bytes)))
                    throw new BackupRefusedException("The backup manifest lists an invalid file.");
            }
            if (!listed.Keys.Order(StringComparer.Ordinal).SequenceEqual(names.Where(name => name != "manifest.json").Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new BackupRefusedException("The backup does not contain exactly the files its manifest lists (tampered or incomplete).");
            // Declared sizes are capped and must match the zip's own sizes before anything is written (no disk fill).
            var declared = listed.Values.Sum(item => item.Bytes);
            if (declared > MaxDeclaredBytes) throw new BackupRefusedException("The backup declares more than 64 GiB of content.", 413);
            foreach (var entry in zip.Entries.Where(entry => entry.FullName != "manifest.json"))
                if (entry.Length != listed[entry.FullName].Bytes) throw new BackupRefusedException("A file in the backup does not have the size its manifest says (tampered).");
            RequireSpace(target, declared);
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
                if (!listed.ContainsKey(item.Entry) && (item.File == DatabaseFile.Catalog || manifest["schema"]?["origins"] is not null))
                    throw new BackupRefusedException("The backup is missing " + item.Name + ".");
            return manifest;
        }
    }

    /// <summary>
    /// SET-8 version check (PIPE-4): a newer format is refused with "Version mismatch: version X required"; older
    /// databases of this format are upgraded by the update check after the restore.
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
            var path = Path.Combine(target, "databases", item.Name);
            if (item.File == DatabaseFile.Origins && !File.Exists(path) && manifest["schema"]?["origins"] is null) continue; // not in this backup
            var facts = BackupDatabases.Check(path, item, writer);
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

    private static SqliteConnection ReadOnly(string path) => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>A digest of the saved manual decisions and the decision log of the live catalogue (preview token part).</summary>
    public string DecisionDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Digest(hash, _database.Paths.Catalog, [.. DecisionTables, ("decision_log", "")]);
        Digest(hash, _database.Paths.Origins, OriginDecisionTables);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Digest(IncrementalHash hash, string path, IEnumerable<(string Table, string Filter)> tables)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\n"));
        if (!File.Exists(path)) return;
        using var connection = ReadOnly(path);
        connection.Open();
        foreach (var (table, filter) in tables)
        {
            if (!HasTable(connection, table)) continue;
            using var command = connection.CreateCommand();
            command.CommandText = table == "decision_log" ? "SELECT COUNT(*), MAX(id) FROM decision_log" : "SELECT * FROM \"" + table + "\"" + filter + " ORDER BY 1,2";
            using var reader = command.ExecuteReader();
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\n"));
            while (reader.Read())
            {
                for (var index = 0; index < reader.FieldCount; index++)
                    hash.AppendData(Encoding.UTF8.GetBytes((reader.IsDBNull(index) ? "\u0000" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)) + "\u001f"));
                hash.AppendData("\n"u8);
            }
        }
    }

    /// <summary>Row counts of the live chapter provenance (what a catalogue-only restore replaces by an empty database).</summary>
    private JsonObject? LiveProvenance()
    {
        var path = _database.Paths.Origins;
        if (!File.Exists(path)) return null;
        using var connection = ReadOnly(path);
        connection.Open();
        var result = new JsonObject();
        foreach (var table in new[] { "served_origin", "chapter_candidate_rejections", "origin_history", "virtual_chapters" })
        {
            long count = 0;
            if (HasTable(connection, table))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM \"" + table + "\"";
                count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            result[table] = count;
        }
        return result;
    }

    /// <summary>Decisions made by a person (not automatic) after <paramref name="since"/>: a restore loses them.</summary>
    private long DecisionsSince(string? since)
    {
        var path = _database.Paths.Catalog;
        if (since is null || !File.Exists(path)) return 0;
        using var connection = ReadOnly(path);
        connection.Open();
        if (!HasTable(connection, "decision_log")) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM decision_log WHERE actor_kind<>'automatic' AND at>$at";
        command.Parameters.AddWithValue("$at", since);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private string Token(string id, string target) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        id + "|" + Sha256File(Path.Combine(target, "manifest.json")) + "|" + CurrentConfigDigest() + "|" + DecisionDigest())));

    private JsonObject Preview(string id, string target, JsonObject manifest, Dictionary<string, DatabaseFacts> facts, string source)
    {
        var databases = new JsonArray();
        var paths = _database.Paths;
        var upgrade = false;
        foreach (var item in BackupDatabases.Files.Where(item => facts.ContainsKey(item.Name)))
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
        var writer = manifest["app_version"]?.GetValue<string>() ?? "";
        var created = manifest["created_at"]?.GetValue<string>();
        var newer = DecisionsSince(created);
        var provenance = facts.ContainsKey("chapter-origins.sqlite") ? null : LiveProvenance();
        return new JsonObject
        {
            ["stage_id"] = id, ["preview_token"] = Token(id, target), ["source"] = source,
            ["backup"] = new JsonObject
            {
                ["created_at"] = created, ["app_version"] = writer, ["kind"] = manifest["kind"]?.DeepClone(),
                ["reason"] = manifest["reason"]?.DeepClone(), ["format_version"] = manifest["format_version"]?.DeepClone(),
                ["compatibility"] = manifest["compatibility"]?.DeepClone(),
            },
            ["version_check"] = new JsonObject
            {
                ["ok"] = true, ["upgrade"] = upgrade,
                ["message"] = upgrade ? "This backup has an older database schema; after the restore, the next start takes an automatic backup and upgrades it."
                    : "This backup matches this version's database schema.",
            },
            ["integrity"] = "ok",
            ["databases"] = databases, ["settings"] = settings,
            ["decisions_newer_than_backup"] = newer,
            ["decisions_warning"] = newer > 0 ? $"{newer} manual decisions newer than the backup will be lost." : null,
            // Hard rule 1 (chapter provenance): a catalogue-only backup replaces the live origins database by an empty one.
            // The pre-restore backup keeps the live file, and the preview token covers its decision tables.
            ["chapter_provenance"] = provenance,
            ["chapter_provenance_warning"] = provenance is { } held && held.Any(pair => pair.Value?.GetValue<long>() > 0)
                ? $"Chapter provenance: {held["served_origin"]} served origins / {held["chapter_candidate_rejections"]} rejections / {held["origin_history"]} history rows "
                  + $"({held["virtual_chapters"]} chapters) on this instance will be replaced by an empty database — the backup has no chapter origins. "
                  + "The pre-restore backup keeps them."
                : null,
            ["share_export"] = (manifest["contents"] as JsonArray ?? []).Any(row => row?["path"]?.GetValue<string>().StartsWith("catalogue/", StringComparison.Ordinal) == true),
            ["not_included"] = Names(notIncluded),
            ["always_excluded"] = manifest["always_excluded"]?.DeepClone() ?? new JsonArray(),
            ["effect"] = "The restore is applied at the next start, before anything opens the databases: a pre-restore backup is taken, then both databases "
                + "(catalogue, manual decisions, chapter provenance, profiles) and the settings are replaced; a crash or a failed check undoes it. "
                + "Until the restart, changes are refused. Secrets of this instance are kept.",
            ["restart_required"] = true,
        };
    }

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(name => (JsonNode?)name).ToArray());

    // ---- arm / apply at the next start --------------------------------------------------------------------------

    public JsonObject? PendingRestore()
    {
        var path = Path.Combine(Root, PendingFile);
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception error) when (error is JsonException or IOException) { _ = error; return null; }
    }

    /// <summary>
    /// Arms a staged backup for the next start (hard rule 1: only with its preview token, and the token still matches
    /// the staged manifest, config.json and the saved decisions). Nothing is replaced now.
    /// </summary>
    public JsonObject Arm(string stageId, string token)
    {
        if (!ValidId(stageId)) throw new BackupRefusedException("Unknown staged backup; preview it again.", 409);
        lock (_gate)
        {
            var target = Path.Combine(Staged, stageId);
            var stagePath = Path.Combine(target, "stage.json");
            if (!File.Exists(stagePath)) throw new BackupRefusedException("This staged backup is gone; preview it again.", 409);
            var stage = JsonNode.Parse(File.ReadAllText(stagePath))!.AsObject();
            if (!string.Equals(stage["token"]?.GetValue<string>(), token, StringComparison.Ordinal) || !string.Equals(Token(stageId, target), token, StringComparison.Ordinal))
                throw new BackupRefusedException("The settings, the saved decisions or the staged backup changed since the preview; open the preview again.", 409);
            var manifest = Verify(target);
            if (File.Exists(Path.Combine(target, "settings", "config.json")))
            {
                ConfigDocument? current;
                lock (SettingsEditor.Gate(ConfigurationRoot)) current = new ConfigStore(ConfigurationRoot).ReadDocument();
                new ConfigStore(ConfigurationRoot).Load(BackupSettings.Merge(BackupSettings.Parse(File.ReadAllBytes(Path.Combine(target, "settings", "config.json"))), current).Document);
            }
            var pending = new JsonObject
            {
                ["stage_id"] = stageId, ["armed_at"] = Now(), ["backup_created_at"] = manifest["created_at"]?.DeepClone(),
                ["backup_app_version"] = manifest["app_version"]?.DeepClone(), ["source"] = stage["source"]?.DeepClone(), ["backup_id"] = stage["backup_id"]?.DeepClone(),
            };
            WriteAtomic(Path.Combine(Root, PendingFile), Encoding.UTF8.GetBytes(pending.ToJsonString()));
            return pending;
        }
    }

    /// <summary>Cancels an armed restore before the restart.</summary>
    public bool Disarm()
    {
        lock (_gate)
        {
            var path = Path.Combine(Root, PendingFile);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    private static JsonObject Verify(string target)
    {
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(target, "manifest.json")))!.AsObject();
        foreach (var row in manifest["contents"]!.AsArray().OfType<JsonObject>())
        {
            var file = Path.Combine(target, row["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file) || Sha256File(file) != row["sha256"]!.GetValue<string>())
                throw new BackupRefusedException("A staged backup file changed since the preview; open the preview again.", 409);
        }
        CheckDatabases(target, manifest);
        return manifest;
    }

    /// <summary>
    /// Start-time step (before any database is opened and before the update check): finishes or undoes an interrupted
    /// swap, then applies an armed restore. Answers what happened (null when nothing was pending). An armed restore is
    /// marked "applying" before anything changes; a start that finds it so never applies it again: the journal decides
    /// (completed → restored; undone → restore-failed, safe mode). When the swap cannot be finished or undone, every file
    /// stays and the update check starts the safe mode ("swap-pending").
    /// </summary>
    public JsonObject? ApplyAtStart(Action<string>? log = null)
    {
        lock (_gate)
        {
            string? recovered;
            try { recovered = DatabaseSwap.Recover(Root, _database.Paths, ConfigurationRoot); }
            catch (Exception error) when (CrashPoints.Handled(error))
            {
                log?.Invoke("An interrupted database swap could not be finished (" + error.GetType().Name + "); its files are kept.");
                return Result(new JsonObject { ["status"] = "swap-pending", ["error"] = Scrub(error.GetType().Name + ": " + error.Message),
                    ["message"] = "An interrupted database swap could not be finished; AutoSourcarr starts in safe mode." });
            }
            if (recovered is not null) log?.Invoke(recovered);
            var pending = PendingRestore();
            if (pending is null)
                return recovered is null ? null : Result(new JsonObject { ["status"] = "recovered", ["message"] = recovered });
            var stageId = UpgradeGuard.Text(pending, "stage_id") ?? "";
            var target = Path.Combine(Staged, stageId);
            var created = UpgradeGuard.Text(pending, "backup_created_at");
            JsonObject result;
            if (UpgradeGuard.Text(pending, "state") == "applying")
            {
                // The previous start was stopped while applying it: decide from what the journal did, never re-apply.
                var applied = recovered?.Contains("completed", StringComparison.Ordinal) == true
                    || recovered is null && (UpgradeGuard.Text(pending, "swap") == "done" || LiveMatches(target));
                if (applied)
                {
                    UpgradeGuard.NoteRestored(this);
                    result = new JsonObject { ["status"] = "restored", ["backup_created_at"] = created, ["pre_restore_backup"] = pending["pre_restore_backup"]?.DeepClone(),
                        ["message"] = "The backup from " + created + " was restored (the start that applied it was interrupted afterwards)." };
                }
                else
                {
                    UpgradeGuard.WriteMarker(this, new JsonObject
                    {
                        ["status"] = "restore-failed", ["to_version"] = AppVersion, ["backup_id"] = pending["backup_id"]?.DeepClone(), ["at"] = Now(),
                        ["error"] = "The start that applied the restore of the backup from " + created + " was interrupted; the restore was undone.",
                    });
                    result = new JsonObject { ["status"] = "failed", ["backup_created_at"] = created,
                        ["message"] = "The restore of the backup from " + created + " was interrupted and undone; AutoSourcarr starts in safe mode." };
                }
                TryDelete(Path.Combine(Root, PendingFile));
                TryDeleteDirectory(target);
                return Result(result);
            }
            string? before = null;
            try
            {
                if (!ValidId(stageId) || !Directory.Exists(target)) throw new BackupRefusedException("The armed backup is no longer staged.", 409);
                var manifest = Verify(target);
                var facts = CheckDatabases(target, manifest);
                byte[]? config = null;
                var configFile = Path.Combine(target, "settings", "config.json");
                if (File.Exists(configFile))
                {
                    ConfigDocument? current;
                    lock (SettingsEditor.Gate(ConfigurationRoot)) current = new ConfigStore(ConfigurationRoot).ReadDocument();
                    var merged = BackupSettings.Merge(BackupSettings.Parse(File.ReadAllBytes(configFile)), current);
                    new ConfigStore(ConfigurationRoot).Load(merged.Document);
                    config = ConfigStore.Serialize(merged.Document);
                }
                pending["state"] = "applying";
                WriteAtomic(Path.Combine(Root, PendingFile), Encoding.UTF8.GetBytes(pending.ToJsonString()));
                CrashPoints.Reach("restore:applying");
                log?.Invoke("Applying the armed restore of the backup from " + created + ": taking a pre-restore backup.");
                before = Create("pre-restore", "Before restoring the backup from " + created).Id;
                pending["pre_restore_backup"] = before;
                WriteAtomic(Path.Combine(Root, PendingFile), Encoding.UTF8.GetBytes(pending.ToJsonString()));
                CrashPoints.Reach("restore:pre-restore");
                var incoming = BackupDatabases.Files.ToDictionary(item => item.Name,
                    item => facts.ContainsKey(item.Name) ? Path.Combine(target, "databases", item.Name) : null, StringComparer.Ordinal);
                DatabaseSwap.Swap(Root, _database.Paths, ConfigurationRoot, incoming, config, "restore", () =>
                {
                    foreach (var item in BackupDatabases.Files.Where(item => facts.ContainsKey(item.Name)))
                    {
                        var placed = BackupDatabases.Facts(BackupDatabases.PathOf(_database.Paths, item.File));
                        if (!placed.Intact || !placed.SameData(facts[item.Name]))
                            throw new BackupRefusedException(item.Name + " did not verify after it was put in place.", 409);
                        BackupDatabases.UseWal(BackupDatabases.PathOf(_database.Paths, item.File));
                    }
                });
                pending["swap"] = "done";
                WriteAtomic(Path.Combine(Root, PendingFile), Encoding.UTF8.GetBytes(pending.ToJsonString()));
                CrashPoints.Reach("restore:swapped");
                UpgradeGuard.NoteRestored(this);
                result = new JsonObject
                {
                    ["status"] = "restored", ["backup_created_at"] = created, ["pre_restore_backup"] = before, ["settings_restored"] = config is not null,
                    ["message"] = "The backup from " + created + " was restored at this start. A backup of the state before it was kept.",
                };
            }
            catch (Exception error) when (CrashPoints.Handled(error))
            {
                // An undo that failed too (DatabaseSwap keeps its journal) leaves files half-swapped: never claim "as they were".
                var halfSwapped = error is AggregateException || DatabaseSwap.Pending(Root, _database.Paths, ConfigurationRoot);
                result = new JsonObject
                {
                    ["status"] = halfSwapped ? "swap-pending" : "failed", ["backup_created_at"] = created, ["pre_restore_backup"] = before, ["error"] = Scrub(error.GetType().Name + ": " + error.Message),
                    ["message"] = halfSwapped
                        ? "The armed restore of the backup from " + created + " failed and could not be undone completely; the databases are kept as they are and AutoSourcarr starts in safe mode. Restore the backup taken before it, or ask for help."
                        : "The armed restore of the backup from " + created + " was not applied; the databases and settings are as they were.",
                };
                log?.Invoke((halfSwapped ? "The armed restore failed and its undo did not finish: " : "The armed restore failed and was undone: ") + Diagnostics.ErrorText.Of(error));
            }
            TryDelete(Path.Combine(Root, PendingFile));
            TryDeleteDirectory(target);
            return Result(result);
        }
    }

    /// <summary>The live databases hold exactly the staged backup (a swap that finished just before a kill).</summary>
    private bool LiveMatches(string target)
    {
        try
        {
            if (!Directory.Exists(target)) return false;
            var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(target, "manifest.json")))!.AsObject();
            var facts = CheckDatabases(target, manifest);
            return facts.Count > 0 && facts.All(pair => BackupDatabases.Files.First(item => item.Name == pair.Key) is var item
                && File.Exists(BackupDatabases.PathOf(_database.Paths, item.File)) && BackupDatabases.Facts(BackupDatabases.PathOf(_database.Paths, item.File)).SameData(pair.Value));
        }
        catch (Exception error) when (CrashPoints.Handled(error)) { _ = error; return false; }
    }

    private JsonObject Result(JsonObject result)
    {
        result["at"] = Now(); result["dismissed"] = false;
        WriteAtomic(Path.Combine(Root, ResultFile), Encoding.UTF8.GetBytes(result.ToJsonString()));
        return result;
    }

    /// <summary>The result of the last restore at start, until dismissed.</summary>
    public JsonObject? RestoreNotice()
    {
        var path = Path.Combine(Root, ResultFile);
        try { return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject row && row["dismissed"]?.GetValue<bool>() != true ? row : null; }
        catch (Exception error) when (error is JsonException or IOException) { _ = error; return null; }
    }

    public bool DismissRestoreNotice()
    {
        var row = RestoreNotice();
        if (row is null) return false;
        row["dismissed"] = true;
        WriteAtomic(Path.Combine(Root, ResultFile), Encoding.UTF8.GetBytes(row.ToJsonString()));
        return true;
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static void WriteAtomic(string path, byte[] data)
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

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
