using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Tests;

public static class SourceManifestTests
{
    private const string PackageName = "eu.kanade.tachiyomi.extension.en.example";
    private const string EntryPoint = "eu/kanade/tachiyomi/extension/en/example/Example.class";
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes("<manifest xmlns:android=\"" + SourceManifest.AndroidNamespace + "\" package=\"" + PackageName + "\" android:versionName=\"1.4.23\" android:versionCode=\"23\"><uses-feature android:name=\"tachiyomi.extension\"/><application><meta-data android:name=\"tachiyomi.extension.class\" android:value=\".Example\"/></application></manifest>");
    private static JsonDocument Fixtures() => JsonDocument.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-manifest.json")));
    private static byte[] Bytes(JsonElement row, string field) => Convert.FromBase64String(row.GetProperty(field).GetString()!);
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Zip(IEnumerable<(string Name, byte[] Contents)> entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, contents) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(contents);
            }
        return output.ToArray();
    }
    private static void Identity(JsonElement expected, SourceManifestIdentity actual)
    {
        Check.Equal(expected[0].GetString(), actual.Package); Check.Equal(expected[1].GetString(), actual.ClassName); Check.Equal(expected[2].GetString(), actual.LibraryVersion);
    }
    private static void Payloads(JsonElement expected, IReadOnlyDictionary<string, SourcePackagePayload> actual)
    {
        Check.Equal(expected.EnumerateObject().Count(), actual.Count);
        foreach (var entry in expected.EnumerateObject())
        {
            Check.True(actual.ContainsKey(entry.Name), "Missing retained payload " + entry.Name);
            Check.Equal(entry.Value[0].GetInt32(), actual[entry.Name].Length); Check.Equal(entry.Value[1].GetString(), actual[entry.Name].Sha256);
        }
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("source manifest: released text, AXML, identity and malformed-input corpus", () =>
        {
            using var fixtures = Fixtures(); var rows = fixtures.RootElement.GetProperty("manifests");
            foreach (var row in rows.EnumerateArray())
            {
                var raw = Bytes(row, "raw"); var package = Zip([("AndroidManifest.xml", raw), ("classes.dex", "dex\n035\0fixture"u8.ToArray())]);
                if (row.TryGetProperty("error", out _)) { Check.Throws<InvalidDataException>(() => SourceManifest.Contract(package)); continue; }
                try
                {
                    Identity(row.GetProperty("identity"), SourceManifest.Contract(package)); var tree = SourceManifest.Read(package);
                    var attributes = tree.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToDictionary(attribute => attribute.Name.ToString(), attribute => attribute.Value);
                    var expected = row.GetProperty("root_attributes"); Check.Equal(expected.EnumerateObject().Count(), attributes.Count);
                    foreach (var attribute in expected.EnumerateObject()) Check.Equal(attribute.Value.GetString(), attributes[attribute.Name]);
                }
                catch (Exception error) { throw new InvalidOperationException("Released manifest case " + row.GetProperty("name").GetString(), error); }
            }
            Console.WriteLine("Released source manifest cases " + rows.GetArrayLength());
        });
        suite.Add("source manifest: retained real Suwayomi Android manifest is parsed without execution", () =>
        {
            using var fixtures = Fixtures(); var raw = File.ReadAllBytes(ConfigurationTests.Fixture("source-manifest-suwayomi-1.4.29.axml"));
            Check.Equal(fixtures.RootElement.GetProperty("retained_fixture_sha256").GetString(), Digest(raw));
            var data = Zip([("AndroidManifest.xml", raw)]); var identity = SourceManifest.Contract(data);
            const string package = "eu.kanade.tachiyomi.extension.all.tachidesk";
            Check.Equal(new SourceManifestIdentity(package, package + ".Tachidesk", "1.4"), identity);
            Check.Equal("1.4.29", (string?)SourceManifest.Read(data).Attribute(SourceManifest.Android + "versionName"));
        });
        suite.Add("source package: released complete-payload hashes and compiled-package archive policy", () =>
        {
            using var fixtures = Fixtures(); var rows = fixtures.RootElement.GetProperty("payload_cases");
            foreach (var row in rows.EnumerateArray())
            {
                var data = Bytes(row, "package");
                if (row.TryGetProperty("error", out _)) { Check.Throws<InvalidDataException>(() => SourcePackage.Payloads(data)); continue; }
                try { Payloads(row.GetProperty("payloads"), SourcePackage.Payloads(data)); }
                catch (Exception error) { throw new InvalidOperationException("Released package case " + row.GetProperty("name").GetString(), error); }
            }
            Console.WriteLine("Released compiled package cases " + rows.GetArrayLength());
        });
        suite.Add("source rollback: exact all-payload comparison, fingerprints and validation-only envelopes", () =>
        {
            using var fixtures = Fixtures(); var rows = fixtures.RootElement.GetProperty("envelopes");
            foreach (var row in rows.EnumerateArray())
            {
                var baseline = Bytes(row, "baseline"); var before = (byte[])baseline.Clone(); var apk = Bytes(row, "apk");
                SourceValidationEnvelope Make() => row.GetProperty("installed_only").GetBoolean() ? SourcePackage.InstalledEnvelope(baseline, apk)
                    : SourcePackage.RestoreManifest(baseline, apk, Bytes(row, "converted"));
                try
                {
                    if (row.TryGetProperty("comparison", out var comparison))
                    {
                        var error = Check.Throws<SourceConversionMismatch>(() => Make());
                        using var json = JsonDocument.Parse(error.Comparison.ToJson().ToJsonString()); Check.JsonEqual(comparison, json.RootElement);
                    }
                    else if (row.TryGetProperty("error", out _)) Check.Throws<InvalidDataException>(() => Make());
                    else
                    {
                        var result = Make(); Identity(row.GetProperty("identity"), result.Identity);
                        var envelope = result.Contents(); Check.Equal(result.Identity, SourceManifest.Contract(envelope));
                        var expected = row.GetProperty("fingerprints"); Check.Equal(expected.EnumerateObject().Count() + 1, result.Fingerprints.Count);
                        foreach (var value in expected.EnumerateObject()) Check.Equal(value.Value.GetString(), result.Fingerprints[value.Name]);
                        Check.Equal(Digest(envelope), result.Fingerprints["validation_envelope_sha256"]);
                        Check.True(!result.Fingerprints.ContainsKey("verified") && !result.Fingerprints.ContainsKey("cold_import_verified"));
                        if (row.GetProperty("installed_only").GetBoolean()) Check.True(!result.Fingerprints.ContainsKey("converted_jar_sha256"));
                        var payloads = SourcePackage.Payloads(envelope).Where(pair => pair.Key != "AndroidManifest.xml").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        Payloads(row.GetProperty("payloads"), payloads);
                        var changed = result.Contents(); changed[0] ^= 1; Check.True(result.Contents().AsSpan().SequenceEqual(envelope));
                    }
                    Check.True(baseline.AsSpan().SequenceEqual(before), "Original rollback bytes were changed");
                }
                catch (Exception error) { throw new InvalidOperationException("Released envelope case " + row.GetProperty("name").GetString(), error); }
            }
            Console.WriteLine("Released rollback/envelope cases " + rows.GetArrayLength());
        });
        suite.Add("source package: local names, strict UTF8, native manifest duplicates and payload CRC cannot disagree", () =>
        {
            var duplicate = Zip([("AndroidManifest.xml", Xml), ("AndroidManifest.xml", Xml)]);
            Check.Throws<InvalidDataException>(() => SourceManifest.Contract(duplicate));
            var local = Zip([(EntryPoint, "SYNTHETIC_PAYLOAD"u8.ToArray())]); local[30] ^= 1;
            Check.Throws<InvalidDataException>(() => SourcePackage.Payloads(local));
            var malformed = Zip([(EntryPoint, "SYNTHETIC_PAYLOAD"u8.ToArray())]);
            var central = malformed.AsSpan().IndexOf("PK\x01\x02"u8);
            BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(central + 8), 0x800); malformed[central + 46] = 0xff;
            Check.Throws<InvalidDataException>(() => SourcePackage.Payloads(malformed));
            foreach (var name in new[] { "assets/has\0nul", "../outside.class", "/outside.class", "dir\\outside.class" })
                Check.Throws<InvalidDataException>(() => SourcePackage.Payloads(Zip([(EntryPoint, "SYNTHETIC_PAYLOAD"u8.ToArray()), (name, "bad"u8.ToArray())])));
        });
        suite.Add("source manifest: plain XML attribute limits and every package byte bound fail closed", () =>
        {
            var text = Encoding.UTF8.GetString(Xml); var extras = string.Join(" ", Enumerable.Range(0, 1025).Select(index => "attribute" + index + "=\"value\""));
            Check.Throws<InvalidDataException>(() => SourceManifest.Parse(Encoding.UTF8.GetBytes(text.Replace("<manifest ", "<manifest " + extras + " ", StringComparison.Ordinal))));
            Check.Throws<InvalidDataException>(() => SourceManifest.Parse(new byte[SourceManifest.MaximumBytes + 1]));
            Check.Throws<InvalidDataException>(() => SourceManifest.Read(new byte[SourcePackage.MaximumBytes + 1]));
            Check.Throws<InvalidDataException>(() => SourcePackage.Payloads(new byte[100]));
            Check.Throws<InvalidDataException>(() => SourcePackage.Payloads(new byte[SourcePackage.MaximumBytes + 1]));
        });
    }
}
