using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Compatibility;

/// <summary>One row of a reader table: the first app version that reads this format version.</summary>
public sealed record CompatibilityReader(int Version, string AppVersion);

/// <summary>
/// PIPE-4: the compatibility list each release publishes (compatibility.json beside this class, embedded in the
/// assembly). It names the share format, extension API and profile format of this build; the database schema is the
/// latest FluentMigrator migration of each database file, read from the assembly so it can never drift. The reader
/// tables turn a format version into "the app version that reads it" for the "Version mismatch: version X required"
/// refusals (META-4) and the update notices (ARC-7).
/// </summary>
public sealed class CompatibilityList
{
    public const string Resource = "AutoSourcarr.Core.Compatibility.compatibility.json";
    private static readonly Lazy<CompatibilityList> Loaded = new(Load);

    private CompatibilityList(string appVersion, int shareFormat, int shareMinimum, IReadOnlyList<CompatibilityReader> shareReaders,
        string extensionApi, int profileFormat, IReadOnlyList<CompatibilityReader> profileReaders, long catalogSchema, long originsSchema)
    {
        AppVersion = appVersion; ShareFormat = shareFormat; ShareFormatMinReader = shareMinimum; ShareReaders = shareReaders;
        ExtensionApi = extensionApi; ProfileFormat = profileFormat; ProfileReaders = profileReaders;
        CatalogSchema = catalogSchema; OriginsSchema = originsSchema;
    }

    /// <summary>The list of this build.</summary>
    public static CompatibilityList Current => Loaded.Value;

    /// <summary>
    /// The version of the running build (the assembly's informational version, VersionPrefix-VersionSuffix, without the
    /// source revision; the same text as the app's runtime version). Catalogue file headers name it as their writer.
    /// <see cref="AppVersion"/> is the release this list describes (the first app version that reads its formats).
    /// </summary>
    public static string RunningVersion { get; } = ReadRunningVersion();

    private static string ReadRunningVersion()
    {
        var text = typeof(CompatibilityList).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var cut = text.IndexOf('+', StringComparison.Ordinal);
        if (cut >= 0) text = text[..cut];
        return text.Length is > 0 and <= 64 ? text : "0.0.0";
    }

    public string AppVersion { get; }
    public int ShareFormat { get; }
    public int ShareFormatMinReader { get; }
    public IReadOnlyList<CompatibilityReader> ShareReaders { get; }
    public string ExtensionApi { get; }
    public int ProfileFormat { get; }
    public IReadOnlyList<CompatibilityReader> ProfileReaders { get; }
    public long CatalogSchema { get; }
    public long OriginsSchema { get; }

    /// <summary>The first app version that reads share format <paramref name="version"/>, when this list knows it.</summary>
    public string? ShareReaderAppVersion(int version) => ShareReaders.FirstOrDefault(reader => reader.Version == version)?.AppVersion;

    /// <summary>The first app version that reads profile format <paramref name="version"/>, when this list knows it.</summary>
    public string? ProfileReaderAppVersion(int version) => ProfileReaders.FirstOrDefault(reader => reader.Version == version)?.AppVersion;

    public JsonObject ToJson() => new()
    {
        ["list_version"] = 1,
        ["app_version"] = AppVersion,
        ["running_version"] = RunningVersion,
        ["database_schema"] = new JsonObject { ["catalog"] = CatalogSchema, ["origins"] = OriginsSchema },
        ["share_format"] = new JsonObject { ["version"] = ShareFormat, ["min_reader_version"] = ShareFormatMinReader, ["readers"] = Readers(ShareReaders) },
        ["extension_api"] = new JsonObject { ["version"] = ExtensionApi },
        ["profile_format"] = new JsonObject { ["version"] = ProfileFormat, ["readers"] = Readers(ProfileReaders) },
    };

    private static JsonArray Readers(IEnumerable<CompatibilityReader> readers) =>
        new(readers.Select(reader => (JsonNode?)new JsonObject { ["version"] = reader.Version, ["app_version"] = reader.AppVersion }).ToArray());

    private static CompatibilityList Load()
    {
        using var stream = typeof(CompatibilityList).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("compatibility.json is not embedded in AutoSourcarr.Core (EmbeddedResource Compatibility/*.json).");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), SchemaMigrator.LatestMigration(DatabaseFile.Catalog), SchemaMigrator.LatestMigration(DatabaseFile.Origins));
    }

    /// <summary>Reads a compatibility.json text; the schema versions come from the running build.</summary>
    public static CompatibilityList Parse(string json, long catalogSchema, long originsSchema)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("compatibility.json must be a JSON object."); }
        catch (JsonException error) { throw new InvalidOperationException("compatibility.json is not valid JSON.", error); }
        if (Text(root, "list") != "autosourcarr-compatibility" || Number(root, "list_version") != 1) throw new InvalidOperationException("compatibility.json is not a version 1 compatibility list.");
        var share = Section(root, "share_format"); var extension = Section(root, "extension_api"); var profile = Section(root, "profile_format");
        var shareReaders = ReaderTable(share); var profileReaders = ReaderTable(profile);
        var shareVersion = Number(share, "version"); var shareMinimum = Number(share, "min_reader_version");
        if (shareMinimum < 1 || shareMinimum > shareVersion || shareReaders.All(row => row.Version != shareVersion)) throw new InvalidOperationException("compatibility.json share_format is inconsistent.");
        var profileVersion = Number(profile, "version");
        if (profileReaders.All(row => row.Version != profileVersion)) throw new InvalidOperationException("compatibility.json profile_format is inconsistent.");
        return new CompatibilityList(Text(root, "app_version"), shareVersion, shareMinimum, shareReaders, Text(extension, "version"), profileVersion, profileReaders, catalogSchema, originsSchema);
    }

    private static JsonObject Section(JsonObject root, string name) =>
        root[name] as JsonObject ?? throw new InvalidOperationException("compatibility.json needs a " + name + " object.");

    private static string Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 64 ? text
            : throw new InvalidOperationException("compatibility.json needs text " + name + ".");

    private static int Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && int.TryParse(value.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number
            : throw new InvalidOperationException("compatibility.json needs a positive whole number " + name + ".");

    private static List<CompatibilityReader> ReaderTable(JsonObject section)
    {
        if (section["readers"] is not JsonArray rows || rows.Count == 0) throw new InvalidOperationException("compatibility.json needs a readers table.");
        var result = new List<CompatibilityReader>();
        foreach (var row in rows)
        {
            if (row is not JsonObject item) throw new InvalidOperationException("compatibility.json reader rows are objects.");
            result.Add(new CompatibilityReader(Number(item, "version"), Text(item, "app_version")));
        }
        if (result.Select(row => row.Version).Distinct().Count() != result.Count) throw new InvalidOperationException("compatibility.json lists a format version twice.");
        return result.OrderBy(row => row.Version).ToList();
    }
}
