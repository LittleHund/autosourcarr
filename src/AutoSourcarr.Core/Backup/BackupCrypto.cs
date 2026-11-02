using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Backup;

/// <summary>A backup was refused (damaged, tampered, wrong passphrase, newer format); nothing was changed.</summary>
public sealed class BackupRefusedException(string message, int status = 400, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The HTTP status the routes answer with (400 refused input, 409 state conflict, 413 too large).</summary>
    public int Status { get; } = status;
}

/// <summary>
/// SET-8 optional password protection, as 0.4.x instance_backup.protect: AES-256-GCM with a key derived by
/// PBKDF2-HMAC-SHA256 (600,000 rounds, 16-byte random salt, 12-byte random nonce). The magic and salt are bound as
/// associated data, so a changed header fails the tag check. The passphrase is never stored or logged.
/// File layout: <c>ASCBK3\n</c> | salt (16) | nonce (12) | ciphertext | tag (16).
/// </summary>
public static class BackupCrypto
{
    public const int Iterations = 600_000;
    private const int SaltSize = 16, NonceSize = 12, TagSize = 16;
    private static readonly byte[] MagicBytes = "ASCBK3\n"u8.ToArray();
    private static readonly byte[] LegacyMagic = "ASCBK2\n"u8.ToArray();

    public static ReadOnlySpan<byte> Magic => MagicBytes;

    public static bool IsProtected(ReadOnlySpan<byte> data) => data.StartsWith(MagicBytes);

    /// <summary>The encrypted 0.4.x (Python) instance backup, format 2.</summary>
    public static bool IsLegacy(ReadOnlySpan<byte> data) => data.StartsWith(LegacyMagic);

    public static void CheckPassphrase(string? passphrase)
    {
        if (passphrase is null || passphrase.Length is < 12 or > 1024)
            throw new BackupRefusedException("Use a passphrase of 12 to 1024 characters.");
    }

    public static byte[] Protect(ReadOnlySpan<byte> plain, string passphrase)
    {
        CheckPassphrase(passphrase);
        var output = new byte[MagicBytes.Length + SaltSize + NonceSize + plain.Length + TagSize];
        MagicBytes.CopyTo(output, 0);
        var salt = output.AsSpan(MagicBytes.Length, SaltSize);
        var nonce = output.AsSpan(MagicBytes.Length + SaltSize, NonceSize);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        var key = Key(passphrase, salt);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            var body = output.AsSpan(MagicBytes.Length + SaltSize + NonceSize, plain.Length);
            var tag = output.AsSpan(output.Length - TagSize, TagSize);
            aes.Encrypt(nonce, plain, body, tag, output.AsSpan(0, MagicBytes.Length + SaltSize));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return output;
    }

    public static byte[] Unprotect(ReadOnlySpan<byte> data, string? passphrase)
    {
        if (!IsProtected(data) || data.Length < MagicBytes.Length + SaltSize + NonceSize + TagSize)
            throw new BackupRefusedException("This is not a password-protected AutoSourcarr backup.");
        if (string.IsNullOrEmpty(passphrase))
            throw new BackupRefusedException("This backup is password protected. Enter its passphrase.");
        CheckPassphrase(passphrase);
        var salt = data.Slice(MagicBytes.Length, SaltSize);
        var nonce = data.Slice(MagicBytes.Length + SaltSize, NonceSize);
        var body = data[(MagicBytes.Length + SaltSize + NonceSize)..^TagSize];
        var tag = data[^TagSize..];
        var plain = new byte[body.Length];
        var key = Key(passphrase, salt);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, body, tag, plain, data[..(MagicBytes.Length + SaltSize)]);
        }
        catch (CryptographicException error) { throw new BackupRefusedException("Wrong passphrase or damaged encrypted backup.", 400, error); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return plain;
    }

    private static byte[] Key(string passphrase, ReadOnlySpan<byte> salt)
    {
        var secret = Encoding.UTF8.GetBytes(passphrase);
        try { return Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
