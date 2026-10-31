using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Core.Persistence;

/// <summary>
/// Creates and upgrades both databases with FluentMigrator (phase 2). Each file has its own migration set
/// (tag "catalog" or "origins") and its own VersionInfo table. Migration 1 is the 0.4.4 baseline: the released
/// SQL plus the conditional upgrades that bring a 0.3.x file to 0.4.4, so new and old files end in the same
/// schema. Later schema changes are written as ordinary fluent migrations, which also run on Postgres
/// (phase 16 adds the Postgres form of the baseline to the same migration numbers).
/// The ASC1 application_id / user_version stamp is checked before any migration touches a file.
/// </summary>
public sealed partial class SchemaMigrator(SqliteDatabase database)
{
    public const string CatalogTag = "catalog";
    public const string OriginsTag = "origins";

    [ThreadStatic] private static Func<string, string>? _normalizeTitle;

    public void Initialize(Func<string, string>? normalizeTitle = null)
    {
        var previous = _normalizeTitle;
        _normalizeTitle = normalizeTitle ?? TextMatcher.NormalizeTitle;
        try
        {
            Migrate(DatabaseFile.Catalog, CatalogTag);
            Migrate(DatabaseFile.Origins, OriginsTag);
            // 0.4.x filled missing normalized preferred titles on every start, not only during the upgrade.
            NormalizePendingTitles(DatabaseFile.Catalog);
            NormalizePendingTitles(DatabaseFile.Origins);
        }
        finally { _normalizeTitle = previous; }
    }

    /// <summary>Feature tables are part of the migrations now; this only re-applies the idempotent script.</summary>
    public void EnsureFeatureSchema(string name)
    {
        if (name is not ("catalogue-identity.sql" or "gallery-policy.sql" or "identity-review.sql"))
            throw new ArgumentException("Unknown feature schema.", nameof(name));
        database.Write(DatabaseFile.Catalog, session => session.Execute(Resource(name)));
    }

    /// <summary>"ASC1": the SQLite application_id written by every AutoSourcarr build since 0.4.4.</summary>
    public const int SchemaApplicationId = 0x41534331;
    /// <summary>Highest schema revision this build understands; newer files are refused unchanged.</summary>
    public const int SchemaRevision = 1;

    /// <summary>Highest FluentMigrator version per file in this build (VersionInfo).</summary>
    public static long LatestMigration(DatabaseFile file) => typeof(SchemaMigrator).Assembly.GetTypes()
        .Select(type => (Type: type, Migration: type.GetCustomAttribute<FluentMigrator.MigrationAttribute>(), Tags: type.GetCustomAttribute<FluentMigrator.TagsAttribute>()))
        .Where(item => item.Migration is not null && item.Tags?.TagNames.Contains(file == DatabaseFile.Catalog ? CatalogTag : OriginsTag) == true)
        .Max(item => item.Migration!.Version);

    private void Migrate(DatabaseFile file, string tag)
    {
        var path = file == DatabaseFile.Catalog ? database.Paths.Catalog : database.Paths.Origins;
        using (var connection = database.Open(file, initialize: true))
            CheckStamp(connection, path);

        using (var services = new ServiceCollection()
                   .AddLogging(logging => logging.ClearProviders())
                   .AddFluentMigratorCore()
                   .ConfigureRunner(runner => runner.AddSQLite()
                       .WithGlobalConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
                       .ScanIn(typeof(SchemaMigrator).Assembly).For.Migrations())
                   .Configure<RunnerOptions>(options => options.Tags = [tag])
                   .BuildServiceProvider(validateScopes: false))
        using (var scope = services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        using (var connection = database.Open(file, initialize: true))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA journal_mode=WAL; PRAGMA application_id={SchemaApplicationId}; PRAGMA user_version={SchemaRevision}; PRAGMA optimize;");
            command.ExecuteNonQuery();
        }
    }

    private void NormalizePendingTitles(DatabaseFile file)
    {
        using var connection = database.Open(file, initialize: true);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='entry_preferred_titles'";
            if (check.ExecuteScalar() is null)
                return;
        }
        using var transaction = connection.BeginTransaction();
        var normalize = _normalizeTitle ?? TextMatcher.NormalizeTitle;
        foreach (var (entry, raw) in PendingTitles(connection, transaction))
            Run(connection, transaction, """
                UPDATE entry_preferred_titles SET normalized=$normalized
                WHERE entry_id=$entry AND raw_title=$raw AND normalized IS NULL
                """, ("$normalized", normalize(raw)), ("$entry", entry), ("$raw", raw));
        transaction.Commit();
    }

    private static void CheckStamp(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        // Same rule as the Python app's check_schema_revision: a foreign or newer
        // file is refused before any CREATE or migration touches it.
        command.CommandText = "PRAGMA application_id";
        var applicationId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA user_version";
        var revision = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (applicationId is not (0 or SchemaApplicationId))
            throw new SchemaRevisionException($"{path} is not an AutoSourcarr database (application_id {applicationId}).");
        if (revision > SchemaRevision)
            throw new SchemaRevisionException($"{path} uses schema revision {revision}; this build supports up to {SchemaRevision}. "
                + "Run the newer AutoSourcarr image or restore a matching backup; the file was not changed.");
    }

    /// <summary>Migration 1 body: released SQL (all CREATE ... IF NOT EXISTS) plus the 0.3.x → 0.4.4 steps.</summary>
    internal static void ApplyBaseline(IDbConnection connection, IDbTransaction? transaction, DatabaseFile file)
    {
        var sql = file == DatabaseFile.Catalog ? Resource("catalog.sql") + Resource("library.sql") : Resource("origins.sql");
        // Every statement is CREATE ... IF NOT EXISTS (or a PRAGMA), so running it again after a crash is safe.
        Run(connection, transaction, sql);
        using var plan = JsonDocument.Parse(Resource("legacy-migrations.json"));
        if (plan.RootElement.GetProperty("format").GetInt32() != 1)
            throw new InvalidDataException("Unsupported embedded database migration plan.");
        // The upgrade steps change data, so they commit together or not at all (as in 0.4.x).
        var own = transaction is null ? connection.BeginTransaction() : null;
        try
        {
            RunSteps(connection, own ?? transaction, plan.RootElement.GetProperty(file == DatabaseFile.Catalog ? "catalog" : "origins"),
                _normalizeTitle ?? TextMatcher.NormalizeTitle);
            own?.Commit();
        }
        finally { own?.Dispose(); }
    }

    /// <summary>Migration 2 body: the feature tables that 0.4.x created on first use.</summary>
    internal static void ApplyFeatureTables(IDbConnection connection, IDbTransaction? transaction)
    {
        foreach (var name in new[] { "catalogue-identity.sql", "gallery-policy.sql", "identity-review.sql" })
            Run(connection, transaction, Resource(name));
    }

    private static void Run(IDbConnection connection, IDbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        command.ExecuteNonQuery();
    }

    private static List<(long Entry, string Raw)> PendingTitles(IDbConnection connection, IDbTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT entry_id,raw_title FROM entry_preferred_titles WHERE normalized IS NULL";
        using var reader = command.ExecuteReader();
        var rows = new List<(long, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return rows;
    }

    private static bool HasColumn(IDbConnection connection, IDbTransaction? transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void RunSteps(IDbConnection connection, IDbTransaction? transaction, JsonElement steps, Func<string, string> normalizeTitle)
    {
        foreach (var step in steps.EnumerateArray())
        {
            switch (step.GetProperty("kind").GetString())
            {
                case "sql":
                    Run(connection, transaction, step.GetProperty("sql").GetString()!);
                    break;
                case "if-missing-column":
                    var table = step.GetProperty("table").GetString()!;
                    var column = step.GetProperty("column").GetString()!;
                    if (!Identifier().IsMatch(table) || !Identifier().IsMatch(column))
                        throw new InvalidDataException("Invalid embedded migration identifier.");
                    if (!HasColumn(connection, transaction, table, column))
                        RunSteps(connection, transaction, step.GetProperty("steps"), normalizeTitle);
                    break;
                case "normalize-preferred-titles":
                    foreach (var (entry, raw) in PendingTitles(connection, transaction))
                        Run(connection, transaction, """
                            UPDATE entry_preferred_titles SET normalized=$normalized
                            WHERE entry_id=$entry AND raw_title=$raw AND normalized IS NULL
                            """, ("$normalized", normalizeTitle(raw)), ("$entry", entry), ("$raw", raw));
                    break;
                default:
                    throw new InvalidDataException("Unknown embedded database migration operation.");
            }
        }
    }

    internal static string Resource(string filename)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AutoSourcarr.Core.Persistence.Schema." + filename)
            ?? throw new InvalidDataException("Missing embedded database schema.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("^[a-z_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}

/// <summary>The database file belongs to another program or to a newer AutoSourcarr; nothing was changed.</summary>
public sealed class SchemaRevisionException(string message) : InvalidOperationException(message);
