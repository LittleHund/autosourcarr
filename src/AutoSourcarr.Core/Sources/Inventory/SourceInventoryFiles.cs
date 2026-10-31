using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Inventory;

internal static class SourceInventoryFiles
{
    public const int PackageLimit = SourcePackage.MaximumBytes;
    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Text(JsonObject value, string name) => value[name]?.GetValueKind() == JsonValueKind.String ? value[name]!.GetValue<string>() : "";
    public static bool IsDigest(string value)
    { try { RawCodeRules.Hash(value); return true; } catch (InvalidDataException) { return false; } }
    public static bool IsApkPackage(string value) => Bounded(value, @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){2,}", 200);
    public static bool IsInstalledFilename(string value) => Bounded(value, @"[A-Za-z0-9_.+\-]+\.(?:apk|jar)", 1024);
    public static bool IsDexName(string value) => Bounded(value, @"classes(?:[2-9][0-9]*)?\.dex", 256);
    private static bool Bounded(string value, string pattern, int maximum) => value.Length <= maximum && Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static void ExistingDirectory(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (!current.Exists || current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0 || File.Exists(current.FullName)) throw new IOException("Source directory is missing, linked or unavailable");
    }
    public static byte[] ReadRequired(string path, int maximum)
    {
        ExistingDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = AtomicSourceFiles.Read(path, maximum) ?? throw new FileNotFoundException("Retained source file is absent");
        if (bytes.Length == 0) throw new InvalidDataException("Retained source file is empty"); return bytes;
    }
    public static JsonObject ReadObject(string path, int maximum)
    {
        var bytes = ReadRequired(path, maximum);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes); using var document = JsonDocument.Parse(text, new() { MaxDepth = 64 });
            Duplicates(document.RootElement); return JsonNode.Parse(text, documentOptions: new() { MaxDepth = 64 }) as JsonObject ?? throw new InvalidDataException("Retained source receipt is not an object");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw new InvalidDataException("Retained source receipt is not bounded UTF-8 JSON", error); }
    }
    private static void Duplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { var keys = new HashSet<string>(StringComparer.Ordinal); foreach (var field in value.EnumerateObject()) { if (!keys.Add(field.Name)) throw new InvalidDataException("Retained source receipt contains duplicate fields"); Duplicates(field.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Duplicates(item);
    }
}
