using System.Globalization;
using System.Text;
using System.Text.Json;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Imaging;

internal sealed class CoverCacheFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _root;
    public CoverCacheFiles(string dataDirectory)
    {
        _root = Path.GetFullPath(Path.Combine(dataDirectory, "cover-cache")); PrivateDocumentStore.ValidateDirectory(_root);
    }
    internal static string SafeKey(string key) => string.Concat(key.EnumerateRunes().Select(rune =>
        rune.Value is '-' or '_' or '.' || Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber ? rune.ToString() : "_"));
    private (string Image, string Metadata) Paths(string key)
    {
        var name = SafeKey(key); return (Path.Combine(_root, name + ".img"), Path.Combine(_root, name + ".json"));
    }
    public CoverData? Read(string key, double ttlSeconds, DateTimeOffset now)
    {
        try
        {
            var paths = Paths(key); var age = (now.UtcDateTime - File.GetLastWriteTimeUtc(paths.Image)).TotalSeconds;
            if (age > ttlSeconds) return null;
            var bytes = AtomicSourceFiles.Read(paths.Image, CoverCache.MaximumImageBytes); if (bytes is null) return null;
            var type = "image/jpeg"; var meta = AtomicSourceFiles.Read(paths.Metadata, 64 * 1024);
            if (meta is not null)
            {
                using var document = JsonDocument.Parse(Utf8.GetString(meta));
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                if (document.RootElement.TryGetProperty("content_type", out var content))
                {
                    if (content.ValueKind is JsonValueKind.Null or JsonValueKind.False || content.ValueKind == JsonValueKind.String && content.GetString()!.Length == 0
                        || content.ValueKind == JsonValueKind.Number && content.TryGetDouble(out var number) && number == 0
                        || content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 0
                        || content.ValueKind == JsonValueKind.Object && !content.EnumerateObject().Any()) { }
                    else if (content.ValueKind != JsonValueKind.String) return null;
                    else type = content.GetString()!;
                }
            }
            return new(bytes, type);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or DecoderFallbackException or ArgumentException) { return null; }
    }
    public double Remaining(string key, double ttlSeconds, DateTimeOffset now)
    {
        try
        {
            var path = Paths(key).Image; PrivateDocumentStore.ValidateDirectory(_root);
            using var file = PrivateDocumentStore.OpenRegular(path, writable: false, create: false, CoverCache.MaximumImageBytes);
            if (file.Length == 0) return double.NegativeInfinity;
            return ttlSeconds - (now.UtcDateTime - File.GetLastWriteTimeUtc(path)).TotalSeconds;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return double.NegativeInfinity; }
    }
    public bool Write(string key, CoverData value)
    {
        if (value.Content.Length > CoverCache.MaximumImageBytes) throw new ArgumentException("Cover exceeds the 32 MiB cache limit");
        if (value.Content.IsEmpty) return false;
        var paths = Paths(key); var metadata = Encoding.UTF8.GetBytes("{\"content_type\": " + CanonicalJson.String(value.ContentType.Length == 0 ? "image/jpeg" : value.ContentType) + "}");
        // Each replacement is durable on its own; this pair is deliberately not advertised as crash-atomic.
        AtomicSourceFiles.Write(paths.Image, value.Content.ToArray(), CoverCache.MaximumImageBytes);
        AtomicSourceFiles.Write(paths.Metadata, metadata, 64 * 1024); return true;
    }
    public int Clear(IReadOnlyList<string> keys)
    {
        PrivateDocumentStore.ValidateDirectory(_root); var count = 0;
        foreach (var key in keys)
        {
            var paths = Paths(key); foreach (var path in new[] { paths.Image, paths.Metadata }) count += Delete(path);
        }
        return count;
    }
    public int ClearAll()
    {
        PrivateDocumentStore.ValidateDirectory(_root); var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(_root))
        {
            var name = Path.GetFileName(path); var extension = Path.GetExtension(name);
            if (extension is ".img" or ".json" or ".tmp" || name.StartsWith(".source-", StringComparison.Ordinal)) count += Delete(path);
        }
        return count;
    }
    private static int Delete(string path)
    {
        try
        {
            // Delete only this directory entry. A linked directory is never traversed.
            if (!File.Exists(path) && new FileInfo(path).LinkTarget is null) return 0;
            File.Delete(path); return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }
}
