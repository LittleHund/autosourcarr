using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoSourcarr.Core.Settings;

/// <summary>
/// Retains requested IDs without approving them for a native identity syscall.
/// The source owner must independently validate its effective Linux UID/GID.
/// </summary>
internal static class SourceIdentityInteger
{
    public static bool TryParse(string value, out BigInteger result)
    {
        result = default;
        var input = value.AsSpan().Trim();
        if (input.IsEmpty || input.Length > 4300)
            return false;

        var start = input[0] is '+' or '-' ? 1 : 0;
        if (start == input.Length)
            return false;
        var point = input.IndexOf('.');
        var whole = point < 0 ? input : input[..point];
        if (whole.Length <= start)
            return false;
        if (point >= 0)
        {
            var fraction = input[(point + 1)..];
            if (fraction.IsEmpty)
                return false;
            foreach (var digit in fraction)
                if (digit != '0')
                    return false;
        }

        for (var index = start; index < whole.Length; index++)
        {
            if (whole[index] is >= '0' and <= '9')
                continue;
            if (whole[index] != '_' || index == start || index + 1 == whole.Length
                || whole[index - 1] is < '0' or > '9' || whole[index + 1] is < '0' or > '9')
                return false;
        }

        // No floating-point path: even a huge all-zero fraction cannot round a
        // nonzero tail away, and no exponent syntax is introduced by .NET parsing.
        return BigInteger.TryParse(whole.ToString().Replace("_", "", StringComparison.Ordinal),
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }
}

/// <summary>Keeps the released numeric JSON shape without an Int64 conversion.</summary>
public sealed class SourceIdentityIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
            throw new JsonException("The source identity must be a JSON integer.");
        using var document = JsonDocument.ParseValue(ref reader);
        var input = document.RootElement.GetRawText();
        if (input.Contains('.', StringComparison.Ordinal)
            || !BigInteger.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result))
            throw new JsonException("The source identity must be a JSON integer.");
        return result;
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture));
}
