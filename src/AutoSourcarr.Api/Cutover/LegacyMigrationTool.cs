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

/// <summary>
/// Phase 14 cutover tool: <c>AutoSourcarr.Api --migrate-0.4 &lt;input&gt; &lt;output&gt;</c>. The input is a 0.4.5 instance backup
/// file (System → Backups; the passphrase is asked, never stored or logged) or a copied 0.4.x folder holding <c>data/</c>
/// and <c>config/</c> (optionally <c>database/</c>). The input is only read. The output folder (new or empty) gets:
/// <c>original-0.4/</c> (the untouched copy the comparison reads), <c>data/</c> and <c>config/</c> (the migrated
/// 0.5.0 folders to mount), <c>database/</c> when DATABASE_DIR was outside /data, and <c>parity-report.md</c>/<c>.json</c>.
/// Migration = env → config.json (EnvConversion), the update notice, then the full runtime opened offline (update guard
/// with its automatic backup, FluentMigrator migrations, every store) and the local one-time startup steps.
/// </summary>
internal static class LegacyMigrationTool
{
    public const string Usage = "Usage: AutoSourcarr.Api --migrate-0.4 <0.4.5 backup file | folder with data/ and config/> <empty output folder>";

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length != 2 || args.Any(string.IsNullOrWhiteSpace))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }
        try
        {
            var outcome = await MigrateAsync(args[0], args[1], () => ReadPassphrase(output), output).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync(outcome.Report.Verdict).ConfigureAwait(false);
            await output.WriteLineAsync("Report:          " + outcome.ReportPath).ConfigureAwait(false);
            await output.WriteLineAsync("Mount at /data:   " + outcome.DataDirectory).ConfigureAwait(false);
            await output.WriteLineAsync("Mount at /config: " + outcome.ConfigDirectory).ConfigureAwait(false);
            if (outcome.DatabaseDirectory is { } database) await output.WriteLineAsync("Mount at DATABASE_DIR: " + database).ConfigureAwait(false);
            return outcome.Report.Ok ? 0 : 1;
        }
        catch (Exception error) when (error is MigrationRefusedException or BackupRefusedException or ConfigurationException)
        {
            await output.WriteLineAsync("Refused: " + error.Message).ConfigureAwait(false);
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            await output.WriteLineAsync("Migration failed (" + error.GetType().Name + "): " + error.Message + " The input was not changed.").ConfigureAwait(false);
            return 3;
        }
    }

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
        CancellationToken cancellationToken = default, Action<string, string>? afterMigration = null)
    {
        ArgumentNullException.ThrowIfNull(passphrase); ArgumentNullException.ThrowIfNull(log);
        input = Path.GetFullPath(input); var output = Path.GetFullPath(outputFolder);
        var isFile = File.Exists(input);
        if (!isFile && !Directory.Exists(input)) throw new MigrationRefusedException("The input does not exist: " + input);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new MigrationRefusedException("The output folder is not empty: " + output + ". Choose a new or empty folder.");
        if (File.Exists(output)) throw new MigrationRefusedException("The output path is a file: " + output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!isFile && (Inside(output, input, comparison) || Inside(input, output, comparison)))
            throw new MigrationRefusedException("The output folder must be outside the input folder (and the other way round).");
        if (!isFile) RefuseLiveInput(input);

        var original = Path.Combine(output, "original-0.4");
        var data = Path.Combine(output, "data"); var config = Path.Combine(output, "config");
        Directory.CreateDirectory(output);
        // 1. The untouched copy the comparison reads (the input itself is only read).
        string originalDb; string containerData = "/data"; string? containerDb; string fromVersion = "0.4.x";
        var warnings = new List<string>();
        if (isFile)
        {
            if (!LegacyBackupReader.IsLegacyBackup(input))
                throw new MigrationRefusedException("This file is not a 0.4.5 instance backup (it must start with ASCBK2). Download it from 0.4.5 System → Backups.");
            await log.WriteLineAsync("Reading the 0.4.5 backup (this takes a few seconds for the key derivation)...").ConfigureAwait(false);
            var manifest = LegacyBackupReader.Extract(input, passphrase(), original);
            fromVersion = manifest.Version.Length > 0 ? manifest.Version : fromVersion;
            if (manifest.Paths.TryGetValue("data", out var d)) containerData = d;
            containerDb = manifest.Paths.TryGetValue("db", out var db) ? db : containerData;
            originalDb = Path.Combine(original, "db");
            Directory.CreateDirectory(Path.Combine(original, "config")); Directory.CreateDirectory(Path.Combine(original, "data"));
            if (Directory.Exists(Path.Combine(original, "engine")))
                warnings.Add("The backup holds the 0.4.x integrated Suwayomi engine folder (installed extensions). 0.5.0 keeps its own source host in data/source-host; "
                    + "reinstall your extensions from your repositories in Sources after the first start. The engine folder is kept in original-0.4/engine.");
        }
        else
        {
            if (!Directory.Exists(Path.Combine(input, "config")) || !Directory.Exists(Path.Combine(input, "data")))
                throw new MigrationRefusedException("The input folder must contain the copied 0.4.x 'data' and 'config' folders (and 'database' when DATABASE_DIR was elsewhere).");
            await log.WriteLineAsync("Copying the 0.4.x folders...").ConfigureAwait(false);
            Copy(Path.Combine(input, "config"), Path.Combine(original, "config"));
            Copy(Path.Combine(input, "data"), Path.Combine(original, "data"));
            if (Directory.Exists(Path.Combine(input, "database"))) Copy(Path.Combine(input, "database"), Path.Combine(original, "database"));
            var env = ReadEnv(Path.Combine(original, "config", EnvConversion.MainEnv));
            if (env.TryGetValue("DATA_DIR", out var envData) && !string.IsNullOrWhiteSpace(envData)) containerData = envData.TrimEnd('/');
            containerDb = env.TryGetValue("DATABASE_DIR", out var envDb) && !string.IsNullOrWhiteSpace(envDb) ? envDb.TrimEnd('/') : containerData;
            originalDb = Directory.Exists(Path.Combine(original, "database")) ? Path.Combine(original, "database")
                : Path.Combine(original, "data", Relative(containerData, containerDb) ?? "");
        }
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" })
            if (!File.Exists(Path.Combine(originalDb, name))) throw new MigrationRefusedException("The 0.4.x " + name + " was not found in the input.");

        // 2. The folders to migrate.
        var relativeDb = Relative(containerData, containerDb ?? containerData);
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
            warnings.Add("DATABASE_DIR (" + containerDb + ") is outside " + containerData + ": mount " + outDb + " at " + containerDb + ".");

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
            ["conversion"] = conversion is null ? "config.json already existed; not converted again" : new JsonObject
            {
                ["settings"] = conversion.SettingKeys.Count, ["source_host"] = conversion.SourceHostKeys.Count, ["extra"] = new JsonArray(conversion.ExtraKeys.Select(key => (JsonNode)key).ToArray()),
                ["encrypted"] = conversion.EncryptedKeys.Count, ["invalid"] = new JsonArray(conversion.InvalidKeys.Select(key => (JsonNode)key).ToArray()),
                ["originals_kept_in"] = "config/" + conversion.BackupDirectory,
            },
        };
        await log.WriteLineAsync("Opening AutoSourcarr " + ApplicationRuntime.Version + " offline on the copy (update guard, migrations, startup steps)...").ConfigureAwait(false);
        await using (var runtime = await ApplicationRuntime.CreateOfflineAsync(new StartupConfigurationSnapshot(settings, config, conversion, environment), cancellationToken).ConfigureAwait(false))
            run["startup"] = await runtime.RunOfflineStartupAsync(cancellationToken).ConfigureAwait(false);
        if (run["startup"]?["safe_mode"] is not null) warnings.Add("The update did not complete: 0.5.0 would start in safe mode (see Migration run below). Do not deploy this output.");
        // Fold the write-ahead logs into the migrated files, so copying the folder to the server cannot miss committed rows.
        foreach (var name in new[] { "catalog.sqlite", "chapter-origins.sqlite" })
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(outDb, name) + ";Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }
        afterMigration?.Invoke(outDb, config);

        // 4. The comparison.
        await log.WriteLineAsync("Comparing the 0.4.x data with the migrated data...").ConfigureAwait(false);
        ParityReport report;
        using (var oldCatalog = new LegacyDatabase(Path.Combine(originalDb, "catalog.sqlite")))
        using (var oldOrigins = new LegacyDatabase(Path.Combine(originalDb, "chapter-origins.sqlite")))
        using (var newCatalog = new LegacyDatabase(Path.Combine(outDb, "catalog.sqlite")))
        using (var newOrigins = new LegacyDatabase(Path.Combine(outDb, "chapter-origins.sqlite")))
        {
            report = new ParityReport
            {
                Input = (isFile ? "backup file " : "folder ") + input, FromVersion = fromVersion, ToVersion = ApplicationRuntime.Version,
                CreatedAt = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                Decisions = DecisionParity.Compare((oldCatalog, oldOrigins), (newCatalog, newOrigins)),
                Counts = [.. DecisionParity.Counts(oldCatalog, newCatalog, "catalog"), .. DecisionParity.Counts(oldOrigins, newOrigins, "origins")],
                UnresolvedEntries = DecisionParity.UnresolvedEntries(oldCatalog, newCatalog),
                OriginalEntries = new EntryIds(oldCatalog).EntryCount,
                Settings = SettingsParity.Compare(Path.Combine(original, "config"), config),
                Notices = SettingsParity.Notices(Path.Combine(original, "config"), config),
                Files = FileParity.Compare([("config", Path.Combine(original, "config"), config), ("data", Path.Combine(original, "data"), data)]),
                Merges = MergeRecheck.Run(newCatalog),
                Run = run, Warnings = warnings,
            };
        }
        var reportPath = Path.Combine(output, "parity-report.md");
        await File.WriteAllTextAsync(reportPath, report.ToMarkdown(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "parity-report.json"), report.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
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
        root = root.TrimEnd('/'); path = path.TrimEnd('/');
        if (path == root) return "";
        return path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..].Replace('/', Path.DirectorySeparatorChar) : null;
    }

    private static Dictionary<string, string> ReadEnv(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        return DotEnv.Parse(File.ReadAllText(path)).Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    }

    /// <summary>Copies a folder (links are not followed; the source is only read).</summary>
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
