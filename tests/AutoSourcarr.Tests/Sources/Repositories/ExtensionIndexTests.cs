using System.IO.Compression;
using System.Text;
using AutoSourcarr.Core.Sources.Repositories;
using AutoSourcarr.Core.Sources.Repositories.Generated;
using Google.Protobuf;

namespace AutoSourcarr.Tests;

public static class ExtensionIndexTests
{
    private static readonly Uri PbUrl = new("https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.pb");
    private static readonly Uri JsonUrl = new("https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.min.json");
    private const string Folder = "https://raw.githubusercontent.com/keiyoushi/extensions/repo/";
    private const long NegativeId = -1234567890123L;

    public static void Register(TestSuite suite)
    {
        suite.Add("extension index: generated protobuf round trip keeps signed ids, metadata and skips unusable rows", RoundTrip);
        suite.Add("extension index: hand-encoded index.pb matches generated encoding and resolves relative URLs", HandEncoded);
        suite.Add("extension index: repeated field 101 occurrences merge like ExtensionRepos.kt", RepeatedList);
        suite.Add("extension index: gzip bodies expand with the bounded manifest reader", Gzip);
        suite.Add("extension index: empty, oversized, garbage, truncated and list-less bodies are refused", Refusals);
        suite.Add("extension index: legacy index.min.json resolves apk/ and icon/ next to the index", Legacy);
    }

    private static void RoundTrip()
    {
        var document = new IndexDocument
        {
            Name = "Keiyoushi", BadgeLabel = "KEI",
            ExtensionList = new IndexExtensionList
            {
                Extensions =
                {
                    new IndexExtension
                    {
                        Name = "Tachiyomi: Multi", Pkg = "eu.kanade.tachiyomi.extension.all.multi",
                        Resources = new IndexResources { ApkUrl = "https://cdn.example/apk/multi.apk", IconUrl = "https://cdn.example/icon/multi.png", JarUrl = "jar/multi.jar" },
                        Lib = "1.6", Code = 42, Version = "1.6.42", ContentWarning = 1,
                        Sources =
                        {
                            new IndexSource { Id = long.MinValue, Name = "Multi", Lang = "en", BaseUrl = "https://multi.example" },
                            new IndexSource { Id = long.MaxValue, Name = "Multi", Lang = "ja", BaseUrl = "https://multi.example/ja" },
                        },
                    },
                    new IndexExtension { Name = "No package", Resources = new IndexResources { ApkUrl = "apk/none.apk" } },
                    new IndexExtension { Name = "No resources", Pkg = "eu.kanade.tachiyomi.extension.en.none" },
                    new IndexExtension { Name = "Script", Pkg = "eu.kanade.tachiyomi.extension.en.script", Resources = new IndexResources { ApkUrl = "javascript:alert(1)" } },
                },
            },
        };
        var index = ExtensionIndex.Parse(document.ToByteArray(), PbUrl);
        Check.Equal("Keiyoushi", index.Name); Check.Equal("KEI", index.BadgeLabel); Check.Equal(false, index.IsLegacy);
        Check.Equal(1, index.Extensions.Count);
        var extension = index.Extensions[0];
        Check.Equal("Multi", extension.Name); Check.Equal("eu.kanade.tachiyomi.extension.all.multi", extension.Package);
        Check.Equal("all", extension.Lang);
        Check.Equal("https://cdn.example/apk/multi.apk", extension.ApkUrl); Check.Equal("https://cdn.example/icon/multi.png", extension.IconUrl);
        Check.Equal(Folder + "jar/multi.jar", extension.JarUrl);
        Check.Equal("1.6", extension.LibraryVersion); Check.Equal(42L, extension.VersionCode); Check.Equal("1.6.42", extension.VersionName);
        Check.Equal(1, extension.ContentWarning); Check.Equal(false, extension.Nsfw);
        Check.Equal(2, extension.Sources.Count);
        Check.Equal(new IndexedSource("-9223372036854775808", "Multi", "en", "https://multi.example"), extension.Sources[0]);
        Check.Equal(new IndexedSource("9223372036854775807", "Multi", "ja", "https://multi.example/ja"), extension.Sources[1]);

        // An index without a name falls back to its URL, like ExtensionRepos.kt.
        var unnamed = ExtensionIndex.Parse(new IndexDocument { ExtensionList = new IndexExtensionList() }.ToByteArray(), PbUrl);
        Check.Equal(PbUrl.AbsoluteUri, unnamed.Name); Check.Equal(0, unnamed.Extensions.Count);
    }

    private static void HandEncoded()
    {
        var hand = HandIndex("Hand", HandExtension());
        var generated = new IndexDocument
        {
            Name = "Hand",
            ExtensionList = new IndexExtensionList
            {
                Extensions =
                {
                    new IndexExtension
                    {
                        Name = "Tachiyomi: Foo", Pkg = "eu.kanade.tachiyomi.extension.en.foo",
                        Resources = new IndexResources { ApkUrl = "apk/tachiyomi-en.foo-v1.4.5.apk", IconUrl = "icon/eu.kanade.tachiyomi.extension.en.foo.png" },
                        Lib = "1.4", Code = 5, Version = "1.4.5", ContentWarning = 3,
                        Sources = { new IndexSource { Id = NegativeId, Name = "Foo", Lang = "en", BaseUrl = "https://foo.example" } },
                    },
                },
            },
        };
        Check.True(hand.SequenceEqual(generated.ToByteArray()), "Hand-encoded index.pb differs from the generated encoding");
        // A negative int64 is a ten-byte two's-complement varint on the wire.
        Check.True(ContainsSequence(hand, [0x08, 0xB5, 0xF6, 0x93, 0xF0, 0x88, 0xDC, 0xFF, 0xFF, 0xFF, 0x01]), "Negative id is not a ten-byte varint");
        Check.Equal(NegativeId, IndexDocument.Parser.ParseFrom(hand).ExtensionList.Extensions[0].Sources[0].Id);

        var index = ExtensionIndex.Parse(hand, PbUrl);
        Check.Equal("Hand", index.Name); Check.Equal(1, index.Extensions.Count);
        var extension = index.Extensions[0];
        Check.Equal("Foo", extension.Name); Check.Equal("en", extension.Lang);
        Check.Equal(Folder + "apk/tachiyomi-en.foo-v1.4.5.apk", extension.ApkUrl);
        Check.Equal(Folder + "icon/eu.kanade.tachiyomi.extension.en.foo.png", extension.IconUrl);
        Check.Equal<string?>(null, extension.JarUrl);
        Check.Equal("1.4", extension.LibraryVersion); Check.Equal(5L, extension.VersionCode); Check.Equal("1.4.5", extension.VersionName);
        Check.Equal(3, extension.ContentWarning); Check.Equal(true, extension.Nsfw);
        Check.Equal(new IndexedSource("-1234567890123", "Foo", "en", "https://foo.example"), extension.Sources.Single());

        // Unknown fields (for example a future top-level field) are skipped.
        var extra = new List<byte>(hand); Text(extra, 9, "ignored"); Number(extra, 10, -1);
        Check.Equal(1, ExtensionIndex.Parse([.. extra], PbUrl).Extensions.Count);
    }

    private static void RepeatedList()
    {
        var second = Message(extension =>
        {
            Text(extension, 1, "Bar"); Text(extension, 2, "eu.kanade.tachiyomi.extension.ja.bar");
            Bytes(extension, 3, Message(resources => Text(resources, 1, "https://mirror.example/bar.apk")));
            Bytes(extension, 8, Message(source => { Number(source, 1, 7); Text(source, 2, "Bar"); Text(source, 3, "ja"); }));
        });
        var body = new List<byte>(HandIndex("Merged", HandExtension()));
        Bytes(body, 101, Message(list => Bytes(list, 1, second)));
        var index = ExtensionIndex.Parse([.. body], PbUrl);
        Check.Equal(2, index.Extensions.Count);
        Check.Equal("eu.kanade.tachiyomi.extension.en.foo", index.Extensions[0].Package);
        Check.Equal("eu.kanade.tachiyomi.extension.ja.bar", index.Extensions[1].Package);
        Check.Equal("https://mirror.example/bar.apk", index.Extensions[1].ApkUrl);
        Check.Equal("ja", index.Extensions[1].Lang); Check.Equal("7", index.Extensions[1].Sources[0].Id);
        Check.Equal<string?>(null, index.Extensions[1].IconUrl);
    }

    private static void Gzip()
    {
        var plain = HandIndex("Zipped", HandExtension());
        var index = ExtensionIndex.Parse(Compress(plain), PbUrl);
        Check.Equal("Zipped", index.Name);
        Check.Equal("-1234567890123", index.Extensions.Single().Sources.Single().Id);
        Check.Equal(Folder + "apk/tachiyomi-en.foo-v1.4.5.apk", index.Extensions.Single().ApkUrl);

        var legacy = ExtensionIndex.Parse(Compress(Encoding.UTF8.GetBytes(LegacyBody)), JsonUrl);
        Check.Equal(true, legacy.IsLegacy); Check.Equal(1, legacy.Extensions.Count);

        // Expansion is bounded by the same 3 MiB limit as the repository manifest.
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse(Compress(new byte[RepositoryManifest.MaximumBytes + 1]), PbUrl));
        var corrupt = Compress(plain); corrupt[^8] ^= 1;
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse(corrupt, PbUrl));
    }

    private static void Refusals()
    {
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse([], PbUrl));
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse(new byte[RepositoryManifest.MaximumBytes + 1], PbUrl));
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse(Encoding.UTF8.GetBytes("<html>Error</html>"), PbUrl));
        var hand = HandIndex("Hand", HandExtension());
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse(hand[..^1], PbUrl));
        // A length prefix that runs past the end of the body.
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse([0x0a, 0x7f, 0x41], PbUrl));
        // A well-formed protobuf without the field-101 extension list is not an index.
        var nameOnly = new List<byte>(); Text(nameOnly, 1, "Only a name");
        Check.Throws<InvalidDataException>(() => ExtensionIndex.Parse([.. nameOnly], PbUrl));
        Check.Throws<ArgumentException>(() => ExtensionIndex.Parse(hand, new Uri("repo/index.pb", UriKind.Relative)));
        Check.Throws<InvalidDataException>(() => ExtensionIndex.FromLegacyJson(Encoding.UTF8.GetBytes("{\"name\":\"not a list\"}"), JsonUrl));
        Check.Throws<InvalidDataException>(() => ExtensionIndex.FromLegacyJson(Encoding.UTF8.GetBytes("[{\"name\":\"x\",\"apk\":\"x.apk\",\"version\":\"1.0\"}]"), JsonUrl));
        Check.Throws<InvalidDataException>(() => ExtensionIndex.FromLegacyJson(Encoding.UTF8.GetBytes("[1]"), JsonUrl));
        Check.Throws<InvalidDataException>(() => ExtensionIndex.FromLegacyJson([0x5b, 0xff, 0x5d], JsonUrl));
    }

    private const string LegacyBody = """
        [{"name":"Tachiyomi: Old","pkg":"eu.kanade.tachiyomi.extension.en.old","apk":"tachiyomi-en.old-v1.3.7.apk","lang":"en",
          "code":7,"version":"1.3.7","nsfw":1,
          "sources":[{"id":"-42","name":"Old","lang":"en","baseUrl":"https://old.example"},
                     {"id":9007199254740993,"name":"Big","lang":"en"},
                     {"id":"not-a-number","name":"Dropped","lang":"en"}]}]
        """;

    private static void Legacy()
    {
        foreach (var index in new[] { ExtensionIndex.Parse(Encoding.UTF8.GetBytes(LegacyBody), JsonUrl), ExtensionIndex.FromLegacyJson(Encoding.UTF8.GetBytes(LegacyBody), JsonUrl) })
        {
            Check.Equal(true, index.IsLegacy); Check.Equal(Folder, index.Name); Check.Equal(1, index.Extensions.Count);
            var extension = index.Extensions[0];
            Check.Equal("Old", extension.Name); Check.Equal("eu.kanade.tachiyomi.extension.en.old", extension.Package); Check.Equal("en", extension.Lang);
            Check.Equal(Folder + "apk/tachiyomi-en.old-v1.3.7.apk", extension.ApkUrl);
            Check.Equal(Folder + "icon/eu.kanade.tachiyomi.extension.en.old.png", extension.IconUrl);
            Check.Equal("1.3", extension.LibraryVersion); Check.Equal(7L, extension.VersionCode); Check.Equal("1.3.7", extension.VersionName);
            Check.Equal(1, extension.ContentWarning); Check.Equal(true, extension.Nsfw);
            Check.Equal(2, extension.Sources.Count);
            Check.Equal(new IndexedSource("-42", "Old", "en", "https://old.example"), extension.Sources[0]);
            Check.Equal(new IndexedSource("9007199254740993", "Big", "en", ""), extension.Sources[1]);
        }
    }

    private static byte[] HandExtension() => Message(extension =>
    {
        Text(extension, 1, "Tachiyomi: Foo");
        Text(extension, 2, "eu.kanade.tachiyomi.extension.en.foo");
        Bytes(extension, 3, Message(resources =>
        {
            Text(resources, 1, "apk/tachiyomi-en.foo-v1.4.5.apk");
            Text(resources, 2, "icon/eu.kanade.tachiyomi.extension.en.foo.png");
        }));
        Text(extension, 4, "1.4");
        Number(extension, 5, 5);
        Text(extension, 6, "1.4.5");
        Number(extension, 7, 3);
        Bytes(extension, 8, Message(source =>
        {
            Number(source, 1, NegativeId);
            Text(source, 2, "Foo");
            Text(source, 3, "en");
            Text(source, 4, "https://foo.example");
        }));
    });

    private static byte[] HandIndex(string name, byte[] extension) => Message(index =>
    {
        Text(index, 1, name);
        Bytes(index, 101, Message(list => Bytes(list, 1, extension)));
    });

    private static byte[] Message(Action<List<byte>> build) { var output = new List<byte>(); build(output); return [.. output]; }

    private static void Varint(List<byte> output, ulong value)
    {
        while (value >= 0x80) { output.Add((byte)(value | 0x80)); value >>= 7; }
        output.Add((byte)value);
    }

    private static void Tag(List<byte> output, int field, int wire) => Varint(output, (ulong)((field << 3) | wire));
    private static void Number(List<byte> output, int field, long value) { Tag(output, field, 0); Varint(output, unchecked((ulong)value)); }
    private static void Bytes(List<byte> output, int field, byte[] value) { Tag(output, field, 2); Varint(output, (ulong)value.Length); output.AddRange(value); }
    private static void Text(List<byte> output, int field, string value) => Bytes(output, field, Encoding.UTF8.GetBytes(value));

    private static bool ContainsSequence(byte[] haystack, byte[] needle) =>
        Enumerable.Range(0, haystack.Length - needle.Length + 1).Any(start => haystack.AsSpan(start, needle.Length).SequenceEqual(needle));

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }
}
