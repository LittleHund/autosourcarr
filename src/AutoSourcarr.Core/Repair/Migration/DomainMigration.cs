using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Repair.Migration;

/// <summary>A refused or failed migration; <see cref="Code"/> is a stable reason for reports and the API.</summary>
public sealed class DomainMigrationException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>The rewritten JAR and its report (replacements per class, structure check).</summary>
public sealed record DomainMigrationResult(byte[] Jar, int Replacements, JsonObject Report);

/// <summary>
/// Phase 6e domain migration: a source whose website moved is repaired by rewriting the old host in the string
/// constants of the extension's compiled code, without compiling. The input is the JAR the source host keeps for an
/// installed extension (dex2jar output plus the APK's assets/, no META-INF). Only CONSTANT_Utf8 entries change; every
/// other entry is copied, and the result must pass <see cref="CheckStructure"/> before it is returned.
/// An APK holds Dalvik code (classes.dex), which this editor does not rewrite: the host converts an installed APK to
/// the JAR first, and that JAR (GET /v1/extensions/{pkg}/jar on the host) is the input.
/// </summary>
public static class DomainMigration
{
    public const int MaximumJar = 64 * 1024 * 1024;
    public const int MaximumEntries = 20_000;
    public const int MaximumEntryBytes = 32 * 1024 * 1024;
    public const long MaximumExpanded = 256L * 1024 * 1024;
    private const int MaximumReportedChanges = 64;
    private static readonly Regex HostName = new(@"\A(?=.{1,253}\z)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z",
        RegexOptions.CultureInvariant);

    /// <summary>A bare lower-case host from "old.example", "https://old.example/path" or an IDN name.</summary>
    public static string NormalizeHost(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new DomainMigrationException("invalid_host", "Not an http(s) address: " + Short(text));
            text = uri.IdnHost;
        }
        text = text.TrimEnd('.').ToLowerInvariant();
        try { text = new IdnMapping().GetAscii(text); }
        catch (ArgumentException) { throw new DomainMigrationException("invalid_host", "Not a valid host name: " + Short(text)); }
        if (!HostName.IsMatch(text)) throw new DomainMigrationException("invalid_host", "Not a valid public host name: " + Short(text));
        return text;
    }

    /// <summary>True when one host is the other or a subdomain of it (www.old.example and old.example).</summary>
    public static bool SameSite(string first, string second) =>
        first == second || first.EndsWith("." + second, StringComparison.Ordinal) || second.EndsWith("." + first, StringComparison.Ordinal);

    /// <summary>
    /// Replaces the old host inside one string: whole host names only (case-insensitive), so "old.example" matches in
    /// "https://old.example/api" and "cdn.old.example" but not in "bold.example" or "old.example.org". With
    /// <paramref name="newScheme"/> "https", "http://old.example" also becomes "https://new.example".
    /// </summary>
    public static string ReplaceHost(string text, string oldHost, string newHost, string? newScheme = null)
    {
        if (text.Length < oldHost.Length || text.IndexOf(oldHost, StringComparison.OrdinalIgnoreCase) < 0) return text;
        var output = new System.Text.StringBuilder(text.Length + 16);
        var position = 0;
        while (position < text.Length)
        {
            var found = text.IndexOf(oldHost, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            var after = found + oldHost.Length;
            var leftOk = found == 0 || !HostCharacter(text[found - 1]) || text[found - 1] == '.';
            var next = after < text.Length ? text[after] : '\0';
            var rightOk = after == text.Length || !(char.IsAsciiLetterOrDigit(next) || next == '-')
                && !(next == '.' && after + 1 < text.Length && char.IsAsciiLetterOrDigit(text[after + 1]));
            if (!leftOk || !rightOk) { output.Append(text, position, after - position); position = after; continue; }
            var start = found;
            if (newScheme == "https" && found >= 7 && string.Compare(text, found - 7, "http://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0)
            {
                start = found - 7;
                output.Append(text, position, start - position).Append("https://");
            }
            else output.Append(text, position, found - position);
            output.Append(newHost);
            position = after;
        }
        output.Append(text, position, text.Length - position);
        return output.ToString();
    }

    private static bool HostCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '-' or '.';

    /// <summary>True for an Android package (Dalvik code or a binary manifest) rather than a JAR.</summary>
    public static bool IsApk(byte[] archive)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
            return zip.Entries.Any(entry => entry.FullName is "AndroidManifest.xml" || Regex.IsMatch(entry.FullName, @"\Aclasses\d*\.dex\z", RegexOptions.CultureInvariant));
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Rewrites every .class entry of <paramref name="jar"/>, writes a new JAR and checks its structure.
    /// Refuses an APK, an unreadable archive and a JAR in which nothing referred to the old host.</summary>
    public static DomainMigrationResult Migrate(byte[] jar, string oldHost, string newHost, string? newScheme = null)
    {
        ArgumentNullException.ThrowIfNull(jar);
        var from = NormalizeHost(oldHost);
        var to = NormalizeHost(newHost);
        if (from == to) throw new DomainMigrationException("same_host", "The old and new host are the same.");
        if (newScheme is not (null or "https")) throw new DomainMigrationException("invalid_scheme", "Only an upgrade to https can be requested.");
        if (IsApk(jar))
            throw new DomainMigrationException("apk_input", "This is an APK with Dalvik code. Install it on the source host and migrate the JAR the host keeps for it.");
        var entries = ReadJar(jar);

        var classes = 0; var replacements = 0; var unparsed = new JsonArray(); var changed = new JsonArray(); var reported = 0;
        var rewritten = new List<JarEntryData>(entries.Count);
        foreach (var entry in entries)
        {
            if (!entry.Name.EndsWith(".class", StringComparison.Ordinal) || entry.Directory) { rewritten.Add(entry); continue; }
            classes++;
            var result = ClassFileStrings.Rewrite(entry.Data, text => ReplaceHost(text, from, to, newScheme));
            if (!result.Parsed)
            {
                unparsed.Add(new JsonObject { ["entry"] = entry.Name, ["error"] = result.Error });
                rewritten.Add(entry);
                continue;
            }
            if (result.Replacements == 0) { rewritten.Add(entry); continue; }
            replacements += result.Replacements;
            rewritten.Add(entry with { Data = result.Bytes });
            foreach (var change in result.Changes)
            {
                if (reported++ >= MaximumReportedChanges) break;
                changed.Add(new JsonObject { ["class"] = entry.Name, ["index"] = change.Index, ["before"] = Short(change.Before), ["after"] = Short(change.After) });
            }
        }
        if (replacements == 0)
            throw new DomainMigrationException("nothing_replaced", "No string constant in " + classes.ToString(CultureInfo.InvariantCulture) + " classes refers to " + from + "; nothing was changed.");

        var output = WriteJar(rewritten);
        var structure = CheckStructure(jar, output);
        var report = new JsonObject
        {
            ["kind"] = "domain-migration",
            ["old_host"] = from,
            ["new_host"] = to,
            ["scheme_upgrade"] = newScheme == "https",
            ["classes"] = classes,
            ["replacements"] = replacements,
            ["classes_changed"] = rewritten.Where((entry, index) => !ReferenceEquals(entry.Data, entries[index].Data)).Count(),
            ["changes"] = changed,
            ["changes_truncated"] = reported > MaximumReportedChanges,
            ["unparsed_classes"] = unparsed,
            ["input_sha256"] = Sha(jar),
            ["output_sha256"] = Sha(output),
            ["structure"] = structure,
        };
        return new(output, replacements, report);
    }

    /// <summary>
    /// STRUCTURE CHECK: same entry count and names in the same order; every non-class entry (manifest, assets,
    /// resources) byte-identical; every class that parsed before still parses with the same version, pool size and
    /// this_class, and its bytes after the constant pool are identical; a class that did not parse is unchanged.
    /// Throws <see cref="DomainMigrationException"/> ("structure_check") on the first difference.
    /// </summary>
    public static JsonObject CheckStructure(byte[] original, byte[] repaired)
    {
        var before = ReadJar(original);
        List<JarEntryData> after;
        try { after = ReadJar(repaired); }
        catch (DomainMigrationException error) { throw Structure("The rewritten JAR cannot be read: " + error.Message); }
        if (before.Count != after.Count) throw Structure($"Entry count changed ({before.Count} to {after.Count})");
        int classes = 0, parsed = 0, identical = 0;
        for (var i = 0; i < before.Count; i++)
        {
            var (a, b) = (before[i], after[i]);
            if (a.Name != b.Name || a.Directory != b.Directory) throw Structure("Entry " + i.ToString(CultureInfo.InvariantCulture) + " changed name: " + a.Name);
            var isClass = a.Name.EndsWith(".class", StringComparison.Ordinal) && !a.Directory;
            if (!isClass)
            {
                if (!a.Data.AsSpan().SequenceEqual(b.Data)) throw Structure("Non-class entry changed: " + a.Name);
                continue;
            }
            classes++;
            ClassFileInfo first;
            try { first = ClassFileStrings.Parse(a.Data); }
            catch (InvalidDataException)
            {
                if (!a.Data.AsSpan().SequenceEqual(b.Data)) throw Structure("A class that did not parse was changed: " + a.Name);
                continue;
            }
            ClassFileInfo second;
            try { second = ClassFileStrings.Parse(b.Data); }
            catch (InvalidDataException error) { throw Structure("Class no longer parses: " + a.Name + " (" + error.Message + ")"); }
            parsed++;
            if (first.MajorVersion != second.MajorVersion || first.MinorVersion != second.MinorVersion || first.PoolCount != second.PoolCount
                || first.ThisClass != second.ThisClass || first.Utf8.Count != second.Utf8.Count)
                throw Structure("Class header or constant pool shape changed: " + a.Name);
            if (!a.Data.AsSpan(first.PoolEnd).SequenceEqual(b.Data.AsSpan(second.PoolEnd)))
                throw Structure("Class body after the constant pool changed: " + a.Name);
            if (a.Data.AsSpan().SequenceEqual(b.Data)) identical++;
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["entries"] = before.Count,
            ["classes"] = classes,
            ["classes_parsed"] = parsed,
            ["classes_identical"] = identical,
            ["other_entries_identical"] = before.Count - classes,
        };
    }

    private static DomainMigrationException Structure(string message) => new("structure_check", message);

    internal sealed record JarEntryData(string Name, bool Directory, byte[] Data, DateTimeOffset LastWrite, bool Stored, int ExternalAttributes);

    internal static List<JarEntryData> ReadJar(byte[] jar)
    {
        if (jar.Length == 0 || jar.Length > MaximumJar) throw new DomainMigrationException("invalid_jar", "The JAR is empty or larger than 64 MiB.");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(jar, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaximumEntries) throw new DomainMigrationException("invalid_jar", "The JAR has too many entries.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<JarEntryData>(zip.Entries.Count);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (!names.Add(entry.FullName)) throw new DomainMigrationException("invalid_jar", "The JAR repeats the entry " + Short(entry.FullName));
                if (entry.IsEncrypted || entry.Length < 0 || entry.Length > MaximumEntryBytes) throw new DomainMigrationException("invalid_jar", "Entry too large or encrypted: " + Short(entry.FullName));
                total += entry.Length;
                if (total > MaximumExpanded) throw new DomainMigrationException("invalid_jar", "The JAR expands beyond 256 MiB.");
                var directory = entry.FullName.EndsWith('/');
                var data = new byte[checked((int)entry.Length)];
                using (var input = entry.Open())
                {
                    input.ReadExactly(data);
                    if (input.ReadByte() != -1) throw new DomainMigrationException("invalid_jar", "Entry size changed while reading: " + Short(entry.FullName));
                }
                result.Add(new(entry.FullName, directory, data, entry.LastWriteTime, entry.CompressedLength == entry.Length, entry.ExternalAttributes));
            }
            return result;
        }
        catch (Exception error) when (error is (InvalidDataException or IOException) and not DomainMigrationException)
        { throw new DomainMigrationException("invalid_jar", "The JAR cannot be read: " + error.Message); }
    }

    internal static byte[] WriteJar(IEnumerable<JarEntryData> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                var created = zip.CreateEntry(entry.Name, entry.Stored || entry.Directory ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                if (entry.LastWrite.Year is >= 1980 and <= 2107) created.LastWriteTime = entry.LastWrite;
                created.ExternalAttributes = entry.ExternalAttributes;
                if (entry.Directory) continue;
                using var stream = created.Open();
                stream.Write(entry.Data);
            }
        return output.ToArray();
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static string Short(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
