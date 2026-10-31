using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Repair;

public sealed record SourcePackagePayload(int Length, string Sha256);

/// <summary>Comparison facts from a rejected conversion. They establish no loading identity.</summary>
public sealed record SourceConversionComparison(int InstalledEntries, int ConvertedEntries, int IdenticalEntries,
    int ChangedEntries, int MissingEntries, int AddedEntries, int ChangedClassEntries, int ChangedResourceEntries,
    int MissingClassEntries, int AddedClassEntries, string InstalledJarSha256, string ConvertedJarSha256)
{
    public JsonObject ToJson() => new()
    {
        ["installed_entries"] = InstalledEntries, ["converted_entries"] = ConvertedEntries, ["identical_entries"] = IdenticalEntries,
        ["changed_entries"] = ChangedEntries, ["missing_entries"] = MissingEntries, ["added_entries"] = AddedEntries,
        ["changed_class_entries"] = ChangedClassEntries, ["changed_resource_entries"] = ChangedResourceEntries,
        ["missing_class_entries"] = MissingClassEntries, ["added_class_entries"] = AddedClassEntries,
        ["installed_jar_sha256"] = InstalledJarSha256, ["converted_jar_sha256"] = ConvertedJarSha256
    };
}

public sealed class SourceConversionMismatch(SourceConversionComparison comparison) : IOException(
    $"Retained APK conversion does not match the installed JAR payloads (changed {comparison.ChangedEntries}, missing {comparison.MissingEntries}, added {comparison.AddedEntries}); no identity was inferred")
{
    public SourceConversionComparison Comparison { get; } = comparison;
}

/// <summary>Exact installed payloads plus retained APK metadata for a caller's isolated cold import.
/// These fingerprints never assert that a cold import or source validation succeeded.</summary>
public sealed class SourceValidationEnvelope
{
    private readonly byte[] _bytes;
    internal SourceValidationEnvelope(byte[] bytes, SourceManifestIdentity identity, Dictionary<string, string> fingerprints)
    { _bytes = bytes; Identity = identity; Fingerprints = new ReadOnlyDictionary<string, string>(fingerprints); }
    public SourceManifestIdentity Identity { get; }
    public IReadOnlyDictionary<string, string> Fingerprints { get; }
    public byte[] Contents() => (byte[])_bytes.Clone();
}

/// <summary>Compiled package integrity and exact decompressed JAR rollback comparison.</summary>
public static class SourcePackage
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const int MaximumEntries = 4096;
    public const int MaximumExpanded = 160 * 1024 * 1024;
    private static readonly Regex VersionCode = new(@"\A[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, SourcePackagePayload> Payloads(byte[] data)
    {
        using var archive = Open(data);
        return Payloads(archive);
    }

    /// <summary>Prepare the original installed bytes with the APK loading contract. This is not conversion equivalence.</summary>
    public static SourceValidationEnvelope InstalledEnvelope(byte[] baseline, byte[] apk) => BuildEnvelope((byte[])baseline.Clone(), (byte[])apk.Clone(), null);

    /// <summary>Require equality of every decompressed payload before adding retained APK metadata.</summary>
    public static SourceValidationEnvelope RestoreManifest(byte[] baseline, byte[] apk, byte[] converted)
    {
        baseline = (byte[])baseline.Clone(); apk = (byte[])apk.Clone(); converted = (byte[])converted.Clone();
        var installed = Payloads(baseline); var checkedPayloads = Payloads(converted);
        if (installed.ContainsKey("AndroidManifest.xml")) throw Invalid("Installed JAR already has a native manifest");
        if (installed.Count != checkedPayloads.Count || installed.Any(pair => !checkedPayloads.TryGetValue(pair.Key, out var payload) || pair.Value != payload))
            throw new SourceConversionMismatch(Compare(installed, checkedPayloads, baseline, converted));
        return BuildEnvelope(baseline, apk, Digest(converted));
    }

    private static SourceValidationEnvelope BuildEnvelope(byte[] baseline, byte[] apk, string? convertedDigest)
    {
        using var installedArchive = Open(baseline); var installed = Payloads(installedArchive);
        if (installed.ContainsKey("AndroidManifest.xml")) throw Invalid("Installed JAR already has a native manifest");
        using var apkArchive = Open(apk); var apkPayloads = Payloads(apkArchive);
        if (!apkPayloads.ContainsKey("classes.dex")) throw Invalid("Retained package is not the original Android APK");
        var manifestInfo = apkArchive.Entries.SingleOrDefault(entry => entry.FullName == "AndroidManifest.xml") ?? throw Invalid("Package has no Android manifest");
        if (manifestInfo.Length > SourceManifest.MaximumBytes) throw Invalid("APK manifest exceeds the size limit");
        var root = SourceManifest.Parse(apkArchive.ReadEntry(manifestInfo, SourceManifest.MaximumBytes));
        var identity = SourceManifest.Identity(root);
        if (!installed.ContainsKey(identity.ClassName.Replace('.', '/') + ".class")) throw Invalid("Original APK entrypoint is absent from the installed JAR");
        if (!VersionCode.IsMatch((string?)root.Attribute(SourceManifest.Android + "versionCode") ?? "") || string.IsNullOrEmpty((string?)root.Attribute(SourceManifest.Android + "versionName")))
            throw Invalid("Original APK version metadata is unavailable");
        if (!root.Elements("uses-feature").Any(element => (string?)element.Attribute(SourceManifest.Android + "name") == "tachiyomi.extension"))
            throw Invalid("Original APK is not a Tachiyomi extension");
        var xml = SourceManifest.XmlBytes(root);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var info in installedArchive.Entries)
            {
                var entry = target.CreateEntry(info.FullName, installedArchive.CompressionMethod(info) == 0 ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = info.LastWriteTime; entry.ExternalAttributes = info.ExternalAttributes;
                using (var stream = entry.Open()) stream.Write(installedArchive.ReadEntry(info, MaximumBytes));
                if (output.Length > MaximumBytes) throw Invalid("Validation envelope exceeds the package limit");
            }
            var manifest = target.CreateEntry("AndroidManifest.xml", CompressionLevel.Optimal);
            manifest.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var stream = manifest.Open()) stream.Write(xml);
        }
        if (output.Length > MaximumBytes) throw Invalid("Validation envelope exceeds the package limit");
        var envelope = output.ToArray();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source_apk_sha256"] = Digest(apk), ["installed_jar_sha256"] = Digest(baseline),
            ["payload_sha256"] = PayloadFingerprint(installed), ["validation_envelope_sha256"] = Digest(envelope)
        };
        if (convertedDigest is not null) fingerprints["converted_jar_sha256"] = convertedDigest;
        return new(envelope, identity, fingerprints);
    }

    private static CheckedZipArchive Open(byte[] data)
    {
        if (data.Length <= 100 || data.Length > MaximumBytes) throw Invalid("Package is empty or exceeds the size limit");
        var archive = CheckedZipArchive.Open(data, MaximumBytes, MaximumEntries, ValidateName);
        try
        {
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                var mode = (uint)entry.ExternalAttributes >> 16;
                if (entry.IsEncrypted || (mode & 0xf000) == 0xa000 || entry.Length < 0 || entry.Length > MaximumBytes) throw Invalid("Package has an unsafe entry");
                expanded += entry.Length;
                if (expanded > MaximumExpanded) throw Invalid("Package has duplicate or oversized entries");
            }
            return archive;
        }
        catch { archive.Dispose(); throw; }
    }
    private static IReadOnlyDictionary<string, SourcePackagePayload> Payloads(CheckedZipArchive archive)
    {
        var result = new Dictionary<string, SourcePackagePayload>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
            if (!entry.FullName.EndsWith('/'))
            {
                var payload = archive.ReadEntry(entry, MaximumBytes); result.Add(entry.FullName, new(payload.Length, Digest(payload)));
            }
        return new ReadOnlyDictionary<string, SourcePackagePayload>(result);
    }
    private static void ValidateName(string name)
    {
        // Compiled packages may contain classes, DEX, signatures, resources and private-looking
        // resource names. Raw-code module selection and private-file exclusions do not apply.
        if (name.StartsWith('/') || name.Contains('\\') || name.Contains('\0') || name.Split('/').Contains("..", StringComparer.Ordinal))
            throw Invalid("Package has an unsafe entry");
    }
    private static SourceConversionComparison Compare(IReadOnlyDictionary<string, SourcePackagePayload> installed,
        IReadOnlyDictionary<string, SourcePackagePayload> converted, byte[] baseline, byte[] checkedJar)
    {
        var shared = installed.Keys.Intersect(converted.Keys, StringComparer.Ordinal).ToArray();
        var changed = shared.Where(name => installed[name] != converted[name]).ToArray();
        var missing = installed.Keys.Except(converted.Keys, StringComparer.Ordinal).ToArray();
        var added = converted.Keys.Except(installed.Keys, StringComparer.Ordinal).ToArray();
        static int Classes(IEnumerable<string> names) => names.Count(name => name.EndsWith(".class", StringComparison.Ordinal));
        return new(installed.Count, converted.Count, shared.Length - changed.Length, changed.Length, missing.Length, added.Length,
            Classes(changed), changed.Length - Classes(changed), Classes(missing), Classes(added), Digest(baseline), Digest(checkedJar));
    }
    private static string PayloadFingerprint(IReadOnlyDictionary<string, SourcePackagePayload> payloads)
    {
        // Released baseline_manifest.py uses json.dumps(sort_keys=True), whose default
        // separators include spaces. Compact CanonicalJson would silently change this ID.
        var text = new StringBuilder("{"); var first = true;
        foreach (var (name, payload) in payloads.OrderBy(pair => pair.Key, UnicodeCodePointComparer.Instance))
        {
            if (!first) text.Append(", "); first = false;
            text.Append(CanonicalJson.String(name)).Append(": [").Append(payload.Length.ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(CanonicalJson.String(payload.Sha256)).Append(']');
        }
        return Digest(Encoding.UTF8.GetBytes(text.Append('}').ToString()));
    }
    private static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static InvalidDataException Invalid(string message) => SourceManifest.Invalid(message);
}
