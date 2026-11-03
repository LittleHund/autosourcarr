using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Backup;
using AutoSourcarr.Core.Cutover;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Api.Cutover;

/// <summary>Thrown for input the tool refuses (a live mount, a non-empty output folder, a wrong layout).</summary>
internal sealed class MigrationRefusedException(string message) : Exception(message);

/// <summary>What one migration produced.</summary>
internal sealed record MigrationOutcome(ParityReport Report, string DataDirectory, string ConfigDirectory, string? DatabaseDirectory, string ReportPath);

/// <summary>Optional inputs: a Compose file whose environment overrides DATA_DIR / DATABASE_DIR, and the service in it.</summary>
internal sealed record MigrationOptions(string? ComposeFile = null, string? Service = null);

/// <summary>
/// Phase 14 cutover tool: <c>AutoSourcarr.Api --migrate-0.4 &lt;input&gt; &lt;output&gt; [--compose file [--service name]]</c>. The input
/// is a 0.4.5 instance backup file (System → Backups; the passphrase is asked, never stored or logged) or a copied 0.4.x
/// folder holding <c>data/</c> and <c>config/</c> (and <c>database/</c> when DATABASE_DIR was outside DATA_DIR). The input is
/// only read. The output folder (new or empty) gets <c>original-0.4/</c> (the untouched copy the comparison reads),
/// <c>data/</c> and <c>config/</c> (the migrated 0.5.0 folders to mount), <c>database/</c> for an outside DATABASE_DIR, and
/// <c>parity-report.md</c>/<c>.json</c>. A run that stops half-way leaves <c>MIGRATION-INCOMPLETE.txt</c> and no report.
/// </summary>
internal static class LegacyMigrationTool
{
    public const string Usage = "Usage: AutoSourcarr.Api --migrate-0.4 <0.4.5 backup file | folder with data/ and config/> <empty output folder> [--compose <file> [--service <name>]]";
    public const string IncompleteMarker = "MIGRATION-INCOMPLETE.txt";
    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args); ArgumentNullException.ThrowIfNull(output);
        string? compose = null, service = null; var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--compose" && i + 1 < args.Length) compose = args[++i];
            else if (args[i] == "--service" && i + 1 < args.Length) service = args[++i];
            else positional.Add(args[i]);
        }
        if (positional.Count != 2 || positional.Any(string.IsNullOrWhiteSpace) || service is not null && compose is null)
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }
        try
        {
            var outcome = await MigrateAsync(positional[0], positional[1], () => ReadPassphrase(output), output, options: new MigrationOptions(compose, service)).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync(outcome.Report.Verdict).ConfigureAwait(false);
            await output.WriteLineAsync("Report:          " + outcome.ReportPath).ConfigureAwait(false);
            if (!outcome.Report.Ok)
            {
                await output.WriteLineAsync("DO NOT DEPLOY this output. Read the report; your 0.4.x data is unchanged.").ConfigureAwait(false);
                return 1;
            }
            await output.WriteLineAsync("Mount at /data:   " + outcome.DataDirectory).ConfigureAwait(false);
            await output.WriteLineAsync("Mount at /config: " + outcome.ConfigDirectory).ConfigureAwait(false);
            if (outcome.DatabaseDirectory is { } database) await output.WriteLineAsync("Mount at DATABASE_DIR: " + database).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is MigrationRefusedException or BackupRefusedException)
        {
            await output.WriteLineAsync("Refused: " + error.Message).ConfigureAwait(false);
            return 2;
        }
        catch (Exception error) when (Expected(error))
        {
            await output.WriteLineAsync("MIGRATION FAILED (" + error.GetType().Name + "): " + Describe(error)
                + " The input was not changed; the output is incomplete (" + IncompleteMarker + "). DO NOT DEPLOY it.").ConfigureAwait(false);
            return 3;
        }
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
        or InvalidDataException or System.Text.Json.JsonException or AggregateException or FormatException or ConfigurationException
        or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException or NotSupportedException;

    private static string Describe(Exception error) => error is AggregateException aggregate
        ? string.Join(" / ", aggregate.Flatten().InnerExceptions.Select(inner => inner.GetType().Name + ": " + inner.Message)) : error.Message;

    /// <summary>The passphrase without echo; from stdin when it is redirected (scripts). Never stored or logged.</summary>
    private static string ReadPassphrase(TextWriter output)
    {
        output.Write("Passphrase of the 0.4.5 backup (not shown): ");
        output.Flush();
        if (Console.IsInputRedirected) return Console.In.ReadLine() ?? "";
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        output.WriteLine();
        return text.ToString();
    }

    internal static async Task<MigrationOutcome> MigrateAsync(string input, string outputFolder, Func<string> passphrase, TextWriter log,
        CancellationToken cancellationToken = default, Action<string, string>? afterMigration = null, MigrationOptions? options = null,
        Action<JsonObject>? afterStartup = null)
    {
        ArgumentNullException.ThrowIfNull(passphrase); ArgumentNullException.ThrowIfNull(log);
        options ??= new MigrationOptions();
        input = Path.GetFullPath(input); var output = Path.GetFullPath(outputFolder);
        var isFile = File.Exists(input);
        if (!isFile && !Directory.Exists(input)) throw new MigrationRefusedException("The input does not exist: " + input);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new MigrationRefusedException("The output folder is not empty: " + output + ". Choose a new or empty folder.");
        if (File.Exists(output)) throw new MigrationRefusedException("The output path is a file: " + output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!isFile && (Inside(output, input, comparison) || Inside(input, output, comparison)))
            throw new MigrationRefusedException("The output folder must be outside the input folder (and the other way round).");
        if (options.ComposeFile is { } composeFile && !File.Exists(composeFile)) throw new MigrationRefusedException("The Compose file does not exist: " + composeFile);
        if (!isFile)
        {
            RefuseLiveInput(input);
            if (!Directory.Exists(Path.Combine(input, "config")) || !Directory.Exists(Path.Combine(input, "data")))
                throw new MigrationRefusedException("The input folder must contain the copied 0.4.x 'data' and 'config' folders (and 'database' when DATABASE_DIR was outside DATA_DIR).");
        }
        else if (!LegacyBackupReader.IsLegacyBackup(input))
            throw new MigrationRefusedException("This file is not a 0.4.5 instance backup (it must start with ASCBK2). Download it from 0.4.5 System → Backups.");
        var layout = isFile ? null : FolderLayout(input, options);

        Directory.CreateDirectory(output);
        try
        {
            return await MigrateCoreAsync(input, output, isFile, layout, passphrase, log, afterMigration, afterStartup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (MigrationRefusedException or BackupRefusedException) || Directory.EnumerateFileSystemEntries(output).Any())
        {
            // Never leave an output that looks usable: the report is not written, the marker says why.
            try
            {
                foreach (var name in new[] { "parity-report.md", "parity-report.json" }) File.Delete(Path.Combine(output, name));
                File.WriteAllText(Path.Combine(output, IncompleteMarker), "The migration stopped before it finished (" + error.GetType().Name + "): " + Describe(error)
                    + "\nDo not deploy anything from this folder. Your 0.4.x input was only read. Fix the cause and run the tool again into a new, empty folder.\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Where DATA_DIR and DATABASE_DIR pointed in 0.4.x, where the databases are in the copy, and where that came from.</summary>
    private sealed record Layout(string ContainerData, string ContainerDb, string DatabaseFolder, string Source, IReadOnlyDictionary<string, string> Compose);

    private static Layout FolderLayout(string input, MigrationOptions options)
    {
        var env = ReadEnv(Path.Combine(input, "config", EnvConversion.MainEnv));
        string? data = env.GetValueOrDefault("DATA_DIR"), db = env.GetValueOrDefault("DATABASE_DIR");
        var sources = new List<string>();
        IReadOnlyDictionary<string, string> composeEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(data)) sources.Add("DATA_DIR from config/autosourcarr.env");
        if (!string.IsNullOrWhiteSpace(db)) sources.Add("DATABASE_DIR from config/autosourcarr.env");
        if (options.ComposeFile is { } compose)
        {
            var services = ComposeEnvironment.Read(compose);
            Dictionary<string, string>? chosen;
            if (options.Service is { } name)
            {
                if (!services.TryGetValue(name, out chosen)) throw new MigrationRefusedException("The Compose file has no service " + name + ".");
            }
            else
            {
                var relevant = services.Where(pair => pair.Value.ContainsKey("DATA_DIR") || pair.Value.ContainsKey("DATABASE_DIR")).ToList();
                var distinct = relevant.Select(pair => (pair.Value.GetValueOrDefault("DATA_DIR"), pair.Value.GetValueOrDefault("DATABASE_DIR"))).Distinct().Count();
                if (distinct > 1) throw new MigrationRefusedException("The Compose file sets DATA_DIR/DATABASE_DIR differently for " + string.Join(", ", relevant.Select(pair => pair.Key))
                    + ". Pass --service <name> for the instance this snapshot belongs to.");
                chosen = relevant.Select(pair => pair.Value).FirstOrDefault();
            }
            composeEnvironment = chosen ?? new Dictionary<string, string>(StringComparer.Ordinal);
            if (chosen?.GetValueOrDefault("DATA_DIR") is { Length: > 0 } composeData) { data = composeData; sources.Add("DATA_DIR from " + Path.GetFileName(compose) + " (overrides the env file)"); }
            if (chosen?.GetValueOrDefault("DATABASE_DIR") is { Length: > 0 } composeDb) { db = composeDb; sources.Add("DATABASE_DIR from " + Path.GetFileName(compose) + " (overrides the env file)"); }
        }
        var containerData = string.IsNullOrWhiteSpace(data) ? "/data" : data.TrimEnd('/');
        var containerDb = string.IsNullOrWhiteSpace(db) ? containerData : db.TrimEnd('/');
        if (sources.Count == 0) sources.Add("DATA_DIR and DATABASE_DIR not set in the env file (default /data; Compose environment: not read — pass --compose to include it)");
        else if (options.ComposeFile is null) sources.Add("Compose environment: not read (pass --compose <file> if it sets DATA_DIR or DATABASE_DIR)");
        var relative = Relative(containerData, containerDb);
        var databaseFolder = Path.Combine(input, "database");
        var inDatabase = File.Exists(Path.Combine(databaseFolder, "catalog.sqlite"));
        if (relative is null)
        {
            if (!inDatabase) throw new MigrationRefusedException("DATABASE_DIR (" + containerDb + ") is outside DATA_DIR (" + containerData + "): copy that folder into the snapshot as 'database'.");
            if (File.Exists(Path.Combine(input, "data", "catalog.sqlite")))
                throw new MigrationRefusedException("Both 'data' and 'database' hold a catalog.sqlite: it is unclear which one 0.4.x used. Keep only the one in DATABASE_DIR (" + containerDb + ").");
            return new(containerData, containerDb, "database", string.Join("; ", sources), composeEnvironment);
        }
        if (inDatabase)
            throw new MigrationRefusedException("The snapshot has a 'database' folder, but DATABASE_DIR (" + containerDb + ") is inside DATA_DIR (" + containerData + "): it is unclear which databases 0.4.x used. Remove the extra copy.");
        var folder = Path.Combine("data", relative);
        if (!File.Exists(Path.Combine(input, folder, "catalog.sqlite")))
            throw new MigrationRefusedException("The 0.4.x catalog.sqlite was not found in " + folder.Replace('\\', '/') + " (DATABASE_DIR " + containerDb + ").");
        return new(containerData, containerDb, folder, string.Join("; ", sources), composeEnvironment);
    }

    private static async Task<MigrationOutcome> MigrateCoreAsync(string input, string output, bool isFile, Layout? layout, Func<string> passphrase, TextWriter log,
        Action<string, string>? afterMigration, Action<JsonObject>? afterStartup, CancellationToken cancellationToken)
    {
        var original = Path.Combine(output, "original-0.4");
        var data = Path.Combine(output, "data"); var config = Path.Combine(output, "config");
        // 1. The untouched copy the comparison reads (the input itself is only read).
        string originalDb, containerData, containerDb, fromVersion = "0.4.x", pathSource;
        var warnings = new List<string>();
        if (isFile)
        {
            await log.WriteLineAsync("Reading the 0.4.5 backup (this takes a few seconds for the key derivation)...").ConfigureAwait(false);
            var manifest = LegacyBackupReader.Extract(input, passphrase(), original);
            fromVersion = manifest.Version.Length > 0 ? manifest.Version : fromVersion;
            containerData = manifest.Paths.GetValueOrDefault("data") is { Length: > 0 } d ? d.TrimEnd('/') : "/data";
            containerDb = manifest.Paths.GetValueOrDefault("db") is { Length: > 0 } b ? b.TrimEnd('/') : containerData;
            pathSource = "DATA_DIR and DATABASE_DIR from the backup manifest (the paths 0.4.5 actually used)";
            originalDb = Path.Combine(original, "db");
            Directory.CreateDirectory(Path.Combine(original, "config")); Directory.CreateDirectory(Path.Combine(original, "data"));
            if (Directory.Exists(Path.Combine(original, "engine")))
                warnings.Add("The backup holds the 0.4.x integrated Suwayomi engine folder (installed extensions). 0.5.0 keeps its own source host in data/source-host; "
                    + "reinstall your extensions from your repositories in Sources after the first start. The engine folder is kept in original-0.4/engine.");
        }
        else
        {
            await log.WriteLineAsync("Copying the 0.4.x folders...").ConfigureAwait(false);
            Copy(Path.Combine(input, "config"), Path.Combine(original, "config"));
            Copy(Path.Combine(input, "data"), Path.Combine(original, "data"));
            if (Directory.Exists(Path.Combine(input, "database"))) Copy(Path.Combine(input, "database"), Path.Combine(original, "database"));
            containerData = layout!.ContainerData; containerDb = layout.ContainerDb; pathSource = layout.Source;
            originalDb = Path.Combine(original, layout.DatabaseFolder);
        }
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" })
            if (!File.Exists(Path.Combine(originalDb, name))) throw new MigrationRefusedException("The 0.4.x " + name + " was not found in the input.");

        // 2. The folders to migrate.
        var relativeDb = Relative(containerData, containerDb);
        var outDb = relativeDb is null ? Path.Combine(output, "database") : Path.Combine(data, relativeDb);
        Copy(Path.Combine(original, "config"), config);
        Copy(Path.Combine(original, "data"), data);
        Directory.CreateDirectory(outDb);
        foreach (var file in Directory.EnumerateFiles(originalDb).Where(path => FileParity.IsDatabaseFile(Path.GetFileName(path))))
        {
            var target = Path.Combine(outDb, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
        if (relativeDb is null)
            warnings.Add("DATABASE_DIR (" + containerDb + ") is outside DATA_DIR (" + containerData + "): mount " + outDb + " at " + containerDb + ".");

        // 3. env → config.json, the update notice, then the runtime offline.
        var now = DateTimeOffset.UtcNow;
        await log.WriteLineAsync("Converting autosourcarr.env to config.json...").ConfigureAwait(false);
        var conversion = EnvConversion.EnsureConverted(config, now);
        UpdateNotices.Check(config, ApplicationRuntime.Version, now);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["SETTINGS_CONFIG_DIR"] = config };
        var startup = StartupConfigurationLoader.Load(output, environment, bundledImage: true, convertLegacyEnvironment: false, now: now);
        var settings = startup.Settings with
        {
            DataDir = data, SettingsConfigDir = config,
            DatabaseDir = Path.GetFullPath(outDb) == Path.GetFullPath(data) ? null : outDb,
            LibraryDir = startup.Settings.LibraryDir is { Length: > 0 } library && Relative(containerData, library) is { } rel ? Path.Combine(data, rel) : null,
            SourceEngineMode = "external", SourceSuwayomiUrl = "http://127.0.0.1:1",
            MainSuwayomiUrl = "", MainSuwayomiUsername = "", MainSuwayomiPassword = "", MainSuwayomiDownloadsPath = "", AuthResetOwner = false,
        };
        var run = new JsonObject
        {
            ["input_kind"] = isFile ? "0.4.5 instance backup" : "copied 0.4.x folders",
            ["paths"] = new JsonObject { ["data_dir"] = containerData, ["database_dir"] = containerDb, ["source"] = pathSource },
            ["conversion"] = conversion is null ? "config.json already existed; not converted again" : new JsonObject
            {
                ["settings"] = conversion.SettingKeys.Count, ["source_host"] = conversion.SourceHostKeys.Count, ["extra"] = new JsonArray(conversion.ExtraKeys.Select(key => (JsonNode)key).ToArray()),
                ["encrypted"] = conversion.EncryptedKeys.Count, ["invalid"] = new JsonArray(conversion.InvalidKeys.Select(key => (JsonNode)key).ToArray()),
                ["originals_kept_in"] = "config/" + conversion.BackupDirectory,
            },
        };
        await log.WriteLineAsync("Opening AutoSourcarr " + ApplicationRuntime.Version + " offline on the copy (update guard, migrations, startup steps)...").ConfigureAwait(false);
        JsonObject started;
        await using (var runtime = await ApplicationRuntime.CreateOfflineAsync(new StartupConfigurationSnapshot(settings, config, conversion, environment), cancellationToken).ConfigureAwait(false))
            started = await runtime.RunOfflineStartupAsync(cancellationToken).ConfigureAwait(false);
        afterStartup?.Invoke(started);
        run["startup"] = started;
        var problems = (started["problems"] as JsonArray ?? []).Select(node => node?.ToString() ?? "").Where(text => text.Length > 0).ToList();
        warnings.AddRange((started["warnings"] as JsonArray ?? []).Select(node => node?.ToString() ?? "").Where(text => text.Length > 0));
        var notCovered = (started["not_covered"] as JsonArray ?? []).Select(node => node?.ToString() ?? "").ToList();
        // Fold the write-ahead logs into the migrated files, so copying the folder to the server cannot miss committed rows.
        // A busy checkpoint or a write-ahead log that is not empty afterwards means the files are not complete on their own.
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" })
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(outDb, name) + ";Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.GetInt64(0) != 0)
                    problems.Add(name + ": the write-ahead log could not be checkpointed (busy); the copied file would miss committed rows");
            }
            var wal = new FileInfo(Path.Combine(outDb, name + "-wal"));
            if (wal.Exists && wal.Length != 0) problems.Add(name + ": the write-ahead log is not empty after the checkpoint (" + wal.Length.ToString(CultureInfo.InvariantCulture) + " bytes)");
        }
        if (problems.Count > 0)
        {
            // Interrupted chapter work that could not be finished names its own reason in the journal.
            using var journal = new LegacyDatabase(Path.Combine(outDb, "catalog.sqlite"));
            if (journal.HasColumns("pending_entry_merges", ["last_error"]) && journal.HasColumns("pending_entry_splits", ["last_error"]))
                warnings.AddRange(journal.Query("SELECT 'pending merge of entry '||duplicate_entry_id||': '||last_error AS line FROM pending_entry_merges WHERE last_error<>'' "
                    + "UNION ALL SELECT 'pending split '||id||': '||last_error FROM pending_entry_splits WHERE last_error<>''").Select(row => row["line"] ?? ""));
        }
        afterMigration?.Invoke(outDb, config);
        if (File.Exists(Path.Combine(config, ConfigStore.FileName)) && !File.Exists(Path.Combine(config, "config.key")))
            problems.Add("config.json exists but config.key is missing: 0.5.0 cannot read the encrypted settings");

        // 4. The comparison.
        await log.WriteLineAsync("Comparing the 0.4.x data with the migrated data...").ConfigureAwait(false);
        ParityReport report;
        using (var oldCatalog = new LegacyDatabase(Path.Combine(originalDb, "catalog.sqlite")))
        using (var oldOrigins = new LegacyDatabase(Path.Combine(originalDb, "chapter-origins.sqlite")))
        using (var newCatalog = new LegacyDatabase(Path.Combine(outDb, "catalog.sqlite")))
        using (var newOrigins = new LegacyDatabase(Path.Combine(outDb, "chapter-origins.sqlite")))
        {
            var kinds = DecisionParity.Kinds.ToList();
            kinds.AddRange(TableCoverage.CountedKinds(oldCatalog, oldOrigins, kinds).ToList());
            var merges = MergeRecheck.Run(newCatalog);
            var settingItems = SettingsParity.Compare(Path.Combine(original, "config"), config);
            if (layout?.Compose is { Count: > 0 } composeValues) settingItems.AddRange(SettingsParity.CompareCompose(composeValues));
            report = new ParityReport
            {
                Input = (isFile ? "backup file " : "folder ") + input, FromVersion = fromVersion, ToVersion = ApplicationRuntime.Version,
                CreatedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                Decisions = DecisionParity.Compare((oldCatalog, oldOrigins), (newCatalog, newOrigins), kinds),
                Counts = [.. DecisionParity.Counts(oldCatalog, newCatalog, "catalog"), .. DecisionParity.Counts(oldOrigins, newOrigins, "origins")],
                UnresolvedEntries = DecisionParity.UnresolvedEntries(oldCatalog, newCatalog),
                OriginalEntries = new EntryIds(oldCatalog).EntryCount,
                Settings = settingItems,
                Notices = SettingsParity.Notices(Path.Combine(original, "config"), config),
                Files = FileParity.Compare([("config", Path.Combine(original, "config"), config), ("data", Path.Combine(original, "data"), data)]),
                Merges = merges,
                NewMerges = DecisionParity.NewMerges(oldCatalog, newCatalog, DecisionParity.DecisionEntries(oldCatalog, oldOrigins, kinds),
                    merges.Failing.Select(item => item.Duplicate).ToHashSet()),
                EmptiedEntries = DecisionParity.EmptiedEntries(oldCatalog, newCatalog),
                DroppedTables = [.. DecisionParity.DroppedTables(oldCatalog, newCatalog, "catalog"), .. DecisionParity.DroppedTables(oldOrigins, newOrigins, "origins")],
                Coverage = TableCoverage.Describe(oldCatalog, oldOrigins, kinds),
                Run = run, Warnings = warnings, MigrationProblems = problems, NotCovered = notCovered,
            };
        }
        var reportPath = Path.Combine(output, "parity-report.md");
        await File.WriteAllTextAsync(reportPath, report.ToMarkdown(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "parity-report.json"), report.ToJson().ToJsonString(Indented), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return new MigrationOutcome(report, data, config, relativeDb is null ? outDb : null, reportPath);
    }

    /// <summary>
    /// Best effort: the tool works on a snapshot only. A database journal written in the last 10 minutes, a database file
    /// another process holds open for writing (Windows), or a container mount path inside a container is refused.
    /// </summary>
    private static void RefuseLiveInput(string input)
    {
        foreach (var file in Directory.EnumerateFiles(input, "*.sqlite*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }))
        {
            var name = Path.GetFileName(file);
            if ((name.EndsWith("-wal", StringComparison.Ordinal) || name.EndsWith("-journal", StringComparison.Ordinal)) && new FileInfo(file).Length > 0
                && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(10))
                throw new MigrationRefusedException(name + " was written less than 10 minutes ago: this looks like a running instance. Stop 0.4.5 (or download a backup) and use a copy.");
            if (!name.EndsWith(".sqlite", StringComparison.Ordinal)) continue;
            try { using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException) { throw new MigrationRefusedException(name + " is open in another program: this looks like a running instance. Stop it and use a copy."); }
        }
        if (OperatingSystem.IsLinux() && File.Exists("/.dockerenv") && (input is "/data" or "/config" || input.StartsWith("/data/", StringComparison.Ordinal) || input.StartsWith("/config/", StringComparison.Ordinal)))
            throw new MigrationRefusedException("The input is a live mount inside a container. Copy the folders (or download a backup) and run the tool on the copy.");
    }

    private static bool Inside(string child, string parent, StringComparison comparison)
    {
        var p = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return child.Equals(Path.TrimEndingDirectorySeparator(parent), comparison) || child.StartsWith(p, comparison);
    }

    /// <summary>The path of <paramref name="path"/> below the container folder <paramref name="root"/> ("" for itself), or null when outside.</summary>
    internal static string? Relative(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(path);
        root = root.TrimEnd('/'); path = path.TrimEnd('/');
        if (path == root) return "";
        return path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..].Replace('/', Path.DirectorySeparatorChar) : null;
    }

    private static Dictionary<string, string> ReadEnv(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        return DotEnv.Parse(File.ReadAllText(path)).Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    }

    /// <summary>Copies a folder with its timestamps (links are not followed; the source is only read).</summary>
    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, destination);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
        }
    }
}
