using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Repair.Runner.Guest;

public sealed record GuestProjectFileReference(string ArtifactId, string LogicalName, bool Executable);
public sealed record GuestProjectCommitRequest(string ProjectId, string ManifestArtifactId, IReadOnlyList<GuestProjectFileReference> Files);
/// <summary>A private complete-tree transaction, not source trust, execution, cold import or repair success.</summary>
public sealed record GuestProjectCommitReceipt(string TreeId, string ProjectId, string ManifestSha256, int Files, long Bytes);
internal sealed record GuestRawFile(long Length, string Sha256, bool Executable);

/// <summary>Composes the existing Core metadata/rules/canonical identity; no ZIP, XML or alternate canonical parser.</summary>
internal sealed class GuestRawProject
{
    private GuestRawProject(string id, byte[] manifest, Dictionary<string, GuestRawFile> files)
    { Id = id; Manifest = manifest; Files = new ReadOnlyDictionary<string, GuestRawFile>(files); }
    internal string Id { get; }
    internal byte[] Manifest { get; }
    internal IReadOnlyDictionary<string, GuestRawFile> Files { get; }
    internal static GuestRawProject Parse(byte[] bytes, string expectedProject)
    {
        if (bytes.Length is < 2 or > RawCodeRules.MaximumManifest) throw new InvalidDataException("Raw manifest exceeds its bound");
        RawCodeRules.Hash(expectedProject);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); CheckUnique(document.RootElement);
            var value = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw new InvalidDataException("Raw manifest must be an object");
            if (!Integer(value["schema"], out var schema) || schema != 1 || RawCodeRules.Text(value, "id") != expectedProject ||
                value["files"] is not JsonObject references || references.Count is < 1 or > RawCodeRules.MaximumFiles)
                throw new InvalidDataException("Raw project identity or file set is missing");
            var metadata = RawCodeRules.Metadata(new JsonObject(value.Where(pair => pair.Key is not ("schema" or "id" or "files")).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone()))));
            var files = new Dictionary<string, GuestRawFile>(StringComparer.Ordinal); long total = 0;
            foreach (var (name, node) in references)
            {
                if (RawCodeRules.RelativeName(name) != name || node is not JsonObject row || row.Count != 3 || !new[] { "sha256", "bytes", "executable" }.All(row.ContainsKey) ||
                    !Integer(row["bytes"], out var length) || length is < 0 or > RawCodeRules.MaximumFile || row["executable"] is not JsonValue executable || !executable.TryGetValue<bool>(out var mode))
                    throw new InvalidDataException("Raw project file identity or executable flag is invalid");
                var hash = RawCodeRules.Hash(RawCodeRules.Text(row, "sha256")); total += length;
                if (total > RawCodeRules.MaximumExpanded) throw new InvalidDataException("Raw project expanded bound exceeded"); files.Add(name, new(length, hash, mode));
            }
            Names(files.Keys);
            metadata["schema"] = 1; metadata["files"] = references.DeepClone();
            if (RawCodeRules.Digest(CanonicalJson.Bytes(metadata)) != expectedProject) throw new InvalidDataException("Raw project canonical fingerprint changed");
            metadata["id"] = expectedProject;
            if (!bytes.AsSpan().SequenceEqual(CanonicalJson.Bytes(metadata))) throw new InvalidDataException("The pinned raw manifest must retain its exact canonical bytes");
            return new(expectedProject, (byte[])bytes.Clone(), files);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Invalid canonical raw project manifest", error); }
    }
    internal static void Names(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (RawCodeRules.RelativeName(name) != name || !seen.Add(name)) throw new InvalidDataException("Raw project names collide on the actual filesystem");
            var parts = name.Split('/');
            if (OperatingSystem.IsWindows() && parts.Any(part => part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || Reserved(part)))
                throw new InvalidDataException("Raw project names cannot be represented exactly on this filesystem");
        }
        foreach (var name in seen)
        {
            var parts = name.Split('/'); var prefix = "";
            foreach (var part in parts[..^1]) { prefix = prefix.Length == 0 ? part : prefix + "/" + part; if (seen.Contains(prefix)) throw new InvalidDataException("Raw project file/directory collision"); }
        }
    }
    private static bool Reserved(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant(); return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }
    private static bool Integer(JsonNode? node, out long number)
    { number = 0; return node is JsonValue scalar && scalar.TryGetValue<JsonElement>(out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number); }
    private static void CheckUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!fields.Add(property.Name)) throw new InvalidDataException("Raw manifest contains duplicate fields"); CheckUnique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckUnique(item);
    }
}
