using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AutoSourcarr.Core.Cutover;

/// <summary>
/// One SQLite file opened read-only for the cutover comparison (never migrated, never written). Rows come back as text
/// (invariant culture), so the 0.4.x file and the migrated file compare value by value.
/// </summary>
public sealed class LegacyDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HashSet<string> _tables;

    public LegacyDatabase(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Database file not found.", path);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        Path = System.IO.Path.GetFullPath(path);
        _connection.Open();
        _tables = Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Select(row => row["name"] ?? "").ToHashSet(StringComparer.Ordinal);
    }

    public string Path { get; }
    public IReadOnlySet<string> Tables => _tables;
    public bool HasTable(string table) => _tables.Contains(table);

    public bool HasColumns(string table, IEnumerable<string> columns)
    {
        if (!HasTable(table)) return false;
        var present = Query("SELECT name FROM pragma_table_info($t)", ("$t", table)).Select(row => row["name"] ?? "").ToHashSet(StringComparer.Ordinal);
        return columns.All(present.Contains);
    }

    public long Count(string table) => long.Parse(Query("SELECT COUNT(*) n FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")[0]["n"]!, CultureInfo.InvariantCulture);

    public List<Dictionary<string, string?>> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, string?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Text(reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Attaches another file (read-only, like this one) under <paramref name="alias"/>, for joins across the two databases.</summary>
    public void Attach(string path, string alias)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Database file not found.", path);
        // An attached file is opened with the flags of the main connection (read-only).
        using var command = _connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $path AS " + alias;
        command.Parameters.AddWithValue("$path", System.IO.Path.GetFullPath(path));
        command.ExecuteNonQuery();
    }

    /// <summary>The columns of a table in declaration order, and which of them form the primary key (in key order).</summary>
    public (IReadOnlyList<string> Columns, IReadOnlyList<string> PrimaryKey) Schema(string table)
    {
        var rows = Query("SELECT name,pk FROM pragma_table_info($t)", ("$t", table));
        return (rows.Select(row => row["name"] ?? "").ToList(),
            rows.Where(row => row["pk"] is { } pk && pk != "0").OrderBy(row => int.Parse(row["pk"]!, CultureInfo.InvariantCulture)).Select(row => row["name"] ?? "").ToList());
    }

    /// <summary>
    /// A cell as compared text: NULL stays null (distinct from ''), integers as digits, reals marked "real:" (so 1 and
    /// 1.0 differ), blobs by SHA-256.
    /// </summary>
    private static string Text(object value) => value switch
    {
        double number => "real:" + number.ToString("R", CultureInfo.InvariantCulture),
        byte[] blob => "blob:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(blob)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public void Dispose() => _connection.Dispose();
}

/// <summary>Entry ids through the merge aliases (entry_aliases old → canonical, followed to the end).</summary>
public sealed class EntryIds
{
    private readonly Dictionary<long, long> _aliases = [];
    private readonly HashSet<long> _entries = [];
    private readonly Dictionary<long, string> _titles = [];

    public EntryIds(LegacyDatabase catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.HasTable("entry_aliases"))
            foreach (var row in catalog.Query("SELECT old_entry_id,canonical_entry_id FROM entry_aliases"))
                if (Id(row["old_entry_id"]) is { } old && Id(row["canonical_entry_id"]) is { } canonical && old != canonical) _aliases[old] = canonical;
        if (catalog.HasTable("entries"))
            foreach (var row in catalog.Query("SELECT id,preferred_title FROM entries"))
                if (Id(row["id"]) is { } id) { _entries.Add(id); _titles[id] = row["preferred_title"] ?? ""; }
    }

    public int EntryCount => _entries.Count;
    public int AliasCount => _aliases.Count;
    public IEnumerable<long> AllIds => _entries.Concat(_aliases.Keys);

    public long Resolve(long id)
    {
        for (var step = 0; step < 256 && _aliases.TryGetValue(id, out var next); step++) id = next;
        return id;
    }

    public bool Exists(long id) => _entries.Contains(Resolve(id));
    public string Title(long id) => _titles.TryGetValue(Resolve(id), out var title) ? title : "";

    public static long? Id(string? text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
