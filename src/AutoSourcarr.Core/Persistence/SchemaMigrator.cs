using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Matching;

namespace AutoSourcarr.Core.Persistence;

/// <summary>Preserves the released SQL and its conditional, per-file migrations.</summary>
public sealed partial class SchemaMigrator(SqliteDatabase database)
{
    public void Initialize(Func<string, string>? normalizeTitle = null)
    {
        normalizeTitle ??= TextMatcher.NormalizeTitle;
        CreateBase(DatabaseFile.Catalog, Resource("catalog.sql") + Resource("library.sql"));
        CreateBase(DatabaseFile.Origins, Resource("origins.sql"));
        using var plan = JsonDocument.Parse(Resource("legacy-migrations.json"));
        if (plan.RootElement.GetProperty("format").GetInt32() != 1)
            throw new InvalidDataException("Unsupported embedded database migration plan.");
        Apply(DatabaseFile.Catalog, plan.RootElement.GetProperty("catalog"), normalizeTitle);
        Apply(DatabaseFile.Origins, plan.RootElement.GetProperty("origins"), normalizeTitle);
    }

    public void EnsureFeatureSchema(string name)
    {
        if (name is not ("catalogue-identity.sql" or "gallery-policy.sql" or "identity-review.sql"))
            throw new ArgumentException("Unknown feature schema.", nameof(name));
        database.Write(DatabaseFile.Catalog, session => session.Execute(Resource(name)));
    }

    private void Apply(DatabaseFile file, JsonElement steps, Func<string, string> normalizeTitle)
    {
        database.Write(file, session =>
        {
            RunSteps(session, steps, normalizeTitle);
            return 0;
        });
    }

    private static void RunSteps(DatabaseSession session, JsonElement steps, Func<string, string> normalizeTitle)
    {
        foreach (var step in steps.EnumerateArray())
        {
            switch (step.GetProperty("kind").GetString())
            {
                case "sql":
                    session.Execute(step.GetProperty("sql").GetString()!);
                    break;
                case "if-missing-column":
                    var table = step.GetProperty("table").GetString()!;
                    var column = step.GetProperty("column").GetString()!;
                    if (!Identifier().IsMatch(table) || !Identifier().IsMatch(column))
                        throw new InvalidDataException("Invalid embedded migration identifier.");
                    if (!session.Query($"PRAGMA table_info(\"{table}\")").Any(row => row.Text("name") == column))
                        RunSteps(session, step.GetProperty("steps"), normalizeTitle);
                    break;
                case "normalize-preferred-titles":
                    foreach (var row in session.Query("SELECT entry_id,raw_title FROM entry_preferred_titles WHERE normalized IS NULL"))
                        session.Execute("""
                            UPDATE entry_preferred_titles SET normalized=$normalized
                            WHERE entry_id=$entry AND raw_title=$raw AND normalized IS NULL
                            """, ("$normalized", normalizeTitle(row.Text("raw_title"))),
                            ("$entry", row.Integer("entry_id")), ("$raw", row.Text("raw_title")));
                    break;
                default:
                    throw new InvalidDataException("Unknown embedded database migration operation.");
            }
        }
    }

    /// <summary>"ASC1": the SQLite application_id written by every AutoSourcarr build since 0.4.4.</summary>
    public const int SchemaApplicationId = 0x41534331;
    /// <summary>Highest schema revision this build understands; newer files are refused unchanged.</summary>
    public const int SchemaRevision = 1;

    private void CreateBase(DatabaseFile file, string sql)
    {
        using var connection = database.Open(file, initialize: true);
        using var command = connection.CreateCommand();
        // Same rule as the Python app's check_schema_revision: a foreign or newer
        // file is refused before any CREATE or migration touches it.
        command.CommandText = "PRAGMA application_id";
        var applicationId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        command.CommandText = "PRAGMA user_version";
        var revision = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        var path = file == DatabaseFile.Catalog ? database.Paths.Catalog : database.Paths.Origins;
        if (applicationId is not (0 or SchemaApplicationId))
            throw new SchemaRevisionException($"{path} is not an AutoSourcarr database (application_id {applicationId}).");
        if (revision > SchemaRevision)
            throw new SchemaRevisionException($"{path} uses schema revision {revision}; this build supports up to {SchemaRevision}. "
                + "Run the newer AutoSourcarr image or restore a matching backup; the file was not changed.");
        command.CommandText = sql;
        command.ExecuteNonQuery();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA application_id={SchemaApplicationId}; PRAGMA user_version={SchemaRevision};");
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA optimize";
        command.ExecuteNonQuery();
    }

    private static string Resource(string filename)
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
