using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources.Inventory;

/// <summary>
/// The inventory part of source_maintenance.py: the daily maintained-source check switch
/// (&lt;configuration&gt;/source-maintenance.json) and the last repair promotion record
/// (&lt;maintenance&gt;/promotion.json). Running the daily check and promoting or rolling back repairs belong to the
/// repair runner; this class only reads and saves what the UI shows.
/// </summary>
public sealed class SourceMaintenanceState(string maintenanceRoot, string configurationRoot)
{
    public const string WatchFile = "source-maintenance.json";
    private readonly string _maintenance = Path.GetFullPath(maintenanceRoot);
    private readonly string _configuration = Path.GetFullPath(configurationRoot);
    private readonly object _gate = new();

    public string WatchPath => Path.Combine(_configuration, WatchFile);
    public string PromotionPath => Path.Combine(_maintenance, "promotion.json");

    /// <summary>The watch switch: enabled, next_check, attempts and the last message. A missing or damaged file reads as off.</summary>
    public JsonObject Watch()
    {
        lock (_gate) return ReadWatch();
    }

    /// <summary>Turns the daily check on or off; the next check becomes due at once (0.4.x PUT /watch).</summary>
    public JsonObject SetWatch(bool enabled)
    {
        lock (_gate)
        {
            var state = ReadWatch();
            state["enabled"] = enabled; state["next_check"] = 0;
            Directory.CreateDirectory(_configuration);
            var temporary = Path.Combine(_configuration, ".source-maintenance-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new Utf8JsonWriter(stream)) state.WriteTo(writer);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, WatchPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return (JsonObject)state.DeepClone();
        }
    }

    /// <summary>The last repair promotion record, or idle. The installed extension stays inside for orphan state checks.</summary>
    public JsonObject Operation()
    {
        var info = new FileInfo(PromotionPath);
        if (!info.Exists || info.LinkTarget is not null || info.Length > 16 * 1024 * 1024) return new JsonObject { ["state"] = "idle" };
        try { return JsonNode.Parse(File.ReadAllBytes(info.FullName), documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? new JsonObject { ["state"] = "idle" }; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { return new JsonObject { ["state"] = "unknown", ["message"] = "The last repair record cannot be read." }; }
    }

    /// <summary>The operation as the UI sees it: without the installed extension and the history plan (0.4.x snapshot).</summary>
    public JsonObject PublicOperation()
    {
        var operation = Operation();
        operation.Remove("extension"); operation.Remove("history_plan");
        return operation;
    }

    private JsonObject ReadWatch()
    {
        var path = WatchPath; var info = new FileInfo(path);
        if (info.Exists && info.LinkTarget is null && info.Length <= 1024 * 1024)
        {
            try { if (JsonNode.Parse(File.ReadAllBytes(path), documentOptions: new() { MaxDepth = 64 }) is JsonObject saved) return saved; }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return new JsonObject { ["enabled"] = false, ["next_check"] = 0, ["attempts"] = new JsonObject() };
    }
}
