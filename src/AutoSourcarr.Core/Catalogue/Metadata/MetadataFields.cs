using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Catalogue.Metadata;

/// <summary>
/// Runs inside the catalogue write that rebuilt an entry's native columns (CatalogueMetadata.ChooseMetadata) and may
/// replace them with a per-field choice. Implemented by <see cref="MetadataChooser"/>.
/// </summary>
public interface IMetadataFieldPolicy
{
    void Apply(DatabaseSession session, long entryId, string timestamp);
}

/// <summary>The fields the chooser decides per entry, and how an override of each is stored.</summary>
public static class MetadataFields
{
    public const string Title = "title";
    public const string Description = "description";
    public const string Authors = "authors";
    public const string Artists = "artists";
    public const string Genres = "genres";
    public const string Status = "status";
    public const string Year = "year";
    public const string Cover = "cover";

    /// <summary>In display order.</summary>
    public static IReadOnlyList<string> All { get; } = [Title, Description, Authors, Artists, Genres, Status, Year, Cover];

    /// <summary>The display_metadata.fields_json key of each overridable field except the title (titles live in preferred_title_overrides).</summary>
    public static IReadOnlyDictionary<string, string> OwnershipKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Description] = "description", [Cover] = "artwork", [Authors] = "author", [Artists] = "artist",
        [Genres] = "genres", [Status] = "status", [Year] = "year",
    };

    /// <summary>Fields whose override may name an attached source record instead of a value (the 0.4.5 "source" ownership).</summary>
    public static bool AllowsSourceOwnership(string field) => field is Description or Cover or Authors or Artists;

    /// <summary>Tachiyomi status codes (0 unknown, 1 ongoing, 2 completed, 3 licensed, 4 publishing finished, 5 cancelled, 6 on hiatus).</summary>
    public static long? StatusCode(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.GetValueKind() == JsonValueKind.Number)
            return value.TryGetValue<long>(out var number) && number is >= 0 and <= 6 ? number : null;
        if (!value.TryGetValue<string>(out var text)) return null;
        return text.Trim().ToLowerInvariant().Replace('_', ' ') switch
        {
            "unknown" or "" => 0,
            "ongoing" or "releasing" or "publishing" => 1,
            "completed" or "complete" or "finished" => 2,
            "licensed" => 3,
            "publishing finished" => 4,
            "cancelled" or "canceled" or "discontinued" => 5,
            "hiatus" or "on hiatus" => 6,
            _ => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 6 ? parsed : null,
        };
    }

    /// <summary>The provider status words of MetadataCandidate.Status as a Tachiyomi code.</summary>
    public static long ProviderStatus(string status) => (status ?? "").Trim().ToLowerInvariant() switch
    {
        "ongoing" => 1, "completed" => 2, "cancelled" => 5, "hiatus" => 6, _ => 0,
    };

    /// <summary>Comma separated names or genres from a string or an array, each once, in order.</summary>
    public static string JoinedNames(JsonNode? node)
    {
        IEnumerable<string> parts = node switch
        {
            JsonArray array => array.Select(item => item is JsonValue v && v.TryGetValue<string>(out var text) ? text : ""),
            JsonValue value when value.TryGetValue<string>(out var text) => text.Replace('|', ',').Split(','),
            _ => [],
        };
        return Join(parts);
    }

    public static string Join(IEnumerable<string> parts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<string>();
        foreach (var raw in parts)
        {
            var part = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (part.Length != 0 && seen.Add(part)) result.Add(part);
        }
        return string.Join(", ", result);
    }
}
