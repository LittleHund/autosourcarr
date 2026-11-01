using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Jobs;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Core.Connections.Types;

/// <summary>One main-library manga to import: its Suwayomi id, source and source-side url/title.</summary>
public sealed record SuwayomiImportItem(long MangaId, string SourceId, string SourceName, string SourceLang, string Url, string RealUrl,
    string Title, string ThumbnailUrl)
{
    public JsonObject ToJson() => new()
    {
        ["manga_id"] = MangaId, ["source_id"] = SourceId, ["source_name"] = SourceName, ["source_lang"] = SourceLang,
        ["url"] = Url, ["real_url"] = RealUrl, ["title"] = Title, ["thumbnail_url"] = ThumbnailUrl,
    };

    public static SuwayomiImportItem? FromJson(JsonObject row)
        => JsonRead.Long(row["manga_id"]) is long id && id > 0
            ? new(id, JsonRead.Text(row["source_id"]), JsonRead.Text(row["source_name"]), JsonRead.Text(row["source_lang"]),
                JsonRead.Text(row["url"]), JsonRead.Text(row["real_url"]), JsonRead.Text(row["title"]), JsonRead.Text(row["thumbnail_url"]))
            : null;
}

/// <summary>
/// CON-3 one-time import: a snapshot of the main Suwayomi library (manga with source, url and title) saved as JSON under
/// &lt;data&gt;/suwayomi-import/plan.json. Creating catalogue entries from it needs the matching/ingest path of conversion
/// phases 8/9, which the C# app does not have yet, so a stored plan has status <see cref="SuwayomiImport.AwaitingMatching"/>.
/// </summary>
public sealed record SuwayomiImportPlan(string CreatedAt, string ServerUrl, string Status, IReadOnlyList<SuwayomiImportItem> Items)
{
    public int Total => Items.Count;
    public int Sources => Items.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count();

    /// <summary>Counts per source, largest first.</summary>
    public IReadOnlyList<(string SourceId, string SourceName, int Count)> BySource => Items
        .GroupBy(item => item.SourceId, StringComparer.Ordinal)
        .Select(group => (group.Key, group.Select(item => item.SourceName).FirstOrDefault(name => name.Length > 0) ?? "", group.Count()))
        .OrderByDescending(row => row.Item3).ThenBy(row => row.Key, StringComparer.Ordinal).ToList();

    public JsonObject Counts() => new()
    {
        ["total"] = Total, ["sources"] = Sources,
        ["by_source"] = new JsonArray(BySource.Select(row => (JsonNode)new JsonObject
            { ["source_id"] = row.SourceId, ["source_name"] = row.SourceName, ["count"] = row.Count }).ToArray()),
    };

    public JsonObject ToJson() => new()
    {
        ["version"] = 1, ["created_at"] = CreatedAt, ["server_url"] = ServerUrl, ["status"] = Status, ["counts"] = Counts(),
        ["items"] = new JsonArray(Items.Select(item => (JsonNode)item.ToJson()).ToArray()),
    };

    public static SuwayomiImportPlan FromJson(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (JsonRead.Int(root["version"]) != 1) throw new InvalidDataException("Unknown Suwayomi import plan version.");
        var items = JsonRead.Objects(root["items"]).Select(SuwayomiImportItem.FromJson).OfType<SuwayomiImportItem>().ToList();
        return new SuwayomiImportPlan(JsonRead.Text(root["created_at"]), JsonRead.Text(root["server_url"]), JsonRead.Text(root["status"]), items);
    }
}

public static class SuwayomiImport
{
    public const string AwaitingMatching = "awaiting-matching";
    public const string AwaitingMatchingNote = "Stored; creating catalogue entries waits for matching and ingest (conversion phase 8).";
    public const string FolderName = "suwayomi-import";
    public static readonly JobType Job = new("suwayomi-import", "Import the main Suwayomi library", JobPriority.Low, StallTimeout: TimeSpan.FromMinutes(5));
    private const string ImportFields = "id sourceId url realUrl title thumbnailUrl source { id name lang }";
    private const string ImportFieldsWithoutSource = "id sourceId url realUrl title thumbnailUrl";

    public static string PlanPath(string dataDirectory) => Path.Combine(dataDirectory, FolderName, "plan.json");

    /// <summary>
    /// Builds the plan from library rows (SuwayomiClient.LibraryMangasAsync or <see cref="ReadAsync"/>) and optionally the
    /// source list (SuwayomiClient.SourcesAsync) for names. Rows without an id, source or url are left out.
    /// </summary>
    public static SuwayomiImportPlan Build(IEnumerable<JsonObject> library, IEnumerable<JsonObject>? sources, string serverUrl, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(library);
        var names = new Dictionary<string, (string Name, string Lang)>(StringComparer.Ordinal);
        foreach (var source in sources ?? [])
            if (JsonRead.Text(source["id"]) is { Length: > 0 } id) names[id] = (JsonRead.Text(source["displayName"]) is { Length: > 0 } display ? display : JsonRead.Text(source["name"]), JsonRead.Text(source["lang"]));
        var items = new List<SuwayomiImportItem>();
        var seen = new HashSet<long>();
        foreach (var row in library)
        {
            if (JsonRead.Long(row["id"]) is not long id || id <= 0 || !seen.Add(id)) continue;
            var nested = row["source"] as JsonObject;
            var sourceId = JsonRead.Text(row["sourceId"]);
            if (sourceId.Length == 0) sourceId = JsonRead.Text(nested?["id"]);
            var url = JsonRead.Text(row["url"]);
            if (sourceId.Length == 0 || url.Length == 0) continue;
            var (name, lang) = names.TryGetValue(sourceId, out var known) ? known : (JsonRead.Text(nested?["name"]), JsonRead.Text(nested?["lang"]));
            items.Add(new SuwayomiImportItem(id, sourceId, name, lang, url, JsonRead.Text(row["realUrl"]), JsonRead.Text(row["title"]), JsonRead.Text(row["thumbnailUrl"])));
        }
        items.Sort((left, right) => left.MangaId.CompareTo(right.MangaId));
        return new SuwayomiImportPlan(at.ToString("O", CultureInfo.InvariantCulture), serverUrl, AwaitingMatching, items);
    }

    /// <summary>Reads the whole main library over GraphQL (read-only) and builds the plan.</summary>
    public static async Task<SuwayomiImportPlan> ReadAsync(SuwayomiGraphQlHttp graph, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        List<JsonObject> rows;
        try { rows = await graph.LibraryAsync(ImportFields, cancellationToken).ConfigureAwait(false); }
        catch (ConnectionHttpException error) when (error.Message.Contains("GraphQL error", StringComparison.Ordinal))
        { rows = await graph.LibraryAsync(ImportFieldsWithoutSource, cancellationToken).ConfigureAwait(false); }
        return Build(rows, null, graph.Endpoint.BaseUri.AbsoluteUri, at);
    }

    public static void Save(string dataDirectory, SuwayomiImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ConfigFiles.WriteAtomic(PlanPath(dataDirectory), Encoding.UTF8.GetBytes(plan.ToJson().ToJsonString()));
    }

    /// <summary>The stored plan; null when none was made yet.</summary>
    public static SuwayomiImportPlan? Load(string dataDirectory)
    {
        var path = PlanPath(dataDirectory);
        if (!File.Exists(path)) return null;
        var root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw new InvalidDataException("The Suwayomi import plan is not a JSON object.");
        return SuwayomiImportPlan.FromJson(root);
    }

    /// <summary>
    /// Queues the one-time import (one at a time: the job key merges repeats). With a plan already stored it does nothing
    /// unless <paramref name="force"/>. The job reads the library with <paramref name="read"/>, stores the plan and ends
    /// with "awaiting matching (phase 8)".
    /// </summary>
    public static JobHandle Enqueue(JobQueue jobs, string dataDirectory, Func<CancellationToken, Task<SuwayomiImportPlan>> read,
        bool force = false, string origin = "manual", Action<SuwayomiImportPlan>? stored = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(read);
        return jobs.Enqueue(Job, "main", async context =>
        {
            if (!force && Load(dataDirectory) is { } existing)
            {
                context.Result("Already imported " + existing.Total.ToString(CultureInfo.InvariantCulture) + " manga on " + existing.CreatedAt + "; " + AwaitingMatchingNote);
                return;
            }
            context.Report(message: "Reading the main Suwayomi library");
            var plan = await read(context.CancellationToken).ConfigureAwait(false);
            context.Report(plan.Total, plan.Total, "Saving the import plan");
            Save(dataDirectory, plan);
            stored?.Invoke(plan);
            context.Result("Read " + plan.Total.ToString(CultureInfo.InvariantCulture) + " manga from " + plan.Sources.ToString(CultureInfo.InvariantCulture)
                + " sources; " + AwaitingMatchingNote);
        }, origin: origin, title: Job.Title);
    }
}
