using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Backup;

namespace AutoSourcarr.Core.Cutover;

/// <summary>What a 0.4.5 instance backup held: its version, creation time and the container paths it was taken from.</summary>
public sealed record LegacyBackupManifest(string Version, string CreatedAt, IReadOnlyDictionary<string, string> Paths, int Files);

/// <summary>
/// Phase 14: reads a 0.4.5 instance backup (System → Backups, 0.4.5 <c>instance_backup.py</c>, format 2) so the cutover tool
/// can migrate it. Container <c>ASCBK2\n</c> | salt (16) | nonce (12) | AES-256-GCM ciphertext and tag (16) over a zip; the key
/// is PBKDF2-HMAC-SHA256 (600,000 rounds) of the passphrase, the associated data is the magic and the salt. The zip holds
/// <c>instance.json</c> (format 2: version, created_at, paths, files with sha256) and the files under <c>config/</c>,
/// <c>data/</c>, <c>db/</c> (both databases) and optionally <c>engine/</c>. Extraction applies the 0.4.5 restore checks
/// (inventory equals manifest, safe relative paths, no links, sizes and hashes). The passphrase is never stored or logged.
/// </summary>
public static class LegacyBackupReader
{
    public static readonly byte[] Magic = "ASCBK2\n"u8.ToArray();
    public const int Iterations = 600_000;
    public const long MaxArchive = 128L * 1024 * 1024;
    public const long MaxTotal = 512L * 1024 * 1024;
    public const int MaxFiles = 20000;
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;
    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal) { "config", "data", "db", "engine" };
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
        { "cover-cache", "instance-backups", "diagnostics", "logs", "support-logs", "downloads", "node_modules", ".cache", "cache", "caches", "kcef", "chrome", "chromium", "runtime" };

    public static bool IsLegacyBackup(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..read].StartsWith(Magic);
    }

    /// <summary>Decrypts the container; a wrong passphrase or a damaged file is refused (same messages as 0.4.5).</summary>
    public static byte[] Decrypt(byte[] content, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(content);
        BackupCrypto.CheckPassphrase(passphrase);
        if (content.Length > MaxArchive + 64) throw new BackupRefusedException("Backup exceeds the 128 MiB encrypted input limit of 0.4.5.", 413);
        if (!content.AsSpan().StartsWith(Magic) || content.Length < Magic.Length + SaltSize + NonceSize + TagSize)
            throw new BackupRefusedException("This is not a 0.4.5 instance backup (ASCBK2).");
        var salt = content.AsSpan(Magic.Length, SaltSize);
        var nonce = content.AsSpan(Magic.Length + SaltSize, NonceSize);
        var bodyLength = content.Length - Magic.Length - SaltSize - NonceSize - TagSize;
        var body = content.AsSpan(Magic.Length + SaltSize + NonceSize, bodyLength);
        var tag = content.AsSpan(content.Length - TagSize, TagSize);
        var plain = new byte[bodyLength];
        var secret = Encoding.UTF8.GetBytes(passphrase);
        var key = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, body, tag, plain, content.AsSpan(0, Magic.Length + SaltSize));
            return plain;
        }
        catch (CryptographicException error) { throw new BackupRefusedException("Wrong passphrase or damaged encrypted backup.", 400, error); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>
    /// Decrypts <paramref name="backupFile"/> and extracts it below <paramref name="target"/> (which must be empty or absent):
    /// <c>target/config</c>, <c>target/data</c>, <c>target/db</c> and <c>target/engine</c>. The backup file is only read.
    /// </summary>
    public static LegacyBackupManifest Extract(string backupFile, string passphrase, string target)
    {
        var info = new FileInfo(backupFile);
        if (!info.Exists) throw new FileNotFoundException("The backup file does not exist.", backupFile);
        if (info.Length > MaxArchive + 64) throw new BackupRefusedException("Backup exceeds the 128 MiB encrypted input limit of 0.4.5.", 413);
        var zip = Decrypt(File.ReadAllBytes(backupFile), passphrase);
        return ExtractZip(zip, target);
    }

    public static LegacyBackupManifest ExtractZip(byte[] zip, string target)
    {
        ArgumentNullException.ThrowIfNull(zip);
        target = Path.GetFullPath(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("The extraction folder is not empty.");
        Directory.CreateDirectory(target);
        using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        var entries = archive.Entries.ToList();
        var names = entries.Select(entry => entry.FullName).ToList();
        if (entries.Count > MaxFiles + 1 || names.Count != names.Distinct(StringComparer.Ordinal).Count() || !names.Contains("instance.json"))
            throw new BackupRefusedException("Invalid backup inventory.");
        if (entries.Sum(entry => entry.Length) > MaxTotal + 8 * 1024 * 1024) throw new BackupRefusedException("Backup decoded byte limit exceeded.", 413);
        var manifestEntry = archive.GetEntry("instance.json")!;
        if (manifestEntry.Length > 8 * 1024 * 1024) throw new BackupRefusedException("Backup inventory limit exceeded.");
        JsonObject manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonNode.Parse(stream) as JsonObject ?? throw new BackupRefusedException("Backup manifest is not an object.");
        if (manifest["format"] is not JsonValue format || !format.TryGetValue<int>(out var number) || number != 2 || manifest["files"] is not JsonObject files)
            throw new BackupRefusedException("Backup manifest is not 0.4.5 format 2.");
        var expected = files.Select(pair => pair.Key).Append("instance.json").ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(names)) throw new BackupRefusedException("Backup manifest does not match archive.");
        foreach (var entry in entries)
        {
            if (entry.FullName == "instance.json") continue;
            var name = entry.FullName;
            var parts = name.Split('/');
            if (name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal) || parts.Length < 2
                || parts.Any(part => part is "" or "." or "..") || !Roots.Contains(parts[0]))
                throw new BackupRefusedException("Unsafe backup path.");
            if (parts.Skip(1).Any(Excluded.Contains) || parts[^1] is "instance-restore.json" or "database-relocation.json")
                throw new BackupRefusedException("Backup contains a reserved instance path.");
            if (parts[0] == "db" && name is not ("db/catalog.sqlite" or "db/chapter-origins.sqlite")) throw new BackupRefusedException("Unexpected database file.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000)) throw new BackupRefusedException("Backup contains a linked/special file.");
            var hash = files[name]?["sha256"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            var destination = Path.GetFullPath(Path.Combine([target, .. parts]));
            if (!destination.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new BackupRefusedException("Unsafe backup path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            using (var source = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += read;
                    if (copied > entry.Length) throw new BackupRefusedException("Backup member size changed.");
                    sha.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
            }
            if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), hash, StringComparison.Ordinal))
                throw new BackupRefusedException("Backup content hash mismatch.");
        }
        var paths = manifest["paths"] is JsonObject roots
            ? roots.Where(pair => pair.Value is JsonValue v && v.TryGetValue<string>(out _)).ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var required in new[] { "db/catalog.sqlite", "db/chapter-origins.sqlite" })
            if (!files.ContainsKey(required)) throw new BackupRefusedException("Backup is missing " + required + ".");
        return new LegacyBackupManifest(Text(manifest, "version"), Text(manifest, "created_at"), paths, files.Count);
    }

    private static string Text(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
