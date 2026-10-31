using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Settings;

/// <summary>
/// Encrypts secret settings stored in config.json with AES-256-GCM (CON-5).
/// The 32-byte key lives in config.key next to config.json, readable by the app user only.
/// The setting name is bound as associated data, so a ciphertext cannot be moved to another key.
/// </summary>
public sealed partial class SettingsSecrets
{
    public const string KeyFileName = "config.key";
    private const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    private SettingsSecrets(byte[] key) => _key = key;

    public static SettingsSecrets Open(string root, bool create)
    {
        var path = Path.Combine(root, KeyFileName);
        var raw = ConfigFiles.ReadBounded(path);
        if (raw is not null)
        {
            byte[] key;
            try { key = Convert.FromBase64String(Encoding.ASCII.GetString(raw).Trim()); }
            catch (FormatException) { throw new ConfigurationException("config.key is damaged; secrets in config.json cannot be read."); }
            if (key.Length != 32)
                throw new ConfigurationException("config.key is damaged; secrets in config.json cannot be read.");
            return new(key);
        }
        if (!create)
            throw new ConfigurationException("config.key is missing; secrets in config.json cannot be read. Restore config.key from the same backup as config.json, or enter the secrets again.");
        var fresh = RandomNumberGenerator.GetBytes(32);
        ConfigFiles.WriteAtomic(path, Encoding.ASCII.GetBytes(Convert.ToBase64String(fresh) + "\n"));
        return new(fresh);
    }

    /// <summary>Known secret settings plus any key whose name marks it as a credential.</summary>
    public static bool IsSecretKey(string key) =>
        AutoSourcarrSettings.Definitions.Any(definition => definition.Secret && string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase))
        || SecretName().IsMatch(key);

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string key, string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var packed = new byte[NonceSize + TagSize + plain.Length];
        var nonce = packed.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_key, TagSize))
            aes.Encrypt(nonce, plain, packed.AsSpan(NonceSize + TagSize), packed.AsSpan(NonceSize, TagSize), Encoding.UTF8.GetBytes(key));
        return Prefix + Convert.ToBase64String(packed);
    }

    public string Unprotect(string key, string value)
    {
        if (!IsProtected(value))
            throw new ConfigurationException($"Secret {key} in config.json is not encrypted.");
        byte[] packed;
        try { packed = Convert.FromBase64String(value[Prefix.Length..]); }
        catch (FormatException) { throw Damaged(key); }
        if (packed.Length < NonceSize + TagSize)
            throw Damaged(key);
        var plain = new byte[packed.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(packed.AsSpan(0, NonceSize), packed.AsSpan(NonceSize + TagSize), packed.AsSpan(NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(key));
        }
        catch (CryptographicException) { throw Damaged(key); }
        return Encoding.UTF8.GetString(plain);
    }

    private static ConfigurationException Damaged(string key) =>
        new($"Secret {key} in config.json cannot be decrypted with this instance's config.key.");

    [GeneratedRegex(@"(?:PASSWORD|PASSWD|TOKEN|SECRET|API_?KEY|PRIVATE_?KEY|COOKIE|CREDENTIAL)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretName();
}
