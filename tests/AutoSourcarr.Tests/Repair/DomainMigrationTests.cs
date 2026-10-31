using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Repair.Migration;

namespace AutoSourcarr.Tests.Repair;

/// <summary>Phase 6e domain migration: constant-pool rewrite, JAR structure check, refusal and repair rollback.</summary>
public static class DomainMigrationTests
{
    private const string OldUrl = "https://old.example/api";

    /// <summary>A hand-written class file. Offsets of the Utf8 entry holding the URL are returned for byte comparisons.</summary>
    private sealed class ClassBuilder
    {
        private readonly MemoryStream _pool = new();
        private int _next = 1;
        public int UrlEntryStart { get; private set; }
        public int UrlEntryEnd { get; private set; }

        private void U1(int value) => _pool.WriteByte((byte)value);
        private void U2(int value) { _pool.WriteByte((byte)(value >> 8)); _pool.WriteByte((byte)value); }
        public int Utf8(byte[] raw) { U1(1); U2(raw.Length); _pool.Write(raw); return _next++; }
        public int Utf8(string ascii) => Utf8(Encoding.ASCII.GetBytes(ascii));
        public int Url(string ascii)
        {
            UrlEntryStart = 10 + (int)_pool.Length;
            var index = Utf8(ascii);
            UrlEntryEnd = 10 + (int)_pool.Length;
            return index;
        }
        public int Ref(int tag, int index) { U1(tag); U2(index); return _next++; }
        public int Pair(int tag, int first, int second) { U1(tag); U2(first); U2(second); return _next++; }
        public int Four(int tag, uint value) { U1(tag); U2((int)(value >> 16)); U2((int)(value & 0xFFFF)); return _next++; }
        public int Eight(int tag, ulong value)
        {
            U1(tag); for (var shift = 56; shift >= 0; shift -= 8) U1((int)(value >> shift) & 0xFF);
            var index = _next; _next += 2; return index; // long and double take two slots
        }
        public int Handle(int kind, int reference) { U1(15); U1(kind); U2(reference); return _next++; }

        public byte[] Build(int thisClass, int superClass)
        {
            using var output = new MemoryStream();
            output.Write([0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x34]); // magic, minor 0, major 52
            output.WriteByte((byte)(_next >> 8)); output.WriteByte((byte)_next);
            _pool.Position = 0; _pool.CopyTo(output);
            // access_flags, this_class, super_class, interfaces, fields, methods, attributes
            output.Write([0x00, 0x21, (byte)(thisClass >> 8), (byte)thisClass, (byte)(superClass >> 8), (byte)superClass, 0, 0, 0, 0, 0, 0, 0, 0]);
            output.Write(Encoding.ASCII.GetBytes("TAIL")); // trailing bytes must survive unchanged
            return output.ToArray();
        }
    }

    /// <summary>Class name, super java/lang/Object, the URL, a Long (double slot), a Double, a MethodHandle and the other tags.</summary>
    private static (byte[] Bytes, ClassBuilder Builder) SampleClass(string name = "eu/kanade/tachiyomi/extension/en/example/Example", string url = OldUrl)
    {
        var b = new ClassBuilder();
        var nameUtf8 = b.Utf8(name);
        var thisClass = b.Ref(7, nameUtf8);
        var objectUtf8 = b.Utf8("java/lang/Object");
        var superClass = b.Ref(7, objectUtf8);
        var urlUtf8 = b.Url(url);
        b.Ref(8, urlUtf8);                                // String
        b.Eight(5, 0x0102030405060708UL);                 // Long
        b.Eight(6, 0x400921FB54442D18UL);                 // Double (pi)
        b.Four(3, 42);                                    // Integer
        b.Four(4, 0x3F800000);                            // Float 1.0
        var init = b.Utf8("<init>");
        var descriptor = b.Utf8("()V");
        var nameAndType = b.Pair(12, init, descriptor);
        var method = b.Pair(10, superClass, nameAndType); // Methodref
        b.Pair(9, superClass, nameAndType);               // Fieldref (shape only)
        b.Pair(11, superClass, nameAndType);              // InterfaceMethodref (shape only)
        b.Handle(7, method);                              // MethodHandle REF_invokeSpecial
        b.Ref(16, descriptor);                            // MethodType
        b.Pair(17, 0, nameAndType);                       // Dynamic
        b.Pair(18, 0, nameAndType);                       // InvokeDynamic
        b.Ref(19, nameUtf8);                              // Module (shape only)
        b.Ref(20, nameUtf8);                              // Package (shape only)
        b.Utf8([0x63, 0x61, 0x66, 0xC3, 0xA9, 0xC0, 0x80]); // "café\0" in modified UTF-8
        b.Utf8("https://bold.example/ and old.example.org stay");
        return (b.Build(thisClass, superClass), b);
    }

    private static byte[] Zip(params (string Name, byte[] Contents)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, contents) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open(); stream.Write(contents);
            }
        return output.ToArray();
    }

    private static Dictionary<string, byte[]> Entries(byte[] jar)
    {
        using var archive = new ZipArchive(new MemoryStream(jar), ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open(); using var copy = new MemoryStream(); stream.CopyTo(copy); result[entry.FullName] = copy.ToArray();
        }
        return result;
    }

    private static byte[] SampleJar(string url = OldUrl) => Zip(
        ("META-INF/MANIFEST.MF", Encoding.ASCII.GetBytes("Manifest-Version: 1.0\r\n\r\n")),
        ("eu/kanade/tachiyomi/extension/en/example/Example.class", SampleClass(url: url).Bytes),
        ("eu/kanade/tachiyomi/extension/en/example/Other.class", SampleClass("eu/kanade/tachiyomi/extension/en/example/Other", "https://cdn.unrelated.example/x").Bytes),
        ("eu/kanade/tachiyomi/extension/en/example/Broken.class", [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 52, 0, 9, 99, 1, 2]),
        ("assets/i18n/messages_en.properties", Encoding.UTF8.GetBytes("site=https://old.example\n")));

    public static void Register(TestSuite suite)
    {
        suite.Add("domain migration: Utf8 constant replaced with a new length; long/double slots, MethodHandle and all other tags parsed; other bytes identical", () =>
        {
            var (original, builder) = SampleClass();
            var before = ClassFileStrings.Parse(original);
            Check.Equal("eu/kanade/tachiyomi/extension/en/example/Example", before.ThisClass);
            Check.True(before.Utf8.Contains("café\0"), "modified UTF-8 (two-byte NUL) decoded");
            Check.Equal(27, before.PoolCount);
            Check.Equal(7, before.Utf8.Count);

            var result = ClassFileStrings.Rewrite(original, text => DomainMigration.ReplaceHost(text, "old.example", "new-site.example"));
            Check.True(result.Parsed, result.Error ?? "");
            Check.Equal(1, result.Replacements);
            Check.Equal(OldUrl, result.Changes[0].Before);
            Check.Equal("https://new-site.example/api", result.Changes[0].After);

            // Recount: parsing the rewritten class finds the same pool, the new string once and the old one nowhere.
            var after = ClassFileStrings.Parse(result.Bytes);
            Check.Equal(before.PoolCount, after.PoolCount);
            Check.Equal(before.Utf8.Count, after.Utf8.Count);
            Check.Equal(before.ThisClass, after.ThisClass);
            Check.Equal(1, after.Utf8.Count(text => text == "https://new-site.example/api"));
            Check.True(!after.Utf8.Any(text => text.Contains("://old.example", StringComparison.Ordinal)), "old URL gone");
            Check.True(after.Utf8.Contains("https://bold.example/ and old.example.org stay"), "look-alike hosts untouched");
            Check.Equal(0, ClassFileStrings.Rewrite(result.Bytes, text => DomainMigration.ReplaceHost(text, "old.example", "new-site.example")).Replacements);

            // Length prefix rewritten; everything before and after the entry byte-identical.
            var delta = "new-site.example".Length - "old.example".Length;
            Check.Equal(original.Length + delta, result.Bytes.Length);
            Check.True(original.AsSpan(0, builder.UrlEntryStart + 1).SequenceEqual(result.Bytes.AsSpan(0, builder.UrlEntryStart + 1)), "prefix identical");
            var length = (result.Bytes[builder.UrlEntryStart + 1] << 8) | result.Bytes[builder.UrlEntryStart + 2];
            Check.Equal("https://new-site.example/api".Length, length);
            Check.True(original.AsSpan(builder.UrlEntryEnd).SequenceEqual(result.Bytes.AsSpan(builder.UrlEntryEnd + delta)), "suffix identical");
            Check.True(result.Bytes.AsSpan(result.Bytes.Length - 4).SequenceEqual("TAIL"u8), "class body kept");

            // A class whose pool does not parse is reported and returned unchanged.
            var broken = original[..40];
            var refused = ClassFileStrings.Rewrite(broken, text => text + "x");
            Check.True(!refused.Parsed && ReferenceEquals(broken, refused.Bytes) && refused.Replacements == 0, "truncated class left untouched");
            var badTag = (byte[])original.Clone(); badTag[10] = 2; // tag 2 does not exist
            Check.True(!ClassFileStrings.Rewrite(badTag, text => text).Parsed, "unknown tag refused");
            Check.True(Check.Throws<InvalidDataException>(() => ClassFileStrings.Parse(badTag)).Message.Contains("tag", StringComparison.Ordinal));

            // Modified UTF-8 round trip of NUL and a supplementary character (two surrogates, three bytes each).
            var sample = "a\0\U0001F600";
            var encoded = ClassFileStrings.EncodeModifiedUtf8(sample);
            Check.Equal(1 + 2 + 6, encoded.Length);
            Check.Equal(sample, ClassFileStrings.DecodeModifiedUtf8(encoded));
        });

        suite.Add("domain migration: host replacement keeps whole host names only and can upgrade http to https", () =>
        {
            Check.Equal("https://new.example/api", DomainMigration.ReplaceHost("https://old.example/api", "old.example", "new.example"));
            Check.Equal("https://cdn.new.example/i.png", DomainMigration.ReplaceHost("https://cdn.old.example/i.png", "old.example", "new.example"));
            Check.Equal("new.example", DomainMigration.ReplaceHost("OLD.EXAMPLE", "old.example", "new.example"));
            Check.Equal("https://bold.example", DomainMigration.ReplaceHost("https://bold.example", "old.example", "new.example"));
            Check.Equal("https://old.example.org/", DomainMigration.ReplaceHost("https://old.example.org/", "old.example", "new.example"));
            Check.Equal("https://old.example-cdn.com", DomainMigration.ReplaceHost("https://old.example-cdn.com", "old.example", "new.example"));
            Check.Equal("https://new.example:443/x", DomainMigration.ReplaceHost("https://old.example:443/x", "old.example", "new.example"));
            Check.Equal("https://new.example/", DomainMigration.ReplaceHost("http://old.example/", "old.example", "new.example", "https"));
            Check.Equal("http://new.example/", DomainMigration.ReplaceHost("http://old.example/", "old.example", "new.example"));
            Check.Equal("new.example", DomainMigration.NormalizeHost("https://New.Example/path"));
            Check.Throws<DomainMigrationException>(() => DomainMigration.NormalizeHost("not a host"));
            Check.True(DomainMigration.SameSite("www.old.example", "old.example") && !DomainMigration.SameSite("new.example", "old.example"));
        });

        suite.Add("domain migration: JAR round trip passes the structure check; manifest, assets and other classes unchanged", () =>
        {
            var jar = SampleJar();
            var result = DomainMigration.Migrate(jar, "https://old.example", "https://new-site.example");
            Check.Equal(1, result.Replacements);
            var report = result.Report;
            Check.Equal("old.example", report["old_host"]!.GetValue<string>());
            Check.Equal("new-site.example", report["new_host"]!.GetValue<string>());
            Check.Equal(1, report["classes_changed"]!.GetValue<int>());
            Check.Equal(1, report["unparsed_classes"]!.AsArray().Count);
            Check.True(report["structure"]!["ok"]!.GetValue<bool>(), "structure ok");
            Check.Equal(5, report["structure"]!["entries"]!.GetValue<int>());
            Check.Equal(2, report["structure"]!["classes_parsed"]!.GetValue<int>());

            var before = Entries(jar); var after = Entries(result.Jar);
            Check.Equal(before.Count, after.Count);
            foreach (var name in new[] { "META-INF/MANIFEST.MF", "assets/i18n/messages_en.properties",
                         "eu/kanade/tachiyomi/extension/en/example/Other.class", "eu/kanade/tachiyomi/extension/en/example/Broken.class" })
                Check.True(before[name].AsSpan().SequenceEqual(after[name]), name + " unchanged");
            var changed = ClassFileStrings.Parse(after["eu/kanade/tachiyomi/extension/en/example/Example.class"]);
            Check.True(changed.Utf8.Contains("https://new-site.example/api"), "class rewritten");

            // The check runs again on its own and catches a changed non-class entry or a class that no longer parses.
            Check.True(DomainMigration.CheckStructure(jar, result.Jar)["ok"]!.GetValue<bool>());
            var tampered = Zip(after.Select(pair => (pair.Key, pair.Key.StartsWith("assets/", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("changed") : pair.Value)).ToArray());
            Check.Equal("structure_check", Check.Throws<DomainMigrationException>(() => DomainMigration.CheckStructure(jar, tampered)).Code);
            var unparsable = Zip(after.Select(pair => (pair.Key, pair.Key.EndsWith("Example.class", StringComparison.Ordinal) ? pair.Value[..30] : pair.Value)).ToArray());
            Check.Equal("structure_check", Check.Throws<DomainMigrationException>(() => DomainMigration.CheckStructure(jar, unparsable)).Code);
            var shorter = Zip(after.Where(pair => !pair.Key.StartsWith("assets/", StringComparison.Ordinal)).Select(pair => (pair.Key, pair.Value)).ToArray());
            Check.Equal("structure_check", Check.Throws<DomainMigrationException>(() => DomainMigration.CheckStructure(jar, shorter)).Code);
        });

        suite.Add("domain migration: refused when nothing refers to the old host, for an APK and for the same host", () =>
        {
            var jar = SampleJar("https://elsewhere.example/api");
            Check.Equal("nothing_replaced", Check.Throws<DomainMigrationException>(() => DomainMigration.Migrate(jar, "old.example", "new.example")).Code);
            var apk = Zip(("AndroidManifest.xml", new byte[64]), ("classes.dex", new byte[64]));
            Check.True(DomainMigration.IsApk(apk), "APK recognised");
            Check.Equal("apk_input", Check.Throws<DomainMigrationException>(() => DomainMigration.Migrate(apk, "old.example", "new.example")).Code);
            Check.Equal("same_host", Check.Throws<DomainMigrationException>(() => DomainMigration.Migrate(SampleJar(), "old.example", "OLD.example")).Code);
            Check.Equal("invalid_jar", Check.Throws<DomainMigrationException>(() => DomainMigration.Migrate([1, 2, 3], "old.example", "new.example")).Code);
        });

        suite.Add("domain repair: keeps the working jar, installs the rewrite, rolls back when the content check fails, and restores on request", () =>
        {
            using var temp = new Sandbox();
            var original = SampleJar();
            var host = new FakeHost(original);
            var repair = new DomainRepair(Path.Combine(temp.Path, "source-maintenance"), host, new SemaphoreSlim(1, 1));

            host.ContentWorks = false;
            var failed = repair.RunAsync(FakeHost.Package, new DomainRepairOptions(NewHost: "new-site.example")).GetAwaiter().GetResult();
            Check.Equal("rolled_back", failed["state"]!.GetValue<string>());
            Check.Equal("pages", failed["content_check"]!["failed_at"]!.GetValue<string>());
            Check.Equal(2, host.Installs.Count);
            Check.True(host.Installs[1].AsSpan().SequenceEqual(original), "kept jar installed again");
            var sha = DomainMigration.Sha(original);
            Check.True(File.Exists(Path.Combine(repair.LastWorkingDirectory(FakeHost.Package), sha + ".jar")), "last working jar kept");
            Check.True(File.Exists(Path.Combine(repair.LastWorkingDirectory(FakeHost.Package), sha + ".json")), "kept record written");
            Check.Equal("rolled_back", repair.LastReport(FakeHost.Package)!["state"]!.GetValue<string>());

            host.ContentWorks = true;
            var repaired = repair.RunAsync(FakeHost.Package, new DomainRepairOptions(NewHost: "https://new-site.example")).GetAwaiter().GetResult();
            Check.Equal("repaired", repaired["state"]!.GetValue<string>());
            Check.Equal(3, host.Installs.Count);
            Check.Equal(DomainMigration.Sha(host.Installs[2]), repaired["installed_sha256"]!.GetValue<string>());
            Check.True(repaired["content_check"]!["ok"]!.GetValue<bool>(), "content check passed");

            var restored = repair.RollbackAsync(FakeHost.Package).GetAwaiter().GetResult();
            Check.Equal("rolled_back", restored["state"]!.GetValue<string>());
            Check.True(host.Installs[^1].AsSpan().SequenceEqual(original), "rollback installs the last kept working jar");

            // Nothing to replace: refused before anything is installed.
            var before = host.Installs.Count;
            host.Jar = SampleJar("https://elsewhere.example/api");
            var refused = repair.RunAsync(FakeHost.Package, new DomainRepairOptions(NewHost: "new-site.example")).GetAwaiter().GetResult();
            Check.Equal("refused", refused["state"]!.GetValue<string>());
            Check.Equal(before, host.Installs.Count);
            Check.Throws<DomainRepairException>(() => repair.RunAsync("../escape").GetAwaiter().GetResult());
        });

        suite.Add("domain repair: an observed cross-site redirect gives the new host; a same-site redirect is refused", () =>
        {
            using var temp = new Sandbox();
            var host = new FakeHost(SampleJar()) { ContentWorks = true };
            var final = "https://new-site.example/";
            var repair = new DomainRepair(Path.Combine(temp.Path, "source-maintenance"), host, new SemaphoreSlim(1, 1),
                (url, _) => Task.FromResult(new JsonObject { ["requested_url"] = url, ["final_url"] = final, ["state"] = "working",
                    ["redirects"] = new JsonArray((JsonNode?)final) }));
            var repaired = repair.RunAsync(FakeHost.Package).GetAwaiter().GetResult();
            Check.Equal("repaired", repaired["state"]!.GetValue<string>());
            Check.Equal("observed-redirect", repaired["address_basis"]!.GetValue<string>());
            Check.Equal("new-site.example", repaired["new_host"]!.GetValue<string>());

            final = "https://www.old.example/";
            host.Jar = SampleJar();
            var refused = repair.RunAsync(FakeHost.Package).GetAwaiter().GetResult();
            Check.Equal("refused", refused["state"]!.GetValue<string>());
        });
    }

    /// <summary>A source host with one installed package whose content check works or fails on demand.</summary>
    private sealed class FakeHost(byte[] jar) : IDomainRepairHost, IContentCheckSource
    {
        public const string Package = "eu.kanade.tachiyomi.extension.en.example";
        public byte[] Jar { get; set; } = jar;
        public string BaseUrl { get; } = "https://old.example";
        public bool ContentWorks { get; set; }
        public List<byte[]> Installs { get; } = [];
        public IContentCheckSource Content => this;

        private JsonObject Row() => new()
        {
            ["pkgName"] = Package, ["versionName"] = "1.4.7", ["isInstalled"] = true, ["loadError"] = null,
            ["source"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "123", ["name"] = "Example", ["lang"] = "en", ["baseUrl"] = BaseUrl }) },
        };
        public Task<JsonObject?> ExtensionAsync(string packageName, CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(packageName == Package ? Row() : null);
        public Task<byte[]> ReadJarAsync(string packageName, CancellationToken cancellationToken) => Task.FromResult(Jar);
        public Task<JsonObject> InstallJarAsync(string packageName, byte[] jar, CancellationToken cancellationToken)
        {
            Installs.Add(jar); Jar = jar;
            return Task.FromResult(Row());
        }

        public Task<JsonObject> BrowseAsync(string sourceId, string mode, string query, CancellationToken cancellationToken) =>
            Task.FromResult(new JsonObject { ["hasNextPage"] = false, ["mangas"] = new JsonArray(new JsonObject { ["id"] = 7, ["sourceId"] = sourceId, ["title"] = "T" }) });
        public Task<JsonObject> DetailsAsync(long mangaId, CancellationToken cancellationToken) =>
            Task.FromResult(new JsonObject { ["manga"] = new JsonObject { ["id"] = mangaId, ["title"] = "T" },
                ["chapters"] = new JsonArray(new JsonObject { ["id"] = 70, ["mangaId"] = mangaId }, new JsonObject { ["id"] = 71, ["mangaId"] = mangaId }) });
        public Task<List<string>> PagesAsync(long chapterId, CancellationToken cancellationToken) =>
            Task.FromResult(ContentWorks ? new List<string> { "/api/v1/manga/7/chapter/1/page/0" } : []);
        public Task<(byte[] Bytes, string ContentType)> FirstImageAsync(long mangaId, long chapterId, CancellationToken cancellationToken) =>
            Task.FromResult((new byte[2048], "image/jpeg"));
    }
}
