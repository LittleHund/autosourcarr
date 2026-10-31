using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Tests;

public static class RawArchiveTests
{
    private const string Module = "src/en/example";
    private static JsonObject Metadata() => new() { ["package"] = "eu.kanade.tachiyomi.extension.en.example", ["version"] = "1.4.23", ["module"] = Module,
        ["ecosystem"] = "keiyoushi", ["recipe"] = "gradle", ["kind"] = "upstream", ["origin"] = new JsonObject { ["repository"] = "https://github.com/keiyoushi/extensions-source", ["revision"] = new string('a', 40) } };
    private static Dictionary<string, string> Files() => new() { ["LICENSE"] = "PUBLIC_LICENSE", ["gradlew"] = "#!/bin/sh\n", [Module + "/src/Example.kt"] = "class Example {}" };
    private static byte[] Zip(Dictionary<string, string> files, string prefix = "")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var (name, text) in files) { using var entry = zip.CreateEntry(prefix + name, CompressionLevel.NoCompression).Open(); entry.Write(Encoding.UTF8.GetBytes(text)); }
        return output.ToArray();
    }
    public static void Register(TestSuite suite)
    {
        suite.Add("raw archive: selected code, build resources, prefix and portable recipe round trip", () =>
        {
            var files = Files(); files["src/en/other/src/Other.kt"] = "class Other {}"; files[Module + "/build/source.apk"] = "not raw";
            files[".codex/config.toml"] = "private agent config";
            var selected = RawArchive.Read(Zip(files, "project-commit/"), Metadata()); Check.Equal(3, selected.Count); Check.True(selected["gradlew"].Executable);
            var exported = RawArchive.Export(selected, Metadata()); var portable = RawArchive.PortableMetadata(exported)!;
            Check.Equal("gradle", portable["recipe"]!.GetValue<string>()); Check.Equal(3, RawArchive.Read(exported, portable).Count);
            var clone = selected[Module + "/src/Example.kt"].Contents(); clone[0] = 0;
            Check.True(selected[Module + "/src/Example.kt"].Contents()[0] != 0);
        });
        suite.Add("raw archive: traversal, wire NUL, duplicate, link and malformed directory fail", () =>
        {
            foreach (var name in new[] { "../escape.kt", "/absolute.kt", "C:/secret.kt", "dir\\escape.kt", "nul\0hidden.kt" })
            { var files = Files(); files[name] = "bad"; Check.Throws<InvalidDataException>(() => RawArchive.Read(Zip(files), Metadata())); }
            var linked = Zip(Files());
            for (var i = 0; i + 46 <= linked.Length; i++) if (BitConverter.ToUInt32(linked, i) == 0x02014b50) { BitConverter.GetBytes(unchecked((int)0xa1ff0000)).CopyTo(linked, i + 38); break; }
            Check.Throws<InvalidDataException>(() => RawArchive.Read(linked, Metadata()));
            var duplicate = Zip(new() { ["a.kt"] = "a", ["b.kt"] = "b" });
            for (var i = 0; i + 4 <= duplicate.Length; i++) if (duplicate.AsSpan(i, 4).SequenceEqual("b.kt"u8)) duplicate[i] = (byte)'a';
            Check.Throws<InvalidDataException>(() => RawArchive.Read(duplicate, Metadata()));
            Check.Throws<InvalidDataException>(() => RawArchive.Read([1, 2, 3], Metadata()));
        });
        suite.Add("raw archive: private settings, compiled payload and corrupt stored CRC fail", () =>
        {
            foreach (var name in new[] { ".env", "local.properties", "auth.json", "cookies.json", "signingkey.jks", "extension.apk", "lib/private.jar" })
            { var files = Files(); files[name] = "PRIVATE_CANARY"; Check.Throws<InvalidDataException>(() => RawArchive.Read(Zip(files), Metadata())); }
            var corrupted = Zip(Files()); var offset = corrupted.AsSpan().IndexOf("PUBLIC_LICENSE"u8); Check.True(offset >= 0); corrupted[offset] ^= 1;
            Check.Throws<InvalidDataException>(() => RawArchive.Read(corrupted, Metadata()));
            var noSource = Zip(new() { ["LICENSE"] = "public" }); Check.Throws<InvalidDataException>(() => RawArchive.Read(noSource, Metadata()));
        });
        suite.Add("raw provenance: unknown fields, credential URLs and changed overlays fail", () =>
        {
            var meta = Metadata(); meta["feedback"] = "private"; Check.Throws<InvalidDataException>(() => RawCodeRules.Metadata(meta));
            foreach (var url in new[] { "https://user:secret@github.com/a/b", "https://github.com/a/b?token=private", "https://127.0.0.1/a", "https://[::1]/a", "https://10.0.0.1/a" })
                Check.Throws<InvalidDataException>(() => RawCodeRules.PublicReference(url));
            meta = Metadata(); meta["recipe"] = "jvm"; meta["overlay_sources"] = new JsonArray(Module + "/src/Missing.java");
            Check.Throws<InvalidDataException>(() => RawArchive.Read(Zip(Files()), meta));
            meta["overlay_sources"] = new JsonArray(Module + "/src/../Escape.java"); Check.Throws<InvalidDataException>(() => RawCodeRules.Metadata(meta));
        });
        suite.Add("raw archive: encrypted flag and strict UTF8 wire identities fail before decoding", () =>
        {
            var encrypted = Zip(Files());
            for (var i = 0; i + 46 <= encrypted.Length; i++) if (BitConverter.ToUInt32(encrypted, i) == 0x02014b50) { encrypted[i + 8] |= 1; break; }
            Check.Throws<InvalidDataException>(() => RawArchive.Read(encrypted, Metadata()));
            var malformed = Zip(new() { ["x.kt"] = "x" });
            for (var i = 0; i + 46 <= malformed.Length; i++) if (BitConverter.ToUInt32(malformed, i) == 0x02014b50) { malformed[i + 9] |= 8; malformed[i + 46] = 0xff; break; }
            Check.Throws<InvalidDataException>(() => RawArchive.Read(malformed, Metadata()));
        });
        suite.Add("raw archive: attacker-sized LZMA dictionary and conflicting local filename are refused", () =>
        {
            var cases = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("raw-code.json")))!.AsArray();
            var row = cases.OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == "compression-14");
            var payload = Convert.FromBase64String(row["payload"]!.GetValue<string>());
            var start = 30 + BitConverter.ToUInt16(payload, 26) + BitConverter.ToUInt16(payload, 28);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, start + 5);
            Check.Throws<InvalidDataException>(() => RawArchive.Read(payload, row["metadata"]!.AsObject()));
            payload = Zip(Files()); payload[30] ^= 1;
            Check.Throws<InvalidDataException>(() => RawArchive.Read(payload, Metadata()));
        });
    }
}
