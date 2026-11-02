using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Backup;

/// <summary>A backup was refused (damaged, tampered, wrong passphrase, newer format); nothing was changed.</summary>
public sealed class BackupRefusedException(string message, int status = 400, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The HTTP status the routes answer with (400 refused input, 409 state conflict, 413 too large, 507 no space).</summary>
    public int Status { get; } = status;
}

/// <summary>What the first bytes of a backup file say it is.</summary>
public enum BackupContainer { Plain, Protected3, Protected4, Legacy2, Unknown }

/// <summary>
/// SET-8 optional password protection. The key is derived with PBKDF2-HMAC-SHA256 (600,000 rounds, 16-byte random
/// salt) and never stored; the passphrase is never logged.
/// <para>Container ASCBK4 (written since the phase 12 audit) is chunked AES-256-GCM so a backup of any size streams
/// through a fixed buffer: <c>ASCBK4\n</c> | salt (16) | nonce prefix (8) | chunks. Each chunk is a 4-byte big-endian
/// plaintext length (at most 1 MiB), the ciphertext and its 16-byte tag; the nonce is the prefix plus the chunk counter,
/// and the header, counter and a final-chunk flag are associated data, so reordering, truncation and appended data fail.</para>
/// <para>Container ASCBK3 (one AES-GCM block over the whole zip, the first phase 12 build) is still read, up to 1 GiB.</para>
/// </summary>
public static class BackupCrypto
{
    public const int Iterations = 600_000;
    public const int ChunkSize = 1 << 20;
    public const long MaxLegacyBytes = 1L << 30;
    private const int SaltSize = 16, PrefixSize = 8, NonceSize = 12, TagSize = 16;
    private static readonly byte[] Magic3 = "ASCBK3\n"u8.ToArray();
    private static readonly byte[] Magic4 = "ASCBK4\n"u8.ToArray();
    private static readonly byte[] Magic2 = "ASCBK2\n"u8.ToArray();

    public static BackupContainer Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Magic4)) return BackupContainer.Protected4;
        if (head.StartsWith(Magic3)) return BackupContainer.Protected3;
        if (head.StartsWith(Magic2)) return BackupContainer.Legacy2;
        return head.Length >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K' ? BackupContainer.Plain : BackupContainer.Unknown;
    }

    public static BackupContainer Detect(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return Detect(head[..read]);
    }

    public static bool IsProtected(ReadOnlySpan<byte> data) => Detect(data) is BackupContainer.Protected3 or BackupContainer.Protected4;

    public static void CheckPassphrase(string? passphrase)
    {
        if (passphrase is null || passphrase.Length is < 12 or > 1024)
            throw new BackupRefusedException("Use a passphrase of 12 to 1024 characters.");
    }

    /// <summary>Encrypts <paramref name="input"/> into <paramref name="output"/> as ASCBK4, chunk by chunk.</summary>
    public static void Encrypt(Stream input, Stream output, string passphrase)
    {
        CheckPassphrase(passphrase);
        var header = new byte[Magic4.Length + SaltSize + PrefixSize];
        Magic4.CopyTo(header, 0);
        RandomNumberGenerator.Fill(header.AsSpan(Magic4.Length, SaltSize + PrefixSize));
        output.Write(header);
        var key = Key(passphrase, header.AsSpan(Magic4.Length, SaltSize));
        try
        {
            using var aes = new AesGcm(key, TagSize);
            var plain = new byte[ChunkSize]; var cipher = new byte[ChunkSize]; var tag = new byte[TagSize];
            var next = new byte[ChunkSize];
            var length = input.ReadAtLeast(plain, ChunkSize, throwOnEndOfStream: false);
            uint counter = 0;
            var size = new byte[4];
            while (true)
            {
                // Look ahead one chunk so the last one carries the final flag (an empty final chunk for an exact multiple).
                var following = length == ChunkSize ? input.ReadAtLeast(next, ChunkSize, throwOnEndOfStream: false) : 0;
                var final = following == 0;
                Seal(aes, header, counter, final, plain.AsSpan(0, length), cipher, tag);
                BinaryPrimitives.WriteUInt32BigEndian(size, (uint)length);
                output.Write(size); output.Write(cipher, 0, length); output.Write(tag);
                if (final) break;
                (plain, next) = (next, plain); length = following;
                counter = checked(counter + 1);
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Decrypts an ASCBK4 or ASCBK3 file into a plain zip file.</summary>
    public static void Decrypt(string inputPath, string outputPath, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new BackupRefusedException("This backup is password protected. Enter its passphrase.");
        CheckPassphrase(passphrase);
        var kind = Detect(inputPath);
        using var input = File.OpenRead(inputPath);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            if (kind == BackupContainer.Protected3) DecryptLegacy(input, output, passphrase);
            else if (kind == BackupContainer.Protected4) DecryptChunked(input, output, passphrase);
            else throw new BackupRefusedException("This is not a password-protected AutoSourcarr backup.");
            output.Flush(flushToDisk: true);
        }
        catch
        {
            output.Dispose();
            File.Delete(outputPath);
            throw;
        }
    }

    private static void DecryptChunked(Stream input, Stream output, string passphrase)
    {
        var header = new byte[Magic4.Length + SaltSize + PrefixSize];
        if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length) throw Damaged();
        var key = Key(passphrase, header.AsSpan(Magic4.Length, SaltSize));
        try
        {
            using var aes = new AesGcm(key, TagSize);
            var cipher = new byte[ChunkSize]; var plain = new byte[ChunkSize]; var tag = new byte[TagSize];
            Span<byte> size = stackalloc byte[4];
            uint counter = 0;
            while (true)
            {
                if (input.ReadAtLeast(size, 4, throwOnEndOfStream: false) != 4) throw Damaged("The encrypted backup is truncated.");
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(size);
                if (length is < 0 or > ChunkSize) throw Damaged();
                if (input.ReadAtLeast(cipher.AsSpan(0, length), length, throwOnEndOfStream: false) != length
                    || input.ReadAtLeast(tag, TagSize, throwOnEndOfStream: false) != TagSize) throw Damaged("The encrypted backup is truncated.");
                // The final flag is not stored: a full chunk is tried as non-final first, a short one as final.
                var final = length < ChunkSize;
                if (!Open(aes, header, counter, final, cipher.AsSpan(0, length), tag, plain.AsSpan(0, length)))
                {
                    if (final || !Open(aes, header, counter, true, cipher.AsSpan(0, length), tag, plain.AsSpan(0, length)))
                        throw new BackupRefusedException("Wrong passphrase or damaged encrypted backup.");
                    final = true;
                }
                output.Write(plain, 0, length);
                if (final)
                {
                    if (input.ReadByte() != -1) throw Damaged("The encrypted backup has data after its end.");
                    return;
                }
                counter = checked(counter + 1);
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void DecryptLegacy(Stream input, Stream output, string passphrase)
    {
        if (input.Length > MaxLegacyBytes + 64) throw new BackupRefusedException("This ASCBK3 backup is larger than 1 GiB.", 413);
        var data = new byte[input.Length];
        input.ReadExactly(data);
        if (data.Length < Magic3.Length + SaltSize + NonceSize + TagSize) throw Damaged();
        var salt = data.AsSpan(Magic3.Length, SaltSize);
        var nonce = data.AsSpan(Magic3.Length + SaltSize, NonceSize);
        var body = data.AsSpan(Magic3.Length + SaltSize + NonceSize, data.Length - Magic3.Length - SaltSize - NonceSize - TagSize);
        var plain = new byte[body.Length];
        var key = Key(passphrase, salt);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, body, data.AsSpan(data.Length - TagSize), plain, data.AsSpan(0, Magic3.Length + SaltSize));
        }
        catch (CryptographicException error) { throw new BackupRefusedException("Wrong passphrase or damaged encrypted backup.", 400, error); }
        finally { CryptographicOperations.ZeroMemory(key); }
        output.Write(plain);
    }

    private static void Seal(AesGcm aes, byte[] header, uint counter, bool final, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag)
    {
        Span<byte> nonce = stackalloc byte[NonceSize]; Span<byte> data = stackalloc byte[header.Length + 5];
        Context(header, counter, final, nonce, data);
        aes.Encrypt(nonce, plain, cipher[..plain.Length], tag, data);
    }

    private static bool Open(AesGcm aes, byte[] header, uint counter, bool final, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain)
    {
        Span<byte> nonce = stackalloc byte[NonceSize]; Span<byte> data = stackalloc byte[header.Length + 5];
        Context(header, counter, final, nonce, data);
        try { aes.Decrypt(nonce, cipher, tag, plain, data); return true; }
        catch (CryptographicException) { return false; }
    }

    private static void Context(byte[] header, uint counter, bool final, Span<byte> nonce, Span<byte> data)
    {
        header.AsSpan(Magic4.Length + SaltSize, PrefixSize).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[PrefixSize..], counter);
        header.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(header.Length, 4), counter);
        data[^1] = final ? (byte)1 : (byte)0;
    }

    private static BackupRefusedException Damaged(string message = "The encrypted backup is damaged.") => new(message);

    private static byte[] Key(string passphrase, ReadOnlySpan<byte> salt)
    {
        var secret = Encoding.UTF8.GetBytes(passphrase);
        try { return Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
