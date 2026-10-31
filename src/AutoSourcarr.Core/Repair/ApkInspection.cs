using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutoSourcarr.Core.Repair;

/// <summary>What the APK checks found (SRC-2). Warnings are shown before install; refusals stop it.</summary>
public sealed record ApkReport(string Package, string ClassName, string LibraryVersion, string Sha256, int Bytes,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> Domains, IReadOnlyList<string> Warnings);

/// <summary>
/// Drag-and-drop APK checks (SRC-2, phase 6) with the existing readers: checked ZIP structure (sizes, CRCs, safe
/// names, no encryption), the binary Android manifest and the extension entry point; plus two rules:
/// native code is refused (Tachiyomi/Mihon extensions are JVM code only; a .so file can do anything the JVM sandbox
/// cannot see), and the web addresses in the code are listed so the person sees which sites the source talks to.
/// Permissions other than network access are reported. These checks cannot prove an extension is safe.
/// </summary>
public static partial class ApkInspection
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly HashSet<string> NetworkPermissions = new(StringComparer.Ordinal)
    {
        "android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE", "android.permission.ACCESS_WIFI_STATE"
    };

    [GeneratedRegex(@"^classes(?:[2-9][0-9]*)?\.dex$")]
    private static partial Regex DexName();

    [GeneratedRegex(@"https?://([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+)", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();

    public static ApkReport Inspect(byte[] data, string filename)
    {
        filename ??= "";
        if (!filename.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || filename.Length > 240 || filename.IndexOfAny(['/', '\\', '\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("Choose a source APK file with a plain filename.");
        if (data.Length is <= 100 or > MaximumBytes) throw new InvalidDataException("A source APK must be nonempty and at most 32 MiB.");
        using var archive = CheckedZipArchive.Open(data, MaximumBytes, SourcePackage.MaximumEntries, name =>
        {
            if (name.StartsWith('/') || name.Contains('\\') || name.Split('/').Contains(".."))
                throw new InvalidDataException("The APK contains an unsafe path.");
        });
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("The APK contains duplicate paths.");
        if (names.Any(name => name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || name.StartsWith("lib/", StringComparison.Ordinal) && !name.EndsWith('/')))
            throw new InvalidDataException("The APK contains native code (lib/*.so). Source extensions are JVM code only; this file is refused.");
        var dex = archive.Entries.Where(entry => DexName().IsMatch(entry.FullName)).ToArray();
        if (dex.Length == 0) throw new InvalidDataException("The file does not contain Android extension code; choose its APK, not a renamed JAR.");
        var manifestEntry = archive.Entries.SingleOrDefault(entry => entry.FullName == "AndroidManifest.xml")
            ?? throw new InvalidDataException("The APK has no Android manifest.");
        var manifest = SourceManifest.Parse(archive.ReadEntry(manifestEntry, 256 * 1024));
        var identity = SourceManifest.Identity(manifest);
        if (identity.Package == "eu.kanade.tachiyomi.extension.all.autosourcarr")
            throw new InvalidDataException("This is the AutoSourcarr reader extension; install it in Mihon, not as a source.");
        var permissions = manifest.Elements("uses-permission").Concat(manifest.Elements("uses-permission-sdk-23"))
            .Select(element => (string?)element.Attribute(SourceManifest.Android + "name") ?? "").Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var domains = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in dex)
        {
            var text = Encoding.Latin1.GetString(archive.ReadEntry(entry, MaximumBytes));
            foreach (Match match in WebAddress().Matches(text))
            {
                domains.Add(match.Groups[1].Value.ToLowerInvariant());
                if (domains.Count >= 200) break;
            }
        }
        var warnings = new List<string>();
        var unusual = permissions.Where(permission => !NetworkPermissions.Contains(permission)).ToArray();
        if (unusual.Length > 0) warnings.Add("The extension asks for permissions sources do not need: " + string.Join(", ", unusual) + ".");
        if (domains.Count == 0) warnings.Add("No web address was found in the code; the source may build its addresses at run time.");
        warnings.Add("These checks cannot prove the extension is safe. Install only extensions you trust.");
        return new ApkReport(identity.Package, identity.ClassName, identity.LibraryVersion, Convert.ToHexStringLower(SHA256.HashData(data)), data.Length,
            permissions, domains.ToArray(), warnings);
    }
}
