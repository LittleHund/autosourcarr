using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Sources.Inventory;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

public static class SourceInventoryTests
{
    internal const string Package = "eu.kanade.tachiyomi.extension.en.example", Version = "1.4.23";
    public static void Register(TestSuite suite)
    {
        suite.Add("source inventory: eight released spaced native identity fingerprints", () =>
        {
            var fixture = Fixture(); foreach (var row in fixture["identities"]!.AsArray()) Check.Equal(Text(row!, "expected"), InstalledSourceIdentity.Hash(new JsonObject { ["source"] = new JsonObject { ["nodes"] = row!["sources"]!.DeepClone() } }));
        });
        suite.Add("source inventory: actual client envelopes and native bytes retain exact identities", () =>
        {
            using var setup = new Setup(); Check.True(setup.Installed.Ready); Check.Equal(0, setup.Http.Calls);
            var rows = setup.Installed.InstalledAsync(default).GetAwaiter().GetResult(); Check.Equal(1, rows.Count); Check.Equal(Package, Text(rows[0], "pkgName")); Check.True(setup.Http.Query.Contains("baseUrl", StringComparison.Ordinal));
            var observed = setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult()!; Check.Equal(Hash(setup.Binary), observed.BinarySha256); Check.Equal("-9223372036854775808", observed.SourceIds[0]); Check.Equal(InstalledSourceIdentity.Hash(observed.Extension), observed.IdentitySha256);
            Check.Equal("https://source.example", Text(observed.Extension["source"]!["nodes"]![0]!, "baseUrl")); observed.Extension["versionName"] = "tampered";
            Check.Equal(Version, Text(setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult()!.Extension, "versionName")); Check.True(File.ReadAllBytes(setup.InstalledPath).AsSpan().SequenceEqual(setup.Binary));
            Check.True(setup.Installed.ObserveAsync(Package, "1.4.22", default).GetAwaiter().GetResult() is null);
        });
        suite.Add("source inventory: readiness and generation changes never manufacture missing packages", () =>
        {
            using var setup = new Setup(); setup.Ready = false; Check.True(!setup.Installed.Ready); Check.Throws<SourceEngineUnavailableException>(() => setup.Installed.InstalledAsync(default).GetAwaiter().GetResult()); Check.Equal(0, setup.Http.Calls);
            setup.Ready = true; setup.Http.Before = call => { if (call == 1) setup.Generation++; };
            Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, "1.4.22", default).GetAwaiter().GetResult()); Check.Equal(1, setup.Http.Calls);
        });
        suite.Add("source inventory: same-version ownership, metadata, filename and binary races refuse publication", () =>
        {
            foreach (var change in new[] { "name", "lang", "baseUrl", "id", "apkName", "version", "binary", "generation" })
            {
                using var setup = new Setup(); setup.Http.Before = call =>
                {
                    if (call != 2) return; var row = setup.Http.Rows[0]!.AsObject();
                    if (change is "name" or "lang" or "baseUrl") row["source"]!["nodes"]![0]![change] = "changed";
                    else if (change == "id") row["source"]!["nodes"]![0]!["id"] = "17";
                    else if (change == "apkName") row["apkName"] = "other.apk";
                    else if (change == "version") row["versionName"] = "1.4.24";
                    else if (change == "binary") File.WriteAllBytes(setup.InstalledPath, Jar("changed"));
                    else setup.Generation++;
                };
                Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult());
            }
        });
        suite.Add("source inventory: ambiguous packages and source IDs fail before binary proof", () =>
        {
            foreach (var shape in new[] { "duplicate-package", "duplicate-source", "wrong-id", "float-id", "missing-source", "missing-installed", "missing-name" })
            {
                using var setup = new Setup(); var row = setup.Http.Rows[0]!.AsObject();
                switch (shape)
                {
                    case "duplicate-package": setup.Http.Rows.Add(row.DeepClone()); break;
                    case "duplicate-source": var other = (JsonObject)row.DeepClone(); other["pkgName"] = "eu.example.other"; setup.Http.Rows.Add(other); break;
                    case "wrong-id": row["source"]!["nodes"]![0]!["id"] = "00017"; break;
                    case "float-id": row["source"]!["nodes"]![0]!["id"] = JsonNode.Parse("9223372036854775807.0"); break;
                    case "missing-source": row.Remove("source"); break;
                    case "missing-installed": row.Remove("isInstalled"); break;
                    case "missing-name": row["source"]!["nodes"]![0]!.AsObject().Remove("name"); break;
                }
                if (shape == "missing-source") Check.Throws<SuwayomiException>(() => setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult());
                else Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult());
            }
        });
        suite.Add("source inventory: installed path guards refuse traversal, missing, empty and oversized files", () =>
        {
            using var setup = new Setup();
            foreach (var filename in new[] { "../fixture.apk", "fixture.txt", "a/b.apk", "a\\b.jar", "/outside.apk", "bad\n.apk" }) Check.Throws<SourceRawConflictException>(() => EngineInstalledSources.InstalledFilename(new JsonObject { ["apkName"] = filename }));
            File.Delete(setup.InstalledPath); Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult());
            File.WriteAllBytes(setup.InstalledPath, []); Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult());
            using (var stream = File.OpenWrite(setup.InstalledPath)) stream.SetLength(32 * 1024 * 1024 + 1L); Check.Throws<SourceRawConflictException>(() => setup.Installed.ObserveAsync(Package, Version, default).GetAwaiter().GetResult());
        });
        suite.Add("source inventory: local receipts and saved manual overrides never infer official APK origin", () =>
        {
            using var setup = new Setup(); Put(setup.Maintenance, stamp: 1); var rows = setup.Installed.InstalledAsync(default).GetAwaiter().GetResult(); Check.Equal("manual-upload", Text(rows[0], "orphan_reason"));
            File.WriteAllText(Path.Combine(setup.Maintenance, "orphans.json"), "{\"labels\":{\"" + Package + "\":{\"orphaned\":false,\"reason\":\"manual-upload\"}}}");
            Check.Equal("", Text(setup.Installed.InstalledAsync(default).GetAwaiter().GetResult()[0], "orphan_reason"));
            File.WriteAllText(Path.Combine(setup.Maintenance, "orphans.json"), "corrupt"); Check.Throws<SourceRawConflictException>(() => setup.Installed.InstalledAsync(default).GetAwaiter().GetResult());
        });
        suite.Add("source inventory: cancellation and stop join blocked physical annotation work", () =>
        {
            using var annotation = new BlockingAnnotation(); using var setup = new Setup(annotation); using var cancellation = new CancellationTokenSource();
            var observation = setup.Installed.ObserveAsync(Package, Version, cancellation.Token); Task? stopped = null;
            try { Check.True(annotation.Entered.Wait(TimeSpan.FromSeconds(5))); cancellation.Cancel(); stopped = setup.Installed.StopAsync(); Check.True(!observation.IsCompleted && !stopped.IsCompleted); }
            finally { annotation.Release.Set(); }
            Check.Throws<OperationCanceledException>(() => observation.GetAwaiter().GetResult()); stopped!.GetAwaiter().GetResult(); Check.Equal(0, setup.Io.Snapshot.Running);
        });
        suite.Add("source inventory bridge: typed reads preserve exact bytes without main source-root access", () =>
        {
            var reader = new ControlledBinaryReader(); using var setup = new Setup(binaryReader: reader);
            var requests = new List<(string Leaf, long Generation)>();
            reader.Read = (leaf, generation, _) => { requests.Add((leaf, generation)); return Task.FromResult(setup.Binary); };
            File.Delete(setup.InstalledPath);
            var capture = setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult()!;
            Check.Equal(2, requests.Count); Check.True(requests.All(r => r == ("fixture.jar", 1L)));
            Check.Equal(Hash(setup.Binary), capture.Source.BinarySha256);
            var returned = capture.Contents(); returned[0] ^= 1;
            Check.True(capture.Contents().AsSpan().SequenceEqual(setup.Binary));
            Check.Equal(2, setup.Http.Calls); Check.True(!File.Exists(setup.InstalledPath));
        });
        suite.Add("source inventory bridge: reused backend buffers cannot hide different observations", () =>
        {
            var reader = new ControlledBinaryReader(); using var setup = new Setup(binaryReader: reader);
            var shared = (byte[])setup.Binary.Clone();
            reader.Read = (_, _, _) => { if (reader.Calls == 2) shared[0] ^= 1; return Task.FromResult(shared); };
            Check.Throws<SourceRawConflictException>(() => setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult());
            Check.Equal(2, reader.Calls); Check.True(File.ReadAllBytes(setup.InstalledPath).AsSpan().SequenceEqual(setup.Binary));
        });
        suite.Add("source inventory bridge: generation and metadata changes still refuse publication", () =>
        {
            foreach (var change in new[] { "first-generation", "second-generation", "identity" })
            {
                var reader = new ControlledBinaryReader(); using var setup = new Setup(binaryReader: reader);
                reader.Read = (_, generation, _) =>
                {
                    Check.Equal(1L, generation);
                    if (change == "first-generation" && reader.Calls == 1 || change == "second-generation" && reader.Calls == 2)
                        Interlocked.Increment(ref setup.Generation);
                    return Task.FromResult(setup.Binary);
                };
                if (change == "identity") setup.Http.Before = call => { if (call == 2) setup.Http.Rows[0]!["source"]!["nodes"]![0]!["baseUrl"] = "https://changed.example"; };
                Check.Throws<SourceRawConflictException>(() => setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult());
                Check.Equal(change == "second-generation" ? 2 : 1, reader.Calls);
            }
        });
        suite.Add("source inventory bridge: a retired HTTP epoch invalidates an in-flight byte read", () =>
        {
            var reader = new ControlledBinaryReader(); using var setup = new Setup(binaryReader: reader, restartable: true);
            reader.Read = (_, _, _) => { setup.Client.BeginTransportPause(); return Task.FromResult(setup.Binary); };
            Check.Throws<OperationCanceledException>(() => setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult());
            Check.Equal(1, reader.Calls); Check.Equal(1, setup.Http.Calls);
        });
        suite.Add("source inventory bridge: empty oversized or absent evidence never becomes missing source", () =>
        {
            foreach (var value in new byte[]?[] { null, [], new byte[32 * 1024 * 1024 + 1] })
            {
                var reader = new ControlledBinaryReader { Read = (_, _, _) => Task.FromResult(value!) };
                using var setup = new Setup(binaryReader: reader);
                Check.Throws<SourceRawConflictException>(() => setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult());
                Check.Equal(1, reader.Calls); Check.Equal(1, setup.Http.Calls); Check.True(setup.Installed.Ready);
            }
        });
        suite.Add("source inventory bridge: local admission deferral leaves the healthy engine usable", () =>
        {
            var reader = new ControlledBinaryReader(); using var setup = new Setup(binaryReader: reader);
            reader.Read = (_, _, _) => throw new SourceWorkDeferredException("Controlled observation lane is busy");
            Check.Throws<SourceWorkDeferredException>(() => setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult());
            Check.True(setup.Installed.Ready); Check.Equal(1L, setup.Generation);
            reader.Read = (_, _, _) => Task.FromResult(setup.Binary);
            Check.True(setup.Installed.CaptureAsync(Package, Version).GetAwaiter().GetResult() is not null);
            Check.Equal(3, reader.Calls);
        });
        suite.Add("source inventory bridge: cancellation and stop join admitted binary IO", () =>
        {
            var reader = new ControlledBinaryReader(); using var setup = new Setup(binaryReader: reader);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            reader.Read = (_, _, token) => setup.Io.RunAsync(() =>
            {
                entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture binary read was not released");
                return setup.Binary;
            }, token);
            var capture = setup.Installed.CaptureAsync(Package, Version, cancellation.Token); Task? stopped = null;
            try
            {
                Check.True(entered.Wait(TimeSpan.FromSeconds(5))); cancellation.Cancel(); stopped = setup.Installed.StopAsync();
                Check.True(!capture.IsCompleted && !stopped.IsCompleted); Check.Equal(1, setup.Io.Snapshot.Running);
            }
            finally { release.Set(); }
            Check.Throws<OperationCanceledException>(() => capture.GetAwaiter().GetResult());
            stopped!.GetAwaiter().GetResult(); Check.Equal(0, setup.Io.Snapshot.Running);
            Check.Equal(1, reader.Calls); Check.Equal(1, setup.Http.Calls);
        });
        suite.Add("source baselines:24 released retained APK precedence, receipt and corruption cases", () =>
        {
            var fixture = Fixture(); var count = 0;
            foreach (var row in fixture["retained_apks"]!.AsArray().OfType<JsonObject>())
            {
                using var setup = new Setup(); var name = Text(row, "name"); var expected = row["expected"]!.AsObject();
                try
                {
                    SeedCase(setup.Maintenance, name);
                    if (Text(expected, "state") == "rejected") Check.Throws<InvalidDataException>(() => setup.Baselines.SelectOriginalAsync(setup.Http.Rows[0]!.AsObject(), new string('1', 64)).GetAwaiter().GetResult());
                    else
                    {
                        var selected = setup.Baselines.SelectOriginalAsync(setup.Http.Rows[0]!.AsObject(), new string('1', 64)).GetAwaiter().GetResult();
                        if (Text(expected, "state") == "missing") Check.True(selected is null, name);
                        else { Check.True(selected is not null, name); Check.Equal(Text(expected, "sha256"), selected!.Sha256); Check.Equal(Text(expected, "basis"), selected.Basis); }
                    }
                    if (name == "hardlinked-selected-apk-refused") Check.True(File.ReadAllBytes(Path.Combine(setup.Maintenance, "other.apk")).AsSpan().SequenceEqual(Convert.FromBase64String(Text(fixture, "apk"))));
                    if (name == "hardlinked-receipt-skipped") Check.Equal(Package, Text(JsonNode.Parse(File.ReadAllText(Path.Combine(setup.Maintenance, "other.json")))!, "pkgName"));
                }
                catch (Exception error) { throw new InvalidOperationException("Retained APK oracle case failed: " + name, error); }
                count++;
            }
            Check.Equal(24, count); Console.WriteLine("Source inventory retained APK oracle cases " + count + "; actual native hard-link guards included");
        });
        suite.Add("source baselines: exact rollback bytes survive absent APK and raw-first input remains distinct", () =>
        {
            using var setup = new Setup(); Check.Throws<InvalidDataException>(() => setup.Baselines.PrepareRepairInputAsync(Package, Version).GetAwaiter().GetResult());
            var backup = Path.Combine(setup.Maintenance, "backups", Hash(setup.Binary)); Check.True(File.ReadAllBytes(Path.Combine(backup, "installed.jar")).AsSpan().SequenceEqual(setup.Binary)); Check.Equal(Package, Text(JsonNode.Parse(File.ReadAllText(Path.Combine(backup, "identity.json")))!, "pkgName"));
            var raw = setup.Baselines.PrepareRepairInputAsync(Package, Version, rawFirst: true).GetAwaiter().GetResult(); Check.True(raw["raw_first"]!.GetValue<bool>()); Check.True(!raw.ContainsKey("source_apk")); Check.Equal(Convert.ToBase64String(setup.Binary), Text(raw, "baseline"));
            Check.Equal("-9223372036854775808", Text(raw["installed_sources"]![0]!, "id")); Check.True(File.ReadAllBytes(setup.InstalledPath).AsSpan().SequenceEqual(setup.Binary));
            var immutable = Path.Combine(backup, "installed.jar"); File.WriteAllText(immutable, "retained corruption"); Check.Throws<InvalidDataException>(() => setup.Baselines.PrepareRepairInputAsync(Package, Version, rawFirst: true).GetAwaiter().GetResult()); Check.Equal("retained corruption", File.ReadAllText(immutable)); Check.True(File.ReadAllBytes(setup.InstalledPath).AsSpan().SequenceEqual(setup.Binary));
        });
        suite.Add("source baselines: explicit original APK retention binds selected JAR and never proves conversion", () =>
        {
            using var setup = new Setup(); var apk = Convert.FromBase64String(Text(Fixture(), "apk")); Check.Throws<InvalidDataException>(() => setup.Baselines.AttachOriginalAsync(Package, Version, apk, false).GetAwaiter().GetResult());
            var result = setup.Baselines.AttachOriginalAsync(Package, Version, apk, true).GetAwaiter().GetResult(); Check.Equal(Hash(apk), Text(result, "sha256")); var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(setup.Maintenance, "repair-apks", Hash(apk), "receipt.json")))!;
            Check.Equal(Hash(setup.Binary), Text(receipt, "target_jar_sha256")); Check.Equal(Version, Text(receipt, "target_version")); Check.True(receipt["confirmed_original"]!.GetValue<bool>());
            var input = setup.Baselines.PrepareRepairInputAsync(Package, Version).GetAwaiter().GetResult(); Check.Equal("user-confirmed-original", Text(input, "original_apk_basis")); Check.Equal(Convert.ToBase64String(apk), Text(input, "source_apk")); Check.True(!input.ContainsKey("entrypoint") && !input.ContainsKey("converted_jar_sha256")); Check.True(File.ReadAllBytes(setup.InstalledPath).AsSpan().SequenceEqual(setup.Binary));
            Check.Throws<InvalidDataException>(() => setup.Baselines.AttachOriginalAsync(Package, Version, Apk("eu.example.other"), true).GetAwaiter().GetResult());
        });
        suite.Add("source baselines: APK package inspector never accepts a renamed class JAR or reader package", () =>
        {
            Check.Throws<InvalidDataException>(() => SourceBaselineStore.InspectApk(Jar("original"), "renamed.apk")); Check.Throws<InvalidDataException>(() => SourceBaselineStore.InspectApk(Apk("eu.kanade.tachiyomi.extension.all.autosourcarr"), "reader.apk")); Check.Throws<InvalidDataException>(() => SourceBaselineStore.InspectApk(Apk(Package), "../bad.apk"));
            Check.Equal(Package, Text(SourceBaselineStore.InspectApk(Convert.FromBase64String(Text(Fixture(), "apk")), "original.apk"), "pkgName"));
        });
    }
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(ConfigurationTests.Fixture("source-inventory.json")))!.AsObject();
    private static string Text(JsonNode value, string key) => value[key]!.GetValue<string>();
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static byte[] OracleApk(string label) => Convert.FromBase64String(Text(Fixture()["apks"]!, label));
    private static byte[] Jar(string label) => Archive(new Dictionary<string, byte[]> { ["eu/example/Fixture.class"] = Encoding.UTF8.GetBytes("class " + label), ["resource.txt"] = Encoding.UTF8.GetBytes("resource") });
    private static byte[] Apk(string package, string label = "original") => Archive(new Dictionary<string, byte[]> { ["AndroidManifest.xml"] = Encoding.UTF8.GetBytes("<manifest package=\"" + package + "\"><application/></manifest>"), ["classes.dex"] = Encoding.UTF8.GetBytes("dex\n035\0" + label) });
    private static byte[] Archive(Dictionary<string, byte[]> files)
    { using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var (name, bytes) in files) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(bytes); } return output.ToArray(); }
    private static string Put(string root, string relative = "uploads", string state = "installed", double stamp = 1, byte[]? bytes = null, JsonObject? extra = null)
    {
        bytes ??= Convert.FromBase64String(Text(Fixture(), "apk")); var digest = Hash(bytes); var folder = Path.Combine(root, relative, digest); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "uploaded.apk"), bytes);
        var receipt = new JsonObject { ["state"] = state, ["pkgName"] = Package, ["sha256"] = digest, ["completed_at"] = stamp }; foreach (var pair in extra ?? new()) receipt[pair.Key] = pair.Value?.DeepClone(); File.WriteAllText(Path.Combine(folder, "receipt.json"), receipt.ToJsonString()); return folder;
    }
    private static void SeedCase(string root, string name)
    {
        if (name == "no-evidence") return;
        if (name.StartsWith("provided-mismatch-", StringComparison.Ordinal) || name == "explicit-original-priority")
        {
            Put(root, stamp: 20, bytes: OracleApk("newer-upload")); var extra = new JsonObject { ["confirmed_original"] = true, ["target_version"] = Version, ["target_jar_sha256"] = new string('1', 64) };
            if (name.Contains("confirmed_original-False", StringComparison.Ordinal)) extra["confirmed_original"] = false;
            if (name.Contains("confirmed_original-1", StringComparison.Ordinal)) extra["confirmed_original"] = 1;
            if (name.Contains("target_version", StringComparison.Ordinal)) extra["target_version"] = "1.4.22";
            if (name.Contains("target_jar_sha256", StringComparison.Ordinal)) extra["target_jar_sha256"] = new string('2', 64);
            Put(root, "repair-apks", "provided", 10, extra: extra); return;
        }
        if (name == "ambiguous-equal-timestamps") { Put(root, stamp: 10, bytes: OracleApk("one")); Put(root, stamp: 10, bytes: OracleApk("two")); return; }
        if (name == "corrupt-latest-no-fallback") { Put(root, stamp: 1, bytes: OracleApk("older")); var folder = Put(root, stamp: 2, bytes: OracleApk("latest")); File.WriteAllBytes(Path.Combine(folder, "uploaded.apk"), Convert.FromBase64String(Text(Fixture(), "apk"))); return; }
        if (name == "selected-apk-wrong-package") { Put(root, bytes: OracleApk("other-package")); return; }
        if (name == "hardlinked-selected-apk-refused") { var folder = Put(root); HardLink(Path.Combine(folder, "uploaded.apk"), Path.Combine(root, "other.apk")); return; }
        if (name == "hardlinked-receipt-skipped") { var folder = Put(root); HardLink(Path.Combine(folder, "receipt.json"), Path.Combine(root, "other.json")); return; }
        if (name == "bounded-folder-scan") { for (var i = 0; i < 513; i++) Directory.CreateDirectory(Path.Combine(root, "uploads", "junk-" + i)); return; }
        var path = Put(root); var receiptPath = Path.Combine(path, "receipt.json"); var receipt = JsonNode.Parse(File.ReadAllText(receiptPath))!.AsObject();
        if (name.StartsWith("invalid-state-", StringComparison.Ordinal)) receipt["state"] = name[14..];
        else if (name == "invalid-completed_at-True") receipt["completed_at"] = true;
        else if (name == "invalid-completed_at--1") receipt["completed_at"] = -1;
        else if (name is "invalid-completed_at-nan" or "invalid-completed_at-inf") { File.WriteAllText(receiptPath, File.ReadAllText(receiptPath).Replace(":1", name.EndsWith("nan", StringComparison.Ordinal) ? ":NaN" : ":Infinity", StringComparison.Ordinal)); return; }
        else if (name.StartsWith("invalid-sha256-", StringComparison.Ordinal)) receipt["sha256"] = new string('0', 64);
        else if (name.StartsWith("invalid-pkgName-", StringComparison.Ordinal)) receipt["pkgName"] = "eu.example.other";
        else if (name == "oversized-receipt-skipped") { File.WriteAllText(receiptPath, new string(' ', 65537)); return; }
        else if (name == "oversized-selected-apk-refused") { using var stream = File.OpenWrite(Path.Combine(path, "uploaded.apk")); stream.SetLength(32 * 1024 * 1024 + 1L); }
        File.WriteAllText(receiptPath, receipt.ToJsonString());
    }
    internal sealed class Setup : IDisposable
    {
        private readonly Sandbox _sandbox = new(); public string Engine { get; } public string Maintenance { get; } public string InstalledPath => Path.Combine(Engine, "extensions", "fixture.jar");
        public byte[] Binary { get; } = Jar("original"); public BoundedWorkScheduler Io { get; } = new(2, 8); public InventoryHttp Http { get; } = new(); public SuwayomiClient Client { get; } public EngineInstalledSources Installed { get; } public SourceBaselineStore Baselines { get; }
        public bool Ready = true; public long Generation = 1;
        public Setup(IInstalledSourceAnnotations? annotation = null, IInstalledSourceBinaryReader? binaryReader = null, bool restartable = false)
        {
            Engine = Path.Combine(_sandbox.Path, "engine"); Maintenance = Path.Combine(_sandbox.Path, "source-maintenance"); Directory.CreateDirectory(Path.Combine(Engine, "extensions")); Directory.CreateDirectory(Maintenance); File.WriteAllBytes(InstalledPath, Binary);
            var settings = new AutoSourcarrSettings { SourceRequestTimeout = 5 };
            Client = restartable
                ? new(settings, "http://127.0.0.1:4567", transportFactory: new InventoryTransportFactory(Http), privacy: new SupportPrivacy())
                : new(settings, "http://127.0.0.1:4567", handler: Http, privacy: new SupportPrivacy());
            Func<(bool Ready, long Generation)> state = () => (Volatile.Read(ref Ready), Volatile.Read(ref Generation));
            Installed = binaryReader is null ? new(Client, Engine, Maintenance, state, Io, annotation) : new(Client, binaryReader, Maintenance, state, Io, annotation);
            Baselines = new(Installed, Maintenance, Io);
        }
        public void Dispose() { Baselines.DisposeAsync().AsTask().GetAwaiter().GetResult(); Installed.DisposeAsync().AsTask().GetAwaiter().GetResult(); Client.Dispose(); Io.DisposeAsync().AsTask().GetAwaiter().GetResult(); _sandbox.Dispose(); }
    }
    private sealed class ControlledBinaryReader : IInstalledSourceBinaryReader
    {
        public int Calls { get; private set; }
        public Func<string, long, CancellationToken, Task<byte[]>> Read { get; set; } = (_, _, _) => throw new InvalidOperationException("Fixture reader was not configured");
        public Task<byte[]> ReadInstalledJarAsync(string leaf, long expectedGeneration, CancellationToken cancellationToken)
        { Calls++; return Read(leaf, expectedGeneration, cancellationToken); }
    }
    internal sealed class InventoryHttp : HttpMessageHandler
    {
        public int Calls; public string Query = ""; public Action<int>? Before;
        public JsonArray Rows { get; } = new(new JsonObject { ["pkgName"] = Package, ["name"] = "Example", ["lang"] = "en", ["apkName"] = "fixture.apk", ["apkUrl"] = "https://example.invalid/fixture.apk", ["isInstalled"] = true, ["isObsolete"] = false, ["hasUpdate"] = false, ["versionName"] = Version, ["storeIndexUrl"] = "https://github.com/keiyoushi/extensions/raw/repo/index.pb", ["source"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "-9223372036854775808", ["name"] = "作品😀", ["lang"] = "ja", ["baseUrl"] = "https://source.example" }) } });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!; Query = Text(body, "query"); var call = Interlocked.Increment(ref Calls); Before?.Invoke(call); return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["data"] = new JsonObject { ["extensions"] = new JsonObject { ["nodes"] = Rows.DeepClone() } } }.ToJsonString(), Encoding.UTF8, "application/json") }; }
    }
    private sealed class InventoryTransportFactory(InventoryHttp first) : ISourceTransportFactory
    {
        private int _issued;
        public HttpMessageHandler Create(SourceTransportPlane plane)
            => plane == SourceTransportPlane.Engine && Interlocked.Exchange(ref _issued, 1) == 0 ? first : new InventoryHttp();
    }
    private sealed class BlockingAnnotation : IInstalledSourceAnnotations, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new();
        public IReadOnlyList<JsonObject> Annotate(IReadOnlyList<JsonObject> extensions) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture annotation was not released"); return extensions; }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
    private static void HardLink(string existing, string link)
    {
        if (OperatingSystem.IsWindows()) Check.True(CreateHardLink(link, existing, IntPtr.Zero), "Native hard-link fixture setup failed: " + Marshal.GetLastWin32Error());
        else if (OperatingSystem.IsLinux()) Check.Equal(0, Link(existing, link));
        else throw new PlatformNotSupportedException("Native retained APK hard-link qualification needs Windows or Linux");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string filename, string existing, IntPtr reserved);
    [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int Link(string existing, string filename);
}
