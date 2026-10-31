using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Core.Repair;

public sealed class RawCodeFile
{
    private readonly byte[] _bytes;
    internal RawCodeFile(byte[] bytes, bool executable) { _bytes = bytes; Executable = executable; }
    public static RawCodeFile Create(ReadOnlySpan<byte> bytes, bool executable = false) => new(bytes.ToArray(), executable);
    public bool Executable { get; }
    public byte[] Contents() => (byte[])_bytes.Clone();
    public int Length => _bytes.Length;
    public string Sha256 => RawCodeRules.Digest(_bytes);
}

/// <summary>Bounded ZIP input, selected source module and public build resources.</summary>
public static class RawArchive
{
    private static readonly HashSet<string> PrivateNames = [".env", "local.properties", "auth.json", "cookies.json", "accounts.json", "signingkey.jks", "signing.properties", "secrets.properties", "credentials.json", ".netrc", ".npmrc", ".pypirc", ".git-credentials"];
    private static readonly HashSet<string> IgnoredDirectories = [".git", ".gradle", ".idea", ".codex", ".claude", ".agents", "build", "out", "node_modules", "__pycache__"];
    private static readonly HashSet<string> RootDirectories = ["src", "gradle", "lib", "lib-multisrc", "common", "core", "compiler", "res", "assets", "java", "kotlin"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static void ValidateContainer(byte[] payload, bool repository = false)
    { using var archive = Open(payload, repository ? RawCodeRules.MaximumRepositoryZip : RawCodeRules.MaximumZip, repository ? 65536 : RawCodeRules.MaximumFiles); }

    public static IReadOnlyDictionary<string, RawCodeFile> Read(byte[] payload, JsonObject metadata, bool repository = false)
    {
        var meta = RawCodeRules.Metadata(metadata); var module = RawCodeRules.Text(meta, "module");
        using var archive = Open(payload, repository ? RawCodeRules.MaximumRepositoryZip : RawCodeRules.MaximumZip, repository ? 65536 : RawCodeRules.MaximumFiles);
        var normalized = new List<(string Name, ZipArchiveEntry Entry)>();
        foreach (var entry in archive.Entries)
        {
            var name = RawCodeRules.RelativeName(entry.FullName.TrimEnd('/')); var mode = (uint)entry.ExternalAttributes >> 16;
            if (entry.IsEncrypted || (mode & 0xf000) is not (0 or 0x8000 or 0x4000)) throw RawCodeRules.Invalid("Raw source archives cannot contain links, special files or encryption");
            if (entry.FullName.EndsWith('/')) continue;
            normalized.Add((name, entry));
        }
        var first = normalized.Select(row => row.Name.Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray(); var prefix = "";
        if (first.Length == 1)
        {
            var candidate = first[0]; var rootNames = normalized.Where(row => row.Name.StartsWith(candidate + "/", StringComparison.Ordinal)).Select(row => row.Name[(candidate.Length + 1)..]).ToHashSet(StringComparer.Ordinal);
            if (!RootDirectories.Contains(candidate) && candidate != module.Split('/')[0] && new[] { "gradlew", "settings.gradle.kts", "settings.gradle", "LICENSE" }.Any(rootNames.Contains)) prefix = candidate + "/";
        }
        var files = new SortedDictionary<string, RawCodeFile>(StringComparer.Ordinal); long total = 0;
        foreach (var (original, entry) in normalized)
        {
            var name = RawCodeRules.RelativeName(prefix.Length == 0 ? original : original[prefix.Length..]);
            if (name == RawCodeRules.PortableName) continue;
            var parts = name.Split('/'); var lower = parts[^1].ToLowerInvariant();
            if (parts[..^1].Any(IgnoredDirectories.Contains)) continue;
            if (PrivateNames.Contains(lower) || lower.StartsWith(".env", StringComparison.Ordinal) || new[] { ".jks", ".keystore", ".pem", ".p12", ".pfx", ".key" }.Any(s => lower.EndsWith(s, StringComparison.Ordinal)))
                throw RawCodeRules.Invalid("Remove private settings, accounts or signing files from the raw-code archive");
            if (module.StartsWith("src/", StringComparison.Ordinal) && name.StartsWith("src/", StringComparison.Ordinal) && !name.StartsWith(module + "/", StringComparison.Ordinal)) continue;
            if (new[] { ".apk", ".dex", ".class" }.Any(s => lower.EndsWith(s, StringComparison.Ordinal)) || lower.EndsWith(".jar", StringComparison.Ordinal) && name != "gradle/wrapper/gradle-wrapper.jar")
                throw RawCodeRules.Invalid("Installed/compiled source packages are not shareable raw code");
            if (files.ContainsKey(name) || files.Count >= RawCodeRules.MaximumFiles || entry.Length is < 0 or > RawCodeRules.MaximumFile) throw RawCodeRules.Invalid("Raw source file is duplicate or too large");
            total += entry.Length; if (total > RawCodeRules.MaximumExpanded) throw RawCodeRules.Invalid("Expanded raw source archive exceeds160MiB");
            var data = archive.ReadEntry(entry, RawCodeRules.MaximumFile);
            if (data.AsSpan().IndexOf("-----BEGIN "u8) >= 0 && data.AsSpan().IndexOf("PRIVATE KEY-----"u8) >= 0) throw RawCodeRules.Invalid("Private key material is not accepted as source code");
            files.Add(name, new(data, ((uint)entry.ExternalAttributes >> 16 & 0x49) != 0 || name == "gradlew"));
        }
        var baseName = module == "." ? "" : module + "/";
        if (!files.Keys.Any(name => name.StartsWith(baseName, StringComparison.Ordinal) && (name.EndsWith(".kt", StringComparison.Ordinal) || name.EndsWith(".java", StringComparison.Ordinal))))
            throw RawCodeRules.Invalid("No Kotlin/Java source files exist in the selected project module");
        if (meta["overlay_sources"] is JsonArray overlays && overlays.Any(name => !files.ContainsKey(RawCodeRules.Text(name)))) throw RawCodeRules.Invalid("Reconstructed overlay references a missing source file");
        return files;
    }
    public static JsonObject? PortableMetadata(byte[] payload)
    {
        using var archive = Open(payload, RawCodeRules.MaximumZip, RawCodeRules.MaximumFiles);
        var entries = archive.Entries.Where(entry => entry.FullName == RawCodeRules.PortableName).ToArray();
        if (entries.Length == 0) return null;
        if (entries.Length != 1) throw RawCodeRules.Invalid("Invalid exported source provenance");
        var value = JsonNode.Parse(StrictUtf8.GetString(archive.ReadEntry(entries[0], 65536)), documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject;
        if (value is null || value.Count != 2 || value["schema"] is not JsonValue schema || !schema.TryGetValue<long>(out var number) || number != 1 || value["metadata"] is not JsonObject meta)
            throw RawCodeRules.Invalid("Invalid exported source provenance");
        return RawCodeRules.Metadata(meta);
    }
    public static byte[] Export(IEnumerable<KeyValuePair<string, RawCodeFile>> files, JsonObject? portableMetadata = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, file) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteEntry(archive, RawCodeRules.RelativeName(name), file.Contents(), file.Executable);
                if (output.Length > RawCodeRules.MaximumZip) throw RawCodeRules.Invalid("Raw source export exceeds32MiB");
            }
            if (portableMetadata is not null) WriteEntry(archive, RawCodeRules.PortableName, CanonicalJson.Bytes(new JsonObject { ["schema"] = 1, ["metadata"] = RawCodeRules.Metadata(portableMetadata) }), false);
        }
        if (output.Length > RawCodeRules.MaximumZip) throw RawCodeRules.Invalid("Raw source export exceeds32MiB");
        return output.ToArray();
    }
    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes, bool executable)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal); entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = (executable ? 0x81ed : 0x81a4) << 16;
        using var stream = entry.Open(); stream.Write(bytes);
    }
    private static CheckedZipArchive Open(byte[] payload, int maximum, int entries) =>
        CheckedZipArchive.Open(payload, maximum, entries, name => { _ = RawCodeRules.RelativeName(name.TrimEnd('/')); });
}
