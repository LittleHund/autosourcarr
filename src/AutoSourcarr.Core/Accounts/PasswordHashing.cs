using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Accounts;

/// <summary>
/// Salted password hashes (AUTH-2): PBKDF2-HMAC-SHA512, 210,000 iterations (OWASP 2023), 16-byte salt, 64-byte hash,
/// stored as <c>pbkdf2-sha512$iterations$salt$hash</c>. Passwords are never stored or logged. Uses the .NET runtime
/// only (ASP.NET Core Identity's PasswordHasher is a separate package; same algorithm family).
/// </summary>
public static class PasswordHashing
{
    public const int Iterations = 210_000;
    public const int MinimumLength = 8;
    private const string Scheme = "pbkdf2-sha512";

    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA512, 64);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string? stored)
    {
        if (password is null || string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations is < 10_000 or > 10_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Rules for a new password; null when it is acceptable.</summary>
    public static string? Problem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength) return "Use at least " + MinimumLength + " characters.";
        if (password.Length > 1024) return "Use at most 1024 characters.";
        return null;
    }

    /// <summary>A random secret for keys and tokens (URL-safe, 256 bits) and its stored SHA-256 fingerprint.</summary>
    public static string NewSecret(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Fingerprint(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
