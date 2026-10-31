using System.IO.Compression;
using System.Text;
using AutoSourcarr.Core.Repair;

namespace AutoSourcarr.Tests.Repair;

/// <summary>Phase 6 drag-and-drop APK checks (SRC-2).</summary>
public static class ApkInspectionTests
{
    private const string Manifest = "<manifest xmlns:android=\"" + SourceManifest.AndroidNamespace + "\" package=\"eu.kanade.tachiyomi.extension.en.example\" android:versionName=\"1.4.23\"><uses-permission android:name=\"android.permission.INTERNET\"/><uses-permission android:name=\"android.permission.READ_CONTACTS\"/><application><meta-data android:name=\"tachiyomi.extension.class\" android:value=\".Example\"/></application></manifest>";

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

    public static void Register(TestSuite suite)
    {
        suite.Add("apk checks: identity, permissions and web addresses reported; native code, missing code, bad names and the reader extension refused", () =>
        {
            var dex = Encoding.Latin1.GetBytes("dex\n035\0padding https://www.example-manga.com/api https://cdn.Example-Manga.com/img x" + new string('a', 200));
            var good = Zip(("AndroidManifest.xml", Encoding.UTF8.GetBytes(Manifest)), ("classes.dex", dex));
            var report = ApkInspection.Inspect(good, "Example.apk");
            Check.Equal("eu.kanade.tachiyomi.extension.en.example", report.Package);
            Check.Equal("eu.kanade.tachiyomi.extension.en.example.Example", report.ClassName);
            Check.Equal("1.4", report.LibraryVersion);
            Check.Equal("cdn.example-manga.com,www.example-manga.com", string.Join(',', report.Domains));
            Check.True(report.Warnings.Any(warning => warning.Contains("READ_CONTACTS", StringComparison.Ordinal)));
            Check.Equal(64, report.Sha256.Length);
            var native = Zip(("AndroidManifest.xml", Encoding.UTF8.GetBytes(Manifest)), ("classes.dex", dex), ("lib/arm64-v8a/libevil.so", new byte[300]));
            Check.True(Check.Throws<InvalidDataException>(() => ApkInspection.Inspect(native, "x.apk")).Message.Contains("native code", StringComparison.Ordinal));
            var jar = Zip(("AndroidManifest.xml", Encoding.UTF8.GetBytes(Manifest)), ("Example.class", new byte[300]));
            Check.Throws<InvalidDataException>(() => ApkInspection.Inspect(jar, "x.apk"));
            Check.Throws<InvalidDataException>(() => ApkInspection.Inspect(good, "x.jar"));
            Check.Throws<InvalidDataException>(() => ApkInspection.Inspect(good, "../x.apk"));
            var reader = Zip(("AndroidManifest.xml", Encoding.UTF8.GetBytes(Manifest.Replace("en.example", "all.autosourcarr", StringComparison.Ordinal))), ("classes.dex", dex));
            Check.Throws<InvalidDataException>(() => ApkInspection.Inspect(reader, "x.apk"));
            Check.Throws<InvalidDataException>(() => ApkInspection.Inspect(new byte[500], "x.apk"));
        });
    }
}
