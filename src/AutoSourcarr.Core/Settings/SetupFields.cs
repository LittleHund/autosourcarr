using System.Reflection;
using System.Text.Json;

namespace AutoSourcarr.Core.Settings;

public static class SetupFields
{
    public static IReadOnlyDictionary<string, JsonElement> ByKey { get; } = Load();

    private static IReadOnlyDictionary<string, JsonElement> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AutoSourcarr.Core.Settings.setup-fields.json")
            ?? throw new InvalidDataException("Missing setup field definitions.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().ToDictionary(
            field => field.GetProperty("key").GetString()!, field => field.Clone(), StringComparer.Ordinal);
    }
}
