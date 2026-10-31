using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AutoSourcarr.Core.Settings;

/// <summary>
/// A requested source identity, not a native credential or launch authorization.
/// Only this fixed versioned frame crosses the unprivileged startup resolver's
/// pipe; it cannot select a path, executable, environment map or shell command.
/// </summary>
public sealed record SourceStartupRequest(string Mode, BigInteger Uid, BigInteger Gid)
{
    public const int MaximumFrameBytes = 16 * 1024;
    private const string Magic = "AUTOSOURCARR-STARTUP-1";

    public static SourceStartupRequest FromSettings(AutoSourcarrSettings settings) =>
        new(settings.SourceEngineMode, settings.SourceEngineUid, settings.SourceEngineGid);

    public byte[] Encode(string nonce)
    {
        ValidateNonce(nonce);
        if (Mode is not ("integrated" or "external"))
            throw new ConfigurationException("Invalid source startup mode.");
        var frame = Encoding.ASCII.GetBytes(string.Join('\n', Magic, nonce, Mode,
            Uid.ToString(CultureInfo.InvariantCulture), Gid.ToString(CultureInfo.InvariantCulture), ""));
        if (frame.Length > MaximumFrameBytes)
            throw new ConfigurationException("Source startup request exceeds its protocol limit.");
        return frame;
    }

    public static SourceStartupRequest Decode(ReadOnlySpan<byte> frame, string expectedNonce)
    {
        ValidateNonce(expectedNonce);
        if (frame.IsEmpty || frame.Length > MaximumFrameBytes || frame[^1] != (byte)'\n')
            throw Invalid();
        foreach (var value in frame)
            if (value > 127 || value == 0 || value == '\r')
                throw Invalid();
        var fields = Encoding.ASCII.GetString(frame).Split('\n');
        if (fields.Length != 6 || fields[0] != Magic || fields[2] is not ("integrated" or "external") || fields[5] != "")
            throw Invalid();
        if (fields[1].Length != 64 || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(fields[1]), Encoding.ASCII.GetBytes(expectedNonce)))
            throw Invalid();
        return new(fields[2], Integer(fields[3]), Integer(fields[4]));
    }

    public void RequireSameRequest(SourceStartupRequest observed)
    {
        if (this != observed)
            throw new ConfigurationException("Source startup settings changed during native bootstrap; a full restart is required.");
    }

    private static BigInteger Integer(string input)
    {
        if (input == "0")
            return BigInteger.Zero;
        var start = input.StartsWith('-') ? 1 : 0;
        if (start == input.Length || input[start] is < '1' or > '9')
            throw Invalid();
        for (var index = start + 1; index < input.Length; index++)
            if (input[index] is < '0' or > '9')
                throw Invalid();
        if (!BigInteger.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            throw Invalid();
        return value;
    }

    private static void ValidateNonce(string nonce)
    {
        if (nonce.Length != 64 || nonce.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("A native bootstrap nonce must contain exactly 32 bytes of lower-case hexadecimal.", nameof(nonce));
    }

    private static InvalidDataException Invalid() => new("Invalid native source startup frame.");
}
