using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>
/// The saved Source browser identity (config/source-browser.json), with the same presets and rules as 0.4.x
/// source_browser.py. The source host starts with it (AUTOSOURCARR_SOURCE_USER_AGENT) and the Source browser page
/// changes it live.
/// </summary>
public static class SourceBrowserPreferences
{
    public const string FileName = "source-browser.json";

    public sealed record Preset(string Id, string Label, string Value);

    public static readonly IReadOnlyList<Preset> Presets =
    [
        new("default", "Engine default (reset)", ""),
        new("chrome-windows", "Chrome 120 · Windows", SourceEngineEnvironment.DefaultUserAgent),
        new("chrome-linux", "Chrome 120 · Linux", SourceEngineEnvironment.DefaultUserAgent.Replace("Windows NT 10.0; Win64; x64", "X11; Linux x86_64", StringComparison.Ordinal)),
        new("firefox-windows", "Firefox 128 · Windows", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0"),
        new("browser", "Use this browser", ""),
        new("custom", "Custom user agent", ""),
    ];

    public static JsonArray PresetsJson() =>
        new(Presets.Select(preset => (JsonNode)new JsonObject { ["id"] = preset.Id, ["label"] = preset.Label, ["value"] = preset.Value }).ToArray());

    /// <summary>Returns (preset, user agent) or throws ArgumentException with the 0.4.x message.</summary>
    public static (string Preset, string UserAgent) Validate(string? preset, string? value)
    {
        preset ??= "";
        var known = Presets.FirstOrDefault(row => row.Id == preset) ?? throw new ArgumentException("Select a known user-agent preset or Custom");
        var free = preset is "custom" or "browser";
        if (free && (value ?? "").IndexOfAny(['\r', '\n', '\t']) >= 0) throw new ArgumentException("User agent must not contain line breaks or tabs");
        var agent = free ? (value ?? "").Trim() : known.Value;
        if (free && agent.Length == 0) throw new ArgumentException("Enter a custom user agent or select Engine default");
        if (agent.Length > 1024 || agent.Any(character => character < 32 || character > 126))
            throw new ArgumentException("User agent must contain at most 1024 printable ASCII characters, without line breaks");
        return (preset, agent);
    }

    public static (string Preset, string UserAgent) Read(string configurationRoot)
    {
        var path = Path.Combine(configurationRoot, FileName);
        if (!File.Exists(path)) return ("default", "");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            return Validate(root.TryGetProperty("preset", out var preset) ? preset.GetString() : "default",
                root.TryGetProperty("user_agent", out var agent) ? agent.GetString() : "");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return ("default", ""); }
    }

    public static void Save(string configurationRoot, string preset, string userAgent)
    {
        Directory.CreateDirectory(configurationRoot);
        var target = Path.Combine(configurationRoot, FileName);
        var temporary = Path.Combine(configurationRoot, ".source-browser-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream))
                    new JsonObject { ["preset"] = preset, ["user_agent"] = userAgent }.WriteTo(writer);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
