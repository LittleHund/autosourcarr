using System.Globalization;
using System.Text.Json;
using AutoSourcarr.Core.Repair.Runner.Protocol;

namespace AutoSourcarr.Core.Repair.Runner.Guest;

internal static class GuestWire
{
    internal static void Identity(string value, int length)
    { if (value is null || value.Length != length || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("Invalid guest protocol identity"); }
    internal static void Exact(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Guest payload must be an object"); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!fields.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException("Ambiguous guest payload fields");
        if (seen.Count != fields.Length) throw new InvalidDataException("Incomplete guest payload");
    }
    internal static string Text(JsonElement value, string field) => value.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : throw new InvalidDataException("Missing guest string field");
    internal static string? OptionalText(JsonElement value, string field) => value.GetProperty(field).ValueKind == JsonValueKind.Null ? null : Text(value, field);
    internal static long Number(JsonElement value, string field) => value.TryGetProperty(field, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var parsed) ? parsed : throw new InvalidDataException("Missing exact guest integer field");
    internal static string RequestId(JsonElement frame, long expected)
    {
        var id = Text(frame, "id");
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number != expected || id != number.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("Guest request identity changed or was replayed"); return id;
    }
    internal static GuestArtifactDescriptor Descriptor(JsonElement payload, GuestArtifactDirection direction)
    {
        Exact(payload, "operation", "artifact_id", "role", "length", "sha256", "logical_name", "project_id"); var name = Text(payload, "role");
        if (!Enum.TryParse<GuestArtifactRole>(name, out var role) || role.ToString() != name) throw new InvalidDataException("Unknown guest artifact role");
        var descriptor = new GuestArtifactDescriptor(role, Number(payload, "length"), Text(payload, "sha256"), OptionalText(payload, "logical_name"), OptionalText(payload, "project_id")); descriptor.Validate(direction); return descriptor;
    }
    internal static void Ack(JsonElement value, string artifact, string phase, int index, long offset)
    {
        Exact(value, "phase", "artifact_id", "index", "next_offset");
        if (Text(value, "phase") != phase || Text(value, "artifact_id") != artifact || Number(value, "index") != index || Number(value, "next_offset") != offset) throw new InvalidDataException("Guest artifact acknowledgement changed");
    }
    internal static void Commit(JsonElement value, string artifact, GuestArtifactDescriptor descriptor, int chunks)
    {
        Exact(value, "phase", "artifact_id", "length", "sha256", "chunks");
        if (Text(value, "phase") != "commit" || Text(value, "artifact_id") != artifact || Number(value, "length") != descriptor.Length || Text(value, "sha256") != descriptor.Sha256 || Number(value, "chunks") != chunks) throw new InvalidDataException("Guest artifact commit changed its exact identity");
    }
}
