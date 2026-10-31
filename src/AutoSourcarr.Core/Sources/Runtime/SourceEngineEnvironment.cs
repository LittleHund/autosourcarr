using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Core.Sources.Runtime;

public sealed record SourceEngineLaunchEnvironment(IReadOnlyDictionary<string, string> Launch, IReadOnlyDictionary<string, string> IntendedChallenges);

/// <summary>Read-only, per-instance environment construction. Does not start a process, change identity or establish readiness.</summary>
public sealed class SourceEngineEnvironment
{
    public const string EngineVersion = "2.3.2243";
    public const string UserAgentJar = "/app/source-browser-agent.jar";
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private static readonly string[] SystemKeys = ["HOME", "PATH", "JAVA_HOME", "LANG", "LC_ALL", "TZ", "SSL_CERT_FILE", "SSL_CERT_DIR"];
    private static readonly HashSet<string> BlockedKeys = new(["HOME", "PATH", "JAVA_HOME", "LD_PRELOAD", "LD_LIBRARY_PATH", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH", "PYTHONPATH", "PYTHONHOME", "BASH_ENV", "ENV", "SHELLOPTS"], StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _configurationRoot;
    private readonly string _dataRoot;
    private readonly CanonicalConfiguration _configuration;
    private readonly SupportPrivacy _privacy;
    internal void RequirePrivacy(SupportPrivacy privacy)
    {
        if (!ReferenceEquals(_privacy, privacy)) throw new ArgumentException("Source environment and diagnostics must share one privacy scope.");
    }

    public SourceEngineEnvironment(string configurationRoot, string engineDataRoot, SupportPrivacy privacy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot); ArgumentException.ThrowIfNullOrWhiteSpace(engineDataRoot);
        _configurationRoot = Path.GetFullPath(configurationRoot); _dataRoot = Path.GetFullPath(engineDataRoot);
        _configuration = new(_configurationRoot); _privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
    }

    /// <summary>Parent values are explicit. The image supplies its JVM25 JAVA_HOME; source settings cannot replace it with a build JDK.</summary>
    public IReadOnlyDictionary<string, string> Build(IReadOnlyDictionary<string, string?> parentEnvironment)
    {
        ArgumentNullException.ThrowIfNull(parentEnvironment);
        var parent = parentEnvironment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in SystemKeys) if (parent.TryGetValue(key, out var value) && value is not null) result[key] = value;
        if (ConfigurationPresent())
            foreach (var (key, value) in _configuration.EnvironmentFile("source-host.env"))
                if (value is not null && !BlockedKeys.Contains(key) && !key.StartsWith("AUTOSOURCARR_", StringComparison.Ordinal) && !key.StartsWith("PYTHON", StringComparison.Ordinal)) result[key] = value;
        result["BIND_IP"] = "127.0.0.1"; result["BIND_PORT"] = "4567"; result["AUTH_MODE"] = "none";
        result["BASIC_AUTH_ENABLED"] = "false"; result["WEB_UI_ENABLED"] = "false";
        result["INITIAL_OPEN_IN_BROWSER_ENABLED"] = "false"; result["KCEF_ENABLED"] = "true";
        var userAgent = ReadUserAgent();
        if (userAgent.Length > 0)
        {
            result["JAVA_TOOL_OPTIONS"] = "-javaagent:" + UserAgentJar;
            result["AUTOSOURCARR_SOURCE_USER_AGENT"] = userAgent;
            result["AUTOSOURCARR_SOURCE_UA_REVISION"] = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(userAgent)));
            result["AUTOSOURCARR_SOURCE_UA_STATUS"] = Path.Combine(_dataRoot, "cache", "autosourcarr-user-agent.properties");
        }
        _privacy.Register(result.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal));
        return ReadOnly(result);
    }

    public SourceEngineLaunchEnvironment BuildLaunch(IReadOnlyDictionary<string, string?> parentEnvironment, AutoSourcarrSettings settings)
    {
        var result = PrepareLaunch(Build(parentEnvironment), settings);
        _privacy.Register(result.IntendedChallenges.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal));
        return result;
    }

    /// <summary>Projects the fixed image's source environment into the native START payload, without launching or granting readiness.</summary>
    internal SourceEngineLaunchEnvironment BuildNativeLaunch(IReadOnlyDictionary<string, string?> parentEnvironment, AutoSourcarrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SourceEngineMode != "integrated") throw new InvalidOperationException("An external source engine has no local native launch.");
        var original = BuildLaunch(parentEnvironment, settings);
        var launch = original.Launch.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        // Native owner and immutable launcher supply these values. Refuse a
        // differing inherited request rather than silently discarding its intent.
        foreach (var (key, expected) in new[] { ("HOME", "/home/suwayomi"), ("PATH", "/usr/bin:/bin"),
            ("JAVA_HOME", "/opt/java/openjdk"), ("LANG", "C.UTF-8") })
        {
            if (launch.TryGetValue(key, out var requested) && requested != expected)
                throw new InvalidDataException("The source image environment conflicts with its fixed native " + key + " contract.");
            launch.Remove(key);
        }
        // Reuse the exact wire guards for the remaining keys/UTF8/quotas. This
        // synthetic envelope is never sent and is not a native boot binding.
        var checkedFrame = NativeSourceOwnerProtocol.EncodeCommand(new byte[32], 1, NativeSourceOwnerOpcode.Start, launch.ToArray());
        CryptographicOperations.ZeroMemory(checkedFrame);
        return new(ReadOnly(launch), original.IntendedChallenges);
    }

    /// <summary>Preserves intended solver settings separately; actual enabling still requires a real challenge owner and readiness check.</summary>
    public static SourceEngineLaunchEnvironment PrepareLaunch(IReadOnlyDictionary<string, string> environment, AutoSourcarrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(settings);
        var launch = environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (settings.AppServicesMode == "integrated") { launch["FLARESOLVERR_URL"] = "http://127.0.0.1:8191"; launch.TryAdd("FLARESOLVERR_ENABLED", "true"); }
        if (!settings.SourceAutoChallenges) launch["FLARESOLVERR_ENABLED"] = "false";
        var intended = new Dictionary<string, string>(launch, StringComparer.Ordinal);
        if (settings.SourceAutoChallenges && launch.ContainsKey("FLARESOLVERR_ENABLED")) launch["FLARESOLVERR_ENABLED"] = "false";
        launch["FLARESOLVERR_TIMEOUT"] = SolverTimeout(launch.GetValueOrDefault("FLARESOLVERR_TIMEOUT", "30"), settings.SourceRequestTimeout).ToString(CultureInfo.InvariantCulture);
        return new(ReadOnly(launch), ReadOnly(intended));
    }

    public static int SolverTimeout(string? value, double sourceRequestTimeout)
    {
        var requested = PythonInteger(value) ?? 30;
        var client = double.IsFinite(sourceRequestTimeout) ? Math.Truncate(sourceRequestTimeout) - 11 : throw new ArgumentException("Source request timeout must be finite");
        return (int)BigInteger.Max(1, BigInteger.Min(BigInteger.Min(requested, 30), new BigInteger(client)));
    }

    private string ReadUserAgent()
    {
        if (!ConfigurationPresent()) return "";
        byte[]? bytes;
        try
        {
            var path = Path.Combine(_configurationRoot, "source-browser.json");
            if (Directory.Exists(path)) throw new InvalidDataException("Source browser preferences must be a regular file");
            using var input = PrivateDocumentStore.OpenRegular(path, writable: false, create: false, maximum: 64 * 1024);
            using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, 64 * 1024 + 1 - (int)output.Length))) != 0)
            { output.Write(buffer, 0, count); if (output.Length > 64 * 1024) throw new InvalidDataException("Source browser preferences exceed their bound"); }
            bytes = output.ToArray();
        }
        catch (FileNotFoundException) { return ""; }
        JsonObject preferences;
        try { preferences = JsonNode.Parse(Utf8.GetString(bytes), documentOptions: new() { MaxDepth = 16 }) as JsonObject ?? throw new ArgumentException("Source browser preferences must be an object"); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw new ArgumentException("Source browser preferences are not bounded UTF-8 JSON"); }
        var preset = preferences.ContainsKey("preset") ? Scalar(preferences["preset"]) : "default";
        var raw = preferences.ContainsKey("user_agent") ? Scalar(preferences["user_agent"]) : "";
        var value = preset switch
        {
            "default" => "", "chrome-windows" => DefaultUserAgent,
            "chrome-linux" => DefaultUserAgent.Replace("Windows NT 10.0; Win64; x64", "X11; Linux x86_64", StringComparison.Ordinal),
            "firefox-windows" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0",
            "custom" or "browser" => raw.Trim(), _ => throw new ArgumentException("Select a known user-agent preset or Custom")
        };
        if ((preset is "custom" or "browser") && (value.Length == 0 || raw.Any(c => c is '\r' or '\n' or '\t')))
            throw new ArgumentException("Enter a printable custom user agent without line breaks or tabs");
        if (value.Length > 1024 || value.Any(c => c is < (char)32 or > (char)126)) throw new ArgumentException("User agent must contain at most 1024 printable ASCII characters, without line breaks");
        return value;
    }

    private bool ConfigurationPresent()
    {
        for (var current = new DirectoryInfo(_configurationRoot); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) || File.Exists(current.FullName)) throw new IOException("Source configuration contains a linked or non-directory path");
            if (!current.Exists) return false;
        }
        return true;
    }
    // Python str leaves a top-level string alone but uses repr for container members.
    // JSON serialization would change quotes, bool/null spelling, insertion order and launched UA hashes.
    internal static string Scalar(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : PythonRepresentation(value);
    private static string PythonRepresentation(JsonNode? value) => value switch
    {
        null => "None",
        JsonObject obj => "{" + string.Join(", ", obj.Select(pair => PythonQuote(pair.Key) + ": " + PythonRepresentation(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(", ", array.Select(PythonRepresentation)) + "]",
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => PythonQuote(text),
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag ? "True" : "False",
        JsonValue scalar when scalar.GetValueKind() == JsonValueKind.Number => PythonNumber(scalar),
        _ => throw new ArgumentException("Unsupported source browser preference value")
    };
    private static string PythonNumber(JsonValue value)
    {
        var raw = value.ToJsonString();
        if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E'))
            return raw.Length <= 1024 && BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? integer.ToString(CultureInfo.InvariantCulture)
                : throw new ArgumentException("Invalid source browser preference number");
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            if (double.IsPositiveInfinity(number)) return "inf";
            if (double.IsNegativeInfinity(number)) return "-inf";
            if (double.IsNaN(number)) return "nan";
        }
        return CanonicalJson.Text(value);
    }
    private static string PythonQuote(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var output = new StringBuilder().Append(quote);
        for (var index = 0; index < text.Length; index++)
        {
            var code = (int)text[index];
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) code = char.ConvertToUtf32(text[index], text[++index]);
            if (code == quote || code == '\\') output.Append('\\').Append((char)code);
            else if (code is 9 or 10 or 13) output.Append(code switch { 9 => "\\t", 10 => "\\n", _ => "\\r" });
            else
            {
                var category = code is >= 0xd800 and <= 0xdfff ? UnicodeCategory.Surrogate : Rune.GetUnicodeCategory(new Rune(code));
                var printable = code == 32 || category is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
                if (printable) output.Append(char.ConvertFromUtf32(code));
                else output.Append(code <= 255 ? "\\x" + code.ToString("x2", CultureInfo.InvariantCulture) : code <= 65535 ? "\\u" + code.ToString("x4", CultureInfo.InvariantCulture) : "\\U" + code.ToString("x8", CultureInfo.InvariantCulture));
            }
        }
        return output.Append(quote).ToString();
    }
    private static BigInteger? PythonInteger(string? value)
    {
        if (value is null) return null;
        var runes = value.Trim().EnumerateRunes().ToArray(); var output = new StringBuilder(); var offset = 0; var digits = 0; var previousDigit = false;
        if (runes.Length > 0 && runes[0].Value is '+' or '-') { output.Append((char)runes[0].Value); offset++; }
        for (; offset < runes.Length; offset++)
        {
            var rune = runes[offset];
            if (rune.Value == '_') { if (!previousDigit || offset + 1 == runes.Length) return null; previousDigit = false; continue; }
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber || ++digits > 4300) return null;
            output.Append((char)('0' + (int)Rune.GetNumericValue(rune))); previousDigit = true;
        }
        return previousDigit && BigInteger.TryParse(output.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
    private static IReadOnlyDictionary<string, string> ReadOnly(Dictionary<string, string> values) => new ReadOnlyDictionary<string, string>(values);
}
