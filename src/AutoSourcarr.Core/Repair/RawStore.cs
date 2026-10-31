using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Repair;

/// <summary>Immutable public raw objects or private uploaded/reconstructed code, never installed binaries.</summary>
public sealed class RawStore
{
    public string Root { get; }
    public bool Public { get; }
    public RawStore(string root, bool publicCode = false)
    {
        Root = Path.GetFullPath(root); Public = publicCode;
        PrivateDocumentStore.ValidateDirectory(Root, Public);
    }
    public JsonObject PutZip(byte[] payload, JsonObject info)
    {
        var meta = RawCodeRules.Metadata(info); return Put(meta, RawArchive.Read(payload, meta));
    }
    public JsonObject PutRepositoryFile(string path, JsonObject info)
    {
        var meta = RawCodeRules.Metadata(info);
        var bytes = AtomicSourceFiles.Read(path, RawCodeRules.MaximumRepositoryZip) ?? throw new FileNotFoundException("Repository cache is absent");
        return Put(meta, RawArchive.Read(bytes, meta, repository: true));
    }
    private JsonObject Put(JsonObject meta, IReadOnlyDictionary<string, RawCodeFile> files)
    {
        var references = new JsonObject();
        foreach (var (name, file) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            references[name] = new JsonObject { ["sha256"] = file.Sha256, ["bytes"] = file.Length, ["executable"] = file.Executable };
        var manifest = (JsonObject)meta.DeepClone(); manifest["schema"] = 1; manifest["files"] = references;
        var id = RawCodeRules.Digest(CanonicalJson.Bytes(manifest)); manifest["id"] = id;
        var encoded = CanonicalJson.Bytes(manifest);
        if (encoded.Length > RawCodeRules.MaximumManifest) throw new InvalidDataException("Raw source receipt exceeds its metadata limit");
        // Every ZIP entry is validated before the first shared object can publish.
        foreach (var file in files.Values) AtomicSourceFiles.Write(ObjectPath(file.Sha256), file.Contents(), RawCodeRules.MaximumFile, Public, immutable: true);
        AtomicSourceFiles.Write(VersionPath(RawCodeRules.Text(meta, "package"), id), encoded, RawCodeRules.MaximumManifest, Public, immutable: true);
        return manifest;
    }
    public JsonObject Get(string package, string id)
    {
        var bytes = AtomicSourceFiles.Read(VersionPath(package, id), RawCodeRules.MaximumManifest) ?? throw new FileNotFoundException("Raw source version is absent");
        try
        {
            var value = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject;
            if (value is null || !Integer(value["schema"], out var schema) || schema != 1 || RawCodeRules.Text(value, "id") != id || RawCodeRules.Text(value, "package") != package)
                throw new InvalidDataException("Raw source receipt identity changed");
            var meta = MetadataOf(value);
            if (value["files"] is not JsonObject refs || refs.Count is < 1 or > RawCodeRules.MaximumFiles) throw new InvalidDataException("Raw source receipt file limit");
            long total = 0;
            foreach (var (name, node) in refs)
            {
                if (RawCodeRules.RelativeName(name) != name || node is not JsonObject row || row.Count != 3 || !row.ContainsKey("sha256") || !row.ContainsKey("bytes") || !row.ContainsKey("executable") ||
                    !Integer(row["bytes"], out var length) || length is < 0 or > RawCodeRules.MaximumFile || row["executable"] is not JsonValue executable || !executable.TryGetValue<bool>(out _))
                    throw new InvalidDataException("Raw source receipt file identity is invalid");
                RawCodeRules.Hash(RawCodeRules.Text(row, "sha256")); total += length;
            }
            meta["schema"] = 1; meta["files"] = refs.DeepClone();
            if (total > RawCodeRules.MaximumExpanded || RawCodeRules.Digest(CanonicalJson.Bytes(meta)) != id) throw new InvalidDataException("Raw source receipt fingerprint changed");
            return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("Raw source receipt is invalid", error); }
    }
    public byte[] ExportZip(string package, string id, bool portable = false)
    {
        var value = Get(package, id); return RawArchive.Export(Contents(value), portable ? MetadataOf(value) : null);
    }
    public JsonObject Materialize(string package, string id, string target)
    {
        target = Path.GetFullPath(target); PrivateDocumentStore.ValidateDirectory(target);
        if (Directory.EnumerateFileSystemEntries(target).Any()) throw new InvalidDataException("Raw code requires an empty private job directory");
        var value = Get(package, id); var names = value["files"]!.AsObject().Select(pair => pair.Key).ToArray();
        ValidateMaterializationNames(names);
        foreach (var (name, file) in Contents(value))
        {
            var path = Path.GetFullPath(Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Raw source path escaped its private job directory");
            AtomicSourceFiles.Write(path, file.Contents(), RawCodeRules.MaximumFile, immutable: true, executable: file.Executable);
        }
        return value;
    }
    public IReadOnlyList<JsonObject> Versions(string package, int limit = 50)
    {
        RawCodeRules.Package(package); var root = Path.Combine(Root, "versions", PackageKey(package));
        if (!Directory.Exists(root)) return [];
        PrivateDocumentStore.ValidateDirectory(root, Public);
        var paths = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Take(4097).ToArray();
        if (paths.Length > 4096) throw new InvalidDataException("Use a specific raw version; source history exceeds the listing limit");
        return paths.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 100))
            .Select(path => Get(package, Path.GetFileNameWithoutExtension(path))).ToArray();
    }
    private IEnumerable<KeyValuePair<string, RawCodeFile>> Contents(JsonObject value)
    {
        foreach (var (name, node) in value["files"]!.AsObject().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var row = node!.AsObject(); var hash = RawCodeRules.Text(row, "sha256");
            if (!Integer(row["bytes"], out var length)) throw new InvalidDataException("Invalid raw source length");
            var bytes = AtomicSourceFiles.Read(ObjectPath(hash), checked((int)length)) ?? throw new FileNotFoundException("Raw source object is absent");
            if (bytes.Length != length || RawCodeRules.Digest(bytes) != hash) throw new InvalidDataException("Raw source object failed its integrity check");
            yield return new(name, RawCodeFile.Create(bytes, row["executable"]!.GetValue<bool>()));
        }
    }
    private string ObjectPath(string hash) { RawCodeRules.Hash(hash); return Path.Combine(Root, "objects", hash[..2], hash[2..]); }
    private string VersionPath(string package, string id) { RawCodeRules.Hash(id); return Path.Combine(Root, "versions", PackageKey(package), id + ".json"); }
    private static string PackageKey(string package) => RawCodeRules.Digest(Encoding.UTF8.GetBytes(RawCodeRules.Package(package)));
    private static JsonObject MetadataOf(JsonObject value) => RawCodeRules.Metadata(new JsonObject(value.Where(pair => pair.Key is not ("schema" or "id" or "files")).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.DeepClone()))));
    private static bool Integer(JsonNode? node, out long number)
    {
        number = 0;
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out number);
    }
    private static void ValidateMaterializationNames(string[] names)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!seen.Add(name)) throw new InvalidDataException("Raw project names collide on this filesystem");
            var parts = name.Split('/');
            if (OperatingSystem.IsWindows() && parts.Any(part => part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                Regex.IsMatch(part.Split('.')[0], "\\A(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\\z", RegexOptions.CultureInvariant)))
                throw new InvalidDataException("Raw project names cannot be represented safely on this filesystem");
        }
        foreach (var name in names)
        {
            var parts = name.Split('/'); var prefix = "";
            foreach (var part in parts[..^1]) { prefix = prefix.Length == 0 ? part : prefix + "/" + part; if (seen.Contains(prefix)) throw new InvalidDataException("Raw project file/directory names collide"); }
        }
    }
}
