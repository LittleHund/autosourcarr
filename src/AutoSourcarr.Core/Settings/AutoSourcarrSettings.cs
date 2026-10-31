using System.Text.Json.Serialization;

namespace AutoSourcarr.Core.Settings;

public sealed partial record AutoSourcarrSettings
{
    [JsonIgnore]
    public string EffectiveSourceUrl => SourceEngineMode == "integrated"
        ? "http://127.0.0.1:4567"
        : SourceSuwayomiUrl;

    [JsonIgnore]
    public string DatabaseBaseDir => DatabaseDir ?? DataDir;

    [JsonIgnore]
    public string CatalogPath => System.IO.Path.Combine(DatabaseBaseDir, "catalog.sqlite");

    [JsonIgnore]
    public string OriginsPath => System.IO.Path.Combine(DatabaseBaseDir, "chapter-origins.sqlite");

    [JsonIgnore]
    public IReadOnlyList<string> ContinuousCatalogModesList
    {
        get
        {
            var modes = Split(ContinuousCatalogModes, upper: true)
                .Where(mode => mode is "LATEST" or "POPULAR").ToArray();
            return modes.Length == 0 ? ["LATEST", "POPULAR"] : modes;
        }
    }

    [JsonIgnore]
    public IReadOnlyList<string> TranslationTargetLanguagesList => Split(TranslationTargetLanguages);

    [JsonIgnore]
    public IReadOnlySet<string> AllowedLanguages => Split(SourceLanguages).ToHashSet(StringComparer.Ordinal);

    public override string ToString() => "AutoSourcarrSettings (configuration values redacted)";

    private static string[] Split(string value, bool upper = false) => value.Split(',')
        .Select(part => part.Trim())
        .Where(part => part.Length > 0)
        .Select(part => upper ? part.ToUpperInvariant() : part.ToLowerInvariant()).ToArray();
}
