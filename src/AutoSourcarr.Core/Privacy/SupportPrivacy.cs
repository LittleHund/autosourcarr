using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Privacy;

/// <summary>Bounded redaction and per-process pseudonyms; raw settings are never a log payload.</summary>
public sealed class SupportPrivacy
{
    private const string Redacted = "[redacted]";
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly object _gate = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private int _secretCharacters;
    private bool _capacityExceeded;
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> Expressions = new();
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex PrivateKey = R(@"^(?:authorization|proxy[-_]authorization|(?:set[-_])?cookie|(?:.*[-_])?(?:password|passwd|passphrase|secret|token|api[-_]?key|access[-_]?key|credential|session(?:[-_]?id)?|csrf(?:[-_]?token)?|device[-_]?code|user[-_]?code|user[-_]?name|email|account[-_]?id|workspace[-_]?id|private[-_]?key))$", IgnoreCase);
    private static readonly Regex PersonalKey = R(@"^(?:user|host(?:name)?|ip|client_ip|remote_addr|title|preferred_title|backup_title|source_title|query|search_query)$", IgnoreCase);
    private static readonly Regex Url = R("\\b(?:https?|wss?)://[^\\s<>\"']+", IgnoreCase);
    private static readonly Regex Assignment = R("([A-Za-z_][A-Za-z0-9_-]*)([\"']?\\s*[:=]\\s*)(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;&{}\\[\\]]+)", RegexOptions.NonBacktracking);
    private static readonly Regex Email = R(@"\b[A-Z0-9._%+-]{1,64}@[A-Z0-9.-]{1,253}\.[A-Z]{2,63}\b", IgnoreCase);
    private static readonly Regex Ipv4 = R(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])");
    private static readonly Regex Ipv6 = R(@"(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.]*(?![\w:])");
    private static readonly Regex Camel = R(@"([a-z0-9])([A-Z])");

    public void Register(IReadOnlyDictionary<string, string?> values)
    {
        lock (_gate)
        {
            foreach (var (key, value) in values)
            {
                if (string.IsNullOrEmpty(value)) continue;
                var candidates = new List<string>();
                if (PrivateKey.IsMatch(Camel.Replace(key, "$1_$2"))) candidates.Add(value);
                if (value.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
                    candidates.AddRange(uri.UserInfo.Split(':', 2));
                foreach (var candidate in candidates.Where(candidate => candidate.Length > 0))
                {
                    if (candidate.Length > 16384) { _capacityExceeded = true; continue; }
                    var escaped = Uri.EscapeDataString(candidate);
                    foreach (var variant in new[] { candidate, escaped, escaped.Replace("%20", "+", StringComparison.Ordinal), JsonSerializer.Serialize(candidate)[1..^1], PythonJsonEscape(candidate) })
                    {
                        if (_secrets.Contains(variant)) continue;
                        if (_secretCharacters + variant.Length > 262144) { _capacityExceeded = true; break; }
                        _secrets.Add(variant);
                        _secretCharacters += variant.Length;
                    }
                }
            }
        }
    }

    public string Text(string? value)
    {
        var text = value ?? "";
        if (text.Length > 65536) return "[oversize diagnostic omitted]";
        string[] secrets;
        lock (_gate)
        {
            if (_capacityExceeded) return "[diagnostic omitted: secret registry capacity]";
            secrets = _secrets.OrderByDescending(secret => secret.Length).ToArray();
        }
        foreach (var secret in secrets) text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        text = R(@"\x1b\[[0-?]*[ -/]*[@-~]").Replace(text, "");
        text = R(@"-----BEGIN [^-]*PRIVATE KEY-----.*?(?:-----END [^-]*PRIVATE KEY-----|$)", RegexOptions.Singleline).Replace(text, "[private key omitted]");
        text = R(@"(https?://)[^\s/\?#@]+@", IgnoreCase).Replace(text, "$1[redacted]@");
        text = R("([?&](?:token|api[_-]?key|password|auth|secret|access_token|reader_api_token)=)[^\\s&#\"']+", IgnoreCase).Replace(text, "$1[redacted]");
        text = R("((?:authorization|proxy-authorization|cookie|set-cookie)[\"']?\\s*[:=]\\s*)[^\\r\\n]+", IgnoreCase).Replace(text, "$1[redacted]");
        text = R("((?:[\\w-]*(?:token|password|secret|api[_-]?key)|authorization|proxy-authorization|cookie|set-cookie)[\"']?\\s*[:=]\\s*)(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;&}]+)", IgnoreCase).Replace(text, "$1[redacted]");
        text = R(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", IgnoreCase).Replace(text, "$1 [redacted]");
        text = R(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+").Replace(text, Redacted);
        text = R(@"\bsk-(?:proj-|ant-)?[A-Za-z0-9_-]{12,}").Replace(text, Redacted);
        text = R("((?:--?(?:storepass|keypass|ks-pass|key-pass|password|passwd|token|api-key|api_key|client-secret|credential))(?:=|\\s+))(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s]+)", IgnoreCase).Replace(text, "$1[redacted]");
        text = R("\\bpass:[^\\s,\"']+", IgnoreCase).Replace(text, "pass:[redacted]");
        text = R("((?:user[ _-]?code|device[ _-]?code|enter (?:the )?code|passphrase|credential|access[ _-]?key|user(?:name)?|email|account[ _-]?id|workspace[ _-]?id)[\"']?\\s*[:=]\\s*)(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;}]+)", IgnoreCase).Replace(text, "$1[redacted]");
        text = Url.Replace(text, UrlAlias);
        text = R("([?&][^\\s&=]+)=[^\\s&#\"']*").Replace(text, "$1=[redacted]");
        text = Assignment.Replace(text, match =>
        {
            var key = Camel.Replace(match.Groups[1].Value, "$1_$2");
            return PrivateKey.IsMatch(key) || PersonalKey.IsMatch(key) ? match.Groups[1].Value + match.Groups[2].Value + Redacted : match.Value;
        });
        text = Email.Replace(text, match => Alias("email", match.Value));
        string Address(Match match) => IPAddress.TryParse(match.Value, out var address) ? Alias("ip", address.ToString()) : match.Value;
        text = Ipv4.Replace(text, Address);
        text = Ipv6.Replace(text, Address);
        text = R("[A-Za-z]:\\\\[^\\r\\n\"<>|]*").Replace(text, match => "[path]/" + match.Value.TrimEnd(' ', '.').Split('\\')[^1]);
        text = R("(?<![\\w:/])/(?:home|Users|mnt|config|data|tmp)/[^\\s\"']+").Replace(text, match => "[path]/" + match.Value.TrimEnd('/').Split('/')[^1]);
        return text;
    }

    public JsonNode? Scrub(JsonNode? value, int depth = 0)
    {
        if (depth > 12) return JsonValue.Create("[depth limited]");
        if (value is JsonObject obj)
        {
            var output = new JsonObject();
            foreach (var (key, item) in obj.Take(200))
                output[key] = PrivateKey.IsMatch(Camel.Replace(key, "$1_$2")) || PersonalKey.IsMatch(key)
                    ? JsonValue.Create(Redacted) : Scrub(item, depth + 1);
            return output;
        }
        if (value is JsonArray array) return new JsonArray(array.Take(200).Select(item => Scrub(item, depth + 1)).ToArray());
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return JsonValue.Create(Text(text));
        return value?.DeepClone();
    }

    private string UrlAlias(Match match)
    {
        if (R(@"\w+://\[host-[0-9a-f]{10}\]").IsMatch(match.Value)) return match.Value;
        if (!Uri.TryCreate(match.Value.Replace(Redacted, "redacted", StringComparison.Ordinal), UriKind.Absolute, out var uri))
            return "[invalid URL omitted]";
        var path = uri.AbsolutePath;
        if (!(path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/static/", StringComparison.Ordinal)
            || path is "/api" or "/graphql" or "/status" or "/health")) path = path.Length > 0 ? "/[path]" : "";
        var query = uri.Query.Length > 0 ? "?" + R(@"[^&=]+(?=&|$)").Replace(uri.Query[1..], Redacted) : "";
        return uri.Scheme + "://" + Alias("host", uri.Host) + path + query;
    }

    private string Alias(string kind, string value) => "[" + kind + "-" + Convert.ToHexStringLower(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(value)))[..10] + "]";
    private static Regex R(string pattern, RegexOptions options = RegexOptions.CultureInvariant) =>
        Expressions.GetOrAdd((pattern, options), key =>
            new(key.Pattern, key.Options | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    private static string PythonJsonEscape(string value)
    {
        var text = new StringBuilder();
        foreach (var c in value)
            if (c == '\\') text.Append("\\\\");
            else if (c == '"') text.Append("\\\"");
            else if (c is < ' ' or > '~') text.Append("\\u").Append(((int)c).ToString("x4"));
            else text.Append(c);
        return text.ToString();
    }
}
