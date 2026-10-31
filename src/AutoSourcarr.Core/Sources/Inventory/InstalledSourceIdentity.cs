using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Sources.Inventory;

public static class InstalledSourceIdentity
{
    /// <summary>Released identity_hash: sorted native (id,lang,name) tuples with Python's spaced ASCII JSON.</summary>
    public static string Hash(JsonObject extension)
    {
        if (extension["source"] is not JsonObject source || source["nodes"] is not JsonArray rows || rows.Any(row => row is not JsonObject)) throw new InvalidDataException("Installed source identity connection is incomplete");
        var tuples = rows.OfType<JsonObject>().Select(row => (Id: Python(row, "id", required: true), Language: Python(row, "lang"), Name: Python(row, "name")))
            .OrderBy(row => row.Id, UnicodeCodePointComparer.Instance).ThenBy(row => row.Language, UnicodeCodePointComparer.Instance).ThenBy(row => row.Name, UnicodeCodePointComparer.Instance);
        var text = "[" + string.Join(", ", tuples.Select(row => "[" + string.Join(", ", new[] { row.Id, row.Language, row.Name }.Select(CanonicalJson.String)) + "]")) + "]";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    private static string Python(JsonObject row, string name, bool required = false)
    {
        if (!row.ContainsKey(name)) return required ? throw new InvalidDataException("Installed source ID is absent") : "";
        var value = row[name]; if (value is null) return "None";
        return value.GetValueKind() switch { JsonValueKind.String => value.GetValue<string>(), JsonValueKind.True => "True", JsonValueKind.False => "False", JsonValueKind.Number when long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) => id.ToString(CultureInfo.InvariantCulture), _ => throw new InvalidDataException("Installed source identity is not a scalar") };
    }
}
