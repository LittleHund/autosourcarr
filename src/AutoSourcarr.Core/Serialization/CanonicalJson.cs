using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Unicode;

namespace AutoSourcarr.Core.Serialization;

/// <summary>The released Python json_bytes contract, retained for signed repair journals.</summary>
public static class CanonicalJson
{
    public static byte[] Bytes(JsonNode? value) => Encoding.UTF8.GetBytes(Text(value));
    public static string Text(JsonNode? value)
    {
        var output = new StringBuilder(); Write(output, value); return output.ToString();
    }
    public static string String(string value)
    {
        var output = new StringBuilder("\"");
        foreach (var character in value)
            output.Append(character switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' or > '~' => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture), _ => character.ToString() });
        return output.Append('"').ToString();
    }
    private static void Write(StringBuilder output, JsonNode? node)
    {
        switch (node)
        {
            case null: output.Append("null"); return;
            case JsonObject obj:
                output.Append('{'); var first = true;
                foreach (var (key, value) in obj.OrderBy(pair => pair.Key, UnicodeCodePointComparer.Instance))
                {
                    if (!first) output.Append(','); first = false; output.Append(String(key)).Append(':'); Write(output, value);
                }
                output.Append('}'); return;
            case JsonArray array:
                output.Append('[');
                for (var i = 0; i < array.Count; i++) { if (i > 0) output.Append(','); Write(output, array[i]); }
                output.Append(']'); return;
            case JsonValue scalar:
                switch (scalar.GetValueKind())
                {
                    case JsonValueKind.String: output.Append(String(scalar.GetValue<string>())); return;
                    case JsonValueKind.True: output.Append("true"); return;
                    case JsonValueKind.False: output.Append("false"); return;
                    case JsonValueKind.Null: output.Append("null"); return;
                    case JsonValueKind.Number:
                        if (scalar.TryGetValue<JsonElement>(out var element))
                        {
                            var raw = element.GetRawText();
                            if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) { output.Append(raw); return; }
                            output.Append(Float(element.GetDouble())); return;
                        }
                        if (scalar.TryGetValue<double>(out var number)) { output.Append(Float(number)); return; }
                        if (scalar.TryGetValue<float>(out var single)) { output.Append(Float(single)); return; }
                        output.Append(scalar.ToJsonString()); return;
                }
                break;
        }
        throw new JsonException("Unsupported value in canonical repair journal");
    }
    private static string Float(double number)
    {
        if (!double.IsFinite(number)) throw new JsonException("Non-finite repair journal number");
        var negative = double.IsNegative(number);
        if (number == 0) return negative ? "-0.0" : "0.0";
        var raw = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
        var e = raw.IndexOf('E'); var exponent = 0;
        if (e >= 0) { exponent = int.Parse(raw[(e + 1)..], CultureInfo.InvariantCulture); raw = raw[..e]; }
        var point = raw.IndexOf('.'); exponent += (point >= 0 ? point : raw.Length) - 1;
        var digits = raw.Replace(".", "", StringComparison.Ordinal);
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; exponent--; }
        digits = digits.TrimEnd('0');
        string result;
        if (exponent < -4 || exponent >= 16)
            result = digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        else if (exponent >= digits.Length - 1) result = digits + new string('0', exponent + 1 - digits.Length) + ".0";
        else if (exponent >= 0) result = digits[..(exponent + 1)] + "." + digits[(exponent + 1)..];
        else result = "0." + new string('0', -exponent - 1) + digits;
        return negative ? "-" + result : result;
    }
}
