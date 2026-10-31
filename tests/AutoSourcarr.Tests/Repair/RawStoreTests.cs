using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Serialization;

namespace AutoSourcarr.Tests;

public static class RawStoreTests
{
    private const string Package = "eu.kanade.tachiyomi.extension.en.example", Module = "src/en/example";
    private static JsonObject Metadata() => new() { ["package"] = Package, ["version"] = "1.4.23", ["module"] = Module,
        ["ecosystem"] = "keiyoushi", ["recipe"] = "gradle", ["kind"] = "upstream", ["origin"] = new JsonObject { ["repository"] = "https://github.com/keiyoushi/extensions-source", ["revision"] = new string('a', 40) } };
    private static byte[] Zip(string source = "class Example {}", Dictionary<string, byte[]>? more = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var files = new Dictionary<string, byte[]> { ["LICENSE"] = "PUBLIC_LICENSE"u8.ToArray(), ["gradlew"] = "#!/bin/sh\n"u8.ToArray(), [Module + "/src/Example.kt"] = Encoding.UTF8.GetBytes(source) };
            if (more is not null) foreach (var (key, value) in more) files[key] = value;
            foreach (var (name, bytes) in files) { using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); entry.Write(bytes); }
        }
        return output.ToArray();
    }
    private static string Id(JsonObject value) => value["id"]!.GetValue<string>();
    public static void Register(TestSuite suite)
    {
        suite.Add("raw code: released archive acceptance, manifest hashes and contents corpus", () =>
        {
            var rows = JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("raw-code.json")))!.AsArray();
            using var sandbox = new Sandbox(); var store = new RawStore(Path.Combine(sandbox.Path, "raw"));
            foreach (var row in rows)
            {
                var payload = Convert.FromBase64String(row!["payload"]!.GetValue<string>()); var meta = row["metadata"]!.AsObject(); var name = row["name"]!.GetValue<string>();
                if (!row["valid"]!.GetValue<bool>()) { Check.Throws<InvalidDataException>(() => store.PutZip(payload, meta)); continue; }
                JsonObject value;
                try { value = store.PutZip(payload, meta); } catch (Exception error) { throw new InvalidOperationException("Rejected released valid archive: " + name, error); }
                Check.Equal(CanonicalJson.Text(row["manifest"]), CanonicalJson.Text(value));
                var exported = RawArchive.Read(store.ExportZip(Package, Id(value)), meta);
                foreach (var (path, bytes) in row["contents"]!.AsObject()) Check.Equal(bytes!.GetValue<string>(), Convert.ToBase64String(exported[path].Contents()));
            }
            Console.WriteLine("Raw archive parity cases " + rows.Count);
        });
        suite.Add("raw store: two instances deduplicate immutable code and retain prior versions", () =>
        {
            using var sandbox = new Sandbox(); var root = Path.Combine(sandbox.Path, "shared"); var first = new RawStore(root, true); var second = new RawStore(root, true);
            var old = first.PutZip(Zip(), Metadata()); var same = second.PutZip(Zip(), Metadata()); Check.Equal(Id(old), Id(same));
            Check.Equal(3, Directory.EnumerateFiles(Path.Combine(root, "objects"), "*", SearchOption.AllDirectories).Count());
            var meta = Metadata(); meta["kind"] = "repaired"; meta["parent"] = Id(old); var newer = second.PutZip(Zip("class Example {} // repaired"), meta);
            Check.True(Id(old) != Id(newer)); Check.Equal(4, Directory.EnumerateFiles(Path.Combine(root, "objects"), "*", SearchOption.AllDirectories).Count());
            old["kind"] = "repaired"; Check.Equal("upstream", first.Get(Package, Id(same))["kind"]!.GetValue<string>());
            Check.Equal(2, first.Versions(Package).Count); Check.Equal(1, second.Versions(Package, 1).Count);
            Check.True(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(path => path.EndsWith(".apk", StringComparison.Ordinal)));
        });
        suite.Add("raw store: portable exports and private materialization keep layout and bytes", () =>
        {
            using var sandbox = new Sandbox(); var store = new RawStore(Path.Combine(sandbox.Path, "raw"), true); var value = store.PutZip(Zip(), Metadata());
            var exported = store.ExportZip(Package, Id(value), true); var meta = RawArchive.PortableMetadata(exported)!;
            var privateStore = new RawStore(Path.Combine(sandbox.Path, "instance", "raw-private"));
            Check.Equal(Id(value), Id(privateStore.PutZip(exported, meta))); Check.True(!privateStore.Public);
            var target = Path.Combine(sandbox.Path, "instance", "job"); privateStore.Materialize(Package, Id(value), target);
            Check.Equal("class Example {}", File.ReadAllText(Path.Combine(target, Module, "src", "Example.kt")));
            Check.True(!File.Exists(Path.Combine(target, RawCodeRules.PortableName))); Check.Throws<InvalidDataException>(() => store.Materialize(Package, Id(value), target));
        });
        suite.Add("raw store: objects, receipts and immutable identity corruption never overwrite old bytes", () =>
        {
            using var sandbox = new Sandbox(); var store = new RawStore(Path.Combine(sandbox.Path, "raw")); var value = store.PutZip(Zip(), Metadata());
            var hash = value["files"]![Module + "/src/Example.kt"]!["sha256"]!.GetValue<string>(); var path = Path.Combine(store.Root, "objects", hash[..2], hash[2..]);
            File.WriteAllText(path, "TAMPERED"); Check.Throws<InvalidDataException>(() => store.ExportZip(Package, Id(value))); Check.Throws<InvalidDataException>(() => store.PutZip(Zip(), Metadata()));
            Check.Equal("TAMPERED", File.ReadAllText(path));
            var receipt = Directory.EnumerateFiles(Path.Combine(store.Root, "versions"), "*.json", SearchOption.AllDirectories).Single();
            File.WriteAllText(receipt, "{broken"); Check.Throws<InvalidDataException>(() => store.Get(Package, Id(value))); Check.Equal("{broken", File.ReadAllText(receipt));
            Check.Throws<FileNotFoundException>(() => store.Get(Package + ".different", Id(value)));
        });
        suite.Add("raw store: large repository input does not widen upload or retained-module limits", () =>
        {
            using var sandbox = new Sandbox(); var store = new RawStore(Path.Combine(sandbox.Path, "raw"));
            var payload = Zip(more: new() { ["src/en/other/build/outputs/source.apk"] = new byte[33 * 1024 * 1024] });
            Check.Throws<InvalidDataException>(() => store.PutZip(payload, Metadata()));
            var cache = Path.Combine(sandbox.Path, "repository.zip"); File.WriteAllBytes(cache, payload); var value = store.PutRepositoryFile(cache, Metadata());
            Check.Equal(3, value["files"]!.AsObject().Count); Check.True(store.ExportZip(Package, Id(value)).Length < 1024 * 1024);
        });
        suite.Add("raw store: materialization rejects file-directory and platform name collisions", () =>
        {
            using var sandbox = new Sandbox(); var store = new RawStore(Path.Combine(sandbox.Path, "raw"));
            var value = store.PutZip(Zip(more: new() { ["common"] = [1], ["common/resource.txt"] = [2] }), Metadata());
            var target = Path.Combine(sandbox.Path, "job"); Check.Throws<InvalidDataException>(() => store.Materialize(Package, Id(value), target));
            Check.True(!Directory.EnumerateFileSystemEntries(target).Any());
            if (OperatingSystem.IsWindows())
            {
                value = store.PutZip(Zip(more: new() { ["common/a.txt"] = [1], ["common/A.txt"] = [2] }), Metadata());
                Check.Throws<InvalidDataException>(() => store.Materialize(Package, Id(value), target)); Check.True(!Directory.EnumerateFileSystemEntries(target).Any());
            }
        });
    }
}
